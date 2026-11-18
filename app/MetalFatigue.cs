// Metal Fatigue page of GameSpy Revival: lobby, server hosting and setup for play through the mfnet relay.
// Copyright (C) 2026 the GameSpy Revival contributors.
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
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MFNet
{
    static class Mf
    {
        public const string Version = "0.5";
    }

    // ---------------------------------------------------------------- settings

    class Settings
    {
        public string GameDir = "";
        public string Server = "127.0.0.1:27900";
        public string Room = "";
        public bool RunInBackground = true;

        public static string DataDir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MFNet");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static string FilePath { get { return Path.Combine(DataDir, "settings.txt"); } }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                        if (k == "game") s.GameDir = v;
                        else if (k == "server") s.Server = v;
                        else if (k == "room") s.Room = v;
                        else if (k == "background") s.RunInBackground = v != "0";
                    }
                }
            }
            catch (Exception) { }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, new string[] {
                    "game=" + GameDir, "server=" + Server, "room=" + Room, "background=" + (RunInBackground ? "1" : "0") });
            }
            catch (Exception) { }
        }

        public string Host
        {
            get { int i = Server.LastIndexOf(':'); return i > 0 ? Server.Substring(0, i) : Server; }
        }

        public int Port
        {
            get
            {
                int i = Server.LastIndexOf(':'); int p;
                if (i > 0 && int.TryParse(Server.Substring(i + 1), out p) && p > 0 && p < 65536) return p;
                return 27900;
            }
        }
    }

    // ---------------------------------------------------------------- the game install

    static class Game
    {
        public const string Exe = "MFatigue.exe";
        public const string ModDll = "DINPUT.dll";
        public const string ModIni = "mfnet.ini";
        public const string Conf = "dgVoodoo.conf";

        public static bool IsGameDir(string dir)
        {
            try { return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, Exe)); }
            catch (Exception) { return false; }
        }

        public static string Find(string remembered)
        {
            if (IsGameDir(remembered)) return remembered;
            try
            {
                string here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (IsGameDir(here)) return here;
                string up = Path.GetDirectoryName(here);
                if (IsGameDir(up)) return up;
            }
            catch (Exception) { }
            try
            {
                List<string> libs = new List<string>();
                object sp = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
                if (sp != null)
                {
                    string steam = sp.ToString().Replace('/', '\\');
                    libs.Add(steam);
                    string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                    }
                }
                foreach (string lib in libs)
                {
                    string dir = Path.Combine(lib, @"steamapps\common\Metal Fatigue");
                    if (IsGameDir(dir)) return dir;
                }
            }
            catch (Exception) { }
            return "";
        }

        public static bool IsRunning()
        {
            try { return Process.GetProcessesByName("MFatigue").Length > 0; }
            catch (Exception) { return false; }
        }

        // Same fingerprint the mod sends to the relay, so the app can tell which games you can join.
        public static string Fingerprint(string dir)
        {
            try
            {
                byte[] b = File.ReadAllBytes(Path.Combine(dir, Exe));
                uint h1 = 0x811C9DC5u, h2 = 0x01000193u ^ 0xDEADBEEFu;
                unchecked
                {
                    for (int i = 0; i < b.Length; i++)
                    {
                        h1 = (h1 ^ b[i]) * 0x01000193u;
                        h2 = (h2 ^ b[i]) * 0x01000193u + (h2 >> 27);
                    }
                }
                return h1.ToString("x8") + h2.ToString("x8") + "-" + b.Length;
            }
            catch (Exception) { return ""; }
        }

        public static byte[] Resource(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException("This copy of MFNet is missing " + name + ".");
                byte[] b = new byte[s.Length];
                int off = 0;
                while (off < b.Length)
                {
                    int n = s.Read(b, off, b.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
                return b;
            }
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static bool Contains(byte[] hay, string needle)
        {
            byte[] n = Encoding.ASCII.GetBytes(needle);
            for (int i = 0; i + n.Length <= hay.Length; i++)
            {
                int j = 0;
                while (j < n.Length && hay[i + j] == n[j]) j++;
                if (j == n.Length) return true;
            }
            return false;
        }

        // 0 = not installed, 1 = installed and current, 2 = installed but a different version, 3 = someone else's DINPUT.dll
        public static int ModState(string dir)
        {
            try
            {
                string p = Path.Combine(dir, ModDll);
                if (!File.Exists(p)) return 0;
                byte[] have = File.ReadAllBytes(p);
                if (SameBytes(have, Resource(ModDll))) return 1;
                return Contains(have, "mfnet ") ? 2 : 3;
            }
            catch (Exception) { return 0; }
        }

        public static void WriteIni(string dir, Settings s)
        {
            string text = "[mfnet]\r\n"
                + "; written by the MFNet app\r\n"
                + "enabled=1\r\n"
                + "relay=" + s.Host + ":" + s.Port + "\r\n"
                + "room=" + s.Room + "\r\n"
                + "run_in_background=" + (s.RunInBackground ? "1" : "0") + "\r\n"
                + "log=1\r\n";
            File.WriteAllText(Path.Combine(dir, ModIni), text, Encoding.ASCII);
        }

        public static void InstallMod(string dir, Settings s)
        {
            File.WriteAllBytes(Path.Combine(dir, ModDll), Resource(ModDll));
            WriteIni(dir, s);
        }

        public static void RemoveMod(string dir)
        {
            string a = Path.Combine(dir, ModDll), b = Path.Combine(dir, ModIni);
            if (File.Exists(a)) File.Delete(a);
            if (File.Exists(b)) File.Delete(b);
        }

        // ---- display mode, through the dgVoodoo graphics wrapper's settings file
        static readonly Encoding Latin = Encoding.GetEncoding(1252);

        static string ConfValue(string text, string key)
        {
            Match m = Regex.Match(text, "^" + key + "[ \\t]*=[ \\t]*([^\\r\\n]*)", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        static string ConfSet(string text, string key, string value)
        {
            Regex r = new Regex("^(" + key + "[ \\t]*=)[^\\r\\n]*", RegexOptions.Multiline);
            return r.Replace(text, "${1} " + value, 1);
        }

        // 0 fullscreen, 1 windowed fullscreen, 2 windowed, -1 no settings file
        public static int DisplayMode(string dir)
        {
            try
            {
                string p = Path.Combine(dir, Conf);
                if (!File.Exists(p)) return -1;
                string t = File.ReadAllText(p, Latin);
                string fs = ConfValue(t, "FullScreenMode"), fa = ConfValue(t, "FullscreenAttributes");
                if (fs == null) return -1;
                if (fs.ToLowerInvariant() == "false") return 2;
                return (fa != null && fa.ToLowerInvariant().Contains("fake")) ? 1 : 0;
            }
            catch (Exception) { return -1; }
        }

        public static void SetDisplayMode(string dir, int mode)
        {
            string p = Path.Combine(dir, Conf);
            string backup = p + ".mfnet-backup";
            if (!File.Exists(backup)) File.Copy(p, backup);
            string t = File.ReadAllText(p, Latin);
            t = ConfSet(t, "FullScreenMode", mode == 2 ? "false" : "true");
            t = ConfSet(t, "FullscreenAttributes", mode == 1 ? "fake" : "");
            File.WriteAllText(p, t, Latin);
        }
    }

    // ---------------------------------------------------------------- relay status client

    class GameInfo
    {
        public string Name = "";
        public int Players, Max;
        public uint Flags;
        public string Files = "";
        public List<string> Who = new List<string>();
    }

    class StatusResult
    {
        public bool Ok;
        public string Error = "";
        public int Online;
        public List<GameInfo> Games = new List<GameInfo>();
    }

    static class Relay
    {
        static readonly byte[] AppGuid = new byte[] {
            0x33, 0x0e, 0xe0, 0x50, 0x6b, 0x46, 0xd1, 0x11, 0xbe, 0x45, 0x00, 0xa0, 0xc9, 0x03, 0x41, 0x72 };

        static void PutName(List<byte> b, string s)
        {
            byte[] raw = Encoding.ASCII.GetBytes(s ?? "");
            int n = Math.Min(raw.Length, 63);
            b.Add((byte)n);
            for (int i = 0; i < n; i++) b.Add(raw[i]);
        }

        static void SendFrame(Stream st, byte type, List<byte> payload)
        {
            int n = payload.Count + 1;
            byte[] f = new byte[2 + n];
            f[0] = (byte)n; f[1] = (byte)(n >> 8); f[2] = type;
            payload.CopyTo(f, 3);
            st.Write(f, 0, f.Length);
        }

        static void ReadFull(Stream st, byte[] buf, int n)
        {
            int off = 0;
            while (off < n)
            {
                int r = st.Read(buf, off, n - off);
                if (r <= 0) throw new IOException("The server closed the connection.");
                off += r;
            }
        }

        static byte[] ReadFrame(Stream st, out byte type)
        {
            byte[] h = new byte[2];
            ReadFull(st, h, 2);
            int n = h[0] | (h[1] << 8);
            if (n < 1) throw new IOException("Bad reply from the server.");
            byte[] body = new byte[n];
            ReadFull(st, body, n);
            type = body[0];
            byte[] p = new byte[n - 1];
            Array.Copy(body, 1, p, 0, n - 1);
            return p;
        }

        static string GetName(byte[] p, ref int o)
        {
            int n = p[o];
            string s = Encoding.ASCII.GetString(p, o + 1, n);
            o += 1 + n;
            return s;
        }

        public static StatusResult Status(string host, int port, string room)
        {
            StatusResult res = new StatusResult();
            TcpClient c = new TcpClient();
            try
            {
                IAsyncResult ar = c.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(2500)) throw new IOException("No answer from " + host + ":" + port + ".");
                c.EndConnect(ar);
                c.ReceiveTimeout = 3000; c.SendTimeout = 3000; c.NoDelay = true;
                Stream st = c.GetStream();

                List<byte> hello = new List<byte>();
                hello.AddRange(new byte[] { 1, 0, 0, 0 });
                hello.AddRange(AppGuid);
                PutName(hello, "lobby-app");
                PutName(hello, room);
                SendFrame(st, 0x01, hello);
                SendFrame(st, 0x0A, new List<byte>());

                byte type; byte[] p;
                do { p = ReadFrame(st, out type); } while (type != 0x8A);

                res.Online = p[0] | (p[1] << 8);
                int count = p[2], o = 3;
                for (int i = 0; i < count; i++)
                {
                    GameInfo g = new GameInfo();
                    g.Flags = BitConverter.ToUInt32(p, o);
                    g.Players = p[o + 4]; g.Max = p[o + 5];
                    o += 6;
                    g.Name = GetName(p, ref o);
                    g.Files = GetName(p, ref o);
                    int np = p[o]; o++;
                    for (int k = 0; k < np; k++) g.Who.Add(GetName(p, ref o));
                    res.Games.Add(g);
                }
                res.Ok = true;
            }
            catch (SocketException) { res.Error = "Can't reach a server at " + host + ":" + port + "."; }
            catch (Exception ex) { res.Error = ex.Message; }
            finally { try { c.Close(); } catch (Exception) { } }
            return res;
        }
    }

    // ---------------------------------------------------------------- main window

    public class MetalFatiguePanel : UserControl
    {
        Settings cfg = Settings.Load();
        string gameDir = "";
        string myFiles = "";

        Label lblGame, lblMod, lblServer;
        TextBox txtServer, txtRoom;
        ListView lvGames;
        Label lblOnline;
        Button btnPlay, btnSecond;

        RadioButton rbLocal, rbPublic;
        NumericUpDown numPort;
        Button btnRelay;
        TextBox txtRelayLog, txtPublicIp;
        Label lblRelay;
        Process relayProc;

        TextBox txtGameDir;
        Label lblModState;
        Button btnInstall, btnRemove;
        RadioButton rbFull, rbBorderless, rbWindowed;
        Button btnDisplay;
        Label lblDisplay;
        CheckBox chkBackground;

        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        bool refreshing;
        bool closing;

        public MetalFatiguePanel()
        {
            Font = new Font("Segoe UI", 9f);
            Size = new Size(760, 580);

            Panel top = new Panel();
            top.SetBounds(0, 0, 760, 74);
            top.BackColor = Color.FromArgb(24, 28, 48);
            Controls.Add(top);
            lblGame = TopLabel(top, 8);
            lblMod = TopLabel(top, 29);
            lblServer = TopLabel(top, 50);

            TabControl tabs = new TabControl();
            tabs.SetBounds(8, 82, 744, 490);
            Controls.Add(tabs);
            tabs.TabPages.Add(BuildPlayTab());
            tabs.TabPages.Add(BuildHostTab());
            tabs.TabPages.Add(BuildSetupTab());
            tabs.TabPages.Add(BuildAboutTab());

            gameDir = Game.Find(cfg.GameDir);
            if (gameDir != cfg.GameDir) { cfg.GameDir = gameDir; cfg.Save(); }
            LoadGameState();

            timer.Interval = 3000;
            timer.Tick += delegate { RefreshStatus(); };
        }

        // The shell calls these when the Metal Fatigue page is shown, hidden, or the app closes.
        public void SetActive(bool on)
        {
            if (on) { timer.Start(); RefreshStatus(); }
            else timer.Stop();
        }

        public void Shutdown()
        {
            closing = true;
            timer.Stop();
            StopRelay();
        }

        Label TopLabel(Panel p, int y)
        {
            Label l = new Label();
            l.SetBounds(12, y, 736, 19);
            l.ForeColor = Color.White;
            l.AutoEllipsis = true;
            p.Controls.Add(l);
            return l;
        }

        static Label Note(Control parent, string text, int x, int y, int w, int h)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, h);
            l.ForeColor = SystemColors.GrayText;
            parent.Controls.Add(l);
            return l;
        }

        static Label Lbl(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y + 3, w, 18);
            parent.Controls.Add(l);
            return l;
        }

        static Button Btn(Control parent, string text, int x, int y, int w, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 28);
            b.Click += click;
            parent.Controls.Add(b);
            return b;
        }

        // ------------------------------------------------------------ Play tab

        TabPage BuildPlayTab()
        {
            TabPage t = new TabPage("Play");

            GroupBox g = new GroupBox();
            g.Text = "Where to play";
            g.SetBounds(10, 8, 714, 96);
            t.Controls.Add(g);
            Lbl(g, "Server address:", 12, 24, 110);
            txtServer = new TextBox();
            txtServer.SetBounds(126, 24, 200, 23);
            txtServer.Text = cfg.Server;
            g.Controls.Add(txtServer);
            Lbl(g, "Private room code:", 350, 24, 120);
            txtRoom = new TextBox();
            txtRoom.SetBounds(474, 24, 130, 23);
            txtRoom.MaxLength = 32;
            txtRoom.Text = cfg.Room;
            g.Controls.Add(txtRoom);
            Btn(g, "Apply", 616, 22, 84, delegate { ApplyServer(); });
            Note(g, "Server: the address of whoever runs the server (127.0.0.1 is this PC). "
                + "Room code: optional. Players who enter the same code only see each other's games; leave it blank for the public list.",
                12, 54, 690, 34);

            lvGames = new ListView();
            lvGames.SetBounds(10, 112, 714, 236);
            lvGames.View = View.Details;
            lvGames.FullRowSelect = true;
            lvGames.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lvGames.Columns.Add("Game", 190);
            lvGames.Columns.Add("Players", 70);
            lvGames.Columns.Add("Status", 190);
            lvGames.Columns.Add("Who's in it", 240);
            t.Controls.Add(lvGames);

            lblOnline = new Label();
            lblOnline.SetBounds(10, 354, 714, 20);
            t.Controls.Add(lblOnline);

            btnPlay = Btn(t, "Play Metal Fatigue", 10, 380, 180, delegate { LaunchSteam(); });
            btnPlay.Font = new Font(Font, FontStyle.Bold);
            btnSecond = Btn(t, "Start a second copy (testing)", 200, 380, 210, delegate { LaunchDirect(); });
            Btn(t, "Refresh", 640, 380, 84, delegate { RefreshStatus(); });

            Note(t, "To host or join: press Play, open Multiplayer in the game, then Host a game or pick one from the list and press Join. "
                + "Leave the game's Search IP box blank; this app already told the game which server to use.",
                10, 416, 714, 36);
            return t;
        }

        void ApplyServer()
        {
            string s = txtServer.Text.Trim();
            if (s.Length == 0) s = "127.0.0.1:27900";
            if (s.IndexOf(':') < 0) s += ":27900";
            string room = txtRoom.Text.Trim();
            foreach (char ch in room)
            {
                if (ch < 33 || ch > 126)
                {
                    MessageBox.Show("Room codes can use letters, numbers and symbols, without spaces.", "MFNet");
                    return;
                }
            }
            cfg.Server = s; cfg.Room = room; cfg.Save();
            txtServer.Text = s;
            WriteIniIfInstalled();
            RefreshStatus();
        }

        void WriteIniIfInstalled()
        {
            if (!Game.IsGameDir(gameDir)) return;
            int st = Game.ModState(gameDir);
            if (st != 1 && st != 2) return;
            try { Game.WriteIni(gameDir, cfg); }
            catch (Exception ex) { MessageBox.Show("Could not save the game's online settings: " + ex.Message, "MFNet"); }
        }

        void RefreshStatus()
        {
            if (refreshing || closing) return;
            refreshing = true;
            string host = cfg.Host, room = cfg.Room; int port = cfg.Port;
            ThreadPool.QueueUserWorkItem(delegate
            {
                StatusResult r = Relay.Status(host, port, room);
                try
                {
                    if (!closing && IsHandleCreated) BeginInvoke((MethodInvoker)delegate { ShowStatus(r); });
                }
                catch (Exception) { }
                refreshing = false;
            });
        }

        void ShowStatus(StatusResult r)
        {
            if (closing) return;
            lvGames.BeginUpdate();
            lvGames.Items.Clear();
            if (r.Ok)
            {
                foreach (GameInfo g in r.Games)
                {
                    string status;
                    if ((g.Flags & 0x20) != 0) status = "In progress";
                    else if (g.Players >= g.Max) status = "Full";
                    else status = "Open";
                    if (myFiles.Length > 0 && g.Files != myFiles) status += " - different game files";
                    ListViewItem it = new ListViewItem(g.Name);
                    it.SubItems.Add(g.Players + " / " + g.Max);
                    it.SubItems.Add(status);
                    it.SubItems.Add(string.Join(", ", g.Who.ToArray()));
                    if (status != "Open") it.ForeColor = SystemColors.GrayText;
                    lvGames.Items.Add(it);
                }
                string room = cfg.Room.Length > 0 ? "private room" : "public list";
                lblServer.Text = "Server:  " + cfg.Server + "  -  connected (" + room + ")";
                lblOnline.Text = r.Games.Count == 0
                    ? "No games open right now. " + r.Online + " player(s) connected."
                    : r.Games.Count + " game(s), " + r.Online + " player(s) connected.";
            }
            else
            {
                lblServer.Text = "Server:  " + cfg.Server + "  -  not reachable";
                lblOnline.Text = r.Error + " If you are the host, start the server on the \"Host a server\" tab.";
            }
            lvGames.EndUpdate();
        }

        void LaunchSteam()
        {
            if (!ReadyToPlay()) return;
            try { Process.Start("steam://rungameid/888040"); }
            catch (Exception ex) { MessageBox.Show("Could not start the game through Steam: " + ex.Message, "MFNet"); }
        }

        void LaunchDirect()
        {
            if (!ReadyToPlay()) return;
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(Path.Combine(gameDir, Game.Exe));
                si.WorkingDirectory = gameDir;
                si.UseShellExecute = true;
                Process.Start(si);
            }
            catch (Exception ex) { MessageBox.Show("Could not start the game: " + ex.Message, "MFNet"); }
        }

        bool ReadyToPlay()
        {
            if (!Game.IsGameDir(gameDir))
            {
                MessageBox.Show("Metal Fatigue was not found. Set its folder on the Setup tab.", "MFNet");
                return false;
            }
            int st = Game.ModState(gameDir);
            if (st == 0 || st == 3)
            {
                MessageBox.Show("The online mod is not installed in the game yet. Use \"Install or update\" on the Setup tab.", "MFNet");
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------ Host tab

        TabPage BuildHostTab()
        {
            TabPage t = new TabPage("Host a server");

            Note(t, "One person runs the server and everyone else points their Server address at it. "
                + "It keeps the list of games and passes the game traffic between players. Leave it running while you play.",
                10, 10, 714, 34).ForeColor = SystemColors.ControlText;

            rbLocal = new RadioButton();
            rbLocal.Text = "Only this PC (for testing with two copies of the game)";
            rbLocal.SetBounds(14, 50, 420, 22);
            rbLocal.Checked = true;
            t.Controls.Add(rbLocal);
            rbPublic = new RadioButton();
            rbPublic.Text = "Friends on my home network or over the internet";
            rbPublic.SetBounds(14, 74, 420, 22);
            t.Controls.Add(rbPublic);

            Lbl(t, "Port:", 470, 50, 40);
            numPort = new NumericUpDown();
            numPort.SetBounds(512, 50, 80, 23);
            numPort.Minimum = 1024; numPort.Maximum = 65535; numPort.Value = 27900;
            t.Controls.Add(numPort);

            btnRelay = Btn(t, "Start server", 14, 104, 150, delegate { ToggleRelay(); });
            btnRelay.Font = new Font(Font, FontStyle.Bold);
            lblRelay = new Label();
            lblRelay.SetBounds(176, 110, 548, 20);
            lblRelay.Text = "Server is stopped.";
            t.Controls.Add(lblRelay);

            txtRelayLog = new TextBox();
            txtRelayLog.SetBounds(10, 140, 714, 190);
            txtRelayLog.Multiline = true;
            txtRelayLog.ReadOnly = true;
            txtRelayLog.ScrollBars = ScrollBars.Vertical;
            txtRelayLog.Font = new Font("Consolas", 8.5f);
            txtRelayLog.BackColor = Color.White;
            t.Controls.Add(txtRelayLog);

            GroupBox g = new GroupBox();
            g.Text = "For friends over the internet";
            g.SetBounds(10, 338, 714, 112);
            t.Controls.Add(g);
            Btn(g, "Find my public address", 12, 22, 170, delegate { FindPublicIp(); });
            txtPublicIp = new TextBox();
            txtPublicIp.SetBounds(192, 24, 220, 23);
            txtPublicIp.ReadOnly = true;
            g.Controls.Add(txtPublicIp);
            Btn(g, "Copy", 420, 22, 70, delegate
            {
                if (txtPublicIp.Text.Length > 0 && txtPublicIp.Text.IndexOf(' ') < 0) Clipboard.SetText(txtPublicIp.Text);
            });
            Note(g, "Friends put this address in their Server address box. Your router must forward the TCP port above to this PC, "
                + "and Windows may ask to allow the server through the firewall. The address lookup asks api.ipify.org. "
                + "This test server is not encrypted and has no password of its own: use a room code and stop it when you are done.",
                12, 54, 690, 52);
            return t;
        }

        // ---- used by the main window: one "host" switch runs the lobby and this relay together
        public bool RelayRunning { get { return relayProc != null; } }
        public int RelayPort { get { return (int)numPort.Value; } }

        public void StartRelayForLobby()
        {
            if (relayProc != null) return;
            rbPublic.Checked = true;
            ToggleRelay();
        }

        public void StopRelayForLobby()
        {
            if (relayProc != null) StopRelay();
        }

        // join from an invite card: use that server, then start the game
        public void JoinRelay(string address)
        {
            if (address.Length > 0 && cfg.Server != address)
            {
                txtServer.Text = address;
                Revival.Config.Set("mf.autoserver", address);
                ApplyServer();
            }
            LaunchSteam();
        }

        public string CurrentServer { get { return cfg.Server; } }

        // point the game at the relay on the lobby host, unless the player chose a server by hand
        public void UseLobbyRelay(string address)
        {
            string auto = Revival.Config.Get("mf.autoserver", "");
            if (cfg.Server != "127.0.0.1:27900" && cfg.Server != auto) return;
            if (cfg.Server == address) return;
            txtServer.Text = address;
            Revival.Config.Set("mf.autoserver", address);
            ApplyServer();
        }

        void ToggleRelay()
        {
            if (relayProc != null) { StopRelay(); return; }
            try
            {
                string exe = Path.Combine(Settings.DataDir, "mfnet-relay.exe");
                Revival.LobbyHost.KillOrphans("mfnet-relay", exe);
                byte[] want = Game.Resource("mfnet-relay.exe");
                try { File.WriteAllBytes(exe, want); }
                catch (IOException) { if (!File.Exists(exe)) throw; }   // an older copy is still running

                string listen = (rbPublic.Checked ? "0.0.0.0" : "127.0.0.1") + ":" + (int)numPort.Value;
                ProcessStartInfo si = new ProcessStartInfo(exe, "-listen " + listen);
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardError = true;
                si.RedirectStandardOutput = true;
                Process p = new Process();
                p.StartInfo = si;
                p.EnableRaisingEvents = true;
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { RelayLine(e.Data); };
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { RelayLine(e.Data); };
                p.Exited += delegate { RelayExited(p); };
                txtRelayLog.Clear();
                p.Start();
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                relayProc = p;
                btnRelay.Text = "Stop server";
                rbLocal.Enabled = rbPublic.Enabled = numPort.Enabled = false;
                lblRelay.Text = "Server is running on " + listen + (rbPublic.Checked ? " (open to other PCs)." : " (this PC only).");
                RefreshStatus();
            }
            catch (Exception ex)
            {
                relayProc = null;
                MessageBox.Show("Could not start the server: " + ex.Message, "MFNet");
            }
        }

        void RelayLine(string line)
        {
            if (line == null || closing) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (txtRelayLog.TextLength > 200000) txtRelayLog.Clear();
                    txtRelayLog.AppendText(line + "\r\n");
                });
            }
            catch (Exception) { }
        }

        void RelayExited(Process p)
        {
            if (closing) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (relayProc != p) return;
                    relayProc = null;
                    btnRelay.Text = "Start server";
                    rbLocal.Enabled = rbPublic.Enabled = numPort.Enabled = true;
                    lblRelay.Text = "Server stopped. If it stopped by itself, the port may already be in use.";
                });
            }
            catch (Exception) { }
        }

        void StopRelay()
        {
            Process p = relayProc;
            relayProc = null;
            if (p != null)
            {
                try { if (!p.HasExited) p.Kill(); }
                catch (Exception) { }
            }
            if (closing) return;
            btnRelay.Text = "Start server";
            rbLocal.Enabled = rbPublic.Enabled = numPort.Enabled = true;
            lblRelay.Text = "Server is stopped.";
        }

        void FindPublicIp()
        {
            txtPublicIp.Text = "Looking it up...";
            int port = (int)numPort.Value;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result;
                try
                {
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                    using (WebClient wc = new WebClient())
                        result = Revival.Invite.PublicIp();
                    IPAddress ip;
                    if (!IPAddress.TryParse(result, out ip)) result = "Lookup failed";
                    else if (port != 27900) result += ":" + port;
                }
                catch (Exception) { result = "Lookup failed"; }
                try { if (!closing) BeginInvoke((MethodInvoker)delegate { txtPublicIp.Text = result; }); }
                catch (Exception) { }
            });
        }

        // ------------------------------------------------------------ Setup tab

        TabPage BuildSetupTab()
        {
            TabPage t = new TabPage("Setup");

            GroupBox g1 = new GroupBox();
            g1.Text = "Game folder";
            g1.SetBounds(10, 8, 714, 62);
            t.Controls.Add(g1);
            txtGameDir = new TextBox();
            txtGameDir.SetBounds(12, 24, 480, 23);
            txtGameDir.ReadOnly = true;
            g1.Controls.Add(txtGameDir);
            Btn(g1, "Browse...", 500, 22, 90, delegate { BrowseGame(); });
            Btn(g1, "Open folder", 598, 22, 102, delegate
            {
                if (Game.IsGameDir(gameDir)) Process.Start("explorer.exe", "\"" + gameDir + "\"");
            });

            GroupBox g2 = new GroupBox();
            g2.Text = "Online mod";
            g2.SetBounds(10, 78, 714, 116);
            t.Controls.Add(g2);
            lblModState = new Label();
            lblModState.SetBounds(12, 24, 690, 20);
            g2.Controls.Add(lblModState);
            btnInstall = Btn(g2, "Install or update", 12, 48, 150, delegate { InstallMod(); });
            btnRemove = Btn(g2, "Remove", 170, 48, 100, delegate { RemoveMod(); });
            Btn(g2, "Open the game's online log", 500, 48, 200, delegate { OpenLog(); });
            chkBackground = new CheckBox();
            chkBackground.Text = "Keep the game running when I Alt-Tab away (stops you being dropped)";
            chkBackground.SetBounds(12, 84, 500, 22);
            chkBackground.Checked = cfg.RunInBackground;
            chkBackground.CheckedChanged += delegate
            {
                cfg.RunInBackground = chkBackground.Checked; cfg.Save(); WriteIniIfInstalled();
            };
            g2.Controls.Add(chkBackground);

            GroupBox g3 = new GroupBox();
            g3.Text = "Display mode";
            g3.SetBounds(10, 202, 714, 138);
            t.Controls.Add(g3);
            rbFull = new RadioButton();
            rbFull.Text = "Fullscreen";
            rbFull.SetBounds(14, 24, 300, 22);
            g3.Controls.Add(rbFull);
            rbBorderless = new RadioButton();
            rbBorderless.Text = "Windowed fullscreen (best for online play and Alt-Tab)";
            rbBorderless.SetBounds(14, 48, 420, 22);
            g3.Controls.Add(rbBorderless);
            rbWindowed = new RadioButton();
            rbWindowed.Text = "Windowed";
            rbWindowed.SetBounds(14, 72, 300, 22);
            g3.Controls.Add(rbWindowed);
            btnDisplay = Btn(g3, "Apply display mode", 14, 100, 160, delegate { ApplyDisplay(); });
            lblDisplay = Note(g3, "", 186, 104, 516, 28);

            Note(t, "Changes here take effect the next time the game starts. Close the game before installing, removing or changing display mode. "
                + "The mod adds two files to the game folder (DINPUT.dll and mfnet.ini) and does not change MFatigue.exe.",
                10, 350, 714, 40);
            return t;
        }

        void LoadGameState()
        {
            bool ok = Game.IsGameDir(gameDir);
            txtGameDir.Text = ok ? gameDir : "(not found - press Browse and pick the folder that holds MFatigue.exe)";
            lblGame.Text = ok ? "Game:  " + gameDir : "Game:  not found - set the folder on the Setup tab";
            myFiles = ok ? Game.Fingerprint(gameDir) : "";

            int st = ok ? Game.ModState(gameDir) : 0;
            string mod;
            if (!ok) mod = "Set the game folder first.";
            else if (st == 1) mod = "Installed, version " + Mf.Version + ".";
            else if (st == 2) mod = "An older or different version is installed. Press \"Install or update\".";
            else if (st == 3) mod = "The game folder has a DINPUT.dll from something else. Installing would replace it.";
            else mod = "Not installed.";
            lblModState.Text = mod;
            lblMod.Text = "Online mod:  " + mod;
            btnInstall.Enabled = ok;
            btnRemove.Enabled = ok && (st == 1 || st == 2);

            int dm = ok ? Game.DisplayMode(gameDir) : -1;
            rbFull.Enabled = rbBorderless.Enabled = rbWindowed.Enabled = btnDisplay.Enabled = dm >= 0;
            rbFull.Checked = dm == 0; rbBorderless.Checked = dm == 1; rbWindowed.Checked = dm == 2;
            lblDisplay.Text = dm >= 0
                ? "Uses the graphics wrapper that came with the widescreen patch."
                : "Needs the widescreen patch (its dgVoodoo settings file was not found in the game folder).";
            btnPlay.Enabled = btnSecond.Enabled = ok;
            lblServer.Text = "Server:  " + cfg.Server;
        }

        void BrowseGame()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Pick the Metal Fatigue folder (the one that holds MFatigue.exe).";
                if (Game.IsGameDir(gameDir)) d.SelectedPath = gameDir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!Game.IsGameDir(d.SelectedPath))
                {
                    MessageBox.Show("That folder does not contain MFatigue.exe.", "MFNet");
                    return;
                }
                gameDir = d.SelectedPath; cfg.GameDir = gameDir; cfg.Save();
                LoadGameState();
            }
        }

        bool GameClosedOrWarn()
        {
            if (!Game.IsRunning()) return true;
            MessageBox.Show("Close Metal Fatigue first (every copy), then try again.", "MFNet");
            return false;
        }

        void InstallMod()
        {
            if (!Game.IsGameDir(gameDir) || !GameClosedOrWarn()) return;
            if (Game.ModState(gameDir) == 3)
            {
                if (MessageBox.Show("The game folder already has a DINPUT.dll that is not from MFNet. Replace it?", "MFNet",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try { File.Copy(Path.Combine(gameDir, Game.ModDll), Path.Combine(gameDir, Game.ModDll + ".before-mfnet"), true); }
                catch (Exception) { }
            }
            try { Game.InstallMod(gameDir, cfg); }
            catch (Exception ex) { MessageBox.Show("Could not install: " + ex.Message, "MFNet"); }
            LoadGameState();
        }

        void RemoveMod()
        {
            if (!Game.IsGameDir(gameDir) || !GameClosedOrWarn()) return;
            try { Game.RemoveMod(gameDir); }
            catch (Exception ex) { MessageBox.Show("Could not remove: " + ex.Message, "MFNet"); }
            LoadGameState();
        }

        void OpenLog()
        {
            string p = Path.Combine(gameDir, "mfnet.log");
            if (File.Exists(p)) Process.Start("notepad.exe", "\"" + p + "\"");
            else MessageBox.Show("There is no log yet. It appears after the game has been started with the mod installed.", "MFNet");
        }

        void ApplyDisplay()
        {
            if (!Game.IsGameDir(gameDir) || !GameClosedOrWarn()) return;
            int mode = rbWindowed.Checked ? 2 : (rbBorderless.Checked ? 1 : 0);
            try
            {
                Game.SetDisplayMode(gameDir, mode);
                lblDisplay.Text = "Saved. It applies the next time the game starts.";
            }
            catch (Exception ex) { MessageBox.Show("Could not change the display mode: " + ex.Message, "MFNet"); }
        }

        // ------------------------------------------------------------ About tab

        TabPage BuildAboutTab()
        {
            TabPage t = new TabPage("About");
            TextBox b = new TextBox();
            b.SetBounds(10, 10, 714, 440);
            b.Multiline = true;
            b.ReadOnly = true;
            b.BorderStyle = BorderStyle.None;
            b.BackColor = SystemColors.Window;
            b.TabStop = false;
            b.Text =
                "MFNet " + Mf.Version + "\r\n\r\n"
                + "Brings online play back to Metal Fatigue (2000). The game's original matchmaking service, Mplayer, "
                + "shut down in 2001. MFNet replaces the game's network layer with one that talks to a small relay server, "
                + "so players connect without VPNs, and only the person running the server forwards a port.\r\n\r\n"
                + "How to play with friends\r\n"
                + "  1. Everyone: Setup tab, Install or update.\r\n"
                + "  2. One person: Host a server tab, start the server, and send friends the address.\r\n"
                + "  3. Everyone: Play tab, enter that address (and a room code if you want a private list), Apply.\r\n"
                + "  4. Press Play. In the game: Multiplayer, then Host or Join.\r\n\r\n"
                + "Good to know\r\n"
                + "  - Every player needs their own copy of the game and identical game files (the same patch options). "
                + "Games hosted from different files are marked in the list and cannot be joined.\r\n"
                + "  - This is a test version. Traffic is not encrypted yet.\r\n"
                + "  - The game's own online log is mfnet.log in the game folder.\r\n\r\n"
                + "Not affiliated with the game's publishers. Free software under the GNU GPL, version 3 or later; "
                + "the source code comes with the app.";
            t.Controls.Add(b);
            return t;
        }
    }
}
