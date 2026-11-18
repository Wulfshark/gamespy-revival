// GameSpy Revival lobby server: accounts, friends and live presence.
// Free software under the GNU GPL, version 3 or (at your option) any later version. No warranty.
//
// Transport: TLS over TCP. The server makes its own certificate on first run; the app
// remembers it per server (trust on first use) and warns if it ever changes.
// Messages: one JSON object per line, each with an "op" field. See PROTOCOL.md.
package main

import (
	"bufio"
	"crypto/pbkdf2"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"flag"
	"fmt"
	"log"
	"math/big"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
)

const (
	protoVersion   = 1
	hashIterations = 210000
	maxLine        = 8192
	idleTimeout    = 120 * time.Second
	maxFriends     = 500
)

// ---------------------------------------------------------------- stored data

type Account struct {
	Name     string            `json:"name"`
	Salt     string            `json:"salt"`
	Hash     string            `json:"hash"`
	Created  time.Time         `json:"created"`
	LastSeen time.Time         `json:"last_seen"`
	Friends  []string          `json:"friends"`  // mutual friends (lower-case keys)
	Incoming []string          `json:"incoming"` // requests waiting for this user
	Outgoing []string          `json:"outgoing"` // requests this user sent
	Tokens   map[string]string `json:"tokens"`   // sha256(token) -> created (RFC3339)
	Inbox    []Stored          `json:"inbox"`    // direct messages sent while this user was offline
}

type Stored struct {
	From string `json:"from"`
	Text string `json:"text"`
	TS   int64  `json:"ts"`
}

// ChatMsg is one saved message in a text channel or a private conversation.
type ChatMsg struct {
	ID        int64               `json:"id"`
	User      string              `json:"user"`
	Text      string              `json:"text"`
	TS        int64               `json:"ts"`
	Kind      string              `json:"kind,omitempty"` // "" text, "invite" game invite card
	Game      string              `json:"game,omitempty"`
	Title     string              `json:"title,omitempty"`
	Address   string              `json:"address,omitempty"`
	Detail    string              `json:"detail,omitempty"`
	ReplyID   int64               `json:"reply_id,omitempty"`
	ReplyUser string              `json:"reply_user,omitempty"`
	ReplyText string              `json:"reply_text,omitempty"`
	Edited    bool                `json:"edited,omitempty"`
	Reactions map[string][]string `json:"reactions,omitempty"` // emoji -> who
}

func (m ChatMsg) toMsg(channel string) Msg {
	o := Msg{"op": "chat", "channel": channel, "id": m.ID, "user": m.User, "text": m.Text, "ts": m.TS}
	if m.Kind != "" {
		o["kind"], o["game"], o["title"], o["address"], o["detail"] = m.Kind, m.Game, m.Title, m.Address, m.Detail
	}
	if m.ReplyID != 0 {
		o["reply_id"], o["reply_user"], o["reply_text"] = m.ReplyID, m.ReplyUser, m.ReplyText
	}
	if m.Edited {
		o["edited"] = true
	}
	if len(m.Reactions) > 0 {
		o["reactions"] = m.Reactions
	}
	return o
}

type ChatStore struct {
	path     string
	NextID   int64                `json:"next_id"`
	Channels map[string][]ChatMsg `json:"channels"`
	DMs      map[string][]ChatMsg `json:"dms"` // key: the two lower-case names, sorted, joined by "|"
}

func loadChat(path string) *ChatStore {
	c := &ChatStore{path: path, NextID: 1, Channels: map[string][]ChatMsg{}, DMs: map[string][]ChatMsg{}}
	if b, err := os.ReadFile(path); err == nil {
		json.Unmarshal(b, c)
	}
	if c.Channels == nil {
		c.Channels = map[string][]ChatMsg{}
	}
	if c.DMs == nil {
		c.DMs = map[string][]ChatMsg{}
	}
	if c.NextID < 1 {
		c.NextID = 1
	}
	return c
}

func (c *ChatStore) save() {
	b, err := json.Marshal(c)
	if err != nil {
		return
	}
	tmp := c.path + ".tmp"
	if os.WriteFile(tmp, b, 0600) == nil {
		os.Rename(tmp, c.path)
	}
}

func pairKey(a, b string) string {
	if a > b {
		a, b = b, a
	}
	return a + "|" + b
}

type Store struct {
	path     string
	Accounts map[string]*Account `json:"accounts"` // key: lower-case name
}

