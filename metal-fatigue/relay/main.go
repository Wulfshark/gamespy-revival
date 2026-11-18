// mfnet relay: lobby list + packet relay for Metal Fatigue's DirectPlay stand-in.
// Protocol: frames of [u16 len][u8 type][payload], little endian. See PROTOCOL.md.
package main

import (
	"bufio"
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"sync"
	"time"
)

const (
	protoVersion = 1

	cHello         = 0x01
	cList          = 0x02
	cCreate        = 0x03
	cJoin          = 0x04
	cCreatePlayer  = 0x05
	cSend          = 0x06
	cSetFlags      = 0x07
	cLeave         = 0x08
	cDestroyPlayer = 0x09
	cStatus        = 0x0A

	sHello  = 0x81
	sList   = 0x82
	sCreate = 0x83
	sJoin   = 0x84
	sPlayer = 0x85
	sData   = 0x86
	sRoster = 0x87
	sDesc   = 0x88
	sLost   = 0x89
	sStatus = 0x8A

	joinOK       = 0
	joinNotFound = 1
	joinFull     = 2
	joinLocked   = 3
	joinVersion  = 4

	flagJoinDisabled       = 0x20 // DPSESSION_JOINDISABLED
	flagNewPlayersDisabled = 0x01

	maxSessions = 256
	maxName     = 63
)

type client struct {
	conn    net.Conn
	out     chan []byte
	app     [16]byte
	fp      string
	room    string
	monitor bool
	quiet   bool
	hello   bool
	sess    *session
	dpid    uint32
	name    string
	addr    string
	closed  bool
}

type session struct {
	id      [16]byte
	app     [16]byte
	fp      string
	room    string
	name    string
	flags   uint32
	max     int
	members []*client
}

var (
	mu       sync.Mutex
	sessions = map[[16]byte]*session{}
	clients  = map[*client]bool{}
	nextID   uint32
	verbose  bool
)

func frame(t byte, payload []byte) []byte {
	b := make([]byte, 3+len(payload))
	binary.LittleEndian.PutUint16(b, uint16(1+len(payload)))
	b[2] = t
	copy(b[3:], payload)
	return b
}

// send queues a frame; a client that cannot keep up is dropped.
func (c *client) send(t byte, payload []byte) {
	if c.closed {
		return
	}
	select {
	case c.out <- frame(t, payload):
	default:
		log.Printf("%s: outbound queue full, dropping client", c.addr)
		c.closed = true
		c.conn.Close()
	}
}

func putName(b []byte, s string) []byte {
	if len(s) > maxName {
		s = s[:maxName]
	}
	b = append(b, byte(len(s)))
	return append(b, s...)
}

func (s *session) players() int {
	n := 0
	for _, m := range s.members {
		if m.dpid != 0 {
			n++
		}
	}
	return n
}

func (s *session) rosterPayload() []byte {
	b := []byte{0}
	n := 0
	for _, m := range s.members {
		if m.dpid == 0 {
			continue
		}
		var id [4]byte
		binary.LittleEndian.PutUint32(id[:], m.dpid)
		b = append(b, id[:]...)
		b = putName(b, m.name)
		n++
	}
	b[0] = byte(n)
	return b
}

func (s *session) broadcastRoster() {
	p := s.rosterPayload()
	for _, m := range s.members {
		m.send(sRoster, p)
	}
}

func allocID() uint32 {
	for {
		var r [4]byte
		rand.Read(r[:])
		id := binary.LittleEndian.Uint32(r[:])&0x7fffffff | 0x00010000
		if id > 1 {
			return id
		}
	}
}

// leave removes c from its session. Caller holds mu.
func leave(c *client) {
	s := c.sess
	if s == nil {
		return
	}
	for i, m := range s.members {
		if m == c {
			s.members = append(s.members[:i], s.members[i+1:]...)
			break
		}
	}
	had := c.dpid != 0
	c.sess, c.dpid, c.name = nil, 0, ""
	if len(s.members) == 0 {
		delete(sessions, s.id)
		log.Printf("session %q closed (empty)", s.name)
		return
	}
	if had {
		s.broadcastRoster()
	}
}

