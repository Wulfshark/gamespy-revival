// GameSpy Revival - connection to the lobby server (accounts, friends, presence).
// Free software under the GNU GPL, version 3 or (at your option) any later version. No warranty.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Revival
{
    class Friend
    {
        public string Name = "", Status = "", Game = "", LastSeen = "", Presence = "offline", Custom = "";
        public bool Online, Owner;
        public int Kind; // 0 friend, 1 incoming request, 2 outgoing request
    }

    enum LobbyState { SignedOut, Connecting, SignedIn }

    class VoiceMember { public string User = ""; public bool Mute, Deaf; }

    // One message in a text channel ("#general") or a private conversation ("@Buddy").
    class ChatMsg
    {
        public long Id;
        public string User = "", Text = "", Kind = "", Game = "", Title = "", Address = "", Detail = "";
        public DateTime Time = DateTime.Now;
        public bool IsInvite { get { return Kind == "invite"; } }
        public long ReplyId;
        public string ReplyUser = "", ReplyText = "";
        public bool Edited;
        public Dictionary<string, List<string>> Reactions = new Dictionary<string, List<string>>();
    }
    class VoiceRoom { public string Name = ""; public List<VoiceMember> Members = new List<VoiceMember>(); }

    // Talks to the lobby server on a background thread; raises events on the UI thread.
    class LobbyClient
    {
        readonly Control ui;
        readonly object gate = new object();
        Thread thread;
        TcpClient tcp;
        Stream stream;
        volatile bool stop;
        string server = "", user = "", pass = "", firstOp = "login";
        bool resuming;   // signing back in with a saved token: keep retrying if the server isn't up yet
        bool remember;
        string status = "Online", game = "";

        public LobbyState State { get; private set; }
        public string User { get; private set; }
        public string Server { get { return server; } }
        public readonly List<Friend> Friends = new List<Friend>();

        public event Action Changed;            // state or friends list changed
        public event Action<string> Info;       // something to show the user
        public event Action<string> Failed;     // sign-in failed (dialog shows this)
        public event Action<string, ChatMsg> Message;            // conversation key ("#general" / "@Buddy"), message
        public event Action<string, List<ChatMsg>> History;      // conversation key, saved messages
        public event Action<string, string> Typing;              // conversation key, who
        public event Action<string, string> Direct;              // a private message arrived: from, text

        public string Group = "";
        public List<string> Channels = new List<string>();
        public List<Friend> Members = new List<Friend>();
        public event Action VoiceChanged;                        // voice channel membership changed

        public VoiceEngine Voice;                                // set by the window
        public List<VoiceRoom> Rooms = new List<VoiceRoom>();
        public string VoiceRoomName = "";                        // channel we are in, "" if none
        public int RelayPort;                                    // Metal Fatigue relay on the lobby host, 0 if none
        int voiceSeq;

        public void JoinVoice(string room)
        {
            VoiceRoomName = room;
            Send(new Dictionary<string, object> { { "op", "voice_join" }, { "room", room } });
            SendVoiceState();
        }

        public void LeaveVoice()
        {
            VoiceRoomName = "";
            Send(new Dictionary<string, object> { { "op", "voice_leave" } });
        }

        public void SendVoiceState()
        {
            bool mu = Voice != null && Voice.Muted, de = Voice != null && Voice.Deafened;
            Send(new Dictionary<string, object> { { "op", "voice_state" }, { "mute", mu }, { "deaf", de } });
        }

        public void SendVoiceFrame(byte[] frame)
        {
            if (State != LobbyState.SignedIn || VoiceRoomName.Length == 0) return;
            Send(new Dictionary<string, object> { { "op", "v" }, { "d", Convert.ToBase64String(frame) }, { "q", ++voiceSeq } });
        }

        public LobbyClient(Control owner) { ui = owner; User = ""; }

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        // ---- called from the UI
        public void SignIn(string srv, string name, string password, bool keep, bool create)
        {
            Stop();
            server = srv.Trim(); user = name.Trim(); pass = password; remember = keep;
            firstOp = create ? "register" : "login";
            resuming = false;
            Start();
        }

        static string TokenKey(string srv) { return "lobby.token." + srv.Trim().ToLowerInvariant(); }

        // every lobby (group) this PC has signed in to
        public static List<string> SavedGroups()
        {
            List<string> l = new List<string>();
            foreach (string g in Config.Get("groups", "").Split('|')) if (g.Trim().Length > 0 && !l.Contains(g.Trim())) l.Add(g.Trim());
            return l;
        }

        // A secret made once per install. Used as the "password" when you leave the password empty,
        // so on your own PC you never have to make up or type one (like Mumble's certificates).
        public static string DeviceSecret
        {
            get
            {
                string s = Config.Get("identity.secret", "");
                if (s.Length < 32)
                {
                    byte[] b = new byte[24];
                    using (RandomNumberGenerator r = RandomNumberGenerator.Create()) r.GetBytes(b);
                    s = "dev-" + BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
                    Config.Set("identity.secret", s);
                }
                return s;
            }
        }

        // ---- recently used accounts, for the "Choose an account" list (like Google's)
        static string AcctTokenKey(string srv, string name) { return "acct.token." + srv.Trim().ToLowerInvariant() + "|" + name.Trim().ToLowerInvariant(); }

        public static List<KeyValuePair<string, string>> RecentAccounts()   // server, user; newest first
        {
            List<KeyValuePair<string, string>> l = new List<KeyValuePair<string, string>>();
            foreach (string e in Config.Get("accounts.recent", "").Split('|'))
            {
                int at = e.LastIndexOf('@');
                if (at > 0 && at < e.Length - 1) l.Add(new KeyValuePair<string, string>(e.Substring(at + 1), e.Substring(0, at)));
            }
            return l;
        }

        static void SaveRecent(List<KeyValuePair<string, string>> l)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, string> kv in l) parts.Add(kv.Value + "@" + kv.Key);
            Config.Set("accounts.recent", string.Join("|", parts.ToArray()));
        }

        static void RememberAccount(string srv, string name, string tok)
        {
            List<KeyValuePair<string, string>> l = RecentAccounts();
            l.RemoveAll(kv => string.Equals(kv.Key, srv, StringComparison.OrdinalIgnoreCase) && string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase));
            l.Insert(0, new KeyValuePair<string, string>(srv, name));
            if (l.Count > 8) l.RemoveRange(8, l.Count - 8);
            SaveRecent(l);
            if (tok.Length > 0) Config.Set(AcctTokenKey(srv, name), tok);
        }

        public static void ForgetAccount(string srv, string name)
        {
            List<KeyValuePair<string, string>> l = RecentAccounts();
            l.RemoveAll(kv => string.Equals(kv.Key, srv, StringComparison.OrdinalIgnoreCase) && string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase));
            SaveRecent(l);
            Config.Set(AcctTokenKey(srv, name), "");
        }

        public static bool HasSavedSignIn(string srv, string name) { return Config.Get(AcctTokenKey(srv, name), "").Length > 0; }

        // sign in as a remembered account without typing the password; false if it needs one
        public bool UseAccount(string srv, string name)
        {
            string tok = Config.Get(AcctTokenKey(srv, name), "");
            if (tok.Length == 0) return false;
            Config.Set(TokenKey(srv), tok);
            Config.Set("lobby.user." + srv.Trim().ToLowerInvariant(), name);
            return Resume(srv);
        }

        public static string GroupName(string srv) { return Config.Get("group.name." + srv.ToLowerInvariant(), srv); }

        public static void ForgetGroup(string srv)
        {
            List<string> l = SavedGroups();
            l.Remove(srv);
            Config.Set("groups", string.Join("|", l.ToArray()));
            Config.Set(TokenKey(srv), "");
        }

        public bool Resume() { return Resume(Config.Get("lobby.server", "")); }

        public bool Resume(string srvWanted)
        {
            if (Config.Get(TokenKey(srvWanted), "").Length == 0 && Config.Get("lobby.token", "").Length > 0 && srvWanted == Config.Get("lobby.server", ""))
                Config.Set(TokenKey(srvWanted), Config.Get("lobby.token", ""));   // settings from 0.4
            string tok = Config.Get(TokenKey(srvWanted), "");
            string srv = srvWanted;
            if (Config.Get("lobby.user." + srv.ToLowerInvariant(), "").Length > 0) Config.Set("lobby.user", Config.Get("lobby.user." + srv.ToLowerInvariant(), ""));
            string name = Config.Get("lobby.user", "");
            if (tok.Length == 0 || srv.Length == 0 || name.Length == 0) return false;
            Stop();
            server = srv; user = name; pass = ""; remember = true; firstOp = "login";
            resuming = true;
            Start();
            return true;
        }

        public void SignOut()
        {
            string tok = Config.Get(TokenKey(server), "");
            if (State == LobbyState.SignedIn) Send(new Dictionary<string, object> { { "op", "logout" }, { "token", tok } });
            Config.Set(TokenKey(server), "");
            Config.Set("lobby.token", "");
            if ((user ?? "").Length > 0) Config.Set(AcctTokenKey(server, user), "");   // stays in the list, asks for the password next time
            Thread.Sleep(150);
            Stop();
            Friends.Clear();
            SetState(LobbyState.SignedOut);
        }

        string presence = Config.Get("status.presence", "online");
        string custom = Config.Get("status.custom", "");
        public string Custom { get { return custom; } }

        public void SetPresence(string pres, string customText)
        {
            Config.Set("status.presence", pres);
            Config.Set("status.custom", customText);
            custom = customText;
            presence = "";   // force a send
            SetStatus(status, game, pres);
        }
        public string Presence { get { return presence; } }
        public void SetPresenceOnly(string pres) { SetStatus(status, game, pres); }

        public void SetStatus(string text, string gameId) { SetStatus(text, gameId, presence); }

        public void SetStatus(string text, string gameId, string pres)
        {
            if (text == status && gameId == game && pres == presence) return;
            status = text; game = gameId; presence = pres;
            if (State == LobbyState.SignedIn)
                Send(new Dictionary<string, object> { { "op", "status" }, { "status", status }, { "game", game }, { "presence", presence }, { "custom", custom } });
        }

        public event Action<string, ChatMsg> MessageUpdated;     // key, new version
        public event Action<string, long> MessageDeleted;        // key, id

        public void SendReply(string channel, string text, long replyTo)
        {
            Send(new Dictionary<string, object> { { "op", "chat" }, { "channel", channel }, { "text", text }, { "reply", replyTo } });
        }

        public void EditMessage(string channel, long id, string text)
        {
            Send(new Dictionary<string, object> { { "op", "edit" }, { "channel", channel }, { "id", id }, { "text", text } });
        }

        public void DeleteMessage(string channel, long id)
        {
            Send(new Dictionary<string, object> { { "op", "delete" }, { "channel", channel }, { "id", id } });
        }

        public void React(string channel, long id, string emoji, bool on)
        {
            Send(new Dictionary<string, object> { { "op", "react" }, { "channel", channel }, { "id", id }, { "emoji", emoji }, { "on", on } });
        }

        public void SendChat(string channel, string text)
        {
            Send(new Dictionary<string, object> { { "op", "chat" }, { "channel", channel }, { "text", text } });
        }

        public void SendInvite(string channel, string game, string title, string address, string detail, string text)
        {
            Send(new Dictionary<string, object> { { "op", "invite" }, { "channel", channel }, { "game", game }, { "title", title },
                { "address", address }, { "detail", detail }, { "text", text } });
        }

        public void SendTyping(string key)   // "#general" or "@Buddy"
        {
            string ch = key.StartsWith("#") ? key.Substring(1) : key;
            Send(new Dictionary<string, object> { { "op", "typing" }, { "channel", ch } });
        }

        public void RequestDmHistory(string user)
        {
            Send(new Dictionary<string, object> { { "op", "dm_history" }, { "with", user } });
        }

        public void SendDm(string to, string text)
        {
            Send(new Dictionary<string, object> { { "op", "dm" }, { "to", to }, { "text", text } });
        }

        public void AddFriend(string name)
        {
            Send(new Dictionary<string, object> { { "op", "friend_add" }, { "user", name } });
        }

        public void RemoveFriend(string name)
        {
            Send(new Dictionary<string, object> { { "op", "friend_remove" }, { "user", name } });
        }

        // ---- plumbing
        void Start()
        {
            stop = false;
            SetState(LobbyState.Connecting);
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Start();
        }

        public void Stop()
        {
            stop = true;
            lock (gate)
            {
                try { if (tcp != null) tcp.Close(); } catch (Exception) { }
                tcp = null; stream = null;
            }
        }

        void Post(Action a)
        {
            try { if (ui.IsHandleCreated && !ui.IsDisposed) ui.BeginInvoke(a); } catch (Exception) { }
        }

        void SetState(LobbyState s)
        {
            State = s;
            Post(delegate { if (Changed != null) Changed(); });
        }

        void Send(Dictionary<string, object> m)
        {
            byte[] b = Encoding.UTF8.GetBytes(Json.Serialize(m) + "\n");
            lock (gate)
            {
                try { if (stream != null) { stream.Write(b, 0, b.Length); stream.Flush(); } }
                catch (Exception) { try { tcp.Close(); } catch (Exception) { } }
            }
        }

        static bool SplitAddress(string a, out string host, out int port)
        {
            host = a; port = 27901;
            int i = a.LastIndexOf(':');
            if (i > 0 && int.TryParse(a.Substring(i + 1), out port)) host = a.Substring(0, i);
            else port = 27901;
            return host.Length > 0 && port > 0 && port < 65536;
        }

        // Trust on first use: remember the server's certificate, refuse if it ever changes.
        string pinError;
        bool CheckCert(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            if (cert == null) { pinError = "The server sent no certificate."; return false; }
            string fp;
            using (SHA256 sha = SHA256.Create()) fp = BitConverter.ToString(sha.ComputeHash(cert.GetRawCertData())).Replace("-", "");
            string key = "lobby.pin." + server.ToLowerInvariant();
            string pinned = Config.Get(key, "");
            if (pinned.Length == 0) { Config.Set(key, fp); return true; }
            if (pinned == fp) return true;
            pinError = "This server's identity changed since you last connected. If the host reinstalled it, "
                + "remove the line starting with " + key + " from settings.txt. Otherwise someone may be impersonating it.";
            return false;
        }

        void Run()
        {
            int delay = 2000;
            bool everSignedIn = false;
            while (!stop)
            {
                string host; int port;
                if (!SplitAddress(server, out host, out port))
                {
                    Fail("That lobby address does not look right. Use something like 203.0.113.25:27901.");
                    return;
                }
                string fatal = null;
                try
                {
                    pinError = null;
                    TcpClient t = new TcpClient();
                    IAsyncResult ar = t.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(6000)) { t.Close(); throw new IOException("No answer from " + server + "."); }
                    t.EndConnect(ar);
                    t.NoDelay = true;
                    SslStream ssl = new SslStream(t.GetStream(), false, CheckCert);
                    ssl.ReadTimeout = 75000;
                    try { ssl.AuthenticateAsClient("gamespy-revival", null, SslProtocols.Tls12, false); }
                    catch (Exception) { t.Close(); if (pinError != null) { fatal = pinError; throw new IOException(pinError); } throw; }
                    lock (gate) { tcp = t; stream = ssl; }

                    Dictionary<string, object> hello = new Dictionary<string, object>();
                    hello["op"] = firstOp;
                    hello["user"] = user;
                    string tok = Config.Get(TokenKey(server), "");
                    if (pass.Length > 0) hello["pass"] = pass;
                    else hello["token"] = tok;
                    if (remember) hello["remember"] = "1";
                    Send(hello);

                    System.Threading.Timer ping = new System.Threading.Timer(delegate { Send(new Dictionary<string, object> { { "op", "ping" } }); }, null, 30000, 30000);
                    try
                    {
                        StreamReader r = new StreamReader(ssl, Encoding.UTF8);
                        string line;
                        while (!stop && (line = r.ReadLine()) != null)
                        {
                            Dictionary<string, object> m = Json.DeserializeObject(line) as Dictionary<string, object>;
                            if (m == null) continue;
                            string res = Handle(m);
                            if (res == "welcome") { everSignedIn = true; delay = 2000; }
                            else if (res != null) { fatal = res; break; }
                        }
                    }
                    finally { ping.Dispose(); }
                }
                catch (Exception ex)
                {
                    bool retryable = resuming && pinError == null && (ex is SocketException || ex is IOException);
                    if (fatal == null && !everSignedIn && !retryable) fatal = ex is SocketException
                        ? "Could not reach the lobby at " + server + ". Check the address, and that the host's lobby server is running."
                        : ex.Message;
                }
                lock (gate)
                {
                    try { if (tcp != null) tcp.Close(); } catch (Exception) { }
                    tcp = null; stream = null;
                }
                if (stop) return;
                if (fatal != null) { Fail(fatal); return; }
                // lost the connection after signing in: keep retrying quietly
                SetState(LobbyState.Connecting);
                Post(delegate { Rooms = new List<VoiceRoom>(); Members = new List<Friend>(); if (VoiceChanged != null) VoiceChanged(); });
                Post(delegate { if (Info != null) Info("Lost the lobby connection. Reconnecting..."); });
                for (int i = 0; i < delay / 250 && !stop; i++) Thread.Sleep(250);
                delay = Math.Min(delay * 2, 30000);
            }
        }

        void Fail(string text)
        {
            Friends.Clear();
            Post(delegate { Rooms = new List<VoiceRoom>(); Members = new List<Friend>(); if (VoiceChanged != null) VoiceChanged(); });
            if (text.IndexOf("expired") >= 0) Config.Set(TokenKey(server), "");
            SetState(LobbyState.SignedOut);
            Post(delegate { if (Failed != null) Failed(text); });
        }

        static string S(Dictionary<string, object> m, string k)
        {
            object v;
            return m.TryGetValue(k, out v) && v != null ? v.ToString() : "";
        }

        static Friend ToFriend(Dictionary<string, object> p)
        {
            Friend f = new Friend();
            f.Name = S(p, "user");
            f.Online = S(p, "online") == "True";
            f.Status = S(p, "status");
            f.Game = S(p, "game");
            f.LastSeen = S(p, "last_seen");
            f.Presence = S(p, "presence");
            if (f.Presence.Length == 0) f.Presence = f.Online ? "online" : "offline";
            f.Owner = S(p, "owner") == "True";
            f.Custom = S(p, "custom");
            return f;
        }

        // Returns "welcome" after signing in, an error text if the session must end, otherwise null.
        string Handle(Dictionary<string, object> m)
        {
            string op = S(m, "op");
            if (op == "welcome")
            {
                string name = S(m, "user");
                string tok = S(m, "token");
                Config.Set("lobby.server", server);
                List<string> gl = SavedGroups();
                if (!gl.Contains(server)) { gl.Add(server); Config.Set("groups", string.Join("|", gl.ToArray())); }
                Config.Set("lobby.user", name);
                Config.Set("lobby.user." + server.ToLowerInvariant(), name);
                if (tok.Length > 0) Config.Set(TokenKey(server), tok);
                RememberAccount(server, name, remember ? (tok.Length > 0 ? tok : Config.Get(TokenKey(server), "")) : "");
                string grp = S(m, "group");
                if (grp.Length > 0) Config.Set("group.name." + server.ToLowerInvariant(), grp);
                List<string> chans = new List<string>();
                foreach (object o in AsList(m, "channels")) chans.Add(o.ToString());
                if (chans.Count == 0) chans.Add("general");
                Post(delegate { Group = grp.Length > 0 ? grp : server; Channels = chans; });
                user = name; pass = ""; firstOp = "login";
                Post(delegate { User = name; });
                User = name;
                SetState(LobbyState.SignedIn);
                Send(new Dictionary<string, object> { { "op", "status" }, { "status", status }, { "game", game }, { "presence", presence }, { "custom", custom } });
                int rp; RelayPort = int.TryParse(S(m, "relay_port"), out rp) ? rp : 0;
                if (VoiceRoomName.Length > 0)
                {
                    Send(new Dictionary<string, object> { { "op", "voice_join" }, { "room", VoiceRoomName } });
                    SendVoiceState();
                }
                return "welcome";
            }
            if (op == "error")
            {
                string re = S(m, "re");
                if (re == "login" || re == "register") return S(m, "msg");
                Post(delegate { if (Info != null) Info(S(m, "msg")); });
                return null;
            }
            if (op == "kicked") return S(m, "msg");
            if (op == "info")
            {
                string t = S(m, "msg");
                Post(delegate { if (Info != null) Info(t); });
                return null;
            }
            if (op == "friends")
            {
                List<Friend> list = new List<Friend>();
                foreach (object o in AsList(m, "incoming")) { Friend f = new Friend(); f.Name = o.ToString(); f.Kind = 1; list.Add(f); }
                foreach (object o in AsList(m, "friends"))
                {
                    Dictionary<string, object> p = o as Dictionary<string, object>;
                    if (p != null) list.Add(ToFriend(p));
                }
                foreach (object o in AsList(m, "outgoing")) { Friend f = new Friend(); f.Name = o.ToString(); f.Kind = 2; list.Add(f); }
                Post(delegate
                {
                    Friends.Clear();
                    Friends.AddRange(list);
                    if (Changed != null) Changed();
                });
                return null;
            }
            if (op == "v")
            {
                VoiceEngine ve = Voice;
                if (ve == null) return null;
                try { ve.Feed(S(m, "u"), Convert.FromBase64String(S(m, "d"))); } catch (FormatException) { }
                return null;
            }
            if (op == "voice_rooms")
            {
                List<VoiceRoom> rooms = new List<VoiceRoom>();
                foreach (object o in AsList(m, "rooms"))
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    VoiceRoom vr = new VoiceRoom();
                    vr.Name = S(d, "name");
                    foreach (object mo in AsList(d, "members"))
                    {
                        Dictionary<string, object> md = mo as Dictionary<string, object>;
                        if (md == null) continue;
                        VoiceMember vm = new VoiceMember();
                        vm.User = S(md, "user"); vm.Mute = S(md, "mute") == "True"; vm.Deaf = S(md, "deaf") == "True";
                        vr.Members.Add(vm);
                    }
                    rooms.Add(vr);
                }
                Post(delegate { Rooms = rooms; if (VoiceChanged != null) VoiceChanged(); });
                return null;
            }
            if (op == "chat")
            {
                string key = "#" + S(m, "channel");
                ChatMsg cm = ToMsg(m, "user");
                Post(delegate { if (Message != null) Message(key, cm); });
                return null;
            }
            if (op == "dm")
            {
                ChatMsg cm = ToMsg(m, "from");
                string key = "@" + cm.User;
                Post(delegate
                {
                    if (Message != null) Message(key, cm);
                    if (Direct != null) Direct(cm.User, cm.Text);
                });
                return null;
            }
            if (op == "history" || op == "dm_history")
            {
                string key = op == "history" ? "#" + S(m, "channel") : "@" + S(m, "with");
                List<ChatMsg> h = new List<ChatMsg>();
                foreach (object o in AsList(m, "msgs"))
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d != null) h.Add(ToMsg(d, op == "history" ? "user" : "from"));
                }
                Post(delegate { if (History != null) History(key, h); });
                return null;
            }
            if (op == "msg_update")
            {
                string key = "#" + S(m, "channel");
                ChatMsg cm = ToMsg(m, "user");
                Post(delegate { if (MessageUpdated != null) MessageUpdated(key, cm); });
                return null;
            }
            if (op == "msg_delete")
            {
                string key = "#" + S(m, "channel");
                long id; long.TryParse(S(m, "id"), out id);
                Post(delegate { if (MessageDeleted != null) MessageDeleted(key, id); });
                return null;
            }
            if (op == "typing")
            {
                string ch = S(m, "channel"), who = S(m, "user");
                string key = ch.StartsWith("@") ? ch : "#" + ch;
                Post(delegate { if (Typing != null) Typing(key, who); });
                return null;
            }
            if (op == "members")
            {
                List<Friend> list = new List<Friend>();
                foreach (object o in AsList(m, "members"))
                {
                    Dictionary<string, object> p = o as Dictionary<string, object>;
                    if (p != null) list.Add(ToFriend(p));
                }
                Post(delegate { Members = list; if (Changed != null) Changed(); });
                return null;
            }
            if (op == "presence")
            {
                Friend upd = ToFriend(m);
                Post(delegate
                {
                    for (int i = 0; i < Friends.Count; i++)
                        if (Friends[i].Kind == 0 && string.Equals(Friends[i].Name, upd.Name, StringComparison.OrdinalIgnoreCase))
                            Friends[i] = upd;
                    bool found = false;
                    for (int i = 0; i < Members.Count; i++)
                        if (string.Equals(Members[i].Name, upd.Name, StringComparison.OrdinalIgnoreCase)) { Members[i] = upd; found = true; }
                    if (!found) Members.Add(upd);
                    if (Changed != null) Changed();
                });
            }
            return null;
        }

        static ChatMsg ToMsg(Dictionary<string, object> m, string userKey)
        {
            ChatMsg c = new ChatMsg();
            long id; long.TryParse(S(m, "id"), out id); c.Id = id;
            c.User = S(m, userKey); c.Text = S(m, "text"); c.Kind = S(m, "kind");
            c.Game = S(m, "game"); c.Title = S(m, "title"); c.Address = S(m, "address"); c.Detail = S(m, "detail");
            long rid; long.TryParse(S(m, "reply_id"), out rid); c.ReplyId = rid;
            c.ReplyUser = S(m, "reply_user"); c.ReplyText = S(m, "reply_text");
            c.Edited = S(m, "edited") == "True";
            object ro;
            Dictionary<string, object> rd = m.TryGetValue("reactions", out ro) ? ro as Dictionary<string, object> : null;
            if (rd != null)
                foreach (KeyValuePair<string, object> kv in rd)
                {
                    List<string> who = new List<string>();
                    foreach (object w in AsList(rd, kv.Key)) who.Add(w.ToString());
                    if (who.Count > 0) c.Reactions[kv.Key] = who;
                }
            long ts = ParseTs(S(m, "ts"));
            if (ts > 0) c.Time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts).ToLocalTime();
            return c;
        }

        public static long ParseTs(string s)
        {
            long v;
            return long.TryParse(s, out v) ? v : 0;
        }

        static IEnumerable<object> AsList(Dictionary<string, object> m, string k)
        {
            object v;
            if (m.TryGetValue(k, out v) && v is object[]) return (object[])v;
            if (v is System.Collections.ArrayList) return ((System.Collections.ArrayList)v).ToArray();
            return new object[0];
        }
    }

    // Runs the lobby server program (bundled inside this app) on this PC.
    // The group server runs as its own background program, separate from the chat window:
    // closing the app doesn't take the group down, and it can start with Windows.
    static class LobbyHost
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "GameSpy Revival group server";

        public static string Folder
        {
            get
            {
                string d = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSpyRevival"), "lobby");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static string LobbyExe { get { return Path.Combine(Folder, "gamespy-lobby.exe"); } }
        static string RelayExe { get { return Path.Combine(Folder, "mfnet-relay.exe"); } }

        static List<Process> Find(string name, string path)
        {
            List<Process> l = new List<Process>();
            try
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    string f = "";
                    try { f = p.MainModule.FileName; } catch (Exception) { }
                    if (string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) l.Add(p); else p.Dispose();
                }
            }
            catch (Exception) { }
            return l;
        }

        static bool Alive(string name, string path)
        {
            List<Process> l = Find(name, path);
            foreach (Process p in l) p.Dispose();
            return l.Count > 0;
        }

        static DateTime checkedAt = DateTime.MinValue;
        static bool lastRunning;
        public static bool Running   // checked at most every 2 seconds (it looks through the process list)
        {
            get
            {
                if ((DateTime.Now - checkedAt).TotalSeconds > 2) { lastRunning = Alive("gamespy-lobby", LobbyExe); checkedAt = DateTime.Now; }
                return lastRunning;
            }
        }
        static void Recheck() { checkedAt = DateTime.MinValue; }
        public static bool RelayRunning { get { return Alive("mfnet-relay", RelayExe); } }

        // ends a stray copy of a helper program (used by the Metal Fatigue page for its own server)
        public static void KillOrphans(string processName, string exePath)
        {
            try
            {
                int me = Process.GetCurrentProcess().Id;
                foreach (Process a in Process.GetProcessesByName("GameSpyRevival"))
                    using (a) if (a.Id != me) return;
                foreach (Process p in Find(processName, exePath))
                    using (p) { try { p.Kill(); p.WaitForExit(2000); } catch (Exception) { } }
            }
            catch (Exception) { }
        }

        static void Extract(string resource, string path)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (s == null) throw new IOException(resource + " is missing from this build.");
                byte[] b = new byte[s.Length];
                int n = 0;
                while (n < b.Length) { int r = s.Read(b, n, b.Length - n); if (r <= 0) break; n += r; }
                bool same = File.Exists(path) && new FileInfo(path).Length == b.Length;
                if (!same) File.WriteAllBytes(path, b);
            }
        }

        static string Args(int port, int relayPort, string name, string channels)
        {
            return "-port " + port + " -relay-port " + relayPort + " -data \"" + Folder + "\""
                + " -log \"" + Path.Combine(Folder, "lobby.log") + "\""
                + (relayPort > 0 ? " -relay-exe \"" + RelayExe + "\"" : "")
                + " -name \"" + (name ?? "").Replace("\"", "'") + "\""
                + ((channels ?? "").Trim().Length > 0 ? " -channels \"" + channels.Replace("\"", "").Replace(" ", "") + "\"" : "");
        }

        // Starts the server, or keeps using it if it's already running in the background.
        public static string Start(int port, int relayPort, string name, string channels)
        {
            Recheck();
            if (Running) return null;
            try
            {
                try { Extract("gamespy-lobby.exe", LobbyExe); } catch (IOException) { if (!File.Exists(LobbyExe)) throw; }
                if (relayPort > 0) { try { Extract("mfnet-relay.exe", RelayExe); } catch (IOException) { if (!File.Exists(RelayExe)) throw; } }
                string args = Args(port, relayPort, name, channels);
                Config.Set("host.args", args);
                ProcessStartInfo si = new ProcessStartInfo(LobbyExe, args);
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.WorkingDirectory = Folder;
                using (Process p = Process.Start(si))
                {
                    Thread.Sleep(800);
                    if (p.HasExited) return "The group server couldn't start because port " + port + " is already in use by another program. "
                        + "Restart your PC, or end that program in Task Manager. Details: lobby.log in " + Folder + ".";
                }
                Recheck();
                SetStartWithWindows(StartWithWindows);
                return null;
            }
            catch (Exception ex) { return "Could not start the group server: " + ex.Message; }
        }

        public static void Stop()
        {
            foreach (Process p in Find("gamespy-lobby", LobbyExe)) using (p) { try { p.Kill(); p.WaitForExit(2000); } catch (Exception) { } }
            foreach (Process p in Find("mfnet-relay", RelayExe)) using (p) { try { p.Kill(); p.WaitForExit(2000); } catch (Exception) { } }
            Recheck();
            SetStartWithWindows(false);
        }

        // ---- start with Windows (only while hosting is on)
        public static bool StartWithWindows
        {
            get { return Config.Get("host.autostart", "1") == "1"; }
            set { Config.Set("host.autostart", value ? "1" : "0"); SetStartWithWindows(value && Running); }
        }

        static void SetStartWithWindows(bool on)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, "\"" + LobbyExe + "\" " + Config.Get("host.args", ""));
                    else if (k.GetValue(RunName) != null) k.DeleteValue(RunName, false);
                }
            }
            catch (Exception) { }
        }
    }

    // Sign in / create account window.
    class SignInDialog : Form
    {
        readonly LobbyClient lobby;
        TextBox txtServer, txtUser, txtPass;
        CheckBox chkKeep;
        Label lblMsg;
        Button btnIn, btnNew;
        bool waiting;

        public SignInDialog(LobbyClient lc) : this(lc, null) { }

        public SignInDialog(LobbyClient lc, string server)
        {
            lobby = lc;
            Text = "Sign in to GameSpy Revival";
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(380, 300);
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;

            Label h = new Label();
            h.Text = "Sign in";
            h.Font = new Font("Segoe UI Semibold", 15f);
            h.SetBounds(20, 14, 300, 30);
            Controls.Add(h);

            txtServer = Field("Invite link or code (an address works too)", 54, server ?? Config.Get("lobby.server", "127.0.0.1:27901"));
            txtUser = Field("Name", 104, Config.Get("lobby.user", ""));
            txtPass = Field("Password (optional, this PC is your key)", 154, "");
            txtPass.UseSystemPasswordChar = true;

            chkKeep = new CheckBox();
            chkKeep.Text = "Keep me signed in on this PC";
            chkKeep.Checked = true;
            chkKeep.AutoSize = true;
            chkKeep.Location = new Point(20, 204);
            Controls.Add(chkKeep);

            lblMsg = new Label();
            lblMsg.SetBounds(20, 228, 340, 34);
            lblMsg.Tag = "muted";
            lblMsg.Text = "New here? Type a name and press Create account. No password needed on this PC.";
            Controls.Add(lblMsg);

            btnIn = new Button();
            btnIn.Text = "Sign in";
            btnIn.Tag = "primary";
            btnIn.SetBounds(20, 262, 160, 30);
            btnIn.Click += delegate { Go(false); };
            Controls.Add(btnIn);
            btnNew = new Button();
            btnNew.Text = "Create account";
            btnNew.SetBounds(200, 262, 160, 30);
            btnNew.Click += delegate { Go(true); };
            Controls.Add(btnNew);
            AcceptButton = btnIn;

            foreach (Control c in Controls) Theme.Style(c);
            if (txtUser.Text.Length > 0) ActiveControl = txtPass;

            lobby.Changed += OnChanged;
            lobby.Failed += OnFailed;
            FormClosed += delegate { lobby.Changed -= OnChanged; lobby.Failed -= OnFailed; };

            filterServer = server;
            BuildChooser();
            Look.Modal(this, Theme.Panel);
            BackColor = Theme.Panel;
            Padding = new Padding(0);
            Paint += delegate(object s, PaintEventArgs e) { using (Pen p = new Pen(Look.Line)) e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1); };
        }

        // ---- "Choose an account", like Google's
        Panel chooser;
        readonly string filterServer;

        void BuildChooser()
        {
            List<KeyValuePair<string, string>> accts = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> kv in LobbyClient.RecentAccounts())
                if (string.IsNullOrEmpty(filterServer) || string.Equals(kv.Key, filterServer.Trim(), StringComparison.OrdinalIgnoreCase)) accts.Add(kv);
            if (chooser != null) { Controls.Remove(chooser); chooser.Dispose(); chooser = null; }
            if (accts.Count == 0) { ClientSize = new Size(380, 300); return; }

            chooser = new Panel();
            chooser.BackColor = Theme.Panel;
            int y = 14;
            Label h = new Label();
            h.Text = "Choose an account";
            h.Font = new Font("Segoe UI Semibold", 15f);
            h.ForeColor = Theme.Text;
            h.SetBounds(20, y, 340, 30);
            chooser.Controls.Add(h);
            y += 32;
            Label sub = new Label();
            sub.Text = "to continue to GameSpy Revival";
            sub.ForeColor = Theme.Muted;
            sub.SetBounds(20, y, 340, 20);
            chooser.Controls.Add(sub);
            y += 30;
            foreach (KeyValuePair<string, string> kv in accts)
            {
                string srv = kv.Key, name = kv.Value;
                bool saved = LobbyClient.HasSavedSignIn(srv, name);
                Panel row = new Panel();
                row.SetBounds(12, y, 356, 52);
                row.Cursor = Cursors.Hand;
                bool hot = false, hotX = false;
                Rectangle rx = new Rectangle(row.Width - 34, 14, 24, 24);
                row.Paint += delegate(object s, PaintEventArgs e)
                {
                    Graphics g = e.Graphics;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(hot ? Color.FromArgb(30, 40, 56) : Theme.Panel);
                    Look.Avatar(g, name, new Rectangle(8, 8, 36, 36), false);
                    using (Font fn = new Font("Segoe UI Semibold", 10f))
                        TextRenderer.DrawText(g, name, fn, new Point(54, 7), Color.White, TextFormatFlags.NoPrefix);
                    using (Font fs = new Font("Segoe UI", 8.5f))
                        TextRenderer.DrawText(g, LobbyClient.GroupName(srv) + (saved ? "" : "  \u00b7  signed out"), fs, new Rectangle(54, 27, row.Width - 100, 18), Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    if (hot) TextRenderer.DrawText(g, "\u2715", Font, rx, hotX ? Color.FromArgb(240, 150, 130) : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                };
                row.MouseEnter += delegate { hot = true; row.Invalidate(); };
                row.MouseLeave += delegate { hot = false; hotX = false; row.Invalidate(); };
                row.MouseMove += delegate(object s, MouseEventArgs e) { bool x = rx.Contains(e.Location); if (x != hotX) { hotX = x; row.Invalidate(); } };
                row.MouseUp += delegate(object s, MouseEventArgs e)
                {
                    if (e.Button != MouseButtons.Left) return;
                    if (rx.Contains(e.Location)) { LobbyClient.ForgetAccount(srv, name); BuildChooser(); return; }
                    Pick(srv, name);
                };
                new ToolTip().SetToolTip(row, srv);
                chooser.Controls.Add(row);
                y += 56;
            }
            Label other = new Label();
            other.Text = "\u2795  Use another account";
            other.ForeColor = Theme.Text;
            other.Cursor = Cursors.Hand;
            other.TextAlign = ContentAlignment.MiddleLeft;
            other.SetBounds(20, y + 4, 340, 34);
            other.Click += delegate { HideChooser(); };
            chooser.Controls.Add(other);
            y += 50;
            ClientSize = new Size(380, Math.Max(300, y));
            chooser.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
            Controls.Add(chooser);
            chooser.BringToFront();
            foreach (Control c in Controls) if (c is Label && c.Text == "\uE711") c.BringToFront();   // keep the X on top
            AcceptButton = null;
        }

        void HideChooser()
        {
            if (chooser != null) { Controls.Remove(chooser); chooser.Dispose(); chooser = null; }
            ClientSize = new Size(380, 300);
            AcceptButton = btnIn;
            ActiveControl = txtUser.Text.Length > 0 ? txtPass : txtUser;
        }

        void Pick(string srv, string name)
        {
            txtServer.Text = srv;
            txtUser.Text = name;
            txtPass.Text = "";
            if (lobby.UseAccount(srv, name))
            {
                waiting = true;
                btnIn.Enabled = btnNew.Enabled = false;
                lblMsg.Text = "Signing in as " + name + "...";
                HideChooser();
                return;
            }
            HideChooser();
            lblMsg.Text = "Enter the password for " + name + ".";
            ActiveControl = txtPass;
        }

        TextBox Field(string label, int y, string value)
        {
            Label l = new Label();
            l.Text = label;
            l.Tag = "muted";
            l.SetBounds(20, y, 300, 18);
            Controls.Add(l);
            TextBox t = new TextBox();
            t.SetBounds(20, y + 20, 340, 24);
            t.Text = value;
            Controls.Add(t);
            return t;
        }

        void Go(bool create)
        {
            if (txtUser.Text.Trim().Length == 0)
            {
                lblMsg.Text = "Enter a name.";
                return;
            }
            string addr = Invite.Resolve(txtServer.Text);
            if (addr.Length == 0) { lblMsg.Text = "That doesn't look like an invite link or code."; return; }
            txtServer.Text = addr;
            string pw = txtPass.Text.Length > 0 ? txtPass.Text : LobbyClient.DeviceSecret;
            waiting = true;
            btnIn.Enabled = btnNew.Enabled = false;
            lblMsg.Text = create ? "Creating your account..." : "Signing in...";
            lobby.SignIn(txtServer.Text, txtUser.Text, pw, chkKeep.Checked || txtPass.Text.Length == 0, create);
        }

        void OnChanged()
        {
            if (waiting && lobby.State == LobbyState.SignedIn) { DialogResult = DialogResult.OK; Close(); }
        }

        void OnFailed(string why)
        {
            if (!waiting) return;
            waiting = false;
            btnIn.Enabled = btnNew.Enabled = true;
            lblMsg.ForeColor = Color.FromArgb(240, 150, 130);
            lblMsg.Text = why;
        }
    }
}