func loadStore(path string) (*Store, error) {
	s := &Store{path: path, Accounts: map[string]*Account{}}
	b, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return s, nil
	}
	if err != nil {
		return nil, err
	}
	if err := json.Unmarshal(b, s); err != nil {
		return nil, fmt.Errorf("reading %s: %w", path, err)
	}
	if s.Accounts == nil {
		s.Accounts = map[string]*Account{}
	}
	return s, nil
}

func (s *Store) save() error {
	b, err := json.MarshalIndent(s, "", " ")
	if err != nil {
		return err
	}
	tmp := s.path + ".tmp"
	if err := os.WriteFile(tmp, b, 0600); err != nil {
		return err
	}
	return os.Rename(tmp, s.path)
}

func hashPassword(pass, saltHex string) string {
	salt, _ := hex.DecodeString(saltHex)
	k, _ := pbkdf2.Key(sha256.New, pass, salt, hashIterations, 32)
	return hex.EncodeToString(k)
}

func randHex(n int) string {
	b := make([]byte, n)
	rand.Read(b)
	return hex.EncodeToString(b)
}

func tokenKey(tok string) string {
	h := sha256.Sum256([]byte(tok))
	return hex.EncodeToString(h[:])
}

func remove(list []string, v string) []string {
	out := list[:0]
	for _, x := range list {
		if x != v {
			out = append(out, x)
		}
	}
	return out
}

func has(list []string, v string) bool {
	for _, x := range list {
		if x == v {
			return true
		}
	}
	return false
}

// ---------------------------------------------------------------- server state

type Msg map[string]any

type Client struct {
	conn   net.Conn
	out    chan []byte
	key    string // lower-case account name once signed in
	status string
	game   string
	since  time.Time
	closed bool
	sent   []time.Time // recent chat messages, for the rate limit
	pres   string      // online, idle, dnd, invisible
	custom string      // custom status text
	// voice
	room       string // voice channel this client is in, "" if none
	mute, deaf bool
	vframes    int   // voice frames this second
	vsecond    int64 // which second vframes counts
}

// sendVoice drops the frame instead of the connection when the client is behind.
func (c *Client) sendVoice(b []byte) {
	if c.closed {
		return
	}
	select {
	case c.out <- b:
	default:
	}
}

type Server struct {
	mu        sync.Mutex
	store     *Store
	online    map[string]*Client // key -> client
	nameRe    *regexp.Regexp
	failLog   map[string][]time.Time // ip -> recent failed logins
	chat      *ChatStore
	group     string   // the group's display name
	channels  []string // text channels
	relayPort int      // Metal Fatigue relay port advertised to clients (0 = none)
}

var voiceRooms = []string{"Lobby", "Quake III Arena", "OpenArena", "Unreal Tournament", "Metal Fatigue", "AFK"}

func validRoom(r string) bool {
	for _, v := range voiceRooms {
		if v == r {
			return true
		}
	}
	return false
}

// who is in which voice channel (caller holds mu)
func (s *Server) voiceMsg() Msg {
	rooms := []Msg{}
	for _, r := range voiceRooms {
		members := []Msg{}
		for _, c := range s.online {
			if c.room == r {
				members = append(members, Msg{"user": s.displayName(c.key), "mute": c.mute, "deaf": c.deaf})
			}
		}
		sort.Slice(members, func(i, j int) bool {
			return strings.ToLower(members[i]["user"].(string)) < strings.ToLower(members[j]["user"].(string))
		})
		rooms = append(rooms, Msg{"name": r, "members": members})
	}
	return Msg{"op": "voice_rooms", "rooms": rooms}
}

func (s *Server) broadcastVoice() {
	m := s.voiceMsg()
	for _, c := range s.online {
		c.send(m)
	}
}

const (
	maxText     = 400
	maxHistory  = 200 // messages kept per channel or conversation
	sendHistory = 60  // messages sent when someone signs in
	maxInbox    = 100
)

// true if this client may send another chat message right now (6 per 10 seconds)
func (c *Client) allowChat() bool {
	cut := time.Now().Add(-10 * time.Second)
	l := c.sent[:0]
	for _, t := range c.sent {
		if t.After(cut) {
			l = append(l, t)
		}
	}
	c.sent = l
	if len(l) >= 6 {
		return false
	}
	c.sent = append(c.sent, time.Now())
	return true
}

