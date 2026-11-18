// GameSpy Revival - the main window, laid out like Discord: groups, channels, chat, members.
// Free software under the GNU GPL, version 3 or (at your option) any later version. No warranty.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Revival
{
    // the window's own top bar: back / forward, where you are, minimize / maximize / close
    class TitleBar : Control
    {
        readonly Shell shell;
        public string Where = "GameSpy Revival", Initials = "GS";
        public bool CanBack, CanForward;
        public event Action Back, Forward;
        Rectangle rBack, rFwd, rMin, rMax, rClose;
        int hot = -1;
        static readonly Font FIcon = new Font("Segoe MDL2 Assets", 8f);

        public TitleBar(Shell s)
        {
            shell = s;
            Height = 30;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Look.Rail;
        }

        void Layout2()
        {
            rBack = new Rectangle(8, 4, 24, 22);
            rFwd = new Rectangle(34, 4, 24, 22);
            rClose = new Rectangle(Width - 46, 0, 46, Height);
            rMax = new Rectangle(Width - 92, 0, 46, Height);
            rMin = new Rectangle(Width - 138, 0, 46, Height);
        }

        int Hit(Point p)
        {
            Layout2();
            if (rBack.Contains(p)) return 0;
            if (rFwd.Contains(p)) return 1;
            if (rMin.Contains(p)) return 2;
            if (rMax.Contains(p)) return 3;
            if (rClose.Contains(p)) return 4;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e) { int h = Hit(e.Location); if (h != hot) { hot = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Hit(e.Location) < 0)
            {
                if (e.Clicks == 2) shell.ToggleMaximize();
                else shell.DragWindow();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            switch (Hit(e.Location))
            {
                case 0: if (CanBack && Back != null) Back(); break;
                case 1: if (CanForward && Forward != null) Forward(); break;
                case 2: shell.WindowState = FormWindowState.Minimized; break;
                case 3: shell.ToggleMaximize(); break;
                case 4: shell.Close(); break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Layout2();
            g.Clear(Look.Rail);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Chevron(g, rBack, true, CanBack, hot == 0);
            Chevron(g, rFwd, false, CanForward, hot == 1);
            using (Font f = new Font("Segoe UI Semibold", 9f))
            {
                Size ts = TextRenderer.MeasureText(Where, f);
                int x = (Width - ts.Width - 24) / 2;
                Rectangle sq = new Rectangle(x, 7, 16, 16);
                Look.FillRound(g, sq, 4, Look.AvatarColor(Where));
                using (Font fi = new Font("Segoe UI", 6.5f, FontStyle.Bold))
                    TextRenderer.DrawText(g, Initials, fi, sq, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Where, f, new Point(x + 22, 7), Look.Soft, TextFormatFlags.NoPrefix);
            }
            Box(g, rMin, "\uE921", hot == 2, false);
            Box(g, rMax, shell.WindowState == FormWindowState.Maximized ? "\uE923" : "\uE922", hot == 3, false);
            Box(g, rClose, "\uE8BB", hot == 4, true);
        }

        static void Chevron(Graphics g, Rectangle r, bool left, bool enabled, bool hot)
        {
            if (hot && enabled) Look.FillRound(g, r, 4, Color.FromArgb(26, 36, 51));
            Color c = enabled ? Look.Soft : Color.FromArgb(70, 80, 96);
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (Pen p = new Pen(c, 1.8f))
            {
                if (left) { g.DrawLine(p, cx + 3, cy - 5, cx - 2, cy); g.DrawLine(p, cx - 2, cy, cx + 3, cy + 5); }
                else { g.DrawLine(p, cx - 3, cy - 5, cx + 2, cy); g.DrawLine(p, cx + 2, cy, cx - 3, cy + 5); }
            }
        }

        static void Box(Graphics g, Rectangle r, string glyph, bool hot, bool close)
        {
            if (hot) using (SolidBrush b = new SolidBrush(close ? Color.FromArgb(196, 43, 28) : Color.FromArgb(26, 36, 51))) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, glyph, FIcon, r, hot && close ? Color.White : Look.Soft, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // bottom of the channel list: voice status, you, mute / deafen / settings
    class UserPanel : Control
    {
        public string Name1 = "", Sub = "", VoiceRoom = "", Group = "", Presence = "online";
        public event Action<Point> AvatarClicked;
        Rectangle rMe;
        public bool SignedIn, Muted, Deafened, Speaking;
        public event Action MuteClicked, DeafClicked, SettingsClicked, LeaveClicked, SignInClicked;
        Rectangle rMute, rDeaf, rSet, rLeave, rSign;
        int hot = -1;

        public UserPanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); BackColor = Color.FromArgb(12, 18, 25); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = rMute.Contains(e.Location) ? 0 : rDeaf.Contains(e.Location) ? 1 : rSet.Contains(e.Location) ? 2
                : rLeave.Contains(e.Location) ? 3 : rSign.Contains(e.Location) ? 4 : rMe.Contains(e.Location) ? 5 : -1;
            if (h != hot) { hot = h; Invalidate(); }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (rMe.Contains(e.Location) && AvatarClicked != null) { AvatarClicked(PointToScreen(new Point(rMe.X, rMe.Y))); return; }
            if (rMute.Contains(e.Location) && MuteClicked != null) MuteClicked();
            else if (rDeaf.Contains(e.Location) && DeafClicked != null) DeafClicked();
            else if (rSet.Contains(e.Location) && SettingsClicked != null) SettingsClicked();
            else if (rLeave.Contains(e.Location) && LeaveClicked != null) LeaveClicked();
            else if (rSign.Contains(e.Location) && SignInClicked != null) SignInClicked();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen lp = new Pen(Look.Line)) g.DrawLine(lp, 0, 0, Width, 0);
            TextFormatFlags tf = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            rLeave = rSign = rMe = Rectangle.Empty;
            int y = 0;
            if (VoiceRoom.Length > 0)
            {
                using (Font fb = new Font("Segoe UI Semibold", 9.5f))
                    TextRenderer.DrawText(g, "Voice Connected", fb, new Rectangle(12, 8, Width - 60, 18), Look.Green, tf);
                using (Font fs = new Font("Segoe UI", 8.25f))
                    TextRenderer.DrawText(g, VoiceRoom + " / " + Group, fs, new Rectangle(12, 26, Width - 60, 16), Look.Muted, tf);
                rLeave = new Rectangle(Width - 44, 8, 32, 32);
                Look.FillRound(g, rLeave, 6, hot == 3 ? Color.FromArgb(70, 34, 38) : Color.FromArgb(28, 38, 54));
                using (Pen p = new Pen(Look.Red, 2f))   // hang up
                {
                    g.DrawArc(p, rLeave.X + 8, rLeave.Y + 12, 16, 12, 200, 140);
                    g.DrawLine(p, rLeave.X + 9, rLeave.Y + 19, rLeave.X + 12, rLeave.Y + 21);
                    g.DrawLine(p, rLeave.Right - 9, rLeave.Y + 19, rLeave.Right - 12, rLeave.Y + 21);
                }
                y = 48;
                using (Pen lp = new Pen(Look.Line)) g.DrawLine(lp, 8, y, Width - 8, y);
            }
            if (!SignedIn)
            {
                rSign = new Rectangle(12, y + 12, Width - 24 - 40, 34);
                Look.FillRound(g, rSign, 6, hot == 4 ? Color.FromArgb(36, 150, 96) : Look.GreenBtn);
                using (Font fb = new Font("Segoe UI Semibold", 10f))
                    TextRenderer.DrawText(g, "Sign in", fb, rSign, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                rMe = new Rectangle(4, y + 6, Width - 124, 46);
                if (hot == 5) Look.FillRound(g, rMe, 6, Color.FromArgb(26, 36, 51));
                Rectangle av = new Rectangle(10, y + 12, 34, 34);
                Look.Avatar(g, Name1, av, Speaking);
                Look.Dot(g, av, Look.PresenceColor(Presence == "invisible" ? "offline" : Presence), hot == 5 ? Color.FromArgb(26, 36, 51) : BackColor);
                using (Font fn = new Font("Segoe UI Semibold", 9.5f))
                    TextRenderer.DrawText(g, Name1, fn, new Rectangle(50, y + 12, Width - 170, 18), Color.White, tf);
                using (Font fs = new Font("Segoe UI", 8f))
                    TextRenderer.DrawText(g, Sub, fs, new Rectangle(50, y + 30, Width - 170, 16), Look.Muted, tf);
            }
            int bx = Width - 112;
            rMute = SignedIn ? new Rectangle(bx, y + 13, 32, 32) : Rectangle.Empty;
            rDeaf = SignedIn ? new Rectangle(bx + 36, y + 13, 32, 32) : Rectangle.Empty;
            rSet = new Rectangle(Width - 40, y + 13, 32, 32);
            if (SignedIn)
            {
                Icon(g, rMute, 0, Muted || Deafened, hot == 0);
                Icon(g, rDeaf, 1, Deafened, hot == 1);
            }
            Icon(g, rSet, 2, false, hot == 2);
        }

        static void Icon(Graphics g, Rectangle r, int kind, bool off, bool hover)
        {
            if (hover) Look.FillRound(g, r, 6, Color.FromArgb(28, 38, 54));
            Color c = off ? Look.Red : Look.Soft;
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (Pen p = new Pen(c, 1.8f))
            {
                if (kind == 0)   // microphone
                {
                    g.DrawPath(p, Look.Round(new Rectangle(cx - 4, cy - 10, 8, 13), 4));
                    g.DrawArc(p, cx - 7, cy - 6, 14, 12, 0, 180);
                    g.DrawLine(p, cx, cy + 6, cx, cy + 10);
                }
                else if (kind == 1)   // headphones
                {
                    g.DrawArc(p, cx - 8, cy - 9, 16, 16, 180, 180);
                    g.DrawPath(p, Look.Round(new Rectangle(cx - 9, cy, 5, 8), 2));
                    g.DrawPath(p, Look.Round(new Rectangle(cx + 4, cy, 5, 8), 2));
                }
                else   // gear
                {
                    g.DrawEllipse(p, cx - 4, cy - 4, 8, 8);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        g.DrawLine(p, cx + (float)(Math.Cos(a) * 6), cy + (float)(Math.Sin(a) * 6), cx + (float)(Math.Cos(a) * 9), cy + (float)(Math.Sin(a) * 9));
                    }
                }
                if (off) using (Pen sp = new Pen(Look.Red, 2f)) g.DrawLine(sp, r.X + 7, r.Y + 7, r.Right - 7, r.Bottom - 7);
            }
        }
    }

    // message box at the bottom of a conversation, with reply and edit modes
    class Composer : Panel
    {
        public readonly TextBox Box = new TextBox();
        readonly Button plus = new Button(), emoji = new Button();
        readonly Label bar = new Label();
        readonly Button barClose = new Button();
        public event Action<string> Submit;
        public event Action Typed, PlusClicked, EditLast;
        public event Action<Point> EmojiClicked;
        public ChatMsg ReplyTo, Editing;
        DateTime lastTyped = DateTime.MinValue;
        string placeholder = "";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        public Composer()
        {
            BackColor = Look.Main;
            Height = 64;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Flat(plus, "+", new Font("Segoe UI", 13f));
            plus.BackColor = Color.FromArgb(38, 50, 68);
            plus.Click += delegate { if (PlusClicked != null) PlusClicked(); };
            Controls.Add(plus);
            Flat(emoji, "\uE76E", new Font("Segoe MDL2 Assets", 13f));
            emoji.BackColor = Look.Card;
            emoji.Click += delegate { if (EmojiClicked != null) EmojiClicked(emoji.PointToScreen(new Point(-280, -170))); };
            new ToolTip().SetToolTip(emoji, "Emoji");
            Controls.Add(emoji);
            bar.BackColor = Color.FromArgb(17, 24, 35);
            bar.ForeColor = Look.Soft;
            bar.Font = new Font("Segoe UI", 9f);
            bar.TextAlign = ContentAlignment.MiddleLeft;
            bar.Padding = new Padding(12, 0, 0, 0);
            bar.Visible = false;
            Controls.Add(bar);
            Flat(barClose, "\uE711", new Font("Segoe MDL2 Assets", 8f));
            barClose.BackColor = Color.FromArgb(17, 24, 35);
            barClose.Visible = false;
            barClose.Click += delegate { Cancel(); };
            Controls.Add(barClose);
            barClose.BringToFront();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Look.Card;
            Box.ForeColor = Look.Text;
            Box.Font = new Font("Segoe UI", 10.5f);
            Box.MaxLength = 400;
            Box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && (ReplyTo != null || Editing != null)) { Cancel(); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.Up && Box.Text.Length == 0 && Editing == null) { if (EditLast != null) EditLast(); e.SuppressKeyPress = true; return; }
                if (e.KeyCode != Keys.Enter || e.Shift) return;
                e.SuppressKeyPress = true;
                string t = Box.Text.Trim();
                if (t.Length == 0) return;
                if (Submit != null) Submit(t);
                Box.Text = "";
                Cancel();
            };
            Box.TextChanged += delegate
            {
                if (Box.Text.Length == 0 || Editing != null || (DateTime.Now - lastTyped).TotalSeconds < 3) return;
                lastTyped = DateTime.Now;
                if (Typed != null) Typed();
            };
            Controls.Add(Box);
        }

        static void Flat(Button b, string text, Font f)
        {
            b.Text = text;
            b.Font = f;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Look.Hover;
            b.ForeColor = Look.Soft;
            b.Cursor = Cursors.Hand;
        }

        public void BeginReply(ChatMsg m)
        {
            Editing = null;
            ReplyTo = m;
            bar.Text = "Replying to " + m.User;
            ShowBar(true);
            Box.Focus();
        }

        public void BeginEdit(ChatMsg m)
        {
            ReplyTo = null;
            Editing = m;
            bar.Text = "Editing message  \u00b7  Esc to cancel, Enter to save";
            ShowBar(true);
            Box.Text = m.Text;
            Box.SelectionStart = Box.Text.Length;
            Box.Focus();
        }

        public void Cancel()
        {
            bool wasEdit = Editing != null;
            ReplyTo = null; Editing = null;
            ShowBar(false);
            if (wasEdit) Box.Text = "";
        }

        void ShowBar(bool on)
        {
            bar.Visible = barClose.Visible = on;
            Height = on ? 94 : 64;
            PerformLayout();
            Invalidate();
        }

        public void Insert(string text)
        {
            int at = Box.SelectionStart;
            Box.Text = Box.Text.Insert(at, text);
            Box.SelectionStart = at + text.Length;
            Box.Focus();
        }

        public void SetPlaceholder(string t)
        {
            placeholder = t;
            if (Box.IsHandleCreated) SendMessage(Box.Handle, 0x1501, (IntPtr)1, t);
            else Box.HandleCreated += delegate { SendMessage(Box.Handle, 0x1501, (IntPtr)1, placeholder); };
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int top = bar.Visible ? 30 : 0;
            bar.SetBounds(16, 6, Width - 32, 30);
            barClose.SetBounds(Width - 16 - 30, 8, 26, 26);
            plus.SetBounds(28, top + 18, 28, 28);
            emoji.SetBounds(Width - 16 - 40, top + 16, 32, 32);
            Box.SetBounds(66, top + 22, Width - 66 - 70, 22);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int top = bar.Visible ? 30 : 0;
            Look.FillRound(e.Graphics, new Rectangle(16, top + 10, Width - 32, 44), 10, Look.Card);
        }
    }

    // ---------------------------------------------------------------- the window

    class Shell : Form
    {
        const int RailW = 72, SideW = 240, MainW = 752, MembersW = 240, H = 800, HeadH = 48;

        LobbyClient lobby;
        VoiceEngine voice;
        GroupRail rail;
        Panel side, mainPanel, mainHead, content, membersPanel, convo;
        Label lblGroup, lblGroupSub, lblTitle, lblTopic, lblTyping;
        Button btnHeadAction;
        NavList nav;
        UserPanel user;
        ChatView chat;
        Composer composer;
        PeopleList members, friendsList;
        Panel friendsPage;
        TextBox txtAddFriend;
        Label lblFriendsMsg;
        SettingsScreen settings;
        NotifyIcon tray;
        readonly Timer tick = new Timer(), presenceTimer = new Timer();

        readonly Dictionary<string, Control> pages = new Dictionary<string, Control>();
        readonly List<ArenaPanel> arenas = new List<ArenaPanel>();
        MFNet.MetalFatiguePanel mf;
        Panel mfHost;

        readonly Dictionary<string, List<ChatMsg>> logs = new Dictionary<string, List<ChatMsg>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> unread = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> mentions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Dictionary<string, DateTime>> typing = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> dmHistoryAsked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool homeMode;
        bool quitting;   // true when the tray menu's Quit closes the window for real
        bool gamesMode;   // the Games area (rail button under +), like Discord's Discover
        static bool IsGameKey(string k) { return k.StartsWith("game:") || k == "page:directory"; }
        string current = "";
        string relayAppliedFor = "";

        static readonly Dictionary<string, string> Topics = new Dictionary<string, string>
        {
            { "general", "Hang out and talk about anything" },
            { "looking-for-game", "Who's up for a game? Invite cards land here" },
            { "metal-fatigue", "Matches, mods and the long AFK games" },
            { "quake", "Quake III Arena and OpenArena" },
            { "unreal", "Unreal Tournament" },
            { "clips-and-screens", "Show off your best moments" },
        };

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags, uCount, dwTimeout; }
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO f);

        TitleBar title;
        Panel sideHead;
        TextBox txtSearch;
        Button btnMembers;
        readonly List<string> backStack = new List<string>(), fwdStack = new List<string>();
        bool navigatingHistory;
        DateTime voiceSince = DateTime.MinValue;
        bool autoIdle;
        readonly HashSet<string> ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [StructLayout(LayoutKind.Sequential)] struct POINTI { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECTI { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINTI ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECTI rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00020000 | 0x00080000;   // minimize box + system menu, so the taskbar can minimize us
                cp.ClassStyle |= 0x00020000;           // drop shadow
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, WM_GETMINMAXINFO = 0x24;
            if (m.Msg == WM_GETMINMAXINFO)
            {
                // maximize to the monitor's work area (keeps the taskbar visible, works with display scaling)
                IntPtr mon = MonitorFromWindow(Handle, 2);
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
                {
                    MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(MINMAXINFO));
                    mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                    mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                    mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                    mmi.ptMinTrackSize.X = MinimumSize.Width;
                    mmi.ptMinTrackSize.Y = MinimumSize.Height;
                    Marshal.StructureToPtr(mmi, m.LParam, false);
                    m.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal && (int)m.Result == 1)
            {
                int lp = m.LParam.ToInt32();
                Point p = PointToClient(new Point((short)(lp & 0xffff), (short)((lp >> 16) & 0xffff)));
                const int e = 6;
                bool l = p.X < e, r = p.X >= ClientSize.Width - e, t = p.Y < e, b = p.Y >= ClientSize.Height - e;
                if (t && l) m.Result = (IntPtr)13; else if (t && r) m.Result = (IntPtr)14;
                else if (b && l) m.Result = (IntPtr)16; else if (b && r) m.Result = (IntPtr)17;
                else if (l) m.Result = (IntPtr)10; else if (r) m.Result = (IntPtr)11;
                else if (t) m.Result = (IntPtr)12; else if (b) m.Result = (IntPtr)15;
            }
        }

        public void DragWindow()
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
        }

        public void ToggleMaximize()
        {
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            else WindowState = FormWindowState.Maximized;
            if (title != null) title.Invalidate();
        }

        public Shell()
        {
            Text = "GameSpy Revival";
            if (Config.Profile.Length > 0) Text += "  (profile " + Config.Profile + ")";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Look.Rail;
            Padding = new Padding(3);
            ClientSize = new Size(RailW + SideW + MainW + MembersW + 6, H + 30 + 6);
            MinimumSize = new Size(1080, 640);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            DoubleBuffered = true;
            Icon = TrayArt.Make(TrayArt.State.Idle, false, 32);
            foreach (string ig in Config.Get("ignored", "").Split('|')) if (ig.Length > 0) ignored.Add(ig);

            lobby = new LobbyClient(this);
            voice = new VoiceEngine();
            lobby.Voice = voice;
            voice.FrameReady += lobby.SendVoiceFrame;

            // docking order: the fill area first, then the edges, the title bar last (it spans the full width)
            BuildMain();
            BuildMembers();
            BuildSide();
            BuildRail();
            title = new TitleBar(this);
            title.Dock = DockStyle.Top;
            title.Back += delegate { GoBack(); };
            title.Forward += delegate { GoForward(); };
            Resize += delegate { if (title != null) title.Invalidate(); if (settings != null && settings.Visible) PlaceSettings(); };
            Controls.Add(title);
            BuildPages();
            settings = new SettingsScreen(this, lobby, voice, arenas);
            settings.Visible = false;
            settings.Dock = DockStyle.None;
            Controls.Add(settings);
            settings.BringToFront();
            membersPanel.Visible = Config.Get("ui.members", "1") == "1";

            tray = new NotifyIcon();
            tray.Icon = TrayArt.Make(TrayArt.State.Idle, false, 16);
            tray.Text = "GameSpy Revival";
            tray.Visible = true;
            tray.BalloonTipClicked += delegate { Activate(); };
            tray.DoubleClick += delegate { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); };
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            DarkMenu.Apply(trayMenu);
            trayMenu.ShowCheckMargin = true;
            trayMenu.Opening += delegate { BuildTrayMenu(trayMenu); };
            tray.ContextMenuStrip = trayMenu;
            tray.MouseClick += delegate(object ts, MouseEventArgs te) { if (te.Button == MouseButtons.Left) { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); } };

            lobby.Changed += OnLobbyChanged;
            lobby.VoiceChanged += delegate { OnVoiceRooms(); RefreshNav(); };
            lobby.Message += OnMessage;
            lobby.History += OnHistory;
            lobby.Typing += OnTyping;
            lobby.MessageUpdated += OnMessageUpdated;
            lobby.MessageDeleted += OnMessageDeleted;
            lobby.Info += delegate(string t) { Toast(t); };
            lobby.Failed += delegate(string t) { Toast(t); };

            tick.Interval = 150;
            tick.Tick += delegate { Tick(); };
            tick.Start();
            presenceTimer.Interval = 5000;
            presenceTimer.Tick += delegate { UpdatePresence(); CheckIdle(); };
            presenceTimer.Start();

            Shown += delegate
            {
                if (Program.StartedByWindows && Config.Get("sys.startmin", "1") == "1") BeginInvoke((MethodInvoker)delegate { Hide(); });
                Invite.RegisterScheme();
                if (Config.Get("lobby.host", "0") == "1")
                {
                    string err = StartHosting();
                    if (err != null) Toast(err);
                }
                UpdatePresence();
                if (!lobby.Resume())
                {
                    homeMode = true; Open("page:friends");
                    if (LobbyClient.SavedGroups().Count == 0) BeginInvoke((MethodInvoker)delegate { AddGroup(true); });
                }
                RefreshAll();
            };
            FormClosing += delegate(object fs, FormClosingEventArgs fe)
            {
                // Settings > System > Minimize to System Tray: X hides the window, the tray icon's Quit closes the app
                if (!quitting && fe.CloseReason == CloseReason.UserClosing && Config.Get("sys.tray", "1") == "1")
                {
                    fe.Cancel = true;
                    Hide();
                    if (Config.Get("sys.trayhint", "0") != "1")
                    {
                        Config.Set("sys.trayhint", "1");
                        try { tray.ShowBalloonTip(4000, "GameSpy Revival is still running", "It's in the tray by the clock. Right-click it to quit. (Settings > System)", ToolTipIcon.None); } catch (Exception) { }
                    }
                    return;
                }
                foreach (ArenaPanel a in arenas) a.Shutdown();
                if (mf != null) mf.Shutdown();
                lobby.Stop();
                voice.Shutdown();
                // the group server keeps running in the background (Settings -> Host this group -> Stop hosting ends it)
                tray.Visible = false;
                tray.Dispose();
            };
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && settings.Visible) { CloseSettings(); e.Handled = true; }
                else if (e.Control && e.Shift && e.KeyCode == Keys.M) { ToggleMute(); e.Handled = true; }
                else if (e.Control && e.Shift && e.KeyCode == Keys.D) { ToggleDeaf(); e.Handled = true; }
                else if (e.Alt && e.KeyCode == Keys.Left) { GoBack(); e.Handled = true; }
                else if (e.Alt && e.KeyCode == Keys.Right) { GoForward(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.F && convo.Visible) { txtSearch.Focus(); e.Handled = true; }
            };
        }

        // ---------------------------------------------------------------- building

        void BuildRail()
        {
            rail = new GroupRail();
            rail.Dock = DockStyle.Left;
            rail.Width = RailW;
            rail.Picked += delegate(GroupRail.Entry e)
            {
                if (e.Home) { homeMode = true; Open("page:friends"); RefreshAll(); }
                else if (e.Add) AddGroup(false);
                else if (e.Games) { string gk = Config.Get("view.games", "game:q3"); Open(IsGameKey(gk) ? gk : "game:q3"); RefreshAll(); }
                else SwitchGroup(e.Key);
            };
            rail.Menu += delegate(GroupRail.Entry e, Point p)
            {
                if (e.Home || e.Add) return;
                GroupMenu(e.Key).Show(rail, p);
            };
            Controls.Add(rail);
        }

        ContextMenuStrip GroupMenu(string key)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            DarkMenu.Apply(m);
            bool here = key == lobby.Server;
            if (here)
            {
                m.Items.Add("Mark As Read", null, delegate
                {
                    foreach (string k in new List<string>(unread.Keys)) if (k.StartsWith("#")) unread.Remove(k);
                    foreach (string k in new List<string>(mentions.Keys)) if (k.StartsWith("#")) mentions.Remove(k);
                    RefreshNav(); RefreshRail(); UpdateTitle();
                });
                m.Items.Add(new ToolStripSeparator());
            }
            m.Items.Add("Invite to Server", null, delegate { InvitePeople(key); });
            bool muted = Config.Get("mute.group." + key, "0") == "1";
            m.Items.Add(muted ? "Unmute Server" : "Mute Server", null, delegate { Config.Set("mute.group." + key, muted ? "0" : "1"); RefreshAll(); });
            m.Items.Add(new ToolStripSeparator());
            if (here && LobbyHost.Running) m.Items.Add("Server Settings", null, delegate { OpenSettings("hosting"); });
            m.Items.Add("Notification Settings", null, delegate { OpenSettings("notify"); });
            m.Items.Add("Edit Profile", null, delegate { OpenSettings("account"); });
            m.Items.Add(new ToolStripSeparator());
            ToolStripItem leave = m.Items.Add("Leave Server", null, delegate
            {
                if (MessageBox.Show(this, "Leave " + LobbyClient.GroupName(key) + "? You can rejoin with its address. Your account on it stays.", "Leave Server", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                if (lobby.Server == key && lobby.State != LobbyState.SignedOut) lobby.SignOut();
                LobbyClient.ForgetGroup(key);
                homeMode = true;
                Open("page:friends");
                RefreshAll();
            });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Copy Server Address", null, delegate { try { Clipboard.SetText(key); } catch (Exception) { } });
            foreach (ToolStripItem ti in m.Items) ti.ForeColor = Look.Text;
            leave.ForeColor = Look.Red; leave.Tag = "danger";
            return m;
        }

        void InvitePeople(string key)
        {
            string addr = key;
            if (LobbyHost.Running && key.StartsWith("127."))
            {
                Toast("Looking up your public address...");
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    string ip = "";
                    ip = Invite.PublicIp();
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (ip.Length == 0) { Toast("Could not find your public address."); return; }
                            string a = Invite.MyLink(ip, HostPort());
                            try { Clipboard.SetText(a); } catch (Exception) { }
                            Toast("Invite link copied: " + a + ". Friends click it, or press + > Join a Group and paste it.");
                        });
                    }
                    catch (Exception) { }
                });
                return;
            }
            try { Clipboard.SetText(Invite.Link(addr)); } catch (Exception) { }
            Toast("Invite link copied: " + Invite.Link(addr) + ". Friends click it, or press + > Join a Group and paste it.");
        }

        void BuildSide()
        {
            side = new Panel();
            side.Dock = DockStyle.Left;
            side.Width = SideW;
            side.BackColor = Look.Side;

            sideHead = new Panel();
            sideHead.Dock = DockStyle.Top;
            sideHead.Height = HeadH;
            sideHead.BackColor = Look.Side;
            sideHead.Cursor = Cursors.Hand;
            sideHead.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Look.Line)) e.Graphics.DrawLine(p, 0, HeadH - 1, SideW, HeadH - 1);
                if (!homeMode && !gamesMode)
                    using (Pen cp = new Pen(Look.Soft, 1.6f))
                    {
                        int cx = SideW - 22, cy = 22;
                        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        e.Graphics.DrawLine(cp, cx - 4, cy - 2, cx, cy + 2);
                        e.Graphics.DrawLine(cp, cx, cy + 2, cx + 4, cy - 2);
                    }
            };
            lblGroup = new Label();
            lblGroup.SetBounds(16, 6, SideW - 48, 20);
            lblGroup.Font = new Font("Segoe UI Semibold", 11f);
            lblGroup.ForeColor = Color.White;
            lblGroup.AutoEllipsis = true;
            sideHead.Controls.Add(lblGroup);
            lblGroupSub = new Label();
            lblGroupSub.SetBounds(16, 26, SideW - 48, 16);
            lblGroupSub.Font = new Font("Segoe UI", 8f);
            lblGroupSub.ForeColor = Look.Muted;
            lblGroupSub.AutoEllipsis = true;
            sideHead.Controls.Add(lblGroupSub);
            MouseEventHandler headClick = delegate(object s, MouseEventArgs e)
            {
                if (homeMode || gamesMode || lobby.Server.Length == 0) return;
                ContextMenuStrip gm = GroupMenu(lobby.Server);
                gm.AutoSize = false;
                gm.Width = sideHead.Width - 16;
                gm.Height = gm.GetPreferredSize(Size.Empty).Height;
                foreach (ToolStripItem gi in gm.Items) gi.AutoSize = true;
                gm.Show(sideHead, new Point(8, HeadH));
            };
            sideHead.MouseUp += headClick;
            lblGroup.MouseUp += headClick;
            lblGroupSub.MouseUp += headClick;
            lblGroup.Cursor = lblGroupSub.Cursor = Cursors.Hand;

            nav = new NavList();
            nav.Dock = DockStyle.Fill;
            nav.Picked += NavPicked;
            nav.Menu += NavMenu;

            user = new UserPanel();
            user.Dock = DockStyle.Bottom;
            user.Height = 106;
            user.MuteClicked += ToggleMute;
            user.DeafClicked += ToggleDeaf;
            user.SettingsClicked += delegate { OpenSettings("voice"); };
            user.LeaveClicked += LeaveVoice;
            user.SignInClicked += delegate { ShowSignIn(null); };
            user.AvatarClicked += StatusMenu;

            side.Controls.Add(nav);
            side.Controls.Add(user);
            side.Controls.Add(sideHead);
            Controls.Add(side);
        }

        void BuildMain()
        {
            mainPanel = new Panel();
            mainPanel.Dock = DockStyle.Fill;
            mainPanel.BackColor = Look.Main;
            Controls.Add(mainPanel);

            mainHead = new Panel();
            mainHead.Dock = DockStyle.Top;
            mainHead.Height = HeadH;
            mainHead.BackColor = Look.Main;
            mainHead.Paint += delegate(object s, PaintEventArgs e) { using (Pen p = new Pen(Look.Line)) e.Graphics.DrawLine(p, 0, HeadH - 1, mainHead.Width, HeadH - 1); };
            lblTitle = new Label();
            lblTitle.Font = new Font("Segoe UI Semibold", 12f);
            lblTitle.ForeColor = Color.White;
            lblTitle.AutoSize = true;
            lblTitle.Location = new Point(18, 12);
            mainHead.Controls.Add(lblTitle);
            lblTopic = new Label();
            lblTopic.Font = new Font("Segoe UI", 9f);
            lblTopic.ForeColor = Look.Muted;
            lblTopic.AutoEllipsis = true;
            mainHead.Controls.Add(lblTopic);
            btnHeadAction = new Button();
            btnHeadAction.Size = new Size(124, 30);
            btnHeadAction.Visible = false;
            btnHeadAction.Click += delegate { HeadAction(); };
            mainHead.Controls.Add(btnHeadAction);
            Theme.Style(btnHeadAction);
            btnMembers = new Button();
            btnMembers.Text = "";
            btnMembers.Font = new Font("Segoe MDL2 Assets", 12f);
            btnMembers.Size = new Size(34, 30);
            btnMembers.FlatStyle = FlatStyle.Flat;
            btnMembers.FlatAppearance.BorderSize = 0;
            btnMembers.FlatAppearance.MouseOverBackColor = Look.Hover;
            btnMembers.BackColor = Look.Main;
            btnMembers.Cursor = Cursors.Hand;
            btnMembers.Click += delegate
            {
                membersPanel.Visible = !membersPanel.Visible;
                Config.Set("ui.members", membersPanel.Visible ? "1" : "0");
                btnMembers.ForeColor = membersPanel.Visible ? Color.White : Look.Muted;
            };
            new ToolTip().SetToolTip(btnMembers, "Show or hide the member list");
            mainHead.Controls.Add(btnMembers);
            txtSearch = new TextBox();
            txtSearch.Size = new Size(180, 24);
            txtSearch.BorderStyle = BorderStyle.FixedSingle;
            txtSearch.BackColor = Color.FromArgb(5, 9, 15);
            txtSearch.ForeColor = Look.Text;
            txtSearch.Font = new Font("Segoe UI", 9.5f);
            Theme.Cue(txtSearch, "Search");
            txtSearch.TextChanged += delegate { chat.Filter = txtSearch.Text; chat.SetMessages(CurrentLog()); };
            txtSearch.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { txtSearch.Text = ""; e.SuppressKeyPress = true; } };
            mainHead.Controls.Add(txtSearch);
            mainHead.Layout += delegate { LayoutHeader(); };
            mainPanel.Controls.Add(mainHead);

            content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Look.Main;
            mainPanel.Controls.Add(content);
            content.BringToFront();

            convo = new Panel();
            convo.Dock = DockStyle.Fill;
            convo.BackColor = Look.Main;
            lblTyping = new Label();
            lblTyping.Dock = DockStyle.Bottom;
            lblTyping.Height = 22;
            lblTyping.Padding = new Padding(20, 0, 0, 4);
            lblTyping.Font = new Font("Segoe UI", 8.5f);
            lblTyping.ForeColor = Look.Soft;
            lblTyping.BackColor = Look.Main;
            composer = new Composer();
            composer.Dock = DockStyle.Bottom;
            composer.Submit += SendFromComposer;
            composer.Typed += delegate { if (current.StartsWith("#") || current.StartsWith("@")) lobby.SendTyping(current); };
            composer.PlusClicked += ComposerPlus;
            composer.EmojiClicked += delegate(Point at)
            {
                EmojiPicker ep = new EmojiPicker();
                ep.Picked += delegate(string em) { composer.Insert(em); };
                ep.Show(at);
            };
            composer.EditLast += delegate
            {
                ChatMsg mine = chat.LastOwn();
                if (mine != null && current.StartsWith("#")) composer.BeginEdit(mine);
            };
            chat = new ChatView();
            chat.Dock = DockStyle.Fill;
            chat.JoinInvite += JoinInvite;
            chat.React += delegate(ChatMsg m, string e, bool on) { if (current.StartsWith("#")) lobby.React(current.Substring(1), m.Id, e, on); };
            chat.PickReaction += delegate(ChatMsg m, Point at)
            {
                EmojiPicker ep = new EmojiPicker();
                ep.Picked += delegate(string em) { if (current.StartsWith("#")) lobby.React(current.Substring(1), m.Id, em, true); };
                ep.Show(at);
            };
            chat.Reply += delegate(ChatMsg m) { composer.BeginReply(m); };
            chat.Edit += delegate(ChatMsg m) { composer.BeginEdit(m); };
            chat.Delete += delegate(ChatMsg m)
            {
                if ((ModifierKeys & Keys.Shift) == 0 &&
                    MessageBox.Show(this, "Delete this message?\n\n" + (m.Text.Length > 200 ? m.Text.Substring(0, 200) + "..." : m.Text), "Delete Message", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                if (current.StartsWith("#")) lobby.DeleteMessage(current.Substring(1), m.Id);
            };
            chat.PersonClicked += delegate(string name, Point at) { ShowProfile(name, at); };
            chat.Ignored = ignored;
            convo.Controls.Add(chat);
            convo.Controls.Add(composer);
            convo.Controls.Add(lblTyping);
            chat.BringToFront();
            convo.Visible = false;
            content.Controls.Add(convo);
        }

        void LayoutHeader()
        {
            int x = mainHead.Width - 12;
            txtSearch.Visible = convo != null && convo.Visible;
            btnMembers.Location = new Point(x - btnMembers.Width, 9); x -= btnMembers.Width + 8;
            btnMembers.ForeColor = membersPanel != null && membersPanel.Visible ? Color.White : Look.Muted;
            if (txtSearch.Visible) { txtSearch.Location = new Point(x - txtSearch.Width, 12); x -= txtSearch.Width + 10; }
            if (btnHeadAction.Visible) { btnHeadAction.Location = new Point(x - btnHeadAction.Width, 9); x -= btnHeadAction.Width + 10; }
            lblTopic.SetBounds(lblTitle.Right + 14, 16, Math.Max(10, x - lblTitle.Right - 24), 20);
        }

        List<ChatMsg> CurrentLog()
        {
            List<ChatMsg> l;
            return logs.TryGetValue(current, out l) ? l : null;
        }

        void BuildMembers()
        {
            membersPanel = new Panel();
            membersPanel.Dock = DockStyle.Right;
            membersPanel.Width = MembersW;
            membersPanel.BackColor = Look.Side;
            members = new PeopleList();
            members.Dock = DockStyle.Fill;
            members.Clicked += delegate(PersonRow r, string a)
            {
                if (r.Key.Length == 0) return;
                Point at = members.PointToScreen(new Point(-316, r.Bounds.Y));
                ShowProfile(r.Key, at);
            };
            members.Menu += delegate(PersonRow r, Point p) { PersonMenu(r.Key, members, p); };
            membersPanel.Controls.Add(members);
            Controls.Add(membersPanel);
        }

        void BuildPages()
        {
            GameDef q3 = new GameDef();
            q3.Id = "q3"; q3.Title = "Quake III Arena"; q3.Protocol = 68;
            q3.Blurb = "1999 arena shooter. Internet servers from the public master servers.";
            q3.Tagline = "1999 \u00b7 Arena shooter \u00b7 Quake III master servers";
            q3.ColorA = Color.FromArgb(150, 70, 24); q3.ColorB = Color.FromArgb(28, 20, 40);
            q3.GetIt = "You need your own copy of the game (Steam or GOG).";
            q3.Masters = new string[] { "master.quake3arena.com:27950", "master.ioquake3.org:27950", "master3.idsoftware.com:27950", "master.maverickservers.com:27950", "dpmaster.deathmask.net:27950" };
            q3.ExeNames = new string[] { "quake3e.x64.exe", "quake3e.exe", "ioquake3.x86_64.exe", "ioquake3.x86.exe", "ioquake3.exe", "quake3.exe" };
            q3.SteamFolders = new string[] { "Quake 3 Arena", "Quake III Arena" };
            q3.Folders = new string[] { @"C:\GOG Games\Quake III Arena", @"C:\Program Files (x86)\Quake III Arena", @"C:\Program Files (x86)\ioquake3", @"C:\Games\ioquake3", @"C:\Games\Quake III Arena" };

            GameDef oa = new GameDef();
            oa.Id = "oa"; oa.Title = "OpenArena"; oa.Protocol = 71;
            oa.Blurb = "Free, open-source arena shooter on the Quake III engine.";
            oa.Tagline = "Free \u00b7 Open source \u00b7 Quake III engine";
            oa.ColorA = Color.FromArgb(24, 110, 130); oa.ColorB = Color.FromArgb(22, 22, 50);
            oa.GetIt = "OpenArena is a free download from openarena.ws.";
            oa.Masters = new string[] { "dpmaster.deathmask.net:27950", "master.ioquake3.org:27950" };
            oa.ExeNames = new string[] { "openarena.exe", "openarena.x86_64.exe", "openarena.x86.exe" };
            oa.SteamFolders = new string[] { "OpenArena" };
            oa.Folders = new string[] { @"C:\Program Files (x86)\OpenArena", @"C:\Program Files\OpenArena", @"C:\Games\OpenArena", @"C:\Games\openarena-0.8.8", @"C:\OpenArena" };

            GameDef ut = new GameDef();
            ut.Id = "ut"; ut.Title = "Unreal Tournament"; ut.Kind = "gs";
            ut.GameName = "ut"; ut.GameKey = "Z5Nfb0"; ut.QueryPort = 7778; ut.JoinFormat = "{0}";
            ut.Blurb = "1999 arena shooter. Servers from the 333networks community master servers.";
            ut.Tagline = "1999 \u00b7 Arena shooter \u00b7 333networks master servers";
            ut.ColorA = Color.FromArgb(40, 70, 160); ut.ColorB = Color.FromArgb(30, 18, 50);
            ut.GetIt = "You need your own copy (Steam or GOG). The free OldUnreal patch (469) is recommended.";
            ut.Masters = new string[] { "master.333networks.com:28900", "master.errorist.eu:28900", "master.newbiesplayground.net:28900",
                "master-uk.unrealarchive.org:28900", "master-de.unrealarchive.org:28900", "master.gonespy.com:28900" };
            ut.ExeNames = new string[] { @"System64\UnrealTournament64.exe", @"System\UnrealTournament.exe", "UnrealTournament.exe" };
            ut.SteamFolders = new string[] { "Unreal Tournament", "Unreal Tournament GOTY" };
            ut.Folders = new string[] { @"C:\GOG Games\Unreal Tournament GOTY", @"C:\GOG Games\Unreal Tournament", @"C:\UnrealTournament",
                @"C:\Program Files (x86)\GOG Galaxy\Games\Unreal Tournament GOTY", @"C:\Games\UnrealTournament" };

            foreach (GameDef gd in new GameDef[] { q3, oa, ut })
            {
                ArenaPanel ap = new ArenaPanel(gd);
                ap.InviteRequested += delegate(GameDef g, string addr, string name, string detail)
                {
                    PostInvite(g.Id, name, addr, detail);
                };
                arenas.Add(ap);
                AddPage("game:" + gd.Id, ap);
            }

            mf = new MFNet.MetalFatiguePanel();
            mfHost = new Panel();
            mfHost.Dock = DockStyle.Fill;
            mf.Location = new Point(0, 6);
            mfHost.Controls.Add(mf);
            AddPage("game:mf", mfHost);

            InfoPage bf2 = new InfoPage("Battlefield 2",
                "Battlefield 2 is already alive, thanks to the community. BF2Hub runs replacement accounts, a server list and ranked stats. "
                + "Project Reality: BF2 is a free standalone realism mod with its own master server.\n\n"
                + "Use their launchers to play. Voice and chat here work alongside them: hop in a voice channel and play together.");
            bf2.AddAction("BF2Hub (bf2hub.com)", delegate { OpenUrl("https://www.bf2hub.com"); });
            bf2.AddAction("Project Reality: BF2 (realitymod.com)", delegate { OpenUrl("https://www.realitymod.com"); });
            AddPage("game:bf2", bf2);
            InfoPage ra2 = new InfoPage("Red Alert 2 / Yuri's Revenge",
                "CnCNet runs online play for Red Alert 2 and Yuri's Revenge, with fixes for modern Windows, ladders and tournaments. "
                + "You need Yuri's Revenge installed, then their launcher.\n\nVoice and chat here work alongside it.");
            ra2.AddAction("CnCNet: Red Alert 2", delegate { OpenUrl("https://cncnet.org/red-alert-2"); });
            ra2.AddAction("CnCNet: Yuri's Revenge", delegate { OpenUrl("https://cncnet.org/yuris-revenge"); });
            AddPage("game:ra2", ra2);
            AddPage("page:directory", new DirectoryPage());

            // Home: friends
            friendsPage = new Panel();
            friendsPage.Dock = DockStyle.Fill;
            friendsPage.BackColor = Look.Main;
            Panel add = new Panel();
            add.Dock = DockStyle.Top;
            add.Height = 92;
            add.BackColor = Look.Main;
            Label la = new Label();
            la.Text = "ADD FRIEND";
            la.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            la.ForeColor = Look.Text;
            la.SetBounds(20, 14, 300, 16);
            add.Controls.Add(la);
            txtAddFriend = new TextBox();
            txtAddFriend.SetBounds(20, 36, 520, 28);
            txtAddFriend.Font = new Font("Segoe UI", 11f);
            Theme.Cue(txtAddFriend, "Type their GameSpy Revival name");
            txtAddFriend.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { AddFriendNow(); e.SuppressKeyPress = true; } };
            add.Controls.Add(txtAddFriend);
            Button ba = new Button();
            ba.Text = "Send Friend Request";
            ba.Tag = "primary";
            ba.SetBounds(550, 34, 180, 32);
            ba.Click += delegate { AddFriendNow(); };
            add.Controls.Add(ba);
            lblFriendsMsg = new Label();
            lblFriendsMsg.SetBounds(20, 68, 700, 18);
            lblFriendsMsg.ForeColor = Look.Muted;
            add.Controls.Add(lblFriendsMsg);
            friendsList = new PeopleList();
            friendsList.Dock = DockStyle.Fill;
            friendsList.BackColor = Look.Main;
            friendsList.RowHeight = 56;
            friendsList.Clicked += delegate(PersonRow r, string a) { FriendAction(r, a); };
            friendsList.Menu += delegate(PersonRow r, Point p) { PersonMenu(r.Key, friendsList, p); };
            friendsPage.Controls.Add(friendsList);
            friendsPage.Controls.Add(add);
            foreach (Control c in add.Controls) Theme.Style(c);
            AddPage("page:friends", friendsPage);

            foreach (KeyValuePair<string, Control> kv in pages) if (kv.Value != friendsPage) Theme.Style(kv.Value);
        }

        void AddPage(string key, Control c)
        {
            c.Dock = DockStyle.Fill;
            c.Visible = false;
            content.Controls.Add(c);
            pages[key] = c;
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- navigation

        public void Open(string key)
        {
            if (key.Length == 0) return;
            string prev = current;
            if (!navigatingHistory && !string.IsNullOrEmpty(prev) && prev != key)
            {
                backStack.Add((homeMode ? "h|" : "g|") + prev);
                if (backStack.Count > 50) backStack.RemoveAt(0);
                fwdStack.Clear();
            }
            current = key;
            gamesMode = IsGameKey(key);
            if (!gamesMode && key.StartsWith("#")) homeMode = false;
            if (!gamesMode && (key.StartsWith("@") || key == "page:friends")) homeMode = true;
            if (key.StartsWith("@") && DmHidden(key.Substring(1))) SetDmHidden(key.Substring(1), false);
            bool conv = key.StartsWith("#") || key.StartsWith("@");
            foreach (KeyValuePair<string, Control> kv in pages)
            {
                bool on = kv.Key == key;
                if (kv.Value.Visible && !on) SetPageActive(kv.Value, false);
                kv.Value.Visible = on;
                if (on) { kv.Value.BringToFront(); SetPageActive(kv.Value, true); }
            }
            convo.Visible = conv;
            if (conv)
            {
                convo.BringToFront();
                chat.Me = lobby.User ?? "";
                chat.MeOwner = false;
                foreach (Friend mf2 in lobby.Members) if (mf2.Owner && string.Equals(mf2.Name, chat.Me, StringComparison.OrdinalIgnoreCase)) chat.MeOwner = true;
                chat.IsChannel = key.StartsWith("#");
                string nm = key.Substring(1);
                chat.WelcomeTitle = chat.IsChannel ? "Welcome to #" + nm + "!" : nm;
                chat.WelcomeText = chat.IsChannel ? "This is the start of the #" + nm + " channel."
                    : "This is the beginning of your direct message history with " + nm + ".";
                List<ChatMsg> l;
                chat.SetMessages(logs.TryGetValue(key, out l) ? l : null);
                composer.SetPlaceholder("Message " + (chat.IsChannel ? "#" : "@") + nm);
                if (!chat.IsChannel && !dmHistoryAsked.Contains(nm) && lobby.State == LobbyState.SignedIn)
                {
                    dmHistoryAsked.Add(nm);
                    lobby.RequestDmHistory(nm);
                }
                unread.Remove(key); mentions.Remove(key);
                composer.Box.Focus();
            }
            UpdateHeader();
            UpdateTyping();
            RefreshNav();
            RefreshMembers();
            RefreshRail();
            UpdateTitle();
            Config.Set("view." + (gamesMode ? "games" : homeMode ? "home" : lobby.Server), key);
            RefreshSideHead();
            sideHead.Invalidate();
        }

        void SetPageActive(Control c, bool on)
        {
            ArenaPanel a = c as ArenaPanel;
            if (a != null) a.SetActive(on);
            if (c == mfHost && mf != null) mf.SetActive(on);
        }

        void UpdateHeader()
        {
            btnHeadAction.Visible = false;
            lblTopic.Text = "";
            if (current.StartsWith("#"))
            {
                string n = current.Substring(1);
                lblTitle.Text = "#  " + n;
                string t; lblTopic.Text = Topics.TryGetValue(n, out t) ? t : "";
            }
            else if (current.StartsWith("@"))
            {
                string n = current.Substring(1);
                lblTitle.Text = "@  " + n;
                Friend f = FindPerson(n);
                lblTopic.Text = f == null ? "" : f.Online ? (f.Status.Length > 0 ? f.Status : "Online") : "Offline";
            }
            else if (current == "page:friends") { lblTitle.Text = "Friends"; lblTopic.Text = "Add friends, accept requests, start a conversation"; }
            else if (current == "page:directory") { lblTitle.Text = "Community Projects"; lblTopic.Text = "The people keeping old games online"; }
            else if (current.StartsWith("game:"))
            {
                string id = current.Substring(5);
                lblTitle.Text = id == "bf2" ? "Battlefield 2" : id == "ra2" ? "Red Alert 2" : Look.GameTitle(id);
                lblTopic.Text = id == "mf" ? "Host or join through your group's server" : id == "bf2" || id == "ra2" ? "Played through community services" : "Live servers";
                if (id == "mf") { btnHeadAction.Text = "Invite crew"; btnHeadAction.Visible = true; }
            }
            LayoutHeader();
        }

        void HeadAction()
        {
            if (current == "game:mf")
            {
                string me = lobby.User ?? "";
                string addr = LobbyHost.Running ? "" : (mf.CurrentServer.StartsWith("127.") ? "" : mf.CurrentServer);
                PostInvite("mf", me + "'s Metal Fatigue game", addr, "Open Metal Fatigue \u2192 Multiplayer \u2192 join " + me + "'s game");
            }
        }

        void UpdateTitle()
        {
            int n = 0;
            foreach (int v in mentions.Values) n += v;
            if (Config.Get("notify.badge", "1") == "0") n = 0;
            Text = (n > 0 ? "(" + n + ") " : "") + "GameSpy Revival" + (Config.Profile.Length > 0 ? "  (profile " + Config.Profile + ")" : "");
            if (title != null)
            {
                string where = gamesMode ? "Games" : homeMode ? "Direct Messages" : (lobby.Group.Length > 0 ? lobby.Group : "GameSpy Revival");
                title.Where = where;
                title.Initials = Initials(where);
                title.CanBack = backStack.Count > 0;
                title.CanForward = fwdStack.Count > 0;
                title.Invalidate();
            }
        }

        // ---------------------------------------------------------------- refreshing

        void RefreshAll()
        {
            RefreshRail();
            RefreshSideHead();
            RefreshNav();
            RefreshMembers();
            RefreshFriendsPage();
            RefreshUser();
            UpdateHeader();
        }

        void RefreshRail()
        {
            rail.Entries.Clear();
            GroupRail.Entry home = new GroupRail.Entry();
            home.Home = true; home.Key = "home"; home.Label = "GS"; home.Tip = "Home: friends and messages";
            foreach (KeyValuePair<string, int> kv in mentions) if (kv.Key.StartsWith("@")) home.Mentions += kv.Value;
            rail.Entries.Add(home);
            foreach (string g in LobbyClient.SavedGroups())
            {
                GroupRail.Entry e = new GroupRail.Entry();
                e.Key = g;
                string name = LobbyClient.GroupName(g);
                e.Tip = name;
                e.Label = Initials(name);
                if (g == lobby.Server)
                {
                    foreach (KeyValuePair<string, int> kv in mentions) if (kv.Key.StartsWith("#")) e.Mentions += kv.Value;
                    foreach (KeyValuePair<string, int> kv in unread) if (kv.Key.StartsWith("#") && kv.Value > 0) e.Unread = true;
                }
                rail.Entries.Add(e);
            }
            GroupRail.Entry add = new GroupRail.Entry();
            add.Add = true; add.Key = "+"; add.Label = "+"; add.Tip = "Add a group: create your own or join a friend's";
            rail.Entries.Add(add);
            GroupRail.Entry games = new GroupRail.Entry();
            games.Games = true; games.Key = "games"; games.Label = "Games"; games.Tip = "Games: server browsers, Metal Fatigue hosting, community projects";
            rail.Entries.Add(games);
            rail.Selected = gamesMode ? "games" : homeMode ? "home" : lobby.Server;
            rail.Rebuild();
        }

        static string Initials(string name)
        {
            string s = "";
            foreach (string w in name.Split(new char[] { ' ', '\'', '-', '_', '.', ':' }, StringSplitOptions.RemoveEmptyEntries))
                if (char.IsLetterOrDigit(w[0])) { s += char.ToUpperInvariant(w[0]); if (s.Length == 2) break; }
            return s.Length == 0 ? "?" : s;
        }

        void RefreshSideHead()
        {
            if (gamesMode)
            {
                lblGroup.Text = "Games";
                lblGroupSub.Text = "Find a server or start a match";
            }
            else if (homeMode)
            {
                lblGroup.Text = "Home";
                lblGroupSub.Text = lobby.State == LobbyState.SignedIn ? "Friends and private messages" : "Sign in to see your friends";
            }
            else
            {
                lblGroup.Text = lobby.Group.Length > 0 ? lobby.Group : LobbyClient.GroupName(lobby.Server);
                lblGroupSub.Text = lobby.State == LobbyState.SignedIn ? (LobbyHost.Running ? "Hosted on this PC" : "Connected") + " \u00b7 " + OnlineCount() + " online"
                    : lobby.State == LobbyState.Connecting ? "Connecting..." : "Not connected";
            }
        }

        int OnlineCount() { int n = 0; foreach (Friend f in lobby.Members) if (f.Online) n++; return n; }

        void RefreshNav()
        {
            nav.Items.Clear();
            if (gamesMode)
            {
                Header("Server browsers");
                Game("Quake III Arena", "game:q3");
                Game("OpenArena", "game:oa");
                Game("Unreal Tournament", "game:ut");
                Header("Host and play");
                Game("Metal Fatigue", "game:mf");
                Header("Community services");
                Game("Battlefield 2", "game:bf2");
                Game("Red Alert 2", "game:ra2");
                Link("Community Projects", "page:directory");
                nav.Selected = current;
                nav.Rebuild();
                return;
            }
            if (homeMode)
            {
                Link("Friends", "page:friends");
                Header("Direct messages");
                List<Friend> fr = new List<Friend>();
                foreach (Friend f in lobby.Friends)
                    if (f.Kind == 0 && (!DmHidden(f.Name) || current == "@" + f.Name || unread.ContainsKey("@" + f.Name))) fr.Add(f);
                fr.Sort(delegate(Friend a, Friend b)
                {
                    if (a.Online != b.Online) return a.Online ? -1 : 1;
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
                foreach (Friend f in fr)
                {
                    NavItem it = new NavItem();
                    it.Kind = NavKind.Dm; it.Text = f.Name; it.Key = "@" + f.Name; it.Online = f.Online;
                    it.Presence = f.Presence;
                    it.Sub = f.Online ? (f.Custom.Length > 0 ? f.Custom : f.Status.Length > 0 && f.Status != "Online" ? f.Status : Look.PresenceName(f.Presence)) : "Offline";
                    Badges(it);
                    nav.Items.Add(it);
                }
                if (fr.Count == 0) { NavItem g = new NavItem(); g.Kind = NavKind.Gap; nav.Items.Add(g); }
            }
            else
            {
                bool textShut = Header("Text channels", "cat:text");
                foreach (string ch in lobby.Channels)
                {
                    NavItem it = new NavItem();
                    it.Kind = NavKind.Channel; it.Text = ch; it.Key = "#" + ch;
                    it.Muted = IsMutedKey(it.Key);
                    Badges(it);
                    if (textShut && it.Key != current && (it.Unread == 0 || it.Muted) && it.Mentions == 0) continue;
                    nav.Items.Add(it);
                }
                bool voiceShut = Header("Voice channels", "cat:voice");
                List<VoiceRoom> rooms = lobby.Rooms.Count > 0 ? lobby.Rooms : null;
                string[] names = { "Lobby", "Quake III Arena", "OpenArena", "Unreal Tournament", "Metal Fatigue", "AFK" };
                if (rooms == null)
                {
                    rooms = new List<VoiceRoom>();
                    foreach (string n in names) { VoiceRoom vr = new VoiceRoom(); vr.Name = n; rooms.Add(vr); }
                }
                foreach (VoiceRoom vr in rooms)
                {
                    NavItem it = new NavItem();
                    it.Kind = NavKind.Voice; it.Text = vr.Name; it.Key = "voice:" + vr.Name; it.Tag = vr.Name;
                    it.Active = lobby.VoiceRoomName == vr.Name;
                    it.Sub = vr.Members.Count > 0 && !it.Active ? vr.Members.Count.ToString() : "";
                    if (voiceShut && !it.Active) continue;
                    nav.Items.Add(it);
                    foreach (VoiceMember m in vr.Members)
                    {
                        NavItem mi = new NavItem();
                        mi.Kind = NavKind.VoiceMember; mi.Text = m.User; mi.Key = "vm:" + m.User; mi.Tag = m.User;
                        mi.Mute = m.Mute; mi.Deaf = m.Deaf;
                        nav.Items.Add(mi);
                    }
                }
            }
            nav.Selected = current;
            UpdateSpeaking();
            nav.Rebuild();
        }

        void Header(string t) { NavItem h = new NavItem(); h.Kind = NavKind.Header; h.Text = t; nav.Items.Add(h); }

        bool Header(string t, string key)
        {
            NavItem h = new NavItem();
            h.Kind = NavKind.Header; h.Text = t; h.Key = key;
            h.Collapsed = ("|" + Config.Get("ui.collapsed", "") + "|").Contains("|" + key + "|");
            nav.Items.Add(h);
            return h.Collapsed;
        }

        void ToggleCategory(string key)
        {
            List<string> l = new List<string>(Config.Get("ui.collapsed", "").Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
            if (l.Contains(key)) l.Remove(key); else l.Add(key);
            Config.Set("ui.collapsed", string.Join("|", l.ToArray()));
            RefreshNav();
        }

        bool IsMutedKey(string key)
        {
            if (key.StartsWith("#") && Config.Get("mute.group." + lobby.Server, "0") == "1") return true;   // Mute Server
            return Config.Get("mute." + lobby.Server + "." + key, "") == "1";
        }

        // Close DM hides the conversation from the list until a new message arrives
        bool DmHidden(string name) { return ("|" + Config.Get("dm.hidden", "") + "|").IndexOf("|" + name.ToLowerInvariant() + "|", StringComparison.Ordinal) >= 0; }
        void SetDmHidden(string name, bool on)
        {
            List<string> l = new List<string>(Config.Get("dm.hidden", "").Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
            l.Remove(name.ToLowerInvariant());
            if (on) l.Add(name.ToLowerInvariant());
            Config.Set("dm.hidden", string.Join("|", l.ToArray()));
        }
        void SetMutedKey(string key, bool on) { Config.Set("mute." + lobby.Server + "." + key, on ? "1" : ""); RefreshNav(); RefreshRail(); }


        void Link(string t, string key) { NavItem h = new NavItem(); h.Kind = NavKind.Link; h.Text = t; h.Key = key; nav.Items.Add(h); }
        void Game(string t, string key) { NavItem h = new NavItem(); h.Kind = NavKind.Game; h.Text = t; h.Key = key; nav.Items.Add(h); }

        void Badges(NavItem it)
        {
            int n;
            if (unread.TryGetValue(it.Key, out n)) it.Unread = n;
            if (mentions.TryGetValue(it.Key, out n)) it.Mentions = n;
        }

        void UpdateSpeaking()
        {
            foreach (NavItem it in nav.Items)
            {
                if (it.Kind != NavKind.VoiceMember) continue;
                string u = (string)it.Tag;
                bool me = string.Equals(u, lobby.User, StringComparison.OrdinalIgnoreCase);
                it.Speaking = lobby.VoiceRoomName.Length > 0 && (me ? voice.Transmitting : voice.IsSpeaking(u));
            }
        }

        void RefreshMembers()
        {
            members.Rows.Clear();
            if (homeMode)
            {
                Section(members, "Active now");
                int n = 0;
                foreach (Friend f in lobby.Friends)
                    if (f.Kind == 0 && f.Online && f.Game.Length > 0) { members.Rows.Add(Person(f)); n++; }
                if (n == 0)
                {
                    PersonRow r = new PersonRow(); r.Header = true; r.Name = "Nobody's playing right now";
                    members.Rows.Add(r);
                }
            }
            else
            {
                List<Friend> playing = new List<Friend>(), online = new List<Friend>(), offline = new List<Friend>();
                foreach (Friend f in lobby.Members)
                    (f.Online ? (f.Game.Length > 0 ? playing : online) : offline).Add(f);
                if (playing.Count > 0) { Section(members, "Playing \u2014 " + playing.Count); foreach (Friend f in playing) members.Rows.Add(Person(f)); }
                if (online.Count > 0) { Section(members, "Online \u2014 " + online.Count); foreach (Friend f in online) members.Rows.Add(Person(f)); }
                if (offline.Count > 0) { Section(members, "Offline \u2014 " + offline.Count); foreach (Friend f in offline) members.Rows.Add(Person(f)); }
            }
            members.Rebuild();
        }

        static void Section(PeopleList l, string t) { PersonRow r = new PersonRow(); r.Header = true; r.Name = t; l.Rows.Add(r); }

        static PersonRow Person(Friend f)
        {
            PersonRow r = new PersonRow();
            r.Name = f.Name; r.Key = f.Name; r.Online = f.Online;
            r.Playing = f.Online && f.Game.Length > 0;
            r.Dim = !f.Online;
            r.Presence = f.Online ? f.Presence : "offline";
            r.Owner = f.Owner;
            r.Sub = f.Online ? (f.Custom.Length > 0 ? f.Custom : f.Status.Length > 0 && f.Status != "Online" ? f.Status : Look.PresenceName(f.Presence)) : Ago(f.LastSeen);
            return r;
        }

        static string Ago(string iso)
        {
            DateTime t;
            if (!DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) return "Offline";
            TimeSpan d = DateTime.UtcNow - t.ToUniversalTime();
            if (d.TotalMinutes < 2) return "Offline, just now";
            if (d.TotalHours < 1) return "Last seen " + (int)d.TotalMinutes + " min ago";
            if (d.TotalDays < 1) return "Last seen " + (int)d.TotalHours + " h ago";
            return "Last seen " + (int)d.TotalDays + " days ago";
        }

        void RefreshFriendsPage()
        {
            friendsList.Rows.Clear();
            List<Friend> inc = new List<Friend>(), on = new List<Friend>(), off = new List<Friend>(), sent = new List<Friend>();
            foreach (Friend f in lobby.Friends)
                (f.Kind == 1 ? inc : f.Kind == 2 ? sent : f.Online ? on : off).Add(f);
            if (lobby.State != LobbyState.SignedIn)
            {
                Section(friendsList, "Sign in (bottom left) to see your friends");
            }
            if (inc.Count > 0)
            {
                Section(friendsList, "Pending \u2014 " + inc.Count);
                foreach (Friend f in inc) { PersonRow r = Person(f); r.Sub = "Incoming friend request"; r.Actions = new string[] { "Accept", "Decline" }; friendsList.Rows.Add(r); }
            }
            if (on.Count > 0)
            {
                Section(friendsList, "Online \u2014 " + on.Count);
                foreach (Friend f in on) { PersonRow r = Person(f); r.Actions = new string[] { "Message" }; friendsList.Rows.Add(r); }
            }
            if (off.Count > 0)
            {
                Section(friendsList, "Offline \u2014 " + off.Count);
                foreach (Friend f in off) { PersonRow r = Person(f); r.Actions = new string[] { "Message" }; friendsList.Rows.Add(r); }
            }
            if (sent.Count > 0)
            {
                Section(friendsList, "Sent \u2014 " + sent.Count);
                foreach (Friend f in sent) { PersonRow r = Person(f); r.Sub = "Outgoing friend request"; r.Online = false; r.Actions = new string[] { "Cancel" }; friendsList.Rows.Add(r); }
            }
            if (lobby.State == LobbyState.SignedIn && lobby.Friends.Count == 0)
                Section(friendsList, "No friends yet. Add someone by name above, or right-click a member in a group.");
            friendsList.Rebuild();
        }

        void RefreshUser()
        {
            user.SignedIn = lobby.State != LobbyState.SignedOut && (lobby.User ?? "").Length > 0;
            user.Name1 = lobby.User ?? "";
            VoiceConfig vc = voice.Settings;
            string pres = autoIdle ? "idle" : lobby.Presence;
            user.Presence = pres;
            user.Sub = lobby.State == LobbyState.Connecting ? "Reconnecting..." : lobby.Custom.Length > 0 ? lobby.Custom
                : vc.PushToTalk ? "Push to talk: " + VoiceConfig.KeyName(vc.PttKey) : Look.PresenceName(pres);
            user.VoiceRoom = lobby.VoiceRoomName.Length > 0 && voiceSince != DateTime.MinValue
                ? lobby.VoiceRoomName + "  \u00b7  " + Elapsed(DateTime.Now - voiceSince) : lobby.VoiceRoomName;
            user.Group = lobby.Group;
            user.Muted = voice.Muted; user.Deafened = voice.Deafened;
            user.Speaking = voice.Transmitting && lobby.VoiceRoomName.Length > 0;
            user.Invalidate();
        }

        // tray menu, rebuilt each time it opens so the checks are current (like Discord's)
        void BuildTrayMenu(ContextMenuStrip m)
        {
            m.Items.Clear();
            ToolStripLabel head = new ToolStripLabel("GameSpy Revival" + (lobby.User != null && lobby.User.Length > 0 ? "  \u00b7  " + lobby.User : ""));
            head.Font = new Font("Segoe UI Semibold", 9.5f);
            m.Items.Add(head);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Open GameSpy Revival", null, delegate { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); });
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem mu = new ToolStripMenuItem("Mute");
            mu.Checked = voice.Muted || voice.Deafened;
            mu.Click += delegate { ToggleMute(); };
            m.Items.Add(mu);
            ToolStripMenuItem de = new ToolStripMenuItem("Deafen");
            de.Checked = voice.Deafened;
            de.Click += delegate { ToggleDeaf(); };
            m.Items.Add(de);
            if (lobby.VoiceRoomName.Length > 0)
                m.Items.Add("Disconnect from " + lobby.VoiceRoomName, null, delegate { LeaveVoice(); });
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem st = new ToolStripMenuItem("Set Status");
            foreach (string pr in new string[] { "online", "idle", "dnd", "invisible" })
            {
                string pp = pr;
                ToolStripMenuItem mi = new ToolStripMenuItem(Look.PresenceName(pr));
                mi.Checked = lobby.Presence == pr;
                mi.Enabled = lobby.State == LobbyState.SignedIn;
                mi.Click += delegate { autoIdle = false; lobby.SetPresence(pp, lobby.Custom); RefreshUser(); };
                st.DropDownItems.Add(mi);
            }
            m.Items.Add(st);
            m.Items.Add("Voice & Video Settings", null, delegate { Show(); Activate(); OpenSettings("voice"); });
            m.Items.Add(new ToolStripSeparator());
            ToolStripItem q = m.Items.Add("Quit GameSpy Revival", null, delegate { quitting = true; Close(); });
            ColorMenu(m.Items);
            head.ForeColor = Color.White;
        }

        // tray icon follows the mic like Discord's: ring in voice, filled while talking, red when muted
        TrayArt.State trayState = TrayArt.State.Idle;
        bool trayBadge;
        void UpdateTrayIcon()
        {
            TrayArt.State st = lobby.VoiceRoomName.Length == 0 ? TrayArt.State.Idle
                : voice.Deafened ? TrayArt.State.Deafened
                : voice.Muted ? TrayArt.State.Muted
                : voice.Transmitting ? TrayArt.State.Talking : TrayArt.State.InVoice;
            int n = 0; foreach (int v in mentions.Values) n += v;
            bool badge = n > 0 && Config.Get("notify.badge", "1") == "1";
            if (st == trayState && badge == trayBadge) return;
            trayState = st; trayBadge = badge;
            Icon old = tray.Icon;
            tray.Icon = TrayArt.Make(st, badge, 16);
            tray.Text = "GameSpy Revival" + (st == TrayArt.State.Idle ? "" : st == TrayArt.State.Muted ? " - muted" : st == TrayArt.State.Deafened ? " - deafened" : " - in voice");
            TrayArt.Free(old);
        }

        void Tick()
        {
            UpdateTrayIcon();
            if (DateTime.Now >= nextJoinCheck)
            {
                nextJoinCheck = DateTime.Now.AddSeconds(1);
                string link = Invite.TakePending();
                if (link.Length > 0) BeginInvoke((MethodInvoker)delegate { JoinLink(link); });
            }
            if (lobby.VoiceRoomName.Length > 0)
            {
                UpdateSpeaking();
                nav.Invalidate();
                bool sp = voice.Transmitting;
                if (sp != user.Speaking) { user.Speaking = sp; user.Invalidate(); }
                if (DateTime.Now.Second != lastTimerSec) { lastTimerSec = DateTime.Now.Second; RefreshUser(); }
            }
            UpdateTyping();
            if (voice.Problem.Length > 0 && lblTyping.Text != voice.Problem && current.StartsWith("#")) Toast(voice.Problem);
        }

        // ---------------------------------------------------------------- lobby events

        // ---- used by the settings pages
        public void RefreshTitle() { UpdateTitle(); }
        public void RefreshChat() { chat.Invalidate(); }
        public void Unignore(string u) { SetIgnored(u, false); }
        public void ShowMembers(bool on) { membersPanel.Visible = on; Config.Set("ui.members", on ? "1" : "0"); }

        const string AppRunName = "GameSpy Revival";
        public static bool AppAutostart
        {
            get
            {
                try { using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return k != null && k.GetValue(AppRunName) != null; }
                catch (Exception) { return false; }
            }
            set
            {
                try
                {
                    using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (k == null) return;
                        if (value) k.SetValue(AppRunName, "\"" + Application.ExecutablePath + "\" --startup");
                        else if (k.GetValue(AppRunName) != null) k.DeleteValue(AppRunName, false);
                    }
                }
                catch (Exception) { }
            }
        }

        // friends coming online / starting a game (Settings > Notifications > Notify me when...)
        readonly Dictionary<string, string> friendSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool friendSeenReady;
        void FriendNotices()
        {
            foreach (Friend f in lobby.Friends)
            {
                if (f.Kind != 0) continue;
                string now = f.Online ? "on|" + f.Game : "off|";
                string was;
                bool known = friendSeen.TryGetValue(f.Name, out was);
                friendSeen[f.Name] = now;
                if (!friendSeenReady || !known || was == now) continue;
                if (!was.StartsWith("on") && f.Online && Config.Get("notify.online", "0") == "1") Notify(f.Name + " is online", f.Custom.Length > 0 ? f.Custom : "Say hi!");
                else if (f.Online && f.Game.Length > 0 && Config.Get("notify.playing", "0") == "1") Notify(f.Name + " is playing", Look.GameTitle(f.Game));
            }
            if (lobby.State == LobbyState.SignedIn) friendSeenReady = true;
        }

        void OnLobbyChanged()
        {
            FriendNotices();
            if (lobby.State == LobbyState.SignedOut && lobby.VoiceRoomName.Length > 0)
            {
                lobby.VoiceRoomName = "";
                voice.SetInRoom(false);
            }
            if (lobby.State == LobbyState.SignedIn)
            {
                ApplyLobbyRelay();
                if (!homeMode && (current.Length == 0 || current == "page:friends" || (current.StartsWith("#") && !lobby.Channels.Contains(current.Substring(1)))))
                {
                    string want = Config.Get("view." + lobby.Server, "");
                    if (want.StartsWith("#") && lobby.Channels.Contains(want.Substring(1))) Open(want);
                    else if (lobby.Channels.Count > 0) Open("#" + lobby.Channels[0]);
                }
            }
            RefreshAll();
        }

        HashSet<string> lastVoice = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void OnVoiceRooms()
        {
            HashSet<string> now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (VoiceRoom vr in lobby.Rooms)
                if (vr.Name == lobby.VoiceRoomName)
                    foreach (VoiceMember m in vr.Members)
                        if (!string.Equals(m.User, lobby.User, StringComparison.OrdinalIgnoreCase)) now.Add(m.User);
            if (lobby.VoiceRoomName.Length > 0)
            {
                foreach (string u in now) if (!lastVoice.Contains(u)) { voice.Play(Sfx.OtherJoin); break; }
                foreach (string u in lastVoice) if (!now.Contains(u)) { voice.Play(Sfx.OtherLeave); break; }
            }
            lastVoice = now;
        }

        void OnHistory(string key, List<ChatMsg> list)
        {
            logs[key] = list;
            if (key == current) chat.SetMessages(list);
        }

        bool MentionsMe(ChatMsg m)
        {
            string me = lobby.User ?? "";
            if (me.Length == 0 || string.Equals(m.User, me, StringComparison.OrdinalIgnoreCase)) return false;
            string t = (m.Text ?? "").ToLowerInvariant();
            return t.Contains("@" + me.ToLowerInvariant()) || t.Contains("@everyone") || t.Contains("@here");
        }

        void OnMessage(string key, ChatMsg m)
        {
            if (key.StartsWith("@") && Config.Get("privacy.strangers", "1") == "0" && !IsFriend(m.User ?? "")
                && !string.Equals(m.User, lobby.User, StringComparison.OrdinalIgnoreCase)) return;   // Data & Privacy: friends only
            if (key.StartsWith("@") && DmHidden(key.Substring(1))) SetDmHidden(key.Substring(1), false);
            List<ChatMsg> l;
            if (!logs.TryGetValue(key, out l)) { l = new List<ChatMsg>(); logs[key] = l; }
            l.Add(m);
            Dictionary<string, DateTime> t;
            if (typing.TryGetValue(key, out t)) t.Remove(m.User);
            bool mine = string.Equals(m.User, lobby.User, StringComparison.OrdinalIgnoreCase);
            bool looking = key == current && ContainsFocus && WindowState != FormWindowState.Minimized && !settings.Visible;
            if (key == current) chat.Add(m);
            if (!mine && looking && Config.Get("notify.snd.current", "0") == "1" && Config.Get("notify.snd.off", "0") != "1" && !IsMutedKey(key)) voice.Play(Sfx.Message);
            if (!mine && !looking)
            {
                int n; unread.TryGetValue(key, out n); unread[key] = n + 1;
                bool ping = (key.StartsWith("@") || MentionsMe(m)) && !ignored.Contains(m.User ?? "");
                if (ping)
                {
                    mentions.TryGetValue(key, out n); mentions[key] = n + 1;
                    if (!IsMutedKey(key) && lobby.Presence != "dnd" && Config.Get("notify.snd.off", "0") != "1") voice.Play(Sfx.Message);
                    Notify(key.StartsWith("@") ? m.User : m.User + " in #" + key.Substring(1), m.IsInvite ? "Invited you to " + Look.GameTitle(m.Game) : m.Text);
                }
                else if (m.IsInvite && !IsMutedKey(key)) Notify(m.User + " is starting a game", Look.GameTitle(m.Game) + ": " + m.Title);
                RefreshNav();
                RefreshRail();
                UpdateTitle();
            }
        }

        void OnTyping(string key, string who)
        {
            Dictionary<string, DateTime> t;
            if (!typing.TryGetValue(key, out t)) { t = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase); typing[key] = t; }
            t[who] = DateTime.Now.AddSeconds(6);
            UpdateTyping();
        }

        void UpdateTyping()
        {
            Dictionary<string, DateTime> t;
            List<string> who = new List<string>();
            if (typing.TryGetValue(current, out t))
                foreach (KeyValuePair<string, DateTime> kv in t) if (kv.Value > DateTime.Now) who.Add(kv.Key);
            string s = who.Count == 0 ? "" : who.Count == 1 ? who[0] + " is typing\u2026"
                : who.Count == 2 ? who[0] + " and " + who[1] + " are typing\u2026" : "Several people are typing\u2026";
            if (toastUntil > DateTime.Now) return;
            if (lblTyping.Text != s) { lblTyping.ForeColor = Look.Soft; lblTyping.Text = s; }
        }

        DateTime toastUntil = DateTime.MinValue;

        void Toast(string text)
        {
            lblTyping.ForeColor = Look.Accent;
            lblTyping.Text = text;
            toastUntil = DateTime.Now.AddSeconds(5);
            if (!convo.Visible && settings != null && !settings.Visible && text.Length > 0)
                Notify("GameSpy Revival", text, true);
        }

        void Notify(string title, string text, bool quietIfFocused = false)
        {
            bool focused = ContainsFocus && WindowState != FormWindowState.Minimized;
            if (focused && quietIfFocused) return;
            if (lobby.Presence == "dnd") return;
            if (!focused && Config.Get("notify.balloon", "1") == "1")
            {
                try { tray.ShowBalloonTip(4000, title, string.IsNullOrEmpty(text) ? " " : text, ToolTipIcon.None); } catch (Exception) { }
            }
            if (!focused && Config.Get("notify.flash", "1") == "1")
            {
                FLASHWINFO f = new FLASHWINFO();
                f.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
                f.hwnd = Handle; f.dwFlags = 3 | 12; f.uCount = 3;
                try { FlashWindowEx(ref f); } catch (Exception) { }
            }
        }

        // ---------------------------------------------------------------- actions

        void NavPicked(NavItem it)
        {
            switch (it.Kind)
            {
                case NavKind.Voice: JoinVoice((string)it.Tag); break;
                case NavKind.VoiceMember: ShowProfile((string)it.Tag, Cursor.Position); break;
                case NavKind.Header: if (it.Key.StartsWith("cat:")) ToggleCategory(it.Key); break;
                default: Open(it.Key); break;
            }
        }

        void NavMenu(NavItem it, Point p)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            DarkMenu.Apply(m);
            if (it.Kind == NavKind.Voice)
            {
                string room = (string)it.Tag;
                if (lobby.VoiceRoomName == room) m.Items.Add("Disconnect", null, delegate { LeaveVoice(); });
                else m.Items.Add("Join Voice", null, delegate { JoinVoice(room); });
                m.Items.Add("Invite to Voice", null, delegate { InvitePeople(lobby.Server); });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("Copy Channel Name", null, delegate { CopyText(room); });
            }
            else if (it.Kind == NavKind.VoiceMember)
            {
                string u = (string)it.Tag;
                m.Items.Add("Profile", null, delegate { ShowProfile(u, Cursor.Position); });
                AddPersonItems(m, u);
            }
            else if (it.Kind == NavKind.Channel || it.Kind == NavKind.Dm)
            {
                string key = it.Key;
                m.Items.Add("Mark As Read", null, delegate { unread.Remove(key); mentions.Remove(key); RefreshNav(); RefreshRail(); UpdateTitle(); });
                m.Items.Add(new ToolStripSeparator());
                if (it.Kind == NavKind.Channel)
                {
                    m.Items.Add("Invite to Channel", null, delegate { InvitePeople(lobby.Server); });
                    m.Items.Add("Copy Channel Name", null, delegate { CopyText("#" + key.Substring(1)); });
                    m.Items.Add(new ToolStripSeparator());
                    bool mu = IsMutedKey(key);
                    m.Items.Add(mu ? "Unmute Channel" : "Mute Channel", null, delegate { SetMutedKey(key, !mu); });
                }
                else
                {
                    string u = it.Text;
                    m.Items.Add("Profile", null, delegate { ShowProfile(u, Cursor.Position); });
                    AddPersonItems(m, u);
                    bool mu = IsMutedKey(key);
                    m.Items.Add(new ToolStripSeparator());
                    m.Items.Add(mu ? "Unmute @" + u : "Mute @" + u, null, delegate { SetMutedKey(key, !mu); });
                    m.Items.Add("Close DM", null, delegate { SetDmHidden(u, true); unread.Remove(key); mentions.Remove(key); if (current == key) Open("page:friends"); RefreshNav(); });
                }
            }
            else if (it.Kind == NavKind.Header && it.Key.StartsWith("cat:"))
            {
                string k = it.Key;
                m.Items.Add(it.Collapsed ? "Expand Category" : "Collapse Category", null, delegate { ToggleCategory(k); });
                m.Items.Add("Collapse All Categories", null, delegate { Config.Set("ui.collapsed", "cat:text|cat:voice"); RefreshNav(); });
                m.Items.Add("Expand All Categories", null, delegate { Config.Set("ui.collapsed", ""); RefreshNav(); });
            }
            else if (it.Kind == NavKind.Game)
            {
                string k = it.Key;
                m.Items.Add("Open", null, delegate { Open(k); });
                if (k == "game:mf") m.Items.Add("Invite Crew to Metal Fatigue", null, delegate { Open(k); HeadAction(); });
            }
            else return;
            ColorMenu(m.Items);
            m.Show(nav, p);
        }

        static void ColorMenu(ToolStripItemCollection items)
        {
            foreach (ToolStripItem ti in items)
            {
                if (ti is ToolStripLabel || ti is ToolStripControlHost) continue;
                if (ti.Tag as string != "danger") ti.ForeColor = Look.Text;
                ToolStripMenuItem mi = ti as ToolStripMenuItem;
                if (mi != null && mi.DropDownItems.Count > 0) { mi.DropDown.BackColor = Look.Card; ColorMenu(mi.DropDownItems); }
            }
        }

        static void CopyText(string t) { try { Clipboard.SetText(t); } catch (Exception) { } }

        Friend FindPerson(string name)
        {
            foreach (Friend f in lobby.Friends) if (f.Kind == 0 && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            foreach (Friend f in lobby.Members) if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        bool IsFriend(string name)
        {
            foreach (Friend f in lobby.Friends) if (f.Kind == 0 && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        void PersonClicked(string name)
        {
            if (string.Equals(name, lobby.User, StringComparison.OrdinalIgnoreCase)) return;
            if (IsFriend(name)) { homeMode = true; Open("@" + name); RefreshAll(); }
            else Toast("Right-click " + name + " to add them as a friend, then you can message them.");
        }

        void AddPersonItems(ContextMenuStrip m, string u)
        {
            bool me = string.Equals(u, lobby.User, StringComparison.OrdinalIgnoreCase);
            if (me) { m.Items.Add("Copy Username", null, delegate { CopyText(u); }); return; }
            if (IsFriend(u)) m.Items.Add("Message", null, delegate { homeMode = true; Open("@" + u); RefreshAll(); });
            else m.Items.Add("Add Friend", null, delegate { lobby.AddFriend(u); });
            m.Items.Add("Add Note", null, delegate
            {
                string n = Prompt("Note about " + u, "Only you can see this.", Config.Get("note." + u.ToLowerInvariant(), ""));
                if (n != null) Config.Set("note." + u.ToLowerInvariant(), n);
            });
            m.Items.Add("Mention", null, delegate
            {
                if (!current.StartsWith("#") && lobby.Channels.Count > 0) { homeMode = false; Open("#" + lobby.Channels[0]); }
                composer.Box.Text = "@" + u + " " + composer.Box.Text;
                composer.Box.SelectionStart = composer.Box.Text.Length;
                composer.Box.Focus();
            });
            bool inMyRoom = false;
            foreach (VoiceRoom vr in lobby.Rooms)
                if (vr.Name == lobby.VoiceRoomName) foreach (VoiceMember vm in vr.Members) if (string.Equals(vm.User, u, StringComparison.OrdinalIgnoreCase)) inMyRoom = true;
            if (inMyRoom)
            {
                m.Items.Add(new ToolStripSeparator());
                int cur = voice.GetUserVolume(u);
                ToolStripMenuItem vol = new ToolStripMenuItem("Volume (" + cur + "%)");
                foreach (int v in new int[] { 25, 50, 75, 100, 150, 200 })
                {
                    int vv = v;
                    ToolStripMenuItem mi = new ToolStripMenuItem(v + "%");
                    mi.Checked = cur == v;
                    mi.ForeColor = Look.Text;
                    mi.Click += delegate { voice.SetUserVolume(u, vv); };
                    vol.DropDownItems.Add(mi);
                }
                vol.DropDown.BackColor = Look.Card;
                m.Items.Add(vol);
                bool lm = voice.IsLocalMuted(u);
                m.Items.Add(lm ? "Unmute for me" : "Mute for me", null, delegate { voice.SetLocalMuted(u, !lm); });
            }
            m.Items.Add(new ToolStripSeparator());
            if (IsFriend(u))
            {
                ToolStripItem rf = m.Items.Add("Remove Friend", null, delegate
                {
                    if (MessageBox.Show(this, "Remove " + u + " from your friends?", "GameSpy Revival", MessageBoxButtons.YesNo) == DialogResult.Yes) lobby.RemoveFriend(u);
                });
                rf.Tag = "danger"; rf.ForeColor = Look.Red;
            }
            bool ig = ignored.Contains(u);
            ToolStripItem igi = m.Items.Add(ig ? "Unignore" : "Ignore", null, delegate { SetIgnored(u, !ig); });
            if (!ig) { igi.Tag = "danger"; igi.ForeColor = Look.Red; }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Copy Username", null, delegate { CopyText(u); });
        }

        void PersonMenu(string name, Control at, Point p)
        {
            if (name.Length == 0) return;
            ContextMenuStrip m = new ContextMenuStrip();
            DarkMenu.Apply(m);
            m.Items.Add("Profile", null, delegate { ShowProfile(name, Cursor.Position); });
            AddPersonItems(m, name);
            ColorMenu(m.Items);
            m.Show(at, p);
        }

        void FriendAction(PersonRow r, string a)
        {
            if (r.Key.Length == 0) return;
            if (a == "Accept") lobby.AddFriend(r.Key);
            else if (a == "Decline" || a == "Cancel") lobby.RemoveFriend(r.Key);
            else if (a == "Message" || a == "") { if (IsFriend(r.Key)) Open("@" + r.Key); }
        }

        // ---------------------------------------------------------------- history, edits, status, profiles

        void GoBack() { StepHistory(backStack, fwdStack); }
        void GoForward() { StepHistory(fwdStack, backStack); }

        void StepHistory(List<string> from, List<string> to)
        {
            if (from.Count == 0) return;
            string target = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            to.Add((homeMode ? "h|" : "g|") + current);
            bool home = target.StartsWith("h|");
            string key = target.Substring(2);
            navigatingHistory = true;
            try
            {
                if (home != homeMode) { homeMode = home; RefreshAll(); }
                Open(key);
            }
            finally { navigatingHistory = false; }
            UpdateTitle();
        }

        void OnMessageUpdated(string key, ChatMsg m)
        {
            List<ChatMsg> l;
            if (logs.TryGetValue(key, out l))
                for (int i = 0; i < l.Count; i++) if (l[i].Id == m.Id) { l[i] = m; break; }
            if (key == current) chat.Replace(m);
        }

        void OnMessageDeleted(string key, long id)
        {
            List<ChatMsg> l;
            if (logs.TryGetValue(key, out l)) l.RemoveAll(x => x.Id == id);
            if (key == current) chat.Remove(id);
            if (composer.Editing != null && composer.Editing.Id == id) composer.Cancel();
            if (composer.ReplyTo != null && composer.ReplyTo.Id == id) composer.Cancel();
        }

        void SetIgnored(string u, bool on)
        {
            if (on) ignored.Add(u); else ignored.Remove(u);
            Config.Set("ignored", string.Join("|", new List<string>(ignored).ToArray()));
            chat.Invalidate();
            Toast(on ? u + " is ignored. Their messages are hidden and won't ping you." : u + " is no longer ignored.");
        }

        void StatusMenu(Point at)
        {
            if (lobby.State == LobbyState.SignedOut) { ShowSignIn(lobby.Server); return; }
            ContextMenuStrip m = new ContextMenuStrip();
            DarkMenu.Apply(m);
            m.ShowImageMargin = true;
            ToolStripLabel head = new ToolStripLabel(lobby.User + (lobby.Custom.Length > 0 ? "\n" + lobby.Custom : ""));
            head.Font = new Font("Segoe UI Semibold", 10f);
            head.ForeColor = Color.White;
            m.Items.Add(head);
            m.Items.Add(new ToolStripSeparator());
            foreach (string pr in new string[] { "online", "idle", "dnd", "invisible" })
            {
                string pp = pr;
                ToolStripMenuItem mi = new ToolStripMenuItem(Look.PresenceName(pr));
                mi.Image = Dot(Look.PresenceColor(pr == "invisible" ? "offline" : pr));
                mi.Checked = lobby.Presence == pr;
                if (pr == "dnd") mi.ToolTipText = "You will not receive desktop notifications";
                mi.Click += delegate { autoIdle = false; lobby.SetPresence(pp, lobby.Custom); RefreshUser(); };
                m.Items.Add(mi);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(lobby.Custom.Length > 0 ? "Edit Custom Status" : "Set Custom Status", null, delegate
            {
                string c = Prompt("Set a custom status", "What's happening?", lobby.Custom);
                if (c != null) { lobby.SetPresence(lobby.Presence, c.Length > 60 ? c.Substring(0, 60) : c); RefreshUser(); }
            });
            if (lobby.Custom.Length > 0) m.Items.Add("Clear Custom Status", null, delegate { lobby.SetPresence(lobby.Presence, ""); RefreshUser(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Edit Profile", null, delegate { OpenSettings("account"); });
            m.Items.Add("Switch Groups", null, delegate { homeMode = true; RefreshAll(); Open("page:friends"); });
            m.Items.Add("Copy Username", null, delegate { CopyText(lobby.User ?? ""); });
            ColorMenu(m.Items);
            m.Show(new Point(at.X, at.Y - m.PreferredSize.Height - 6));
        }

        static Bitmap Dot(Color c)
        {
            Bitmap b = new Bitmap(12, 12);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush br = new SolidBrush(c)) g.FillEllipse(br, 1, 1, 10, 10);
            }
            return b;
        }

        void ShowProfile(string name, Point at)
        {
            if (string.IsNullOrEmpty(name)) return;
            bool me = string.Equals(name, lobby.User, StringComparison.OrdinalIgnoreCase);
            Friend f = FindPerson(name);
            if (f == null)
            {
                f = new Friend();
                f.Name = name;
                if (me) { f.Online = lobby.State == LobbyState.SignedIn; f.Presence = lobby.Presence; f.Custom = lobby.Custom; }
            }
            ProfileCard card = new ProfileCard(f, IsFriend(name), me);
            card.Message += delegate(string u) { homeMode = true; Open("@" + u); RefreshAll(); };
            card.AddFriend += delegate(string u) { lobby.AddFriend(u); Toast("Friend request sent to " + u + "."); };
            Rectangle wa = Screen.FromPoint(at).WorkingArea;
            Size sz = card.PreferredSize;
            int x = Math.Min(at.X, wa.Right - sz.Width - 8), y = Math.Min(at.Y, wa.Bottom - sz.Height - 8);
            card.Show(new Point(Math.Max(wa.X, x), Math.Max(wa.Y, y)));
        }

        void CheckIdle()
        {
            if (lobby.State != LobbyState.SignedIn) return;
            LASTINPUTINFO li = new LASTINPUTINFO();
            li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref li)) return;
            uint idleMs = (uint)Environment.TickCount - li.dwTime;
            bool away = idleMs > 10 * 60 * 1000;
            string chosen = Config.Get("status.presence", "online");
            if (away && !autoIdle && chosen == "online")
            {
                autoIdle = true;
                lobby.SetPresenceOnly("idle");
                RefreshUser();
            }
            else if (!away && autoIdle)
            {
                autoIdle = false;
                lobby.SetPresenceOnly(chosen);
                RefreshUser();
            }
        }

        string Prompt(string heading, string hint, string value)
        {
            using (Form f = new Form())
            {
                f.StartPosition = FormStartPosition.CenterParent;
                f.Text = heading;
                f.BackColor = Look.Card;
                f.ClientSize = new Size(420, 150);
                Label l = new Label();
                l.Text = heading; l.ForeColor = Color.White; l.Font = new Font("Segoe UI Semibold", 12f);
                l.SetBounds(20, 14, 380, 26);
                TextBox t = new TextBox();
                t.Text = value; t.BackColor = Look.Rail; t.ForeColor = Look.Text; t.BorderStyle = BorderStyle.FixedSingle;
                t.Font = new Font("Segoe UI", 10.5f);
                t.SetBounds(20, 50, 380, 26);
                Label h = new Label();
                h.Text = hint; h.ForeColor = Look.Muted; h.SetBounds(20, 80, 380, 18);
                Button ok = new Button(); ok.Text = "Save"; ok.DialogResult = DialogResult.OK; ok.SetBounds(300, 108, 100, 30);
                Button no = new Button(); no.Text = "Cancel"; no.DialogResult = DialogResult.Cancel; no.SetBounds(190, 108, 100, 30);
                Theme.Primary(ok); Theme.Style(no);
                f.Controls.AddRange(new Control[] { l, t, h, ok, no });
                f.AcceptButton = ok; f.CancelButton = no;
                Look.Modal(f, Look.Card);
                f.BackColor = Look.Card;
                f.Paint += delegate(object s2, PaintEventArgs e2) { using (Pen bp = new Pen(Look.Line)) e2.Graphics.DrawRectangle(bp, 0, 0, f.ClientSize.Width - 1, f.ClientSize.Height - 1); };
                return f.ShowDialog(this) == DialogResult.OK ? t.Text.Trim() : null;
            }
        }

        void AddFriendNow()
        {
            string n = txtAddFriend.Text.Trim();
            if (n.Length == 0) return;
            if (lobby.State != LobbyState.SignedIn) { lblFriendsMsg.Text = "Sign in first (bottom left)."; return; }
            lobby.AddFriend(n);
            lblFriendsMsg.ForeColor = Look.Green;
            lblFriendsMsg.Text = "Request sent to " + n + ". They'll see it under Pending.";
            txtAddFriend.Text = "";
        }

        void SendFromComposer(string text)
        {
            if (lobby.State != LobbyState.SignedIn) { Toast("Sign in first (bottom left) to chat."); return; }
            if (current.StartsWith("#") && composer.Editing != null)
            {
                if (text != composer.Editing.Text) lobby.EditMessage(current.Substring(1), composer.Editing.Id, text);
                return;
            }
            if (current.StartsWith("#") && composer.ReplyTo != null) lobby.SendReply(current.Substring(1), text, composer.ReplyTo.Id);
            else if (current.StartsWith("#")) lobby.SendChat(current.Substring(1), text);
            else if (current.StartsWith("@"))
            {
                string to = current.Substring(1);
                lobby.SendDm(to, text);
                ChatMsg m = new ChatMsg();
                m.User = lobby.User; m.Text = text; m.Time = DateTime.Now;
                List<ChatMsg> l;
                if (!logs.TryGetValue(current, out l)) { l = new List<ChatMsg>(); logs[current] = l; }
                l.Add(m);
                chat.Add(m);
            }
        }

        void ComposerPlus()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            DarkMenu.Apply(m);
            m.Items.Add("Invite the crew to Metal Fatigue", null, delegate { Open("game:mf"); HeadAction(); });
            m.Items.Add("Pick a Quake III server to share", null, delegate { Open("game:q3"); Toast("Pick a server, then press Invite crew."); });
            m.Items.Add("Pick an Unreal Tournament server to share", null, delegate { Open("game:ut"); Toast("Pick a server, then press Invite crew."); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Ping everyone (@everyone)", null, delegate { composer.Box.Text = "@everyone " + composer.Box.Text; composer.Box.SelectionStart = composer.Box.Text.Length; composer.Box.Focus(); });
            foreach (ToolStripItem ti in m.Items) ti.ForeColor = Look.Text;
            m.Show(composer, new Point(28, -m.PreferredSize.Height + 14));
        }

        void PostInvite(string game, string title, string address, string detail)
        {
            if (lobby.State != LobbyState.SignedIn) { Toast("Sign in to a group first to invite people."); return; }
            string ch = lobby.Channels.Contains("looking-for-game") ? "looking-for-game" : lobby.Channels.Count > 0 ? lobby.Channels[0] : "general";
            lobby.SendInvite(ch, game, title, address, detail, "");
            homeMode = false;
            Open("#" + ch);
            RefreshAll();
            Toast("Invite posted in #" + ch + ".");
        }

        void JoinInvite(ChatMsg m)
        {
            if (m.Game == "mf")
            {
                string addr = m.Address;
                if (addr.Length == 0 || addr.StartsWith("127."))
                {
                    if (lobby.RelayPort <= 0) { Toast("This group's host isn't running a Metal Fatigue server right now."); return; }
                    string host = lobby.Server;
                    int i = host.LastIndexOf(':');
                    if (i > 0) host = host.Substring(0, i);
                    if (host == "localhost" || LobbyHost.Running) host = "127.0.0.1";
                    addr = host + ":" + lobby.RelayPort;
                }
                Open("game:mf");
                mf.JoinRelay(addr);
                Toast("Starting Metal Fatigue. Open Multiplayer and join " + m.User + "'s game.");
                return;
            }
            foreach (ArenaPanel a in arenas)
                if (a.Game.Id == m.Game)
                {
                    Open("game:" + m.Game);
                    a.JoinAddress(m.Address);
                    return;
                }
            Toast("This app can't start " + Look.GameTitle(m.Game) + ".");
        }

        // ---- voice
        int lastTimerSec = -1;

        static string Elapsed(TimeSpan d)
        {
            return d.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)d.TotalHours, d.Minutes, d.Seconds) : string.Format("{0}:{1:00}", d.Minutes, d.Seconds);
        }

        void JoinVoice(string room)
        {
            if (lobby.State != LobbyState.SignedIn) { Toast("Sign in first to use voice."); return; }
            if (lobby.VoiceRoomName == room) return;
            bool was = lobby.VoiceRoomName.Length > 0;
            lobby.JoinVoice(room);
            voice.SetInRoom(true);
            if (!was) { voice.Play(Sfx.Join); voiceSince = DateTime.Now; }
            RefreshNav();
            RefreshUser();
        }

        void LeaveVoice()
        {
            if (lobby.VoiceRoomName.Length == 0) return;
            lobby.LeaveVoice();
            voice.SetInRoom(false);
            voice.Play(Sfx.Leave);
            voiceSince = DateTime.MinValue;
            RefreshNav();
            RefreshUser();
        }

        void ToggleMute()
        {
            if (voice.Deafened) { voice.Deafened = false; voice.Muted = false; voice.Play(Sfx.Undeafen); }
            else { voice.Muted = !voice.Muted; voice.Play(voice.Muted ? Sfx.Mute : Sfx.Unmute); }
            lobby.SendVoiceState();
            RefreshUser();
        }

        void ToggleDeaf()
        {
            voice.Deafened = !voice.Deafened;
            voice.Play(voice.Deafened ? Sfx.Deafen : Sfx.Undeafen);
            lobby.SendVoiceState();
            RefreshUser();
        }

        // ---- groups and sign-in
        DateTime nextJoinCheck = DateTime.MinValue;

        // a gamespy://join/ link was clicked (or the app was opened a second time)
        void JoinLink(string link)
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
            if (link == "show") return;
            string addr = Invite.Resolve(link);
            if (addr.Length == 0) { Toast("That invite link doesn't look right."); return; }
            foreach (string g in LobbyClient.SavedGroups())
                if (string.Equals(g, addr, StringComparison.OrdinalIgnoreCase)) { SwitchGroup(g); return; }
            ShowSignIn(addr);
        }

        // the + button: Discord-style "Create Your Group" wizard, or join a friend's
        public void AddGroup(bool firstRun)
        {
            bool join;
            using (GroupWizard w = new GroupWizard(lobby, firstRun))
            {
                DialogResult r = w.ShowDialog(this);
                join = w.JoinChosen;
                if (r == DialogResult.OK) { homeMode = false; current = ""; OnLobbyChanged(); RefreshAll(); return; }
            }
            if (join) ShowSignIn("");
        }

        public void ShowSignIn(string server)
        {
            string before = lobby.Server;
            if (lobby.VoiceRoomName.Length > 0 && server != null && server != before) LeaveVoice();
            logs.Clear(); unread.Clear(); mentions.Clear(); typing.Clear(); dmHistoryAsked.Clear();
            using (SignInDialog d = new SignInDialog(lobby, server))
                if (d.ShowDialog(this) == DialogResult.OK) { homeMode = false; current = ""; OnLobbyChanged(); }
        }

        void SwitchGroup(string server)
        {
            if (server == lobby.Server && lobby.State != LobbyState.SignedOut)
            {
                homeMode = false;
                string want = Config.Get("view." + server, "");
                if (want.Length == 0 || IsGameKey(want) || want.StartsWith("@") || want.StartsWith("page:")) want = lobby.Channels.Count > 0 ? "#" + lobby.Channels[0] : "#general";
                Open(want);
                RefreshAll();
                return;
            }
            if (lobby.VoiceRoomName.Length > 0) LeaveVoice();
            logs.Clear(); unread.Clear(); mentions.Clear(); typing.Clear(); dmHistoryAsked.Clear();
            homeMode = false;
            current = "";
            if (!lobby.Resume(server)) ShowSignIn(server);
            RefreshAll();
        }

        // ---- settings
        public void OpenSettings(string pane)
        {
            settings.Show(pane);
            PlaceSettings();
            settings.Visible = true;
            settings.BringToFront();
            settings.Focus();
        }

        // settings cover the whole window under the title bar, like Discord
        void PlaceSettings()
        {
            if (settings == null) return;
            settings.Dock = DockStyle.None;
            int top = title != null ? title.Bottom : Padding.Top;
            settings.Bounds = new Rectangle(Padding.Left, top, ClientSize.Width - Padding.Horizontal, Math.Max(0, ClientSize.Height - top - Padding.Bottom));
        }

        public void CloseSettings()
        {
            settings.Visible = false;
            RefreshUser();
            RefreshAll();
        }

        // ---------------------------------------------------------------- hosting, presence

        public static int HostPort()
        {
            int p;
            return int.TryParse(Config.Get("lobby.port", "27901"), out p) && p > 0 && p < 65536 ? p : 27901;
        }

        public string StartHosting()
        {
            int relay = 27900;   // the group server runs the Metal Fatigue server itself
            if (mf != null) { try { mf.StopRelayForLobby(); } catch (Exception) { } }
            string err = LobbyHost.Start(HostPort(), relay, Config.Get("host.name", "Game Night"), Config.Get("host.channels", ""));
            if (err == null && Config.Get("lobby.server", "").Length == 0) Config.Set("lobby.server", "127.0.0.1:" + HostPort());
            return err;
        }

        public void StopHosting()
        {
            LobbyHost.Stop();
        }

        public bool RelayRunning { get { return LobbyHost.RelayRunning; } }

        void ApplyLobbyRelay()
        {
            if (mf == null || lobby.State != LobbyState.SignedIn || lobby.RelayPort <= 0) return;
            string key = lobby.Server + "/" + lobby.RelayPort;
            if (key == relayAppliedFor) return;
            relayAppliedFor = key;
            string host = lobby.Server;
            int i = host.LastIndexOf(':');
            if (i > 0) host = host.Substring(0, i);
            if (host == "localhost" || LobbyHost.Running) host = "127.0.0.1";
            mf.UseLobbyRelay(host + ":" + lobby.RelayPort);
        }

        static readonly string[][] Watched = new string[][]
        {
            new string[] { "mf", "Metal Fatigue", "MFatigue" },
            new string[] { "q3", "Quake III Arena", "quake3e.x64", "quake3e", "ioquake3.x86_64", "ioquake3.x86", "ioquake3", "quake3" },
            new string[] { "oa", "OpenArena", "openarena", "openarena.x86_64", "openarena.x86" },
            new string[] { "ut", "Unreal Tournament", "UnrealTournament", "UnrealTournament64" },
            new string[] { "bf2", "Battlefield 2", "BF2" },
            new string[] { "ra2", "Red Alert 2", "gamemd", "ra2", "gamemd-spawn" },
        };

        void UpdatePresence()
        {
            if (Config.Get("activity.share", "1") == "1")
            foreach (string[] w in Watched)
                for (int i = 2; i < w.Length; i++)
                {
                    Process[] ps = null;
                    try { ps = Process.GetProcessesByName(w[i]); } catch (Exception) { }
                    if (ps != null && ps.Length > 0)
                    {
                        foreach (Process p in ps) p.Dispose();
                        lobby.SetStatus("Playing " + w[1], w[0]);
                        return;
                    }
                }
            string text = Config.Get("activity.app", "1") == "0" ? "Online"
                : lobby.VoiceRoomName.Length > 0 ? "In voice \u00b7 " + lobby.VoiceRoomName
                : current.StartsWith("game:") ? "Browsing " + lblTitle.Text + " servers" : "Online";
            lobby.SetStatus(text, "");
        }
    }

    // ---------------------------------------------------------------- full-window settings, Discord style

    // Discord's round settings close button with "ESC" under it
    class CloseX : Control
    {
        public event Action Clicked;
        bool hot;
        public CloseX()
        {
            Size = new Size(40, 58);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            BackColor = Look.Main;
        }
        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left && Clicked != null) Clicked(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle c = new Rectangle(2, 2, 35, 35);
            if (hot) using (SolidBrush b = new SolidBrush(Color.FromArgb(26, 36, 51))) g.FillEllipse(b, c);
            using (Pen p = new Pen(hot ? Color.White : Look.Soft, 2f)) g.DrawEllipse(p, c);
            using (Pen p = new Pen(hot ? Color.White : Look.Soft, 2f))
            {
                g.DrawLine(p, 14, 14, 25, 25);
                g.DrawLine(p, 25, 14, 14, 25);
            }
            using (Font f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, "ESC", f, new Rectangle(0, 40, Width, 16), Look.Muted, TextFormatFlags.HorizontalCenter);
        }
    }

    class SettingsScreen : Panel
    {
        readonly Shell shell;
        readonly LobbyClient lobby;
        readonly VoiceEngine voice;
        readonly List<ArenaPanel> arenas;
        readonly NavList menu = new NavList();
        readonly Panel pane = new Panel();
        readonly Dictionary<string, Control> panes = new Dictionary<string, Control>();
        string currentPane = "";

        public SettingsScreen(Shell s, LobbyClient lc, VoiceEngine ve, List<ArenaPanel> ar)
        {
            shell = s; lobby = lc; voice = ve; arenas = ar;
            BackColor = Look.Main;

            Panel left = new Panel();
            left.Dock = DockStyle.Left;
            left.Width = 300;
            left.BackColor = Look.Side;
            menu.SetBounds(60, 40, 218, 700);
            menu.Picked += delegate(NavItem it)
            {
                if (it.Key == "signout")
                {
                    if (MessageBox.Show(FindForm(), "Sign out of " + lobby.Group + "?", "GameSpy Revival", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    { lobby.SignOut(); shell.CloseSettings(); }
                }
                else Show(it.Key);
            };
            left.Controls.Add(menu);

            pane.Dock = DockStyle.Fill;
            pane.BackColor = Look.Main;
            pane.Padding = new Padding(30, 30, 80, 20);

            CloseX close = new CloseX();
            close.Clicked += delegate { shell.CloseSettings(); };
            // like Discord: on a big window settings float as a centered card over the dimmed app;
            // on a small window the card fills the whole window
            Panel card = new Panel();
            card.BackColor = Look.Main;
            card.Controls.Add(pane);
            card.Controls.Add(left);
            card.Controls.Add(close);
            Controls.Add(card);
            BackColor = Color.FromArgb(3, 5, 9);
            Layout += delegate
            {
                bool floating = Width >= 1280 && Height >= 760;
                int cw = floating ? Math.Min(1400, Width - 120) : Width;
                int ch = floating ? Height - 70 : Height;
                card.Bounds = new Rectangle((Width - cw) / 2, floating ? 35 : 0, cw, ch);
                int lw = cw >= 1100 ? 250 : 220;
                if (left.Width != lw) left.Width = lw;
                menu.SetBounds(12, 24, lw - 20, Math.Max(100, left.Height - 24));
                int contentW = cw - lw;
                int side = Math.Max(30, (contentW - 760) / 2);
                pane.Padding = new Padding(side, 30, Math.Max(70, side), 0);
                close.Location = new Point(cw - 62, 22);
                close.BringToFront();
            };

            panes["voice"] = new VoiceSettingsPage(voice);
            BuildMenu();
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            // same order as Discord's settings
            Head("User settings");
            Item("My Account", "account");
            Item("Data & Privacy", "privacy");
            Item("Notifications", "notify");
            Head("Experience");
            Item("Voice & Video", "voice");
            Item("Appearance", "appearance");
            Item("Accessibility", "a11y");
            Item("System", "system");
            Item("Language & Time", "lang");
            Head("Games & Apps");
            Item("Registered Games", "games");
            Item("Activity Privacy", "activity");
            Head("Hosting");
            Item("Host this group", "hosting");
            NavItem gap = new NavItem(); gap.Kind = NavKind.Gap; menu.Items.Add(gap);
            Item("Log Out", "signout");
            menu.Selected = currentPane;
            menu.Rebuild();
        }

        void Head(string t) { NavItem h = new NavItem(); h.Kind = NavKind.Header; h.Text = t; menu.Items.Add(h); }
        void Item(string t, string k) { NavItem h = new NavItem(); h.Kind = NavKind.Link; h.Text = t; h.Key = k; menu.Items.Add(h); }

        public void Show(string key)
        {
            currentPane = key;
            Control c;
            if (key != "voice" || !panes.TryGetValue(key, out c))
            {
                c = Build(key);
                if (key != "voice") { Control old; if (panes.TryGetValue(key, out old)) { pane.Controls.Remove(old); old.Dispose(); } }
                panes[key] = c;
            }
            foreach (Control k in pane.Controls) k.Visible = false;
            if (!pane.Controls.Contains(c)) { c.Dock = DockStyle.Fill; Look.DarkScroll(c); pane.Controls.Add(c); }
            c.Visible = true;
            menu.Selected = key;
            menu.Invalidate();
        }

        // a simple vertical page of settings
        class Page : Panel
        {
            int y = 0;
            public Page(string title)
            {
                AutoScroll = true;
                BackColor = Look.Main;
                Label t = new Label();
                t.Text = title.Replace("&", "&&");
                t.Font = new Font("Segoe UI Semibold", 18f);
                t.ForeColor = Color.White;
                t.SetBounds(0, 0, 700, 36);
                Controls.Add(t);
                y = 54;
            }

            public Label Text1(string text, Color c, float size = 10f, int h = 0)
            {
                Label l = new Label();
                l.Text = text;
                l.Font = new Font("Segoe UI", size);
                l.ForeColor = c;
                l.MaximumSize = new Size(700, 0);
                l.AutoSize = true;
                l.Location = new Point(0, y);
                Controls.Add(l);
                y += Math.Max(h, l.PreferredHeight) + 10;
                return l;
            }

            public Label Caption(string text) { y += 8; Label l = Text1(text.ToUpperInvariant(), Look.Soft, 8f); l.Font = new Font("Segoe UI", 8f, FontStyle.Bold); return l; }

            // a Discord settings row: title on the left, switch on the right, description underneath
            public CheckBox Check(string text, string sub, bool on, Action<bool> changed)
            {
                ToggleCheck cb = new ToggleCheck();
                cb.SwitchRight = true;
                cb.Text = text;
                cb.Checked = on;
                cb.AutoSize = false;
                cb.SetBounds(0, y, 700, 26);
                cb.ForeColor = Look.Text;
                cb.Font = new Font("Segoe UI Semibold", 10f);
                cb.CheckedChanged += delegate { changed(cb.Checked); };
                Controls.Add(cb);
                y += 26;
                if (sub != null) { Label l = Text1(sub, Look.Muted, 9f); l.MaximumSize = new Size(620, 0); y -= 4; }
                y += 12;
                return cb;
            }

            public Label Section(string text)
            {
                y += 10;
                Label l = new Label();
                l.Text = text;
                l.Font = new Font("Segoe UI Semibold", 14f);
                l.ForeColor = Color.White;
                l.SetBounds(0, y, 700, 30);
                Controls.Add(l);
                y += 40;
                return l;
            }

            public void Radio(string[] labels, int selected, Action<int> changed)
            {
                for (int i = 0; i < labels.Length; i++)
                {
                    int idx = i;
                    RadioButton r = new RadioButton();
                    r.Text = labels[i];
                    r.Checked = i == selected;
                    r.ForeColor = Look.Text;
                    r.Font = new Font("Segoe UI", 10f);
                    r.SetBounds(0, y, 700, 26);
                    r.CheckedChanged += delegate { if (r.Checked) changed(idx); };
                    Controls.Add(r);
                    y += 30;
                }
                y += 8;
            }

            public Button Btn(string text, string tag, EventHandler click, int w = 220)
            {
                Button b = new Button();
                b.Text = text;
                b.Tag = tag;
                b.SetBounds(0, y, w, 34);
                b.Click += click;
                Controls.Add(b);
                Theme.Style(b);
                y += 44;
                return b;
            }

            public TextBox Field(string value, int w = 420)
            {
                TextBox t = new TextBox();
                t.Text = value;
                t.SetBounds(0, y, w, 26);
                t.Font = new Font("Segoe UI", 10.5f);
                Controls.Add(t);
                Theme.Style(t);
                y += 36;
                return t;
            }

            public Panel Divider() { Panel p = new Panel(); p.SetBounds(0, y + 4, 700, 1); p.BackColor = Look.Line; Controls.Add(p); y += 18; return p; }
            public int Y { get { return y; } set { y = value; } }
        }

        Control Build(string key)
        {
            switch (key)
            {
                case "account": return AccountPage();
                case "notify": return NotifyPage();
                case "keys": return SystemPage();
                case "system": return SystemPage();
                case "privacy": return PrivacyPage();
                case "appearance": return AppearancePage();
                case "a11y": return AccessibilityPage();
                case "lang": return LanguagePage();
                case "activity": return ActivityPage();
                case "games": return GamesPage();
                case "hosting": return HostingPage();
            }
            return new Page("Settings");
        }

        Control AccountPage()
        {
            Page p = new Page("My Account");
            if (lobby.State == LobbyState.SignedIn)
            {
                p.Text1(lobby.User, Color.White, 16f);
                p.Text1("Signed in to " + lobby.Group + " (" + lobby.Server + ")", Look.Muted);
                p.Divider();
                p.Caption("This PC");
                p.Text1("\"Keep me signed in\" is saved per group, so you can belong to several groups with different accounts.", Look.Muted, 9.5f);
                p.Btn("Sign out", "danger", delegate { lobby.SignOut(); shell.CloseSettings(); });
            }
            else
            {
                p.Text1("You're not signed in.", Look.Muted);
                p.Btn("Sign in", "primary", delegate { shell.CloseSettings(); shell.ShowSignIn(null); });
            }
            p.Divider();
            p.Caption("Your data");
            p.Text1("Settings live in " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSpyRevival")
                + ". Messages and accounts are kept by whoever hosts the group.", Look.Muted, 9.5f);
            p.Divider();
            p.Caption("Your key");
            p.Text1("This PC is your sign-in key for every group you joined without a password. Back it up so a new PC "
                + "or a Windows reinstall doesn't lock you out. Keep the file private: it works like a password.", Look.Muted, 9.5f);
            p.Btn("Back up my identity...", null, delegate
            {
                using (SaveFileDialog d = new SaveFileDialog())
                {
                    d.FileName = "GameSpyRevival-identity.gsrkey";
                    d.Filter = "GameSpy Revival identity (*.gsrkey)|*.gsrkey";
                    if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                    try { Invite.SaveIdentity(d.FileName); MessageBox.Show(FindForm(), "Saved. Keep it somewhere safe, like a USB stick.", "GameSpy Revival"); }
                    catch (Exception ex) { MessageBox.Show(FindForm(), "Couldn't save it: " + ex.Message, "GameSpy Revival"); }
                }
            }, 240);
            p.Btn("Restore from a backup...", null, delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "GameSpy Revival identity (*.gsrkey)|*.gsrkey|All files|*.*";
                    if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                    if (MessageBox.Show(FindForm(), "Replace this PC's key with the one in the backup? Accounts made with this PC's current key stop working here.",
                        "GameSpy Revival", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    string err = null;
                    try { err = Invite.LoadIdentity(d.FileName); } catch (Exception ex) { err = ex.Message; }
                    MessageBox.Show(FindForm(), err ?? "Restored. Press + > Join a Group or pick your account from the list to sign in.", "GameSpy Revival");
                }
            }, 240);
            return p;
        }

        Control NotifyPage()
        {
            Page p = new Page("Notifications");
            p.Section("Overview");
            p.Check("Enable Desktop Notifications", "Pop-ups for @mentions, private messages and game invites when you're not looking at the app.",
                Config.Get("notify.balloon", "1") == "1", delegate(bool on) { Config.Set("notify.balloon", on ? "1" : "0"); });
            p.Check("Enable Taskbar Flashing", "Flashes the app in your taskbar when you have new notifications.",
                Config.Get("notify.flash", "1") == "1", delegate(bool on) { Config.Set("notify.flash", on ? "1" : "0"); });
            p.Caption("Notify me when...");
            p.Check("Friends come online", null, Config.Get("notify.online", "0") == "1", delegate(bool on) { Config.Set("notify.online", on ? "1" : "0"); });
            p.Check("Friends start playing a game", null, Config.Get("notify.playing", "0") == "1", delegate(bool on) { Config.Set("notify.playing", on ? "1" : "0"); });
            p.Divider();
            p.Section("Sounds");
            p.Check("New Message", "Plays for @mentions and private messages.", voice.Settings.SndMessage, delegate(bool on)
            {
                VoiceConfig c = VoiceConfig.Load(); c.SndMessage = on; c.Save(); voice.Reload();
            });
            p.Check("New Message in the channel I'm currently reading", null, Config.Get("notify.snd.current", "0") == "1",
                delegate(bool on) { Config.Set("notify.snd.current", on ? "1" : "0"); });
            p.Check("Disable All Notification Sounds", "Turns off message sounds. Voice sounds (mute, deafen, joins) are under Voice && Video.",
                Config.Get("notify.snd.off", "0") == "1", delegate(bool on) { Config.Set("notify.snd.off", on ? "1" : "0"); });
            p.Divider();
            p.Section("Badges");
            p.Check("Enable Unread Message Badge", "Shows the number of unread mentions in the window and taskbar title.",
                Config.Get("notify.badge", "1") == "1", delegate(bool on) { Config.Set("notify.badge", on ? "1" : "0"); shell.RefreshTitle(); });
            return p;
        }

        Control PrivacyPage()
        {
            Page p = new Page("Data & Privacy");
            p.Section("Who can reach you");
            p.Check("Allow private messages from people who aren't your friends", "When this is off, messages from strangers in your groups are dropped without a ping.",
                Config.Get("privacy.strangers", "1") == "1", delegate(bool on) { Config.Set("privacy.strangers", on ? "1" : "0"); });
            p.Divider();
            p.Section("Ignored people");
            List<string> ig = new List<string>(Config.Get("ignored", "").Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
            if (ig.Count == 0) p.Text1("You haven't ignored anyone. Right-click a name and choose Ignore to hide their messages.", Look.Muted, 9.5f);
            foreach (string u in ig)
            {
                string who = u;
                p.Text1(who, Look.Text, 10.5f);
                p.Btn("Unignore " + who, null, delegate { shell.Unignore(who); Show("privacy"); }, 200);
            }
            p.Divider();
            p.Section("Your data");
            p.Text1("Settings live in " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSpyRevival")
                + ". Messages and accounts live on the PC that hosts each group. Nothing is sent anywhere else.", Look.Muted, 9.5f);
            p.Btn("Open my settings folder", null, delegate
            {
                try { Process.Start("explorer.exe", "\"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSpyRevival") + "\""); } catch (Exception) { }
            }, 240);
            return p;
        }

        Control AppearancePage()
        {
            Page p = new Page("Appearance");
            p.Section("Theme");
            bool mid = Config.Get("ui.theme", "dark") == "midnight";
            p.Radio(new string[] { "Dark (navy)", "Midnight (pure black, like Discord's darkest)" }, mid ? 1 : 0, delegate(int i)
            {
                Config.Set("ui.theme", i == 1 ? "midnight" : "dark");
                MessageBox.Show(FindForm(), "The new theme shows the next time you open GameSpy Revival.", "GameSpy Revival");
            });
            p.Divider();
            p.Section("Layout");
            p.Check("Show the member list", "The list of people on the right side of a channel.", Config.Get("ui.members", "1") == "1",
                delegate(bool on) { shell.ShowMembers(on); });
            return p;
        }

        Control AccessibilityPage()
        {
            Page p = new Page("Accessibility");
            p.Section("Text Readability");
            p.Caption("Text size in chat");
            string[] sizes = { "9.5", "10.25", "11.5", "13" };
            string cur = Config.Get("ui.chatsize", "10.25");
            int sel = Array.IndexOf(sizes, cur); if (sel < 0) sel = 1;
            p.Radio(new string[] { "Small", "Default", "Large", "Larger" }, sel, delegate(int i)
            {
                Config.Set("ui.chatsize", sizes[i]);
                MessageBox.Show(FindForm(), "The new text size shows the next time you open GameSpy Revival.", "GameSpy Revival");
            });
            p.Divider();
            p.Section("Motion");
            p.Check("Flash the speaking ring around avatars", "Turn off if the green ring around people talking is distracting.",
                Config.Get("ui.speakring", "1") == "1", delegate(bool on) { Config.Set("ui.speakring", on ? "1" : "0"); });
            return p;
        }

        Control LanguagePage()
        {
            Page p = new Page("Language & Time");
            p.Section("Time");
            p.Check("Use a 24-hour clock", "Shows 21:30 instead of 9:30 PM next to messages.", Config.Get("ui.24h", "0") == "1",
                delegate(bool on) { Config.Set("ui.24h", on ? "1" : "0"); shell.RefreshChat(); });
            p.Divider();
            p.Section("Language");
            p.Text1("English. Other languages are welcome: the text lives in the source code, and translations can be added there.", Look.Muted, 9.5f);
            return p;
        }

        Control ActivityPage()
        {
            Page p = new Page("Activity Privacy");
            p.Section("What Activity I Share");
            p.Check("Share my activity", "Shows the game you're playing (Quake III, Unreal Tournament, Metal Fatigue...) to your groups and friends.",
                Config.Get("activity.share", "1") == "1", delegate(bool on) { Config.Set("activity.share", on ? "1" : "0"); });
            p.Check("Share what I'm doing in the app", "Shows things like \"In voice: Lobby\" or \"Browsing Quake III Arena servers\".",
                Config.Get("activity.app", "1") == "1", delegate(bool on) { Config.Set("activity.app", on ? "1" : "0"); });
            p.Divider();
            p.Text1("Want to look offline? Click your name at the bottom left and choose Invisible.", Look.Muted, 9.5f);
            return p;
        }

        Control SystemPage()
        {
            Page p = new Page("System");
            p.Section("General");
            p.Check("Automatically open GameSpy Revival when your computer starts up", null, Shell.AppAutostart,
                delegate(bool on) { Shell.AppAutostart = on; });
            p.Check("Start GameSpy Revival minimized", "Start in the background (system tray), out of your way.",
                Config.Get("sys.startmin", "1") == "1", delegate(bool on) { Config.Set("sys.startmin", on ? "1" : "0"); });
            p.Check("Minimize GameSpy Revival to System Tray", "Clicking X hides the window to the tray instead of closing it. Right-click the tray icon to quit.",
                Config.Get("sys.tray", "1") == "1", delegate(bool on) { Config.Set("sys.tray", on ? "1" : "0"); });
            p.Divider();
            p.Section("Keybinds");
            p.Caption("While the app is focused");
            p.Text1("Ctrl + Shift + M     Mute or unmute", Look.Text);
            p.Text1("Ctrl + Shift + D     Deafen or undeafen", Look.Text);
            p.Text1("Esc     Close settings", Look.Text);
            p.Text1("Alt + Left / Alt + Right     Back and forward", Look.Text);
            p.Text1("Ctrl + F     Search messages", Look.Text);
            p.Text1("Up (in an empty message box)     Edit your last message", Look.Text);
            p.Text1("Esc (while replying or editing)     Cancel", Look.Text);
            p.Divider();
            p.Caption("Anywhere, even in a game");
            VoiceConfig vc = voice.Settings;
            p.Text1("Push to talk: " + VoiceConfig.KeyName(vc.PttKey) + (vc.PushToTalk ? "" : "  (Push to talk is off)"), Look.Text);
            p.Btn("Change push to talk in Voice && Video", null, delegate { Show("voice"); }, 300);
            return p;
        }

        Control GamesPage()
        {
            Page p = new Page("Game folders");
            p.Text1("Where each game is installed, so Join and invite cards can start it.", Look.Muted);
            foreach (ArenaPanel a in arenas)
            {
                ArenaPanel ap = a;
                p.Divider();
                p.Caption(ap.GameTitle);
                string path = ap.InstalledPath();
                Label l = p.Text1(path.Length > 0 ? path : "Not found yet.", path.Length > 0 ? Look.Text : Look.Red, 9.5f);
                Button f = new Button(); f.Text = "Find it"; f.SetBounds(0, p.Y, 110, 32);
                Button b = new Button(); b.Text = "Browse..."; b.SetBounds(120, p.Y, 110, 32);
                f.Click += delegate { ap.FindExe(false); string q = ap.InstalledPath(); l.Text = q.Length > 0 ? q : "Still not found. Use Browse and pick the game's .exe."; l.ForeColor = q.Length > 0 ? Look.Text : Look.Red; };
                b.Click += delegate { ap.BrowseExe(); string q = ap.InstalledPath(); if (q.Length > 0) { l.Text = q; l.ForeColor = Look.Text; } };
                p.Controls.Add(f); p.Controls.Add(b);
                Theme.Style(f); Theme.Style(b);
                p.Y += 42;
            }
            p.Divider();
            p.Caption("Metal Fatigue");
            p.Text1("The game folder, the online mod and display mode are on the Metal Fatigue page, in its Setup tab.", Look.Muted, 9.5f);
            p.Btn("Go to Metal Fatigue", null, delegate { shell.CloseSettings(); shell.Open("game:mf"); });
            return p;
        }

        Control HostingPage()
        {
            Page p = new Page("Host this group");
            p.Text1("Hosting runs your group's server on this PC: accounts, chat history, voice and the Metal Fatigue server. "
                + "Friends join with your address. The server runs in the background on its own, so the group stays up "
                + "after you close this window. Stop hosting turns it off.", Look.Muted);
            p.Divider();
            Label state = p.Text1(LobbyHost.Running ? "Running on port " + Shell.HostPort() + (shell.RelayRunning ? ", Metal Fatigue on its port too." : ".") : "Not running.",
                LobbyHost.Running ? Look.Green : Look.Muted, 10f);
            p.Caption("Group name");
            TextBox name = p.Field(Config.Get("host.name", "Game Night"));
            p.Caption("Text channels (comma separated)");
            TextBox chans = p.Field(Config.Get("host.channels", "general, looking-for-game, metal-fatigue, quake, unreal, clips-and-screens"), 600);
            Button go = null;
            go = p.Btn(LobbyHost.Running ? "Save and restart the server" : "Start hosting", "primary", delegate
            {
                Config.Set("host.name", name.Text.Trim().Replace("\"", "'"));
                Config.Set("host.channels", chans.Text.Trim().Replace("\"", ""));
                if (LobbyHost.Running) shell.StopHosting();
                string err = shell.StartHosting();
                if (err != null) { state.Text = err; state.ForeColor = Look.Red; return; }
                Config.Set("lobby.host", "1");
                state.Text = "Running on port " + Shell.HostPort() + ". Everyone reconnects by themselves in a few seconds.";
                state.ForeColor = Look.Green;
                go.Text = "Save and restart the server";
            }, 260);
            p.Check("Start the group server when Windows starts", "Your group is up whenever this PC is on, even if you never open the app.",
                LobbyHost.StartWithWindows, delegate(bool on) { LobbyHost.StartWithWindows = on; });
            p.Btn("Stop hosting", "danger", delegate
            {
                if (MessageBox.Show(FindForm(), "Stop the server? Everyone in the group gets disconnected.", "GameSpy Revival", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                shell.StopHosting();
                Config.Set("lobby.host", "0");
                state.Text = "Not running."; state.ForeColor = Look.Muted;
                go.Text = "Start hosting";
            }, 260);
            p.Divider();
            p.Caption("Invite friends");
            Label addr = p.Text1("Press the button to make an invite link for your friends.", Look.Text);
            p.Btn("Copy invite link", null, delegate
            {
                addr.Text = "Looking up your public address...";
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    string ip = "";
                    ip = Invite.PublicIp();
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (ip.Length == 0 && Invite.MyName.Length == 0) { addr.Text = "Could not find your public address. Set a permanent address below."; return; }
                            string a = Invite.MyLink(ip, Shell.HostPort());
                            try { Clipboard.SetText(a); } catch (Exception) { }
                            addr.Text = "Copied " + a + ". Friends click it, or press + > Join a Group and paste it.";
                        });
                    }
                    catch (Exception) { }
                });
            }, 260);
            p.Caption("Permanent address (optional)");
            p.Text1("Most home internet addresses change now and then, which changes your invite link. A free name from "
                + "duckdns.org (or any dynamic-DNS service) follows your PC instead: make one there, run their updater, "
                + "and type the name here, like mygroup.duckdns.org.", Look.Muted, 9.5f);
            TextBox pn = p.Field(Invite.MyName, 360);
            p.Btn("Save address", null, delegate
            {
                string v = pn.Text.Trim().ToLowerInvariant();
                if (v.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(v, @"^[a-z0-9.\-]+\.[a-z]{2,}$")) { addr.Text = "That doesn't look like an address name (example: mygroup.duckdns.org)."; return; }
                Config.Set("host.publicname", v);
                addr.Text = v.Length > 0 ? "Saved. Invite links now use " + v + "." : "Cleared. Invite links use your IP again.";
            }, 160);
            p.Divider();
            p.Text1("Your router must forward TCP 27901 (group) and TCP 27900 (Metal Fatigue) to this PC, "
                + "and Windows Firewall must allow gamespy-lobby.exe and mfnet-relay.exe.", Look.Muted, 9.5f);
            return p;
        }
    }
}
