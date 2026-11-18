// GameSpy Revival - voice screens: the channel panel in the right rail and the Voice & Audio settings page.
// Free software under the GNU GPL, version 3 or (at your option) any later version. No warranty.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Revival
{
    // Live input level with the sensitivity threshold drawn on it; drag to set the threshold by hand.
    class LevelMeter : Control
    {
        public VoiceEngine Engine;
        public bool Draggable;
        public event Action<int> ThresholdDragged;
        bool dragging;

        public LevelMeter()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Height = 18;
        }

        static float ToX(float db, int w) { float t = (db + 80f) / 80f; if (t < 0) t = 0; if (t > 1) t = 1; return t * w; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int w = Width, h = Height;
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(28, 38, 54))) g.FillRectangle(bg, 0, 0, w, h);
            if (Engine == null) return;
            float lvl = Engine.LevelDb, thr = Engine.ThresholdNow;
            bool ptt = Engine.Settings.PushToTalk;
            float tx = ToX(thr, w);
            // the bar is drawn as notches, like a classic meter
            for (int x = 2; x < w - 2; x += 6)
            {
                bool lit = x < ToX(lvl, w);
                Color c = !lit ? Color.FromArgb(45, 58, 78) : (ptt || x >= tx) ? Theme.Green : Color.FromArgb(200, 170, 80);
                using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, x, 3, 3, h - 6);
            }
            if (!ptt)
            {
                using (Pen p = new Pen(Color.White, 2f)) g.DrawLine(p, tx, 0, tx, h);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (Draggable) { dragging = true; Drag(e.X); } }
        protected override void OnMouseMove(MouseEventArgs e) { if (dragging) Drag(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; }

        void Drag(int x)
        {
            int db = (int)Math.Round(x * 80.0 / Width - 80);
            if (db < -80) db = -80; if (db > 0) db = 0;
            if (ThresholdDragged != null) ThresholdDragged(db);
        }
    }

    // ---------------------------------------------------------------- right rail: voice channels

    class VoicePanel : Panel
    {
        class Row { public bool IsRoom; public string Room = ""; public VoiceMember M; public int Count; }

        readonly LobbyClient lobby;
        readonly VoiceEngine engine;
        readonly ListBox list = new ListBox();
        readonly Label lblStatus = new Label();
        readonly Button btnMute = new Button(), btnDeaf = new Button(), btnLeave = new Button(), btnSettings = new Button();
        readonly Timer tick = new Timer();
        HashSet<string> lastMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public event Action OpenSettings;

        public VoicePanel(LobbyClient lc, VoiceEngine ve)
        {
            lobby = lc; engine = ve;
            BackColor = Theme.Panel;
            Size = new Size(210, 380);

            Label h = new Label();
            h.Text = "VOICE CHANNELS";
            h.Font = new Font("Segoe UI", 8f);
            h.ForeColor = Theme.Muted;
            h.SetBounds(16, 6, 180, 16);
            Controls.Add(h);

            list.SetBounds(8, 24, 194, 222);
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Theme.Panel;
            list.DrawMode = DrawMode.OwnerDrawVariable;
            list.IntegralHeight = false;
            list.MeasureItem += delegate(object s, MeasureItemEventArgs e)
            {
                Row r = list.Items[e.Index] as Row;
                e.ItemHeight = r != null && r.IsRoom ? 28 : 24;
            };
            list.DrawItem += DrawRow;
            // one click on a channel joins it, like Discord
            list.MouseUp += delegate(object s, MouseEventArgs e)
            {
                int i = list.IndexFromPoint(e.Location);
                if (i < 0) return;
                list.SelectedIndex = i;
                Row r = list.Items[i] as Row;
                if (e.Button == MouseButtons.Left && r != null && r.IsRoom) JoinRoom(r.Room);
            };
            list.MouseDown += delegate(object s, MouseEventArgs e)
            {
                int i = list.IndexFromPoint(e.Location);
                if (i >= 0 && e.Button == MouseButtons.Right) list.SelectedIndex = i;
            };
            ContextMenuStrip cm = new ContextMenuStrip();
            DarkMenu.Apply(cm);
            cm.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e) { BuildMenu(cm, e); };
            list.ContextMenuStrip = cm;
            Controls.Add(list);

            lblStatus.SetBounds(16, 250, 186, 32);
            lblStatus.Font = new Font("Segoe UI", 8.5f);
            Controls.Add(lblStatus);

            SetupButton(btnMute, "Mute", 8, 286, 95);
            SetupButton(btnDeaf, "Deafen", 107, 286, 95);
            SetupButton(btnLeave, "Leave", 8, 320, 95);
            SetupButton(btnSettings, "Settings", 107, 320, 95);
            btnMute.Click += delegate { ToggleMute(); };
            btnDeaf.Click += delegate { ToggleDeaf(); };
            btnLeave.Click += delegate { LeaveRoom(); };
            btnSettings.Click += delegate { if (OpenSettings != null) OpenSettings(); };
            ToolTip tt = new ToolTip();
            tt.SetToolTip(btnSettings, "Voice & Audio settings");
            tt.SetToolTip(btnMute, "Mute your microphone");
            tt.SetToolTip(btnDeaf, "Deafen: hear no one, and mute yourself");

            lobby.VoiceChanged += OnRooms;
            lobby.Changed += delegate
            {
                if (lobby.State == LobbyState.SignedOut && lobby.VoiceRoomName.Length > 0)
                {
                    Log("signed out: leaving voice");
                    lobby.VoiceRoomName = "";
                    engine.SetInRoom(false);
                }
                Rebuild();
            };
            tick.Interval = 120;
            tick.Tick += delegate
            {
                if (lobby.VoiceRoomName.Length > 0) list.Invalidate();
                UpdateStatus();
            };
            tick.Start();
            Rebuild();
        }

        void SetupButton(Button b, string text, int x, int y, int w)
        {
            b.Text = text;
            b.SetBounds(x, y, w, 30);
            b.Font = new Font("Segoe UI", 8.5f);
            Controls.Add(b);
            Theme.Style(b);
        }

        // ---- actions (also used by the window's hotkeys)
        public static void Log(string t)
        {
            try
            {
                string d = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSpyRevival");
                System.IO.File.AppendAllText(System.IO.Path.Combine(d, "voice" + (Config.Profile.Length > 0 ? "-" + Config.Profile : "") + ".log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + t + Environment.NewLine);
            }
            catch (Exception) { }
        }

        public void JoinRoom(string room)
        {
            Log("join requested: " + room + " state=" + lobby.State + " current='" + lobby.VoiceRoomName + "'");
            if (lobby.State != LobbyState.SignedIn)
            {
                MessageBox.Show(FindForm(), "Sign in first (top right) to use voice.", "Voice");
                return;
            }
            if (lobby.VoiceRoomName == room) return;
            bool was = lobby.VoiceRoomName.Length > 0;
            try
            {
                lobby.JoinVoice(room);
                Log("joinvoice sent");
                engine.SetInRoom(true);
                Log("engine in room");
                if (!was) engine.Play(Sfx.Join);
            }
            catch (Exception ex) { Log("join failed: " + ex); }
            Rebuild();
            Log("after rebuild: current='" + lobby.VoiceRoomName + "'");
        }

        public void LeaveRoom()
        {
            Log("leave: current='" + lobby.VoiceRoomName + "'");
            if (lobby.VoiceRoomName.Length == 0) return;
            lobby.LeaveVoice();
            engine.SetInRoom(false);
            engine.Play(Sfx.Leave);
            Rebuild();
        }

        public void ToggleMute()
        {
            if (engine.Deafened)
            {
                // un-muting while deafened undeafens too, like Discord
                engine.Deafened = false; engine.Muted = false;
                engine.Play(Sfx.Undeafen);
            }
            else
            {
                engine.Muted = !engine.Muted;
                engine.Play(engine.Muted ? Sfx.Mute : Sfx.Unmute);
            }
            lobby.SendVoiceState();
            UpdateButtons();
        }

        public void ToggleDeaf()
        {
            engine.Deafened = !engine.Deafened;
            engine.Play(engine.Deafened ? Sfx.Deafen : Sfx.Undeafen);
            lobby.SendVoiceState();
            UpdateButtons();
        }

        void BuildMenu(ContextMenuStrip cm, System.ComponentModel.CancelEventArgs e)
        {
            cm.Items.Clear();
            Row r = list.SelectedItem as Row;
            if (r == null) { e.Cancel = true; return; }
            if (r.IsRoom)
            {
                string room = r.Room;
                if (lobby.VoiceRoomName == room) cm.Items.Add("Leave channel", null, delegate { LeaveRoom(); });
                else cm.Items.Add("Join channel", null, delegate { JoinRoom(room); });
            }
            else if (r.M != null && !string.Equals(r.M.User, lobby.User, StringComparison.OrdinalIgnoreCase))
            {
                string u = r.M.User;
                int cur = engine.GetUserVolume(u);
                ToolStripLabel lab = new ToolStripLabel("Volume for " + u + ": " + cur + "%");
                lab.ForeColor = Theme.Muted;
                cm.Items.Add(lab);
                foreach (int v in new int[] { 25, 50, 75, 100, 150, 200 })
                {
                    int vv = v;
                    ToolStripMenuItem mi = new ToolStripMenuItem(v + "%");
                    mi.Checked = cur == v;
                    mi.Click += delegate { engine.SetUserVolume(u, vv); };
                    cm.Items.Add(mi);
                }
                cm.Items.Add(new ToolStripSeparator());
                bool lm = engine.IsLocalMuted(u);
                cm.Items.Add(lm ? "Unmute " + u + " for me" : "Mute " + u + " for me", null, delegate { engine.SetLocalMuted(u, !lm); list.Invalidate(); });
            }
            else { e.Cancel = true; return; }
            foreach (ToolStripItem i in cm.Items) if (!(i is ToolStripLabel)) i.ForeColor = Theme.Text;
        }

        // ---- membership
        void OnRooms()
        {
            HashSet<string> now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (VoiceRoom vr in lobby.Rooms)
                if (vr.Name == lobby.VoiceRoomName)
                    foreach (VoiceMember m in vr.Members)
                        if (!string.Equals(m.User, lobby.User, StringComparison.OrdinalIgnoreCase)) now.Add(m.User);
            if (lobby.VoiceRoomName.Length > 0)
            {
                foreach (string u in now) if (!lastMembers.Contains(u)) { engine.Play(Sfx.OtherJoin); break; }
                foreach (string u in lastMembers) if (!now.Contains(u)) { engine.Play(Sfx.OtherLeave); break; }
            }
            lastMembers = now;
            Rebuild();
        }

        void Rebuild()
        {
            object selKey = null;
            Row sel = list.SelectedItem as Row;
            if (sel != null) selKey = sel.IsRoom ? "#" + sel.Room : (sel.M != null ? sel.M.User : null);
            list.BeginUpdate();
            list.Items.Clear();
            List<VoiceRoom> show = lobby.Rooms.Count > 0 ? lobby.Rooms : DefaultRooms();
            foreach (VoiceRoom vr in show)
            {
                Row rr = new Row(); rr.IsRoom = true; rr.Room = vr.Name; rr.Count = vr.Members.Count;
                int i = list.Items.Add(rr);
                if (selKey != null && selKey.Equals("#" + vr.Name)) list.SelectedIndex = i;
                foreach (VoiceMember m in vr.Members)
                {
                    Row mr = new Row(); mr.Room = vr.Name; mr.M = m;
                    int j = list.Items.Add(mr);
                    if (selKey != null && selKey.Equals(m.User)) list.SelectedIndex = j;
                }
            }
            list.EndUpdate();
            UpdateButtons();
            UpdateStatus();
        }

        static List<VoiceRoom> DefaultRooms()
        {
            List<VoiceRoom> l = new List<VoiceRoom>();
            foreach (string n in new string[] { "General" })
            {
                VoiceRoom vr = new VoiceRoom(); vr.Name = n; l.Add(vr);
            }
            return l;
        }

        void UpdateButtons()
        {
            bool inRoom = lobby.VoiceRoomName.Length > 0;
            btnLeave.Enabled = inRoom;
            btnMute.Text = engine.Muted || engine.Deafened ? "Unmute" : "Mute";
            btnDeaf.Text = engine.Deafened ? "Undeafen" : "Deafen";
            btnMute.BackColor = engine.Muted || engine.Deafened ? Color.FromArgb(120, 40, 40) : Color.FromArgb(28, 38, 54);
            btnDeaf.BackColor = engine.Deafened ? Color.FromArgb(120, 40, 40) : Color.FromArgb(28, 38, 54);
            list.Invalidate();
        }

        void UpdateStatus()
        {
            string prob = engine.Problem;
            if (prob.Length > 0)
            {
                lblStatus.ForeColor = Color.FromArgb(240, 150, 130);
                lblStatus.Text = prob;
            }
            else if (lobby.VoiceRoomName.Length > 0)
            {
                lblStatus.ForeColor = Theme.Green;
                string mode = engine.Settings.PushToTalk ? "Push to talk: " + VoiceConfig.KeyName(engine.Settings.PttKey) : "Voice activity";
                lblStatus.Text = "Voice connected · " + lobby.VoiceRoomName + "\n" + mode;
            }
            else
            {
                lblStatus.ForeColor = Theme.Muted;
                lblStatus.Text = lobby.State == LobbyState.SignedIn ? "Click a channel to join." : "Sign in to use voice.";
            }
        }

        // ---- drawing
        void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            Graphics g = e.Graphics;
            Row r = list.Items[e.Index] as Row;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (SolidBrush bg = new SolidBrush(sel ? Theme.Sel : Theme.Panel)) g.FillRectangle(bg, e.Bounds);
            if (r == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle b = e.Bounds;
            TextFormatFlags tf = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            if (r.IsRoom)
            {
                bool mine = lobby.VoiceRoomName == r.Room;
                Color c = mine ? Theme.Green : Theme.Muted;
                // a little speaker icon
                using (SolidBrush ib = new SolidBrush(c))
                {
                    g.FillRectangle(ib, b.X + 8, b.Y + 11, 4, 6);
                    g.FillPolygon(ib, new Point[] { new Point(b.X + 12, b.Y + 11), new Point(b.X + 17, b.Y + 7), new Point(b.X + 17, b.Y + 21), new Point(b.X + 12, b.Y + 17) });
                }
                using (Font f = new Font("Segoe UI Semibold", 9.5f))
                    TextRenderer.DrawText(g, r.Room, f, new Rectangle(b.X + 24, b.Y, b.Width - 50, b.Height), mine ? Color.White : Theme.Text, tf);
                if (r.Count > 0)
                    using (Font f = new Font("Segoe UI", 8f))
                        TextRenderer.DrawText(g, r.Count.ToString(), f, new Rectangle(b.Right - 26, b.Y, 20, b.Height), Theme.Muted, tf | TextFormatFlags.Right);
                return;
            }
            VoiceMember m = r.M;
            bool me = string.Equals(m.User, lobby.User, StringComparison.OrdinalIgnoreCase);
            bool speaking = me ? engine.Transmitting : engine.IsSpeaking(m.User);
            bool lmuted = !me && engine.IsLocalMuted(m.User);
            Rectangle av = new Rectangle(b.X + 24, b.Y + 4, 16, 16);
            using (SolidBrush ab = new SolidBrush(Color.FromArgb(52, 66, 90))) g.FillEllipse(ab, av);
            if (speaking && !m.Mute && !lmuted)
                using (Pen sp = new Pen(Theme.Green, 2f)) g.DrawEllipse(sp, av.X - 1, av.Y - 1, av.Width + 1, av.Height + 1);
            using (Font fi = new Font("Segoe UI", 7f, FontStyle.Bold))
                TextRenderer.DrawText(g, m.User.Substring(0, 1).ToUpperInvariant(), fi, av, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            string tag = m.Deaf ? "deafened" : m.Mute ? "muted" : lmuted ? "muted for you" : "";
            using (Font f = new Font("Segoe UI", 9f))
                TextRenderer.DrawText(g, m.User, f, new Rectangle(b.X + 46, b.Y, b.Width - 50, b.Height), speaking ? Color.White : Theme.Text, tf);
            if (tag.Length > 0)
                using (Font f = new Font("Segoe UI", 7.5f))
                    TextRenderer.DrawText(g, tag, f, new Rectangle(b.X + 46, b.Y, b.Width - 52, b.Height), Color.FromArgb(230, 130, 120), tf | TextFormatFlags.Right);
        }
    }

    // ---------------------------------------------------------------- Voice & Audio settings page

    class VoiceSettingsPage : Panel
    {
        readonly VoiceEngine engine;
        VoiceConfig c;
        bool loading;
        int y;
        const int X = 28, W = 680;

        ComboBox cbIn, cbOut;
        TrackBar tbIn, tbOut, tbDelay, tbBuffer, tbSnd;
        Label lInVal, lOutVal, lDelayVal, lBufferVal, lSndVal, lSensHint;
        Button btnTest, btnKey;
        LevelMeter meterTest, meterSens;
        RadioButton rbVa, rbPtt;
        CheckBox chkAuto, chkNr, chkHp, chkAgc;
        readonly List<CheckBox> soundChecks = new List<CheckBox>();
        readonly Timer meterTimer = new Timer();
        Timer keyTimer;

        public VoiceSettingsPage(VoiceEngine ve)
        {
            engine = ve;
            Dock = DockStyle.Fill;
            AutoScroll = true;
            BackColor = Theme.Bg;
            c = VoiceConfig.Load();
            Build();
            LoadValues();
            meterTimer.Interval = 50;
            meterTimer.Tick += delegate { meterTest.Invalidate(); meterSens.Invalidate(); if (engine.Problem.Length > 0) lSensHint.Text = engine.Problem; };
            VisibleChanged += delegate
            {
                if (Visible) { c = VoiceConfig.Load(); LoadDevices(); LoadValues(); engine.SetMetering(true); meterTimer.Start(); }
                else
                {
                    meterTimer.Stop();
                    engine.SetMetering(false);
                    if (engine.Testing) { engine.SetTesting(false); btnTest.Text = "Mic Test"; }
                }
            };
        }

        // ---- layout helpers
        Label Heading(string t)
        {
            y += 10;
            Label l = new Label();
            l.Text = t;
            l.Font = new Font("Segoe UI Semibold", 13f);
            l.ForeColor = Color.White;
            l.SetBounds(X, y, W, 26);
            Controls.Add(l);
            y += 32;
            return l;
        }

        Label Small(string t, int x, int yy, int w)
        {
            Label l = new Label();
            l.Text = t;
            l.Font = new Font("Segoe UI", 9f);
            l.ForeColor = Theme.Muted;
            l.SetBounds(x, yy, w, 18);
            Controls.Add(l);
            return l;
        }

        Label Caption(string t, int x, int yy, int w)
        {
            Label l = new Label();
            l.Text = t;
            l.Font = new Font("Segoe UI Semibold", 9.5f);
            l.ForeColor = Theme.Text;
            l.SetBounds(x, yy, w, 20);
            Controls.Add(l);
            return l;
        }

        TrackBar Slider(int x, int yy, int w, int min, int max, out Label val)
        {
            TrackBar t = new TrackBar();
            t.SetBounds(x, yy, w - 50, 30);
            t.Minimum = min; t.Maximum = max;
            t.TickStyle = TickStyle.None;
            t.BackColor = Theme.Bg;
            t.ValueChanged += delegate { SaveFromUi(); };
            Controls.Add(t);
            val = new Label();
            val.SetBounds(x + w - 46, yy + 4, 46, 18);
            val.ForeColor = Theme.Text;
            val.Font = new Font("Segoe UI", 9f);
            Controls.Add(val);
            return t;
        }

        CheckBox Check(string text, string sub, int x, int yy)
        {
            CheckBox cb = new ToggleCheck();
            cb.Text = text;
            cb.AutoSize = true;
            cb.Location = new Point(x, yy);
            cb.ForeColor = Theme.Text;
            cb.Font = new Font("Segoe UI", 9.5f);
            cb.CheckedChanged += delegate { SaveFromUi(); };
            Controls.Add(cb);
            if (sub != null) Small(sub, x + 18, yy + 22, W - (x - X) - 20);
            return cb;
        }

        void Divider()
        {
            y += 8;
            Panel p = new Panel();
            p.SetBounds(X, y, W, 1);
            p.BackColor = Theme.Line;
            Controls.Add(p);
            y += 10;
        }

        void Build()
        {
            y = 18;
            Label title = new Label();
            title.Text = "Voice && Video";
            title.Font = new Font("Segoe UI Semibold", 20f);
            title.ForeColor = Color.White;
            title.SetBounds(X, y, W, 38);
            Controls.Add(title);
            y += 44;

            // devices
            Heading("Voice");
            int half = (W - 24) / 2;
            Caption("Microphone", X, y, half);
            Caption("Speaker", X + half + 24, y, half);
            y += 22;
            cbIn = new ComboBox(); cbIn.DropDownStyle = ComboBoxStyle.DropDownList; cbIn.SetBounds(X, y, half, 26);
            cbOut = new ComboBox(); cbOut.DropDownStyle = ComboBoxStyle.DropDownList; cbOut.SetBounds(X + half + 24, y, half, 26);
            cbIn.SelectedIndexChanged += delegate { SaveFromUi(); };
            cbOut.SelectedIndexChanged += delegate { SaveFromUi(); };
            Controls.Add(cbIn); Controls.Add(cbOut);
            y += 38;
            Caption("Microphone volume", X, y, half);
            Caption("Speaker volume", X + half + 24, y, half);
            y += 22;
            tbIn = Slider(X, y, half, 0, 200, out lInVal);
            tbOut = Slider(X + half + 24, y, half, 0, 200, out lOutVal);
            y += 40;
            btnTest = new Button();
            btnTest.Text = "Mic Test";
            btnTest.Tag = "primary";
            btnTest.SetBounds(X, y, 110, 32);
            btnTest.Click += delegate
            {
                bool on = !engine.Testing;
                engine.SetTesting(on);
                btnTest.Text = on ? "Stop Testing" : "Mic Test";
            };
            Controls.Add(btnTest);
            meterTest = new LevelMeter();
            meterTest.Engine = engine;
            meterTest.SetBounds(X + 124, y + 7, W - 124, 18);
            Controls.Add(meterTest);
            y += 38;
            Small("Mic Test plays your voice back to you after all the processing below, so you hear what friends hear.", X, y, W);
            y += 24;
            Divider();

            // input mode
            Heading("Input Mode");
            rbVa = new RadioButton(); rbVa.Text = "Voice Activity"; rbVa.AutoSize = true; rbVa.Location = new Point(X, y);
            rbVa.ForeColor = Theme.Text; rbVa.Font = new Font("Segoe UI", 9.5f);
            rbVa.CheckedChanged += delegate { SaveFromUi(); };
            Controls.Add(rbVa);
            Small("Your mic opens by itself when you talk.", X + 18, y + 22, W);
            y += 46;
            rbPtt = new RadioButton(); rbPtt.Text = "Push to Talk"; rbPtt.AutoSize = true; rbPtt.Location = new Point(X, y);
            rbPtt.ForeColor = Theme.Text; rbPtt.Font = new Font("Segoe UI", 9.5f);
            rbPtt.CheckedChanged += delegate { SaveFromUi(); };
            Controls.Add(rbPtt);
            Small("Hold a key or mouse button to talk. Works while a game has focus.", X + 18, y + 22, W);
            y += 50;
            Caption("Shortcut", X, y, 200);
            Caption("Push to Talk release delay", X + half + 24, y, half);
            y += 22;
            btnKey = new Button();
            btnKey.SetBounds(X, y, 200, 30);
            btnKey.Click += delegate { CaptureKey(); };
            Controls.Add(btnKey);
            tbDelay = Slider(X + half + 24, y, half, 0, 1000, out lDelayVal);
            tbDelay.SmallChange = 20; tbDelay.LargeChange = 100;
            y += 42;
            Divider();

            // sensitivity
            Heading("Input Sensitivity");
            chkAuto = Check("Automatically determine input sensitivity", null, X, y);
            y += 28;
            meterSens = new LevelMeter();
            meterSens.Engine = engine;
            meterSens.Draggable = true;
            meterSens.SetBounds(X, y, W, 22);
            meterSens.ThresholdDragged += delegate(int db)
            {
                if (chkAuto.Checked) return;
                c.ThresholdDb = db; c.Save(); engine.Reload();
            };
            Controls.Add(meterSens);
            y += 28;
            lSensHint = Small("", X, y, W);
            lSensHint.Height = 34;
            y += 38;
            Divider();

            // processing
            Heading("Voice Processing");
            chkNr = Check("Noise reduction (basic)", "Turns down background noise in the gaps between words. Not as strong as Krisp.", X, y);
            y += 48;
            chkHp = Check("Remove low rumble", "Filters out hum, fans and desk thumps below about 100 Hz.", X, y);
            y += 48;
            chkAgc = Check("Automatic gain control", "Evens out your volume so you're not too quiet or too loud.", X, y);
            y += 48;
            Small("Echo cancellation isn't available yet. Use headphones so friends don't hear themselves.", X, y, W);
            y += 26;
            Divider();

            // network
            Heading("Connection");
            Caption("Voice buffer", X, y, half);
            y += 22;
            tbBuffer = Slider(X, y, half, 40, 400, out lBufferVal);
            tbBuffer.SmallChange = 20; tbBuffer.LargeChange = 40;
            Small("Raise it if voices crackle or cut out. Lower it for less delay.", X + half + 24, y + 4, half);
            y += 42;
            Divider();

            // sounds
            Heading("Sounds");
            string[] names = { "Mute", "Unmute", "Deafen", "Undeafen", "You join a voice channel", "You leave a voice channel",
                "Someone joins or leaves your channel", "New private message" };
            for (int i = 0; i < names.Length; i++)
            {
                int col = i % 2, row = i / 2;
                CheckBox cb = Check(names[i], null, X + col * (half + 24), y + row * 30);
                soundChecks.Add(cb);
                Sfx kind = i == 0 ? Sfx.Mute : i == 1 ? Sfx.Unmute : i == 2 ? Sfx.Deafen : i == 3 ? Sfx.Undeafen
                    : i == 4 ? Sfx.Join : i == 5 ? Sfx.Leave : i == 6 ? Sfx.OtherJoin : Sfx.Message;
                LinkLabel prev = new LinkLabel();
                prev.Text = "Preview";
                prev.SetBounds(X + col * (half + 24) + half - 70, y + row * 30 + 3, 60, 18);
                prev.LinkColor = Theme.Accent; prev.ActiveLinkColor = Theme.Green;
                prev.LinkClicked += delegate { engine.Play(kind); };
                Controls.Add(prev);
            }
            y += 4 * 30 + 8;
            Caption("Sound volume", X, y, half);
            y += 22;
            tbSnd = Slider(X, y, half, 0, 100, out lSndVal);
            y += 42;
            Divider();

            // advanced
            Heading("Advanced");
            Button reset = new Button();
            reset.Text = "Reset all voice settings";
            reset.SetBounds(X, y, 220, 32);
            reset.Tag = "danger";
            reset.Click += delegate
            {
                if (MessageBox.Show(FindForm(), "Put every Voice & Audio setting back to its default?", "Voice && Video", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                VoiceConfig.Reset();
                engine.Reload();
                c = VoiceConfig.Load();
                LoadDevices();
                LoadValues();
            };
            Controls.Add(reset);
            y += 44;
            Small("Not built yet: camera, screen sharing and soundboard.", X, y, W);
            y += 30;
            Panel pad = new Panel(); pad.SetBounds(X, y, 10, 10); pad.BackColor = Theme.Bg; Controls.Add(pad);

            foreach (Control k in Controls)
                if (k is Button || k is ComboBox) Theme.Style(k);
            LoadDevices();
        }

        void LoadDevices()
        {
            loading = true;
            cbIn.Items.Clear(); cbOut.Items.Clear();
            cbIn.Items.Add("Windows default"); cbOut.Items.Add("Windows default");
            try { foreach (string n in WinMM.InputNames()) cbIn.Items.Add(n); } catch (Exception) { }
            try { foreach (string n in WinMM.OutputNames()) cbOut.Items.Add(n); } catch (Exception) { }
            int i = c.InputDevice.Length == 0 ? 0 : cbIn.Items.IndexOf(c.InputDevice);
            int o = c.OutputDevice.Length == 0 ? 0 : cbOut.Items.IndexOf(c.OutputDevice);
            cbIn.SelectedIndex = i < 0 ? 0 : i;
            cbOut.SelectedIndex = o < 0 ? 0 : o;
            loading = false;
        }

        void LoadValues()
        {
            loading = true;
            tbIn.Value = c.InputVolume; tbOut.Value = c.OutputVolume;
            rbPtt.Checked = c.PushToTalk; rbVa.Checked = !c.PushToTalk;
            btnKey.Text = VoiceConfig.KeyName(c.PttKey);
            tbDelay.Value = c.PttReleaseMs > 1000 ? 1000 : c.PttReleaseMs;
            chkAuto.Checked = c.AutoSensitivity;
            chkNr.Checked = c.NoiseReduction; chkHp.Checked = c.HighPass; chkAgc.Checked = c.Agc;
            tbBuffer.Value = c.BufferMs;
            tbSnd.Value = c.SoundVolume;
            bool[] snd = { c.SndMute, c.SndUnmute, c.SndDeafen, c.SndUndeafen, c.SndJoin, c.SndLeave, c.SndOthers, c.SndMessage };
            for (int i = 0; i < soundChecks.Count; i++) soundChecks[i].Checked = snd[i];
            loading = false;
            UpdateLabels();
        }

        void UpdateLabels()
        {
            lInVal.Text = tbIn.Value + "%";
            lOutVal.Text = tbOut.Value + "%";
            lDelayVal.Text = tbDelay.Value + " ms";
            lBufferVal.Text = tbBuffer.Value + " ms";
            lSndVal.Text = tbSnd.Value + "%";
            btnKey.Enabled = rbPtt.Checked;
            tbDelay.Enabled = rbPtt.Checked;
            chkAuto.Enabled = !rbPtt.Checked;
            meterSens.Draggable = !chkAuto.Checked && !rbPtt.Checked;
            lSensHint.Text = rbPtt.Checked ? "Push to Talk is on, so sensitivity doesn't apply."
                : chkAuto.Checked ? "The white line follows the noise in your room. When the bar passes it, friends hear you."
                : "Drag the white line. When the bar passes it, friends hear you.";
        }

        void SaveFromUi()
        {
            if (loading) return;
            c.InputDevice = cbIn.SelectedIndex <= 0 ? "" : (string)cbIn.SelectedItem;
            c.OutputDevice = cbOut.SelectedIndex <= 0 ? "" : (string)cbOut.SelectedItem;
            c.InputVolume = tbIn.Value; c.OutputVolume = tbOut.Value;
            c.PushToTalk = rbPtt.Checked;
            c.PttReleaseMs = tbDelay.Value;
            c.AutoSensitivity = chkAuto.Checked;
            c.NoiseReduction = chkNr.Checked; c.HighPass = chkHp.Checked; c.Agc = chkAgc.Checked;
            c.BufferMs = tbBuffer.Value;
            c.SoundVolume = tbSnd.Value;
            if (soundChecks.Count == 8)
            {
                c.SndMute = soundChecks[0].Checked; c.SndUnmute = soundChecks[1].Checked;
                c.SndDeafen = soundChecks[2].Checked; c.SndUndeafen = soundChecks[3].Checked;
                c.SndJoin = soundChecks[4].Checked; c.SndLeave = soundChecks[5].Checked;
                c.SndOthers = soundChecks[6].Checked; c.SndMessage = soundChecks[7].Checked;
            }
            c.Save();
            engine.Reload();
            UpdateLabels();
        }

        // waits for the next key or mouse button (not the left button that clicked here)
        void CaptureKey()
        {
            btnKey.Text = "Press a key or mouse button...";
            if (keyTimer != null) keyTimer.Stop();
            bool released = false;
            int waited = 0;
            keyTimer = new Timer();
            keyTimer.Interval = 30;
            keyTimer.Tick += delegate
            {
                waited += 30;
                bool any = false;
                for (int vk = 2; vk < 255; vk++)
                {
                    if (vk == 0x10 || vk == 0x11 || vk == 0x12) continue;   // generic shift/ctrl/alt: use the left/right ones
                    if ((WinMM.GetAsyncKeyState(vk) & 0x8000) == 0) continue;
                    any = true;
                    if (!released) continue;
                    keyTimer.Stop();
                    if (vk == 0x1B) { btnKey.Text = VoiceConfig.KeyName(c.PttKey); return; }   // Esc cancels
                    c.PttKey = vk; c.Save(); engine.Reload();
                    btnKey.Text = VoiceConfig.KeyName(vk);
                    return;
                }
                if (!any) released = true;
                if (waited > 8000) { keyTimer.Stop(); btnKey.Text = VoiceConfig.KeyName(c.PttKey); }
            };
            keyTimer.Start();
        }
    }
}