func cleanText(t string) string {
	t = strings.TrimSpace(strings.Map(func(r rune) rune {
		if r < 32 {
			return ' '
		}
		return r
	}, t))
	if r := []rune(t); len(r) > maxText {
		t = string(r[:maxText])
	}
	return t
}

func (c *Client) send(m Msg) {
	if c.closed {
		return
	}
	b, _ := json.Marshal(m)
	b = append(b, '\n')
	select {
	case c.out <- b:
	default: // client is not reading; drop it rather than block everyone
		c.conn.Close()
	}
}

func (s *Server) displayName(key string) string {
	if a := s.store.Accounts[key]; a != nil {
		return a.Name
	}
	return key
}

// presence of one user as seen by a friend (caller holds mu)
func (s *Server) presenceOf(key string) Msg {
	m := Msg{"op": "presence", "user": s.displayName(key), "online": false, "status": "", "game": "", "presence": "offline",
		"owner": key == s.ownerKey()}
	if c := s.online[key]; c != nil && c.pres != "invisible" {
		m["online"] = true
		m["status"] = c.status
		m["game"] = c.game
		m["presence"] = c.pres
		m["custom"] = c.custom
		if c.pres == "" {
			m["presence"] = "online"
		}
	} else if a := s.store.Accounts[key]; a != nil && !a.LastSeen.IsZero() {
		m["last_seen"] = a.LastSeen.UTC().Format(time.RFC3339)
	}
	return m
}

// full friends list for one user (caller holds mu)
func (s *Server) friendsMsg(key string) Msg {
	a := s.store.Accounts[key]
	friends := []Msg{}
	for _, f := range a.Friends {
		friends = append(friends, s.presenceOf(f))
	}
	sort.Slice(friends, func(i, j int) bool {
		return strings.ToLower(friends[i]["user"].(string)) < strings.ToLower(friends[j]["user"].(string))
	})
	in, out := []string{}, []string{}
	for _, k := range a.Incoming {
		in = append(in, s.displayName(k))
	}
	for _, k := range a.Outgoing {
		out = append(out, s.displayName(k))
	}
	return Msg{"op": "friends", "friends": friends, "incoming": in, "outgoing": out}
}

// tell every online friend of key about key's presence (caller holds mu)
func (s *Server) broadcastPresence(key string) {
	a := s.store.Accounts[key]
	if a == nil {
		return
	}
	p := s.presenceOf(key)
	for _, c := range s.online { // everyone in the group sees everyone, like a Discord server
		c.send(p)
	}
}

// every account and what it is doing (caller holds mu)
func (s *Server) membersMsg() Msg {
	list := []Msg{}
	for k := range s.store.Accounts {
		list = append(list, s.presenceOf(k))
	}
	sort.Slice(list, func(i, j int) bool {
		return strings.ToLower(list[i]["user"].(string)) < strings.ToLower(list[j]["user"].(string))
	})
	return Msg{"op": "members", "members": list}
}

// the group's owner: the first account created on this server (caller holds mu)
func (s *Server) ownerKey() string {
	best, bt := "", time.Time{}
	for k, a := range s.store.Accounts {
		if best == "" || a.Created.Before(bt) {
			best, bt = k, a.Created
		}
	}
	return best
}

func (s *Server) findMsg(ch string, id int64) *ChatMsg {
	l := s.chat.Channels[ch]
	for i := range l {
		if l[i].ID == id {
			return &l[i]
		}
	}
	return nil
}

func (s *Server) broadcast(m Msg) {
	for _, oc := range s.online {
		oc.send(m)
	}
}

func (s *Server) validChannel(ch string) bool {
	for _, c := range s.channels {
		if c == ch {
			return true
		}
	}
	return false
}

func lastN(l []ChatMsg, n int) []ChatMsg {
	if len(l) > n {
		return l[len(l)-n:]
	}
	return l
}

func (s *Server) addMessage(channel string, m ChatMsg) Msg {
	m.ID = s.chat.NextID
	s.chat.NextID++
	m.TS = time.Now().Unix()
	l := append(s.chat.Channels[channel], m)
	s.chat.Channels[channel] = lastN(l, maxHistory)
	s.chat.save()
	return m.toMsg(channel)
}

func (s *Server) pushFriends(key string) {
	if c := s.online[key]; c != nil {
		c.send(s.friendsMsg(key))
	}
}

