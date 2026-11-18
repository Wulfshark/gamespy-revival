// GameSpy Revival - a standalone launcher and server browser for old multiplayer games.
// Copyright (C) 2026 Warsh's revival project. A fan project, not affiliated with GameSpy or its owners.
// Free software under the GNU GPL, version 3 or (at your option) any later version. No warranty.
//
// Written for the C# 5 compiler that ships with Windows (.NET Framework 4.x csc.exe).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Revival
{
    static class Program
    {
        public const string Version = "0.6.1-dev";   // bump on every build; see CHANGELOG.md

        public static void CrashLog(Exception ex)
        {
            if (ex == null) return;
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), DateTime.Now + "  v" + Version + "\r\n" + ex + "\r\n\r\n"); } catch (Exception) { }
        }

        static Mutex single;   // one window per profile
        public static bool StartedByWindows;   // opened with --startup (Settings > System)

        [STAThread]   // Windows file dialogs (Save, Open, Browse) need this
        static void Main(string[] args)
        {
            // --profile 2 keeps a separate set of settings (for testing two accounts on one PC)
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--profile" && System.Text.RegularExpressions.Regex.IsMatch(args[i + 1], "^[A-Za-z0-9]{1,12}$"))
                    Config.Profile = args[i + 1];
            // gamespy://join/... links: hand them to the window that's already open, or open with them
            foreach (string a0 in args) if (a0 == "--startup") StartedByWindows = true;
            string join = "";
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--join") join = args[i + 1];
            bool first;
            single = new Mutex(true, "GameSpyRevival-" + (Config.Profile.Length > 0 ? Config.Profile : "main"), out first);
            if (join.Length > 0) Invite.SetPending(join);
            if (!first)
            {
                if (join.Length == 0) Invite.SetPending("show");
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                CrashLog(e.Exception);
                MessageBox.Show(e.Exception.Message, "GameSpy Revival", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { CrashLog(e.ExceptionObject as Exception); };
            try { Application.Run(new Shell()); }
            catch (Exception ex)
            {
                CrashLog(ex);
                MessageBox.Show("GameSpy Revival hit a problem and has to close:\n\n" + ex.Message + "\n\nDetails were saved to crash.log next to the app.", "GameSpy Revival", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // ---------------------------------------------------------------- saved settings

    static class Config
    {
        static readonly Dictionary<string, string> values = new Dictionary<string, string>();
        static readonly object sync = new object();
        static bool loaded;
        public static string Profile = "";

        static string FilePath
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSpyRevival");
                Directory.CreateDirectory(d);
                return Path.Combine(d, Profile.Length > 0 ? "settings-" + Profile + ".txt" : "settings.txt");
            }
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) values[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch (Exception) { }
        }

        public static string Get(string key, string fallback)
        {
            lock (sync)
            {
                Load();
                string v;
                return values.TryGetValue(key, out v) ? v : fallback;
            }
        }

        public static void Set(string key, string value)
        {
            lock (sync) SetLocked(key, value);
        }

        static void SetLocked(string key, string value)
        {
            Load();
            values[key] = value;
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in values) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception) { }
        }
    }

    // ---------------------------------------------------------------- games that use the Quake III network protocol

    class GameDef
    {
        public string Id, Title, Blurb, GetIt;
        public int Protocol;
        public string[] Masters, ExeNames, Folders, SteamFolders;
        public string Tagline;
        public Color ColorA, ColorB;
        public string Kind = "q3";          // "q3": Quake III protocol; "gs": classic GameSpy master + UDP query (Unreal Tournament)
        public string GameName = "", GameKey = "";
        public int QueryPort = 27960;       // default port when someone types an address without one
        public string JoinFormat = "+connect {0}";
    }

    class ServerInfo
    {
        public IPEndPoint Ep;
        public string Key = "";
        public string Name = "", Map = "", Mod = "";
        public int Players = -1, Max = -1, Ping = -1, Protocol = -1;
        public bool Locked, Responded;
        public int JoinPort;                // game port when it differs from the query port
        public long SentAt;
        public string Challenge = "";

        public ServerInfo Copy() { return (ServerInfo)MemberwiseClone(); }
    }

    class PlayerRow
    {
        public string Name = "";
        public int Score, Ping;
    }

    // Asks the master servers for the server list, then asks every server for its details.
    class Scanner
    {
        static readonly Encoding Latin = Encoding.GetEncoding(1252);
        static readonly Random rng = new Random();

        readonly object gate = new object();
        readonly Dictionary<string, ServerInfo> servers = new Dictionary<string, ServerInfo>();
        readonly Stopwatch clock = Stopwatch.StartNew();
        Socket sock;
        volatile bool stop;
        int generation;

        public volatile int Version;
        public volatile string Phase = "";
        public volatile bool Busy;
        public volatile int MastersAnswered;

        public static byte[] Packet(string text)
        {
            byte[] t = Latin.GetBytes(text);
            byte[] p = new byte[t.Length + 4];
            p[0] = p[1] = p[2] = p[3] = 255;
            Array.Copy(t, 0, p, 4, t.Length);
            return p;
        }

        static Socket NewSocket()
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // without this, one closed port makes Windows fail the next receive on the whole socket
                s.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception) { }
            s.Bind(new IPEndPoint(IPAddress.Any, 0));
            s.ReceiveTimeout = 400;
            return s;
        }

        public static IPEndPoint Resolve(string address, int defaultPort)
        {
            string host = address.Trim();
            int port = defaultPort;
            int i = host.LastIndexOf(':');
            if (i > 0)
            {
                int p;
                if (!int.TryParse(host.Substring(i + 1), out p) || p < 1 || p > 65535) return null;
                port = p;
                host = host.Substring(0, i);
            }
            if (host.Length == 0) return null;
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
                return ip.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(ip, port) : null;
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(host))
                    if (a.AddressFamily == AddressFamily.InterNetwork) return new IPEndPoint(a, port);
            }
            catch (Exception) { }
            return null;
        }

        static string NewChallenge()
        {
            lock (rng) return rng.Next(0x10000000, 0x7fffffff).ToString("x8");
        }

        public static Dictionary<string, string> ParseInfoString(string info)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] parts = info.Split('\\');
            for (int i = 1; i + 1 < parts.Length; i += 2) d[parts[i]] = parts[i + 1];
            return d;
        }

        public static string Clean(string s)
        {
            if (s == null) return "";
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '^' && i + 1 < s.Length && s[i + 1] != '^') { i++; continue; }   // colour codes
                if (c < 32 || c == 127) continue;
                b.Append(c);
            }
            string r = b.ToString().Trim();
            return r.Length > 80 ? r.Substring(0, 80) : r;
        }

        static int Num(Dictionary<string, string> d, string key)
        {
            string v; int n;
            return d.TryGetValue(key, out v) && int.TryParse(v, out n) ? n : -1;
        }

        static void Fill(ServerInfo s, Dictionary<string, string> d)
        {
            string v;
            s.Name = Clean(d.TryGetValue("hostname", out v) ? v : (d.TryGetValue("sv_hostname", out v) ? v : ""));
            if (s.Name.Length == 0) s.Name = "(unnamed server)";
            s.Map = Clean(d.TryGetValue("mapname", out v) ? v : "");
            s.Mod = Clean(d.TryGetValue("game", out v) ? v : (d.TryGetValue("gamename", out v) ? v : ""));
            s.Players = Num(d, "clients");
            s.Max = Num(d, "sv_maxclients");
            s.Protocol = Num(d, "protocol");
            s.Locked = Num(d, "g_needpass") == 1;
            s.Responded = true;
        }

        public List<ServerInfo> Snapshot()
        {
            List<ServerInfo> list = new List<ServerInfo>();
            lock (gate) foreach (ServerInfo s in servers.Values) list.Add(s.Copy());
            return list;
        }

        void Add(IPEndPoint ep)
        {
            string key = ep.ToString();
            lock (gate)
            {
                if (servers.ContainsKey(key) || servers.Count >= 4000) return;
                ServerInfo s = new ServerInfo();
                s.Ep = ep; s.Key = key;
                servers[key] = s;
            }
            Version++;
        }

        public void Stop()
        {
            stop = true;
            generation++;
            Socket s = sock;
            sock = null;
            if (s != null) { try { s.Close(); } catch (Exception) { } }
            Busy = false;
        }

        public void Start(GameDef game, List<string> extraAddresses)
        {
            Stop();
            lock (gate) servers.Clear();
            Version++;
            stop = false;
            Busy = true;
            MastersAnswered = 0;
            Phase = "Getting the list...";
            int gen = generation;
            Socket s;
            try { s = NewSocket(); }
            catch (Exception) { Busy = false; Phase = "Could not open a network socket."; return; }
            sock = s;
            Thread rx = new Thread(delegate() { Receive(s, gen); });
            rx.IsBackground = true;
            rx.Start();
            Thread tx = new Thread(delegate() { Run(s, gen, game, extraAddresses); });
            tx.IsBackground = true;
            tx.Start();
        }

        bool Gone(int gen) { return stop || gen != generation; }

        bool Pause(int ms, int gen)
        {
            for (int t = 0; t < ms; t += 50)
            {
                if (Gone(gen)) return false;
                Thread.Sleep(50);
            }
            return !Gone(gen);
        }

        volatile GameDef current;

        void Run(Socket s, int gen, GameDef game, List<string> extra)
        {
            current = game;
            if (game.Kind == "gs") { RunGs(s, gen, game, extra); return; }
            try
            {
                byte[] ask = Packet("getservers " + game.Protocol + " empty full");
                foreach (string m in game.Masters)
                {
                    if (Gone(gen)) return;
                    IPEndPoint ep = Resolve(m, 27950);
                    if (ep == null) continue;
                    try { s.SendTo(ask, ep); } catch (Exception) { }
                }
                foreach (string a in extra)
                {
                    IPEndPoint ep = Resolve(a, 27960);
                    if (ep != null) Add(ep);
                }
                if (!Pause(1600, gen)) return;

                for (int pass = 0; pass < 2; pass++)
                {
                    Phase = pass == 0 ? "Checking..." : "Rechecking...";
                    List<ServerInfo> todo = new List<ServerInfo>();
                    lock (gate) foreach (ServerInfo v in servers.Values) if (!v.Responded) todo.Add(v);
                    int sentNow = 0;
                    foreach (ServerInfo v in todo)
                    {
                        if (Gone(gen)) return;
                        string ch = NewChallenge();
                        lock (gate) { v.Challenge = ch; v.SentAt = clock.ElapsedMilliseconds; }
                        try { s.SendTo(Packet("getinfo " + ch), v.Ep); } catch (Exception) { }
                        if (++sentNow % 10 == 0) Thread.Sleep(15);   // short bursts keep ping readings honest
                    }
                    if (!Pause(1400, gen)) return;
                }
                if (!Gone(gen)) { Phase = "Done."; Busy = false; Version++; }
            }
            catch (Exception) { if (!Gone(gen)) { Phase = "Stopped."; Busy = false; } }
        }

        void Receive(Socket s, int gen)
        {
            byte[] buf = new byte[4096];
            while (!Gone(gen))
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = s.ReceiveFrom(buf, ref from); }
                catch (SocketException) { continue; }
                catch (Exception) { return; }
                if (n > 2 && buf[0] == (byte)'\\')
                {
                    try { GsReply(Latin.GetString(buf, 0, n), (IPEndPoint)from); } catch (Exception) { }
                    continue;
                }
                if (n < 8 || buf[0] != 255 || buf[1] != 255 || buf[2] != 255 || buf[3] != 255) continue;
                try
                {
                    string head = Latin.GetString(buf, 4, Math.Min(n - 4, 20));
                    if (head.StartsWith("getserversResponse"))
                    {
                        MastersAnswered++;
                        int i = 4 + 18;
                        while (i < n)
                        {
                            if (buf[i] != (byte)'\\') { i++; continue; }
                            if (i + 3 < n && buf[i + 1] == 'E' && buf[i + 2] == 'O' && buf[i + 3] == 'T' && n - (i + 4) <= 3) break;
                            if (i + 7 > n) break;
                            int port = (buf[i + 5] << 8) | buf[i + 6];
                            if (port != 0)
                                Add(new IPEndPoint(new IPAddress(new byte[] { buf[i + 1], buf[i + 2], buf[i + 3], buf[i + 4] }), port));
                            i += 7;
                        }
                    }
                    else if (head.StartsWith("infoResponse"))
                    {
                        string text = Latin.GetString(buf, 4, n - 4);
                        int nl = text.IndexOf('\n');
                        if (nl < 0) continue;
                        Dictionary<string, string> d = ParseInfoString(text.Substring(nl + 1));
                        string key = ((IPEndPoint)from).ToString();
                        lock (gate)
                        {
                            ServerInfo v;
                            if (!servers.TryGetValue(key, out v)) continue;
                            string ch;
                            if (!d.TryGetValue("challenge", out ch) || ch != v.Challenge) continue;
                            Fill(v, d);
                            v.Ping = (int)Math.Max(1, clock.ElapsedMilliseconds - v.SentAt);
                        }
                        Version++;
                    }
                }
                catch (Exception) { }
            }
        }

        // ---------------------------------------------------------------- classic GameSpy (Unreal Tournament)

        // The GameSpy "secure/validate" answer (the well-documented gsmsalg algorithm).
        public static string GsValidate(string secure, string key)
        {
            byte[] src = Latin.GetBytes(secure), k = Latin.GetBytes(key);
            if (src.Length < 1 || src.Length > 65 || k.Length == 0) return "";
            byte[] t = new byte[256];
            for (int i = 0; i < 256; i++) t[i] = (byte)i;
            byte a = 0, b = 0, x, y;
            for (int i = 0; i < 256; i++)
            {
                a = (byte)(a + t[i] + k[i % k.Length]);
                x = t[a]; t[a] = t[i]; t[i] = x;
            }
            a = 0;
            int n = src.Length, size = n;
            while (size % 3 != 0) size++;
            byte[] tmp = new byte[size];
            for (int i = 0; i < n; i++)
            {
                a = (byte)(a + src[i] + 1);
                x = t[a];
                b = (byte)(b + x);
                y = t[b];
                t[b] = x;
                t[a] = y;
                tmp[i] = (byte)(src[i] ^ t[(byte)(x + y)]);
            }
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            StringBuilder o = new StringBuilder();
            for (int i = 0; i < size; i += 3)
            {
                int p = tmp[i], q = tmp[i + 1], r = tmp[i + 2];
                o.Append(chars[p >> 2]);
                o.Append(chars[((p & 3) << 4) | (q >> 4)]);
                o.Append(chars[((q & 15) << 2) | (r >> 6)]);
                o.Append(chars[r & 63]);
            }
            return o.ToString();
        }

        // Ask one master server (TCP) for the list of game servers.
        static List<IPEndPoint> GsList(string master, GameDef game)
        {
            List<IPEndPoint> list = new List<IPEndPoint>();
            IPEndPoint ep = Resolve(master, 28900);
            if (ep == null) return list;
            using (TcpClient tc = new TcpClient())
            {
                IAsyncResult ar = tc.BeginConnect(ep.Address, ep.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(4000)) return list;
                tc.EndConnect(ar);
                NetworkStream ns = tc.GetStream();
                ns.ReadTimeout = 6000;
                byte[] buf = new byte[65536];
                StringBuilder sb = new StringBuilder();
                // the master greets with \basic\\secure\XXXXXX
                int got = ns.Read(buf, 0, buf.Length);
                if (got <= 0) return list;
                Dictionary<string, string> hello = ParseInfoString(Latin.GetString(buf, 0, got));
                string secure;
                hello.TryGetValue("secure", out secure);
                string ask = "\\gamename\\" + game.GameName + "\\location\\0\\validate\\" + GsValidate(secure ?? "", game.GameKey)
                    + "\\final\\\\list\\\\gamename\\" + game.GameName + "\\final\\";
                byte[] q = Latin.GetBytes(ask);
                ns.Write(q, 0, q.Length);
                while (true)
                {
                    int r;
                    try { r = ns.Read(buf, 0, buf.Length); } catch (IOException) { break; }
                    if (r <= 0) break;
                    sb.Append(Latin.GetString(buf, 0, r));
                    if (sb.ToString().EndsWith("\\final\\") || sb.Length > 2000000) break;
                }
                string[] parts = sb.ToString().Split('\\');
                for (int i = 1; i + 1 < parts.Length; i++)
                {
                    if (parts[i] != "ip") continue;
                    IPEndPoint se = Resolve(parts[i + 1], game.QueryPort);
                    if (se != null) list.Add(se);
                    i++;
                }
            }
            return list;
        }

        void RunGs(Socket s, int gen, GameDef game, List<string> extra)
        {
            try
            {
                List<Thread> threads = new List<Thread>();
                foreach (string m in game.Masters)
                {
                    string mm = m;
                    Thread t = new Thread(delegate()
                    {
                        try
                        {
                            List<IPEndPoint> l = GsList(mm, game);
                            if (l.Count > 0) MastersAnswered++;
                            foreach (IPEndPoint e in l) if (!Gone(gen)) Add(e);
                        }
                        catch (Exception) { }
                    });
                    t.IsBackground = true;
                    t.Start();
                    threads.Add(t);
                }
                foreach (string a in extra)
                {
                    IPEndPoint ep = Resolve(a, game.QueryPort);
                    if (ep != null) Add(ep);
                }
                foreach (Thread t in threads) { while (t.IsAlive) { if (!Pause(100, gen)) return; } }

                byte[] ask = Latin.GetBytes("\\info\\");
                for (int pass = 0; pass < 3; pass++)
                {
                    // the first answer is often slow; later passes ask everyone again and keep the best time
                    Phase = pass == 0 ? "Checking..." : "Measuring ping...";
                    List<ServerInfo> todo = new List<ServerInfo>();
                    lock (gate) foreach (ServerInfo v in servers.Values) if (pass > 0 || !v.Responded) todo.Add(v);
                    int sentNow = 0;
                    foreach (ServerInfo v in todo)
                    {
                        if (Gone(gen)) return;
                        lock (gate) v.SentAt = clock.ElapsedMilliseconds;
                        try { s.SendTo(ask, v.Ep); } catch (Exception) { }
                        if (++sentNow % 10 == 0) Thread.Sleep(15);
                    }
                    if (!Pause(1600, gen)) return;
                }
                if (!Gone(gen)) { Phase = "Done."; Busy = false; Version++; }
            }
            catch (Exception) { if (!Gone(gen)) { Phase = "Stopped."; Busy = false; } }
        }

        static void FillGs(ServerInfo s, Dictionary<string, string> d)
        {
            string v;
            s.Name = Clean(d.TryGetValue("hostname", out v) ? v : "");
            if (s.Name.Length == 0) s.Name = "(unnamed server)";
            s.Map = Clean(d.TryGetValue("mapname", out v) ? v : "");
            string gt = d.TryGetValue("gametype", out v) ? v : "";
            int dot = gt.LastIndexOf('.');
            s.Mod = Clean(dot >= 0 ? gt.Substring(dot + 1) : gt);
            s.Players = Num(d, "numplayers");
            s.Max = Num(d, "maxplayers");
            int hp = Num(d, "hostport");
            if (hp > 0) s.JoinPort = hp;
            if (d.TryGetValue("password", out v)) s.Locked = v.Equals("true", StringComparison.OrdinalIgnoreCase);
            s.Responded = true;
        }

        void GsReply(string text, IPEndPoint from)
        {
            Dictionary<string, string> d = ParseInfoString(text);
            if (!d.ContainsKey("hostname") && !d.ContainsKey("mapname")) return;
            string key = from.ToString();
            lock (gate)
            {
                ServerInfo v;
                if (!servers.TryGetValue(key, out v)) return;
                bool had = v.Responded && v.Ping > 0;
                FillGs(v, d);
                int p = (int)Math.Max(1, clock.ElapsedMilliseconds - v.SentAt);
                v.Ping = had ? Math.Min(v.Ping, p) : p;
            }
            Version++;
        }

        // details for one classic GameSpy server: \status\ can come back in several packets
        static bool AskGs(IPEndPoint ep, out ServerInfo info, out List<PlayerRow> players)
        {
            info = new ServerInfo();
            info.Ep = ep; info.Key = ep.ToString();
            players = new List<PlayerRow>();
            Socket s = null;
            try
            {
                s = NewSocket();
                s.ReceiveTimeout = 1500;
                Stopwatch sw = Stopwatch.StartNew();
                s.SendTo(Latin.GetBytes("\\status\\"), ep);
                Dictionary<string, string> all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                byte[] buf = new byte[8192];
                bool first = true, done = false;
                for (int tries = 0; tries < 12 && !done; tries++)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = s.ReceiveFrom(buf, ref from); } catch (SocketException) { break; }
                    if (!((IPEndPoint)from).Equals(ep)) continue;
                    if (first) { info.Ping = (int)Math.Max(1, sw.ElapsedMilliseconds); first = false; }
                    string text = Latin.GetString(buf, 0, n);
                    foreach (KeyValuePair<string, string> kv in ParseInfoString(text)) all[kv.Key] = kv.Value;
                    if (text.Contains("\\final\\")) done = true;
                }
                if (first) return false;
                FillGs(info, all);
                for (int i = 0; i < 64; i++)
                {
                    string name;
                    if (!all.TryGetValue("player_" + i, out name)) continue;
                    PlayerRow p = new PlayerRow();
                    p.Name = Clean(name);
                    p.Score = Num(all, "frags_" + i);
                    p.Ping = Num(all, "ping_" + i);
                    players.Add(p);
                }
                return true;
            }
            catch (Exception) { return false; }
            finally { if (s != null) s.Close(); }
        }

        public static bool AskAny(GameDef game, IPEndPoint ep, bool status, out ServerInfo info, out List<PlayerRow> players)
        {
            if (game.Kind == "gs") return AskGs(ep, out info, out players);
            return Ask(ep, status, out info, out players);
        }

        // One question to one server, used for the details pane and for "Check".
        public static bool Ask(IPEndPoint ep, bool status, out ServerInfo info, out List<PlayerRow> players)
        {
            info = new ServerInfo();
            info.Ep = ep; info.Key = ep.ToString();
            players = new List<PlayerRow>();
            Socket s = null;
            try
            {
                s = NewSocket();
                s.ReceiveTimeout = 1500;
                string ch = NewChallenge();
                Stopwatch sw = Stopwatch.StartNew();
                s.SendTo(Packet((status ? "getstatus " : "getinfo ") + ch), ep);
                byte[] buf = new byte[8192];
                for (int tries = 0; tries < 4; tries++)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = s.ReceiveFrom(buf, ref from);
                    if (n < 8 || !((IPEndPoint)from).Equals(ep)) continue;
                    string text = Latin.GetString(buf, 4, n - 4);
                    string want = status ? "statusResponse" : "infoResponse";
                    if (!text.StartsWith(want)) continue;
                    string[] lines = text.Split('\n');
                    if (lines.Length < 2) continue;
                    Dictionary<string, string> d = ParseInfoString(lines[1]);
                    string got;
                    if (!d.TryGetValue("challenge", out got) || got != ch) continue;
                    Fill(info, d);
                    info.Ping = (int)Math.Max(1, sw.ElapsedMilliseconds);
                    if (status)
                    {
                        for (int i = 2; i < lines.Length; i++)
                        {
                            Match m = Regex.Match(lines[i], "^(-?\\d+) (-?\\d+) \"(.*)\"");
                            if (!m.Success) continue;
                            PlayerRow p = new PlayerRow();
                            p.Score = int.Parse(m.Groups[1].Value);
                            p.Ping = int.Parse(m.Groups[2].Value);
                            p.Name = Clean(m.Groups[3].Value);
                            players.Add(p);
                        }
                        info.Players = players.Count;
                    }
                    return true;
                }
            }
            catch (Exception) { }
            finally { if (s != null) { try { s.Close(); } catch (Exception) { } } }
            return false;
        }
    }

    // ---------------------------------------------------------------- server browser page

    class ArenaPanel : UserControl
    {
        readonly GameDef game;
        readonly Scanner scanner = new Scanner();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        int shownVersion = -1;
        int sortColumn = 4;
        bool sortDescending = false;
        bool closing, rebuilding;
        string detailsFor = "";

        ServerList lvServers;
        ListView lvPlayers;
        TextBox txtFilter, txtAddress, txtExe;
        ComboBox cmbSort;
        CheckBox chkEmpty, chkFull, chkFavs, chkPlain;
        Label lblStatus, lblDetails, lblHint;
        Button btnRefresh, btnFav, btnJoin;
        Hero hero;

        public string GameTitle { get { return game.Title; } }
        public GameDef Game { get { return game; } }

        // raised by "Invite crew": game, server address, server name, detail line
        public event Action<GameDef, string, string, string> InviteRequested;

        // join a server someone shared in chat
        public void JoinAddress(string address)
        {
            txtAddress.Text = address;
            LoadDetails(address, false);
            Join();
        }

        void Invite()
        {
            string a = txtAddress.Text.Trim();
            if (a.Length == 0) { lblHint.Text = "Pick a server first, then press Invite crew."; return; }
            string name = a, detail = "";
            foreach (ServerInfo si in scanner.Snapshot())
                if (si.Key == a)
                {
                    name = si.Name;
                    detail = si.Map + (si.Max > 0 ? " \u00b7 " + si.Players + " of " + si.Max + " playing" : "") + " \u00b7 " + si.Key;
                }
            if (detail.Length == 0) detail = a;
            if (InviteRequested != null) InviteRequested(game, a, name, detail);
        }

        public string InstalledPath()
        {
            string p = txtExe.Text.Trim().Trim('"');
            return p.Length > 0 && File.Exists(p) ? p : "";
        }

        public ArenaPanel(GameDef g)
        {
            game = g;
            Font = new Font("Segoe UI", 9f);
            Size = new Size(752, 668);

            hero = new Hero(g.Title, g.Tagline != null ? g.Tagline : g.Blurb, g.ColorA, g.ColorB);
            hero.SetBounds(16, 12, 720, 168);
            Controls.Add(hero);
            btnJoin = new Button();
            btnJoin.Text = "Join game";
            btnJoin.Tag = "primary";
            btnJoin.SetBounds(hero.Width - 146, hero.Height - 54, 126, 36);
            btnJoin.Click += delegate { Join(); };
            hero.Controls.Add(btnJoin);
            Button btnInv = new Button();
            btnInv.Text = "Invite crew";
            btnInv.SetBounds(hero.Width - 282, hero.Height - 54, 126, 36);
            btnInv.Click += delegate { Invite(); };
            hero.Controls.Add(btnInv);

            txtFilter = new TextBox();
            txtFilter.SetBounds(16, 192, 214, 24);
            txtFilter.TextChanged += delegate { shownVersion = -1; };
            Controls.Add(txtFilter);
            Theme.Cue(txtFilter, "Filter by server, map, or mod");
            cmbSort = new ComboBox();
            cmbSort.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSort.Items.AddRange(new object[] { "Lowest ping", "Most players", "Name" });
            cmbSort.SelectedIndex = 0;
            cmbSort.SetBounds(238, 192, 116, 24);
            cmbSort.SelectedIndexChanged += delegate
            {
                if (cmbSort.SelectedIndex == 1) { sortColumn = 3; sortDescending = true; }
                else if (cmbSort.SelectedIndex == 2) { sortColumn = 1; sortDescending = false; }
                else { sortColumn = 4; sortDescending = false; }
                shownVersion = -1;
            };
            Controls.Add(cmbSort);
            chkFavs = MakeCheck("Favorites", 366, 194, false);
            chkEmpty = MakeCheck("Hide empty", 452, 194, true);
            chkFull = MakeCheck("Hide full", 548, 194, false);
            chkPlain = MakeCheck("No mods", 632, 194, false);
            chkPlain.Visible = g.Kind == "q3";

            Label heading = new Label();
            heading.Text = "Community servers";
            heading.Font = new Font("Segoe UI Semibold", 12.5f);
            heading.SetBounds(16, 230, 220, 26);
            Controls.Add(heading);
            lblStatus = new Label();
            lblStatus.SetBounds(236, 234, 370, 20);
            lblStatus.TextAlign = ContentAlignment.TopRight;
            lblStatus.Tag = "muted";
            Controls.Add(lblStatus);
            btnRefresh = new Button();
            btnRefresh.Text = "Refresh";
            btnRefresh.SetBounds(616, 230, 120, 26);
            btnRefresh.Click += delegate { StartScan(); };
            Controls.Add(btnRefresh);

            lvServers = new ServerList();
            lvServers.SetBounds(16, 262, 720, 214);
            lvServers.SelectedIndexChanged += delegate { ServerSelected(); };
            lvServers.DoubleClick += delegate { Join(); };
            Controls.Add(lvServers);

            Panel cp = Card("Who is playing", 16, 486, 296, 172);
            lblDetails = new Label();
            lblDetails.SetBounds(12, 32, 272, 18);
            lblDetails.AutoEllipsis = true;
            lblDetails.Text = "Pick a server to see its players.";
            lblDetails.Tag = "muted";
            cp.Controls.Add(lblDetails);
            lvPlayers = new ListView();
            lvPlayers.SetBounds(12, 54, 272, 106);
            lvPlayers.View = View.Details;
            lvPlayers.FullRowSelect = true;
            lvPlayers.HeaderStyle = ColumnHeaderStyle.None;
            lvPlayers.BorderStyle = BorderStyle.None;
            lvPlayers.Columns.Add("Player", 156);
            lvPlayers.Columns.Add("Score", 52);
            lvPlayers.Columns.Add("Ping", 50);
            cp.Controls.Add(lvPlayers);

            Panel cc = Card("Connect", 322, 486, 414, 172);
            Label la = new Label(); la.Text = "Address"; la.SetBounds(12, 40, 56, 18); la.Tag = "muted"; cc.Controls.Add(la);
            txtAddress = new TextBox();
            txtAddress.SetBounds(72, 36, 168, 24);
            cc.Controls.Add(txtAddress);
            Btn(cc, "Check", 246, 35, 52, delegate { Check(); });
            Btn(cc, "Copy", 302, 35, 48, delegate
            {
                if (txtAddress.Text.Trim().Length > 0) Clipboard.SetText(txtAddress.Text.Trim());
            });
            btnFav = Btn(cc, "Fav", 354, 35, 48, delegate { ToggleFavorite(); });

            Label le = new Label(); le.Text = "Game"; le.SetBounds(12, 74, 56, 18); le.Tag = "muted"; cc.Controls.Add(le);
            txtExe = new TextBox();
            txtExe.SetBounds(72, 70, 168, 24);
            txtExe.Text = Config.Get(game.Id + ".exe", "");
            txtExe.Leave += delegate { Config.Set(game.Id + ".exe", txtExe.Text.Trim().Trim('"')); };
            cc.Controls.Add(txtExe);
            Btn(cc, "Find", 246, 69, 52, delegate { FindExe(true); });
            Btn(cc, "Browse...", 302, 69, 100, delegate { BrowseExe(); });

            lblHint = new Label();
            lblHint.SetBounds(12, 106, 390, 58);
            lblHint.Tag = "muted";
            lblHint.Text = "Double-click a server to join. To play with a friend, pick the same server or send them its address with Copy. " + g.GetIt;
            cc.Controls.Add(lblHint);

            if (txtExe.Text.Length == 0) FindExe(false);

            timer.Interval = 400;
            timer.Tick += delegate { Tick(); };
        }

        Panel Card(string title, int x, int y, int w, int h)
        {
            Panel p = new Panel();
            p.Tag = "card";
            p.SetBounds(x, y, w, h);
            Label t = new Label();
            t.Text = title;
            t.Font = new Font("Segoe UI Semibold", 10f);
            t.SetBounds(12, 8, w - 24, 20);
            p.Controls.Add(t);
            Controls.Add(p);
            return p;
        }

        static Button Btn(Control parent, string text, int x, int y, int w, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 26);
            b.Click += click;
            parent.Controls.Add(b);
            return b;
        }

        CheckBox MakeCheck(string text, int x, int y, bool on)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Location = new Point(x, y);
            c.Checked = on;
            c.CheckedChanged += delegate { shownVersion = -1; };
            Controls.Add(c);
            return c;
        }

        // ---- favorites
        List<string> Favorites()
        {
            List<string> list = new List<string>();
            foreach (string f in Config.Get(game.Id + ".favorites", "").Split(','))
                if (f.Trim().Length > 0) list.Add(f.Trim());
            return list;
        }

        void ToggleFavorite()
        {
            IPEndPoint ep = Scanner.Resolve(txtAddress.Text, game.QueryPort);
            if (ep == null) { lblHint.Text = "Enter or pick a server address first."; return; }
            List<string> favs = Favorites();
            string key = ep.ToString();
            if (favs.Contains(key)) { favs.Remove(key); lblHint.Text = "Removed from favorites."; }
            else { favs.Add(key); lblHint.Text = "Added to favorites. They are checked on every refresh."; }
            Config.Set(game.Id + ".favorites", string.Join(",", favs.ToArray()));
            shownVersion = -1;
        }

        // ---- page life cycle, driven by the shell
        public void SetActive(bool on)
        {
            if (on)
            {
                timer.Start();
                if (shownVersion < 0 && !scanner.Busy && lvServers.Items.Count == 0) StartScan();
            }
            else timer.Stop();
        }

        public void Shutdown()
        {
            closing = true;
            timer.Stop();
            scanner.Stop();
        }

        void StartScan()
        {
            lvPlayers.Items.Clear();
            detailsFor = "";
            scanner.Start(game, Favorites());
            shownVersion = -1;
        }

        void Tick()
        {
            if (closing) return;
            int v = scanner.Version;
            if (v == shownVersion) return;
            shownVersion = v;
            Rebuild();
        }

        static int Compare(ServerInfo a, ServerInfo b, int column)
        {
            switch (column)
            {
                case 1: return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                case 2: return string.Compare(a.Map, b.Map, StringComparison.OrdinalIgnoreCase);
                case 3: return a.Players.CompareTo(b.Players);
                case 4: return a.Ping.CompareTo(b.Ping);
                case 5: return string.Compare(a.Mod, b.Mod, StringComparison.OrdinalIgnoreCase);
                default: return string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            }
        }

        void Rebuild()
        {
            List<ServerInfo> all = scanner.Snapshot();
            List<string> favs = Favorites();
            string filter = txtFilter.Text.Trim().ToLowerInvariant();
            List<ServerInfo> show = new List<ServerInfo>();
            int answered = 0;
            foreach (ServerInfo s in all)
            {
                if (!s.Responded) continue;
                answered++;
                if (chkEmpty.Checked && s.Players <= 0) continue;
                if (chkFull.Checked && s.Max > 0 && s.Players >= s.Max) continue;
                if (chkFavs.Checked && !favs.Contains(s.Key)) continue;
                if (chkPlain.Checked && s.Mod.Length > 0 && !s.Mod.Equals("baseq3", StringComparison.OrdinalIgnoreCase)
                    && !s.Mod.Equals("baseoa", StringComparison.OrdinalIgnoreCase)) continue;
                if (filter.Length > 0 && (s.Name + " " + s.Map + " " + s.Mod + " " + s.Key).ToLowerInvariant().IndexOf(filter) < 0) continue;
                show.Add(s);
            }
            int col = sortColumn; bool desc = sortDescending;
            show.Sort(delegate(ServerInfo a, ServerInfo b)
            {
                int c = Compare(a, b, col);
                if (c == 0) c = a.Ping.CompareTo(b.Ping);
                if (c == 0) c = string.Compare(a.Key, b.Key, StringComparison.Ordinal);
                return desc ? -c : c;
            });

            string selected = lvServers.SelectedItem != null ? ((ServerInfo)lvServers.SelectedItem).Key : null;
            rebuilding = true;
            lvServers.BeginUpdate();
            lvServers.Favs = favs;
            lvServers.Items.Clear();
            int selIndex = -1;
            foreach (ServerInfo s in show)
            {
                int idx = lvServers.Items.Add(s);
                if (s.Key == selected) selIndex = idx;
            }
            if (selIndex >= 0) lvServers.SelectedIndex = selIndex;
            lvServers.EndUpdate();
            rebuilding = false;

            if (scanner.Busy) lblStatus.Text = scanner.Phase + " " + answered + " / " + all.Count;
            else if (all.Count == 0)
                lblStatus.Text = scanner.MastersAnswered == 0 ? "No master server answered." : "No servers listed.";
            else lblStatus.Text = show.Count + " shown, " + answered + " online";
        }

        void ServerSelected()
        {
            if (rebuilding || lvServers.SelectedItem == null) return;
            string key = ((ServerInfo)lvServers.SelectedItem).Key;
            txtAddress.Text = key;
            LoadDetails(key, false);
        }

        void LoadDetails(string address, bool report)
        {
            detailsFor = address;
            lblDetails.Text = "Asking " + address + "...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                IPEndPoint ep = Scanner.Resolve(address, game.QueryPort);
                ServerInfo info = null; List<PlayerRow> players = null;
                bool ok = ep != null && Scanner.AskAny(game, ep, true, out info, out players);
                try
                {
                    if (closing || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (detailsFor != address) return;
                        lvPlayers.Items.Clear();
                        if (!ok)
                        {
                            lblDetails.Text = ep == null ? "That address could not be found." : "No answer from " + address + ".";
                            if (report) lblHint.Text = "The server did not answer. Check the address and port.";
                            return;
                        }
                        lblDetails.Text = info.Name + "  -  " + info.Map + "  -  " + info.Players + " / " + info.Max;
                        foreach (PlayerRow p in players)
                        {
                            ListViewItem it = new ListViewItem(p.Name);
                            it.SubItems.Add(p.Score.ToString());
                            it.SubItems.Add(p.Ping == 0 ? "bot" : p.Ping.ToString());
                            lvPlayers.Items.Add(it);
                        }
                        if (report)
                            lblHint.Text = "Server answered in " + info.Ping + " ms" + (info.Locked ? " (needs a password)." : ".");
                    });
                }
                catch (Exception) { }
            });
        }

        void Check()
        {
            string a = txtAddress.Text.Trim();
            if (a.Length == 0) { lblHint.Text = "Type a server address first, like 203.0.113.25:27960."; return; }
            LoadDetails(a, true);
        }

        // ---- finding and starting the game
        List<string> Candidates()
        {
            List<string> dirs = new List<string>();
            try
            {
                object sp = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
                if (sp != null)
                {
                    List<string> libs = new List<string>();
                    string steam = sp.ToString().Replace('/', '\\');
                    libs.Add(steam);
                    string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                    foreach (string lib in libs)
                        foreach (string f in game.SteamFolders)
                            dirs.Add(Path.Combine(lib, @"steamapps\common\" + f));
                }
            }
            catch (Exception) { }
            dirs.AddRange(game.Folders);
            return dirs;
        }

        public void FindExe(bool report)
        {
            foreach (string dir in Candidates())
            {
                foreach (string exe in game.ExeNames)
                {
                    try
                    {
                        string p = Path.Combine(dir, exe);
                        if (!File.Exists(p)) continue;
                        txtExe.Text = p;
                        Config.Set(game.Id + ".exe", p);
                        if (report) lblHint.Text = "Found " + exe + ".";
                        return;
                    }
                    catch (Exception) { }
                }
            }
            if (report) lblHint.Text = "Not found in the usual places. Use Browse and pick the game's .exe.";
        }

        public void BrowseExe()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Pick the " + game.Title + " program";
                d.Filter = "Programs (*.exe)|*.exe";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                txtExe.Text = d.FileName;
                Config.Set(game.Id + ".exe", d.FileName);
            }
        }

        void Join()
        {
            string a = txtAddress.Text.Trim();
            if (a.Length == 0) { lblHint.Text = "Pick a server from the list or type its address."; return; }
            string exe = txtExe.Text.Trim().Trim('"');
            if (exe.Length == 0 || !File.Exists(exe))
            {
                MessageBox.Show("Set the game program first: press Find, or Browse to the game's .exe.", "GameSpy Revival");
                return;
            }
            IPEndPoint ep = Scanner.Resolve(a, game.QueryPort);
            if (ep == null) { lblHint.Text = "That address could not be found."; return; }
            IPEndPoint target = ep;
            if (game.Kind == "gs")
            {
                // the list shows query ports; the game wants its game port (usually one lower)
                int jp = 0;
                foreach (ServerInfo si0 in scanner.Snapshot()) if (si0.Key == ep.ToString() && si0.JoinPort > 0) jp = si0.JoinPort;
                if (jp == 0)
                {
                    ServerInfo inf; List<PlayerRow> pl;
                    if (Scanner.AskAny(game, ep, false, out inf, out pl) && inf.JoinPort > 0) jp = inf.JoinPort;
                }
                target = new IPEndPoint(ep.Address, jp > 0 ? jp : Math.Max(1, ep.Port - 1));
            }
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, string.Format(game.JoinFormat, target));
                si.WorkingDirectory = Path.GetDirectoryName(exe);
                si.UseShellExecute = false;
                Process.Start(si);
                lblHint.Text = "Game started, connecting to " + target + ".";
            }
            catch (Exception ex) { MessageBox.Show("Could not start the game: " + ex.Message, "GameSpy Revival"); }
        }
    }

}