func handle(c *client, t byte, p []byte) bool {
	mu.Lock()
	defer mu.Unlock()
	if !c.hello && t != cHello {
		return false
	}
	switch t {
	case cHello:
		if len(p) < 21 {
			return false
		}
		ver := binary.LittleEndian.Uint32(p)
		copy(c.app[:], p[4:20])
		n := int(p[20])
		if len(p) < 21+n {
			return false
		}
		c.fp = string(p[21 : 21+n])
		// optional private room code; players only see games in their own room
		if rest := p[21+n:]; len(rest) >= 1 && len(rest) >= 1+int(rest[0]) {
			c.room = string(rest[1 : 1+int(rest[0])])
		}
		c.hello = true
		// the lobby app polls every few seconds; keep it out of the log
		c.quiet = c.fp == "lobby-app"
		var r [4]byte
		binary.LittleEndian.PutUint32(r[:], protoVersion)
		c.send(sHello, r[:])
		if !c.quiet {
			log.Printf("%s: connected, game files %s, room %q", c.addr, c.fp, c.room)
		}
		if ver != protoVersion {
			return false
		}
	case cList:
		b := []byte{0}
		n, hidden := 0, 0
		for _, s := range sessions {
			if s.app != c.app || s.room != c.room {
				continue
			}
			if s.flags&(flagJoinDisabled|flagNewPlayersDisabled) != 0 || s.players() >= s.max {
				continue
			}
			if s.fp != c.fp {
				hidden++
				continue
			}
			if n >= 8 {
				break
			}
			b = append(b, s.id[:]...)
			var f [4]byte
			binary.LittleEndian.PutUint32(f[:], s.flags)
			b = append(b, f[:]...)
			b = append(b, byte(s.players()), byte(s.max))
			b = putName(b, s.name)
			n++
		}
		b[0] = byte(n)
		if hidden > 255 {
			hidden = 255
		}
		b = append(b, byte(hidden))
		c.send(sList, b)
	case cCreate:
		if len(p) < 6 || len(p) < 6+int(p[5]) {
			return false
		}
		leave(c)
		resp := make([]byte, 17)
		if len(sessions) >= maxSessions {
			resp[0] = 1
			c.send(sCreate, resp)
			return true
		}
		s := &session{app: c.app, fp: c.fp, room: c.room}
		s.flags = binary.LittleEndian.Uint32(p)
		s.max = int(p[4])
		if s.max < 1 || s.max > 8 {
			s.max = 8
		}
		s.name = string(p[6 : 6+int(p[5])])
		rand.Read(s.id[:])
		s.members = []*client{c}
		sessions[s.id] = s
		c.sess = s
		copy(resp[1:], s.id[:])
		c.send(sCreate, resp)
		log.Printf("%s: hosted %q (%s)", c.addr, s.name, hex.EncodeToString(s.id[:4]))
	case cJoin:
		if len(p) < 16 {
			return false
		}
		leave(c)
		var id [16]byte
		copy(id[:], p)
		s := sessions[id]
		st := byte(joinOK)
		switch {
		case s == nil || s.app != c.app || s.room != c.room:
			st = joinNotFound
		case s.fp != c.fp:
			st = joinVersion
		case s.flags&(flagJoinDisabled|flagNewPlayersDisabled) != 0:
			st = joinLocked
		case len(s.members) >= s.max:
			st = joinFull
		}
		b := []byte{st, 0, 0, 0, 0, 0}
		if st == joinOK {
			s.members = append(s.members, c)
			c.sess = s
			binary.LittleEndian.PutUint32(b[1:], s.flags)
			b[5] = byte(s.max)
			b = putName(b, s.name)
			c.send(sJoin, b)
			c.send(sRoster, s.rosterPayload())
			log.Printf("%s: joined %q", c.addr, s.name)
		} else {
			b = append(b, 0)
			c.send(sJoin, b)
			log.Printf("%s: join refused (%d)", c.addr, st)
		}
	case cCreatePlayer:
		if len(p) < 1 || len(p) < 1+int(p[0]) {
			return false
		}
		resp := make([]byte, 5)
		if c.sess == nil {
			resp[0] = 1
			c.send(sPlayer, resp)
			return true
		}
		if c.dpid == 0 {
			c.dpid = allocID()
		}
		c.name = string(p[1 : 1+int(p[0])])
		binary.LittleEndian.PutUint32(resp[1:], c.dpid)
		c.send(sPlayer, resp)
		c.sess.broadcastRoster()
		log.Printf("%s: player %q id=%08x in %q", c.addr, c.name, c.dpid, c.sess.name)
	case cSend:
		if len(p) < 4 || c.sess == nil || c.dpid == 0 {
			return true // not fatal: stale send after a leave
		}
		to := binary.LittleEndian.Uint32(p)
		b := make([]byte, 8+len(p)-4)
		binary.LittleEndian.PutUint32(b, c.dpid)
		binary.LittleEndian.PutUint32(b[4:], to)
		copy(b[8:], p[4:])
		for _, m := range c.sess.members {
			if m == c || m.dpid == 0 {
				continue
			}
			if to == 0 || to == m.dpid {
				m.send(sData, b)
			}
		}
		if verbose {
			log.Printf("%s: data %08x -> %08x (%d bytes)", c.addr, c.dpid, to, len(p)-4)
		}
	case cSetFlags:
		if len(p) < 4 || c.sess == nil {
			return true
		}
		c.sess.flags = binary.LittleEndian.Uint32(p)
		for _, m := range c.sess.members {
			if m != c {
				m.send(sDesc, p[:4])
			}
		}
		log.Printf("%s: session %q flags=%#x", c.addr, c.sess.name, c.sess.flags)
	case cLeave:
		leave(c)
	case cDestroyPlayer:
		if c.sess != nil && c.dpid != 0 {
			c.dpid, c.name = 0, ""
			c.sess.broadcastRoster()
		}
	case cStatus:
		// read-only overview for the lobby app: every game in this room, joinable or not
		c.monitor = true
		online := 0
		for o := range clients {
			if o.hello && !o.monitor && o.app == c.app && o.room == c.room {
				online++
			}
		}
		b := []byte{byte(online), byte(online >> 8), 0}
		n := 0
		for _, s := range sessions {
			if s.app != c.app || s.room != c.room || n >= 64 {
				continue
			}
			var f [4]byte
			binary.LittleEndian.PutUint32(f[:], s.flags)
			b = append(b, f[:]...)
			b = append(b, byte(s.players()), byte(s.max))
			b = putName(b, s.name)
			b = putName(b, s.fp)
			b = append(b, byte(s.players()))
			for _, m := range s.members {
				if m.dpid != 0 {
					b = putName(b, m.name)
				}
			}
			n++
		}
		b[2] = byte(n)
		c.send(sStatus, b)
	default:
		return false
	}
	return true
}