func (s *Server) tooManyFailures(ip string) bool {
	cut := time.Now().Add(-10 * time.Minute)
	l := s.failLog[ip][:0]
	for _, t := range s.failLog[ip] {
		if t.After(cut) {
			l = append(l, t)
		}
	}
	s.failLog[ip] = l
	return len(l) >= 10
}

// ---------------------------------------------------------------- request handling

func errMsg(re, text string) Msg { return Msg{"op": "error", "re": re, "msg": text} }

func str(m Msg, k string) string {
	v, _ := m[k].(string)
	return strings.TrimSpace(v)
}

func (s *Server) signIn(c *Client, key string) {
	if old := s.online[key]; old != nil && old != c {
		old.send(Msg{"op": "kicked", "msg": "Signed in from another place."})
		old.key = ""
		old.room = ""
		old.conn.Close()
	}
	c.key = key
	c.status = "Online"
	c.since = time.Now()
	s.online[key] = c
	a := s.store.Accounts[key]
	a.LastSeen = time.Now()
	s.store.save()
	s.broadcastPresence(key)
}

func (s *Server) handle(c *Client, m Msg) {
	op := str(m, "op")
	ip, _, _ := net.SplitHostPort(c.conn.RemoteAddr().String())
	s.mu.Lock()
	defer s.mu.Unlock()

	if op == "ping" {
		c.send(Msg{"op": "pong"})
		return
	}
	if c.key == "" {
		switch op {
		case "hello":
			c.send(Msg{"op": "hello", "proto": protoVersion, "server": "GameSpy Revival lobby"})
		case "register":
			name, pass := str(m, "user"), str(m, "pass")
			if !s.nameRe.MatchString(name) {
				c.send(errMsg(op, "Names are 3 to 20 letters, numbers, _ or -."))
				return
			}
			if len(pass) < 8 {
				c.send(errMsg(op, "Use a password of at least 8 characters."))
				return
			}
			if s.tooManyFailures(ip) {
				c.send(errMsg(op, "Too many tries. Wait 10 minutes."))
				return
			}
			key := strings.ToLower(name)
			if s.store.Accounts[key] != nil {
				c.send(errMsg(op, "That name is taken."))
				return
			}
			salt := randHex(16)
			s.store.Accounts[key] = &Account{Name: name, Salt: salt, Hash: hashPassword(pass, salt),
				Created: time.Now(), Friends: []string{}, Incoming: []string{}, Outgoing: []string{}, Tokens: map[string]string{}}
			s.failLog[ip] = append(s.failLog[ip], time.Now()) // registrations count toward the limit too
			log.Printf("new account %s from %s", name, ip)
			s.store.save()
			defer func() {
				m := s.membersMsg()
				for _, oc := range s.online {
					oc.send(m)
				}
			}()
			fallthrough
		case "login":
			name, pass, tok := str(m, "user"), str(m, "pass"), str(m, "token")
			key := strings.ToLower(name)
			if s.tooManyFailures(ip) {
				c.send(errMsg("login", "Too many tries. Wait 10 minutes."))
				return
			}
			a := s.store.Accounts[key]
			ok := false
			if a != nil && tok != "" {
				_, ok = a.Tokens[tokenKey(tok)]
			} else if a != nil && pass != "" {
				ok = subtle.ConstantTimeCompare([]byte(hashPassword(pass, a.Salt)), []byte(a.Hash)) == 1
			}
			if !ok {
				s.failLog[ip] = append(s.failLog[ip], time.Now())
				if tok != "" {
					c.send(errMsg("login", "Your saved sign-in expired. Sign in again."))
				} else {
					c.send(errMsg("login", "Wrong name or password."))
				}
				return
			}
			newTok := ""
			if str(m, "remember") == "1" || tok != "" {
				if tok != "" {
					delete(a.Tokens, tokenKey(tok)) // rotate
				}
				newTok = randHex(32)
				if a.Tokens == nil {
					a.Tokens = map[string]string{}
				}
				a.Tokens[tokenKey(newTok)] = time.Now().UTC().Format(time.RFC3339)
				for len(a.Tokens) > 10 { // keep the newest few devices
					oldest, ot := "", "9999"
					for k, v := range a.Tokens {
						if v < ot {
							oldest, ot = k, v
						}
					}
					delete(a.Tokens, oldest)
				}
			}
			s.signIn(c, key)
			c.send(Msg{"op": "welcome", "user": a.Name, "token": newTok, "relay_port": s.relayPort,
				"group": s.group, "channels": s.channels})
			c.send(s.voiceMsg())
			c.send(s.friendsMsg(key))
			c.send(s.membersMsg())
			for _, ch := range s.channels {
				msgs := []Msg{}
				for _, cm := range lastN(s.chat.Channels[ch], sendHistory) {
					msgs = append(msgs, cm.toMsg(ch))
				}
				c.send(Msg{"op": "history", "channel": ch, "msgs": msgs})
			}
			if len(a.Inbox) > 0 {
				for _, m := range a.Inbox {
					c.send(Msg{"op": "dm", "from": m.From, "text": m.Text, "ts": m.TS, "offline": true})
				}
				a.Inbox = nil
				s.store.save()
			}
			log.Printf("%s signed in from %s", a.Name, ip)
		default:
			c.send(errMsg(op, "Sign in first."))
		}
		return
	}

	me := s.store.Accounts[c.key]
	switch op {
	case "status":
		st, game := str(m, "status"), str(m, "game")
		if len(st) > 80 {
			st = st[:80]
		}
		if len(game) > 40 {
			game = game[:40]
		}
		c.status, c.game = st, game
		if cs, ok := m["custom"].(string); ok {
			if r := []rune(cleanText(cs)); len(r) > 60 {
				cs = string(r[:60])
			} else {
				cs = string(r)
			}
			c.custom = cs
		}
		switch p := str(m, "presence"); p {
		case "online", "idle", "dnd", "invisible":
			c.pres = p
		}
		s.broadcastPresence(c.key)
	case "friends":
		c.send(s.friendsMsg(c.key))
	case "friend_add":
		k := strings.ToLower(str(m, "user"))
		other := s.store.Accounts[k]
		switch {
		case other == nil:
			c.send(errMsg(op, "No one is called "+str(m, "user")+" on this server."))
			return
		case k == c.key:
			c.send(errMsg(op, "That's you."))
			return
		case has(me.Friends, k):
			c.send(errMsg(op, other.Name+" is already your friend."))
			return
		case len(me.Friends)+len(me.Outgoing) >= maxFriends:
			c.send(errMsg(op, "Your friends list is full."))
			return
		}
		if has(me.Incoming, k) { // they asked first: accepting
			me.Incoming = remove(me.Incoming, k)
			other.Outgoing = remove(other.Outgoing, c.key)
			me.Friends = append(me.Friends, k)
			other.Friends = append(other.Friends, c.key)
			c.send(Msg{"op": "info", "msg": "You and " + other.Name + " are now friends."})
			if oc := s.online[k]; oc != nil {
				oc.send(Msg{"op": "info", "msg": me.Name + " accepted your friend request."})
			}
		} else if !has(me.Outgoing, k) {
			me.Outgoing = append(me.Outgoing, k)
			other.Incoming = append(other.Incoming, c.key)
			c.send(Msg{"op": "info", "msg": "Friend request sent to " + other.Name + "."})
			if oc := s.online[k]; oc != nil {
				oc.send(Msg{"op": "info", "msg": me.Name + " sent you a friend request."})
			}
		}
		s.store.save()
		s.pushFriends(c.key)
		s.pushFriends(k)
	case "friend_remove": // also declines a request or cancels one you sent
		k := strings.ToLower(str(m, "user"))
		other := s.store.Accounts[k]
		if other == nil {
			return
		}
		me.Friends, other.Friends = remove(me.Friends, k), remove(other.Friends, c.key)
		me.Incoming, other.Outgoing = remove(me.Incoming, k), remove(other.Outgoing, c.key)
		me.Outgoing, other.Incoming = remove(me.Outgoing, k), remove(other.Incoming, c.key)
		s.store.save()
		s.pushFriends(c.key)
		s.pushFriends(k)
	case "chat":
		t := cleanText(str(m, "text"))
		if t == "" {
			return
		}
		if !c.allowChat() {
			c.send(errMsg(op, "Slow down a little."))
			return
		}
		ch := str(m, "channel")
		if ch == "" && len(s.channels) > 0 {
			ch = s.channels[0]
		}
		if !s.validChannel(ch) {
			c.send(errMsg(op, "No such channel."))
			return
		}
		cm := ChatMsg{User: me.Name, Text: t}
		if rid, ok := m["reply"].(float64); ok && rid > 0 {
			if orig := s.findMsg(ch, int64(rid)); orig != nil {
				cm.ReplyID, cm.ReplyUser = orig.ID, orig.User
				r := []rune(orig.Text)
				if orig.Kind == "invite" {
					r = []rune("Game invite: " + orig.Title)
				}
				if len(r) > 90 {
					r = append(r[:90], '\u2026')
				}
				cm.ReplyText = string(r)
			}
		}
		out := s.addMessage(ch, cm)
		s.broadcast(out)
	case "edit", "delete", "react":
		ch := str(m, "channel")
		idf, _ := m["id"].(float64)
		msg := s.findMsg(ch, int64(idf))
		if msg == nil {
			return
		}
		mine := strings.EqualFold(msg.User, me.Name)
		switch op {
		case "edit":
			t := cleanText(str(m, "text"))
			if !mine || t == "" || msg.Kind != "" {
				return
			}
			msg.Text, msg.Edited = t, true
		case "delete":
			if !mine && c.key != s.ownerKey() {
				c.send(errMsg(op, "You can only delete your own messages."))
				return
			}
			l := s.chat.Channels[ch]
			for i := range l {
				if l[i].ID == msg.ID {
					s.chat.Channels[ch] = append(l[:i:i], l[i+1:]...)
					break
				}
			}
			s.chat.save()
			s.broadcast(Msg{"op": "msg_delete", "channel": ch, "id": int64(idf)})
			return
		case "react":
			e := str(m, "emoji")
			if e == "" || len(e) > 16 {
				return
			}
			if msg.Reactions == nil {
				msg.Reactions = map[string][]string{}
			}
			who := msg.Reactions[e]
			on, _ := m["on"].(bool)
			if on && !has(who, me.Name) {
				if len(msg.Reactions) >= 20 && len(who) == 0 {
					return
				}
				who = append(who, me.Name)
			} else if !on {
				who = remove(who, me.Name)
			}
			if len(who) == 0 {
				delete(msg.Reactions, e)
			} else {
				msg.Reactions[e] = who
			}
		}
		s.chat.save()
		u := msg.toMsg(ch)
		u["op"] = "msg_update"
		s.broadcast(u)
	case "invite":
		ch := str(m, "channel")
		if !s.validChannel(ch) {
			ch = s.channels[0]
		}
		if !c.allowChat() {
			c.send(errMsg(op, "Slow down a little."))
			return
		}
		cut := func(v string, n int) string {
			if r := []rune(cleanText(v)); len(r) > n {
				return string(r[:n])
			}
			return cleanText(v)
		}
		out := s.addMessage(ch, ChatMsg{User: me.Name, Text: cut(str(m, "text"), 200), Kind: "invite",
			Game: cut(str(m, "game"), 20), Title: cut(str(m, "title"), 80), Address: cut(str(m, "address"), 80), Detail: cut(str(m, "detail"), 120)})
		for _, oc := range s.online {
			oc.send(out)
		}
	case "typing":
		ch := str(m, "channel")
		if strings.HasPrefix(ch, "@") {
			if oc := s.online[strings.ToLower(ch[1:])]; oc != nil {
				oc.send(Msg{"op": "typing", "channel": "@" + me.Name, "user": me.Name})
			}
			return
		}
		if !s.validChannel(ch) {
			return
		}
		for _, oc := range s.online {
			if oc != c {
				oc.send(Msg{"op": "typing", "channel": ch, "user": me.Name})
			}
		}
	case "dm_history":
		k := strings.ToLower(str(m, "with"))
		msgs := []Msg{}
		for _, cm := range lastN(s.chat.DMs[pairKey(c.key, k)], sendHistory) {
			o := cm.toMsg("")
			o["op"] = "dm"
			o["from"] = cm.User
			msgs = append(msgs, o)
		}
		c.send(Msg{"op": "dm_history", "with": s.displayName(k), "msgs": msgs})
	case "dm":
		t := cleanText(str(m, "text"))
		k := strings.ToLower(str(m, "to"))
		if t == "" {
			return
		}
		other := s.store.Accounts[k]
		if other == nil || !has(me.Friends, k) {
			c.send(errMsg(op, "You can only message your friends."))
			return
		}
		if !c.allowChat() {
			c.send(errMsg(op, "Slow down a little."))
			return
		}
		now := time.Now().Unix()
		pk := pairKey(c.key, k)
		s.chat.DMs[pk] = lastN(append(s.chat.DMs[pk], ChatMsg{ID: s.chat.NextID, User: me.Name, Text: t, TS: now}), maxHistory)
		s.chat.NextID++
		s.chat.save()
		if oc := s.online[k]; oc != nil {
			oc.send(Msg{"op": "dm", "from": me.Name, "text": t, "ts": now})
		} else {
			other.Inbox = append(other.Inbox, Stored{From: me.Name, Text: t, TS: now})
			if len(other.Inbox) > maxInbox {
				other.Inbox = other.Inbox[len(other.Inbox)-maxInbox:]
			}
			s.store.save()
			c.send(Msg{"op": "info", "msg": other.Name + " is offline. They'll get it when they sign in."})
		}
	case "voice_join":
		r := str(m, "room")
		if !validRoom(r) {
			c.send(errMsg(op, "No such voice channel."))
			return
		}
		c.room = r
		s.broadcastVoice()
	case "voice_leave":
		if c.room != "" {
			c.room = ""
			s.broadcastVoice()
		}
	case "voice_state":
		mu, _ := m["mute"].(bool)
		de, _ := m["deaf"].(bool)
		if mu != c.mute || de != c.deaf {
			c.mute, c.deaf = mu, de
			if c.room != "" {
				s.broadcastVoice()
			}
		}
	case "v":
		if c.room == "" || c.mute {
			return
		}
		d := str(m, "d")
		if d == "" || len(d) > 2000 {
			return
		}
		sec := time.Now().Unix()
		if sec != c.vsecond {
			c.vsecond, c.vframes = sec, 0
		}
		c.vframes++
		if c.vframes > 60 {
			return
		}
		b, _ := json.Marshal(Msg{"op": "v", "u": me.Name, "d": d, "q": m["q"]})
		b = append(b, '\n')
		for _, oc := range s.online {
			if oc != c && oc.room == c.room && !oc.deaf {
				oc.sendVoice(b)
			}
		}
	case "logout":
		delete(me.Tokens, tokenKey(str(m, "token")))
		s.store.save()
		c.conn.Close()
	default:
		c.send(errMsg(op, "Unknown request."))
	}
}

func (s *Server) serve(conn net.Conn) {
	c := &Client{conn: conn, out: make(chan []byte, 1024)}
	go func() {
		for b := range c.out {
			conn.SetWriteDeadline(time.Now().Add(15 * time.Second))
			if _, err := conn.Write(b); err != nil {
				conn.Close()
			}
		}
	}()
	defer func() {
		s.mu.Lock()
		if c.key != "" && s.online[c.key] == c {
			delete(s.online, c.key)
			if c.room != "" {
				s.broadcastVoice()
			}
			if a := s.store.Accounts[c.key]; a != nil {
				a.LastSeen = time.Now()
				s.store.save()
			}
			s.broadcastPresence(c.key)
			log.Printf("%s signed out", s.displayName(c.key))
		}
		c.closed = true
		close(c.out)
		s.mu.Unlock()
		conn.Close()
	}()
	r := bufio.NewReaderSize(conn, maxLine)
	for {
		conn.SetReadDeadline(time.Now().Add(idleTimeout))
		line, err := r.ReadSlice('\n')
		if err != nil {
			return
		}
		var m Msg
		if json.Unmarshal(line, &m) != nil {
			return
		}
		s.handle(c, m)
	}
}

// ---------------------------------------------------------------- certificate

func loadOrMakeCert(dir string) (tls.Certificate, string, error) {
	cp, kp := filepath.Join(dir, "lobby-cert.pem"), filepath.Join(dir, "lobby-key.pem")
	if _, err := os.Stat(cp); err != nil {
		key, err := rsa.GenerateKey(rand.Reader, 2048)
		if err != nil {
			return tls.Certificate{}, "", err
		}
		serial, _ := rand.Int(rand.Reader, big.NewInt(1<<62))
		tpl := x509.Certificate{
			SerialNumber: serial,
			Subject:      pkix.Name{CommonName: "GameSpy Revival lobby"},
			NotBefore:    time.Now().Add(-time.Hour),
			NotAfter:     time.Now().AddDate(20, 0, 0),
			KeyUsage:     x509.KeyUsageDigitalSignature | x509.KeyUsageKeyEncipherment,
			ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		}
		der, err := x509.CreateCertificate(rand.Reader, &tpl, &tpl, &key.PublicKey, key)
		if err != nil {
			return tls.Certificate{}, "", err
		}
		os.WriteFile(cp, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), 0644)
		os.WriteFile(kp, pem.EncodeToMemory(&pem.Block{Type: "RSA PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(key)}), 0600)
		log.Printf("made a new certificate in %s", dir)
	}
	cert, err := tls.LoadX509KeyPair(cp, kp)
	if err != nil {
		return cert, "", err
	}
	sum := sha256.Sum256(cert.Certificate[0])
	return cert, strings.ToUpper(hex.EncodeToString(sum[:])), nil
}

func main() {
	port := flag.Int("port", 27901, "TCP port to listen on")
	dir := flag.String("data", ".", "folder for accounts.json and the certificate")
	relay := flag.Int("relay-port", 0, "Metal Fatigue relay port to tell clients about (0 = none)")
	group := flag.String("name", "Game Night", "the group's name, shown to everyone")
	chans := flag.String("channels", "general,looking-for-game,metal-fatigue,quake,unreal,clips-and-screens", "text channels, comma separated")
	logPath := flag.String("log", "", "write the log to this file instead of the console")
	relayExe := flag.String("relay-exe", "", "start and look after this Metal Fatigue relay (listens on -relay-port)")
	flag.Parse()
	os.MkdirAll(*dir, 0700)
	log.SetFlags(log.Ldate | log.Ltime)
	if *logPath != "" {
		trimLog(*logPath)
		if f, err := os.OpenFile(*logPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600); err == nil {
			log.SetOutput(f)
		}
	}
	if *relayExe != "" && *relay > 0 {
		go superviseRelay(*relayExe, *relay)
	}

	store, err := loadStore(filepath.Join(*dir, "accounts.json"))
	if err != nil {
		log.Fatal(err)
	}
	cert, fp, err := loadOrMakeCert(*dir)
	if err != nil {
		log.Fatal(err)
	}
	ln, err := tls.Listen("tcp", fmt.Sprintf(":%d", *port), &tls.Config{
		Certificates: []tls.Certificate{cert},
		MinVersion:   tls.VersionTLS12,
	})
	if err != nil {
		log.Fatal(err)
	}
	log.Printf("GameSpy Revival lobby listening on port %d, %d accounts, certificate %s", *port, len(store.Accounts), fp)
	s := &Server{store: store, online: map[string]*Client{}, failLog: map[string][]time.Time{},
		nameRe: regexp.MustCompile(`^[A-Za-z0-9_\-]{3,20}$`), relayPort: *relay,
		chat: loadChat(filepath.Join(*dir, "chat.json")), group: strings.TrimSpace(*group)}
	for _, ch := range strings.Split(*chans, ",") {
		ch = strings.ToLower(strings.TrimSpace(ch))
		if ch != "" && len(ch) <= 32 && !s.validChannel(ch) {
			s.channels = append(s.channels, ch)
		}
	}
	if len(s.channels) == 0 {
		s.channels = []string{"general"}
	}
	if s.group == "" {
		s.group = "Game Night"
	}
	for {
		conn, err := ln.Accept()
		if err != nil {
			log.Print(err)
			time.Sleep(100 * time.Millisecond)
			continue
		}
		go s.serve(conn)
	}
}

// keep the log from growing forever: if it's over 2 MB, keep the last 256 KB
func trimLog(path string) {
	b, err := os.ReadFile(path)
	if err != nil || len(b) < 2<<20 {
		return
	}
	os.WriteFile(path, b[len(b)-256<<10:], 0600)
}

// run the Metal Fatigue relay next to the group server, and restart it if it stops
func superviseRelay(exe string, port int) {
	wait := 2 * time.Second
	for {
		cmd := exec.Command(exe, "-listen", fmt.Sprintf("0.0.0.0:%d", port))
		hideWindow(cmd)
		start := time.Now()
		if err := cmd.Start(); err != nil {
			log.Printf("Metal Fatigue server: could not start: %v", err)
		} else {
			log.Printf("Metal Fatigue server running on port %d", port)
			err = cmd.Wait()
			log.Printf("Metal Fatigue server stopped: %v", err)
		}
		if time.Since(start) > time.Minute {
			wait = 2 * time.Second
		} else if wait < time.Minute {
			wait *= 2
		}
		time.Sleep(wait)
	}
}