func serve(conn net.Conn) {
	c := &client{conn: conn, out: make(chan []byte, 4096), addr: conn.RemoteAddr().String()}
	if tc, ok := conn.(*net.TCPConn); ok {
		tc.SetNoDelay(true)
		tc.SetKeepAlive(true)
		tc.SetKeepAlivePeriod(20 * time.Second)
	}
	mu.Lock()
	clients[c] = true
	mu.Unlock()
	done := make(chan struct{})
	go func() {
		for {
			select {
			case b := <-c.out:
				conn.SetWriteDeadline(time.Now().Add(10 * time.Second))
				if _, err := conn.Write(b); err != nil {
					conn.Close()
					return
				}
			case <-done:
				return
			}
		}
	}()
	r := bufio.NewReaderSize(conn, 1<<16)
	hdr := make([]byte, 2)
	for {
		if _, err := io.ReadFull(r, hdr); err != nil {
			break
		}
		n := int(binary.LittleEndian.Uint16(hdr))
		if n < 1 {
			break
		}
		body := make([]byte, n)
		if _, err := io.ReadFull(r, body); err != nil {
			break
		}
		if !handle(c, body[0], body[1:]) {
			log.Printf("%s: bad frame type %#x, closing", c.addr, body[0])
			break
		}
	}
	mu.Lock()
	c.closed = true
	leave(c)
	delete(clients, c)
	mu.Unlock()
	close(done)
	conn.Close()
	if c.hello && !c.quiet {
		log.Printf("%s: disconnected", c.addr)
	}
}

func main() {
	addr := flag.String("listen", "127.0.0.1:27900", "address to listen on")
	flag.BoolVar(&verbose, "v", false, "log every relayed packet")
	flag.Parse()
	log.SetFlags(log.Ltime)
	ln, err := net.Listen("tcp", *addr)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot listen:", err)
		os.Exit(1)
	}
	log.Printf("mfnet relay listening on %s (protocol %d)", *addr, protoVersion)
	for {
		conn, err := ln.Accept()
		if err != nil {
			log.Print(err)
			continue
		}
		go serve(conn)
	}
}
