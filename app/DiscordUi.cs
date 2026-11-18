// GameSpy Revival - the Discord-style building blocks: sidebar list, chat view, members list, friends list.
// Free software under the GNU GPL, version 3 or (at your option) any later version. No warranty.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Revival
{
    // Discord-style scrollbar: a thin rounded gray thumb on the page's own background, no arrows, no track.
    // Wrap() puts any scrolling control (panel, list, text box) inside a clip panel that hides
    // Windows' own scrollbar just past the edge, and lays this thin bar over the right side.
    class PillScroll : Control
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct SCROLLINFO { public uint cbSize, fMask; public int nMin, nMax; public uint nPage; public int nPos, nTrackPos; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetScrollInfo(IntPtr h, int bar, ref SCROLLINFO si);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public static readonly Color ThumbColor = Color.FromArgb(62, 72, 88);
        readonly Control sc;
        Panel clip;
        bool fitting, hover, dragging;
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        int dragY, dragVal, lastPos = -1, lastMax = -1, lastPage = -1;
        int min, max, page, pos;
        bool vOn, hOn;

        static Color Bg(Control c)
        {
            for (Control k = c; k != null; k = k.Parent)
                if (k.BackColor != SystemColors.Control && k.BackColor != SystemColors.Window && k.BackColor.A == 255) return k.BackColor;
            return Look.Main;
        }

        void Read()
        {
            vOn = hOn = false; min = max = page = pos = 0;
            if (!sc.IsHandleCreated) return;
            int style = GetWindowLong(sc.Handle, -16);
            vOn = (style & 0x00200000) != 0;
            hOn = (style & 0x00100000) != 0;
            SCROLLINFO si = new SCROLLINFO();
            si.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(SCROLLINFO));
            si.fMask = 0x17;
            if (GetScrollInfo(sc.Handle, 1, ref si)) { min = si.nMin; max = si.nMax; page = (int)si.nPage; pos = si.nPos; }
            if (page <= 0) page = 1;
            if (max - min + 1 <= page) vOn = false;
        }

        public static Control Wrap(Control inner)
        {
            if (inner == null || inner.Parent is Clip) return inner;
            Clip clip = new Clip();
            Color bg = Bg(inner);
            clip.BackColor = bg;
            Control parent = inner.Parent;
            int idx = parent != null ? parent.Controls.GetChildIndex(inner) : 0;
            clip.Dock = inner.Dock;
            clip.Bounds = inner.Bounds;
            clip.Anchor = inner.Anchor;
            bool shown = true;
            try { shown = (bool)typeof(Control).GetMethod("GetState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new Type[] { typeof(int) }, null).Invoke(inner, new object[] { 2 }); } catch (Exception) { }
            clip.Visible = shown;
            if (parent != null) { parent.Controls.Remove(inner); parent.Controls.Add(clip); parent.Controls.SetChildIndex(clip, idx); }
            inner.Dock = DockStyle.None;
            inner.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            inner.Visible = true;
            inner.Location = Point.Empty;
            clip.Controls.Add(inner);
            PillScroll bar = new PillScroll(inner);
            bar.BackColor = inner is ScrollableControl ? bg : (inner.BackColor.A == 255 ? inner.BackColor : bg);
            bar.clip = clip;
            clip.Controls.Add(bar);
            bar.BringToFront();
            clip.Resize += delegate { bar.Fit(); };
            inner.Layout += delegate { bar.Fit(); };
            bar.Fit();
            return clip;
        }

        // wraps every list, rich text box and scrolling panel under root that isn't wrapped yet
        public static void WrapAll(Control root)
        {
            List<Control> found = new List<Control>();
            Find(root, found);
            foreach (Control c in found) Wrap(c);
        }

        static void Find(Control c, List<Control> found)
        {
            foreach (Control k in c.Controls)
            {
                if (k is Clip || k is PillScroll) { foreach (Control k2 in k.Controls) if (!(k2 is PillScroll)) Find(k2, found); continue; }
                bool scrolls = k is ListBox || k is ListView || k is TreeView || k is RichTextBox
                    || (k is ScrollableControl && ((ScrollableControl)k).AutoScroll && !(k is Form));
                if (scrolls) found.Add(k);
                ComboBox cb = k as ComboBox;
                if (cb != null && cb.DrawMode == DrawMode.Normal)
                {
                    cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Look.Card; cb.ForeColor = Look.Text;
                    cb.DrawMode = DrawMode.OwnerDrawFixed;
                    ComboFrame.Attach(cb);
                    ComboBox cbx = cb;
                    cb.DrawItem += delegate(object o, DrawItemEventArgs e)
                    {
                        bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                        using (SolidBrush b = new SolidBrush(sel ? Look.Sel : Look.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                        if (e.Index >= 0)
                            TextRenderer.DrawText(e.Graphics, cbx.GetItemText(cbx.Items[e.Index]), cbx.Font, new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height), Look.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    };
                }
                CheckBox chk = k as CheckBox;
                if (chk != null && !(chk.AccessibleDescription == "gs-styled")) { chk.Paint += PaintCheck; chk.AccessibleDescription = "gs-styled"; chk.Invalidate(); }
                RadioButton rb = k as RadioButton;
                if (rb != null && !(rb.AccessibleDescription == "gs-styled")) { rb.Paint += PaintRadio; rb.AccessibleDescription = "gs-styled"; rb.Invalidate(); }
                NumericUpDown nu = k as NumericUpDown;
                if (nu != null) { nu.BackColor = Look.Card; nu.ForeColor = Look.Text; nu.BorderStyle = BorderStyle.FixedSingle; if (nu.Controls.Count > 0) nu.Controls[0].Visible = false; }
                TextBox tbs = k as TextBox;
                if (tbs != null && !tbs.Multiline && tbs.BorderStyle == BorderStyle.Fixed3D) tbs.BorderStyle = BorderStyle.FixedSingle;
                Find(k, found);
            }
        }

        class Clip : Panel { }

        static readonly Color Blurple = Color.FromArgb(88, 101, 242);
        static Rectangle Glyph(ButtonBase c) { int sz = 16; return new Rectangle(0, (c.Height - sz) / 2, sz, sz); }
        static Color AreaBg(Control c) { return Bg(c); }

        static void PaintCheck(object o, PaintEventArgs e)
        {
            CheckBox c = (CheckBox)o;
            Rectangle g = Glyph(c);
            Graphics gr = e.Graphics;
            using (SolidBrush b = new SolidBrush(AreaBg(c))) gr.FillRectangle(b, g);
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = new Rectangle(g.X + 1, g.Y + 1, 14, 14);
            if (c.Checked)
            {
                Look.FillRound(gr, box, 4, Blurple);
                using (Pen p = new Pen(Color.White, 2f))
                    gr.DrawLines(p, new Point[] { new Point(box.X + 3, box.Y + 7), new Point(box.X + 6, box.Y + 10), new Point(box.X + 11, box.Y + 4) });
            }
            else
            {
                using (GraphicsPath path = RoundPath(box, 4))
                using (Pen p = new Pen(Look.Muted, 1.5f)) gr.DrawPath(p, path);
            }
        }

        static void PaintRadio(object o, PaintEventArgs e)
        {
            RadioButton c = (RadioButton)o;
            Rectangle g = Glyph(c);
            Graphics gr = e.Graphics;
            using (SolidBrush b = new SolidBrush(AreaBg(c))) gr.FillRectangle(b, g);
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = new Rectangle(g.X + 1, g.Y + 1, 14, 14);
            using (Pen p = new Pen(c.Checked ? Blurple : Look.Muted, 1.6f)) gr.DrawEllipse(p, box);
            if (c.Checked) using (SolidBrush b = new SolidBrush(Blurple)) gr.FillEllipse(b, box.X + 4, box.Y + 4, 6, 6);
        }

        static GraphicsPath RoundPath(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // paints over the dropdown's Windows border and arrow box after Windows draws them
        class ComboFrame : NativeWindow
        {
            readonly ComboBox cb;
            ComboFrame(ComboBox c) { cb = c; if (c.IsHandleCreated) AssignHandle(c.Handle); c.HandleCreated += delegate { AssignHandle(c.Handle); }; c.HandleDestroyed += delegate { ReleaseHandle(); }; }
            public static void Attach(ComboBox c) { new ComboFrame(c); }
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == 0xF || m.Msg == 0x85 || m.Msg == 0x7 || m.Msg == 0x8)
                {
                    try
                    {
                        using (Graphics g = Graphics.FromHwnd(cb.Handle))
                        {
                            Rectangle r = new Rectangle(0, 0, cb.Width, cb.Height);
                            int aw = SystemInformation.VerticalScrollBarWidth;
                            using (SolidBrush b = new SolidBrush(Look.Card)) g.FillRectangle(b, r.Right - aw - 1, 1, aw, r.Height - 2);
                            using (Pen p = new Pen(Look.Line)) g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            int cx = r.Right - aw / 2 - 1, cy = r.Height / 2;
                            using (Pen p = new Pen(Look.Muted, 1.6f))
                                g.DrawLines(p, new Point[] { new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2) });
                        }
                    }
                    catch (Exception) { }
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr h, string app, string idList);
        static void DarkTheme(Control c, string theme)
        {
            if (c.IsHandleCreated) { try { SetWindowTheme(c.Handle, theme, null); } catch (Exception) { } }
            else c.HandleCreated += delegate { try { SetWindowTheme(c.Handle, theme, null); } catch (Exception) { } };
        }

        void Fit()
        {
            if (clip == null || fitting) return;
            fitting = true;
            try
            {
                Read();
                int style = sc.IsHandleCreated ? GetWindowLong(sc.Handle, -16) : 0;
                bool vs = (style & 0x00200000) != 0, hs = (style & 0x00100000) != 0;
                int w = clip.ClientSize.Width + (vs ? SystemInformation.VerticalScrollBarWidth : 0);
                int h = clip.ClientSize.Height + (hs ? SystemInformation.HorizontalScrollBarHeight : 0);
                if (sc.Width != w || sc.Height != h) sc.SetBounds(0, 0, w, h);
                SetBounds(clip.ClientSize.Width - 10, 0, 10, clip.ClientSize.Height);
            }
            finally { fitting = false; }
        }

        PillScroll(Control inner)
        {
            sc = inner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Visible = false;
            tick.Interval = 60;
            tick.Tick += delegate
            {
                if (clip == null || !clip.Visible) return;
                bool wasV = vOn;
                int style = sc.IsHandleCreated ? GetWindowLong(sc.Handle, -16) : 0;
                bool vs = (style & 0x00200000) != 0, hs = (style & 0x00100000) != 0;
                int wantW = clip.ClientSize.Width + (vs ? SystemInformation.VerticalScrollBarWidth : 0);
                int wantH = clip.ClientSize.Height + (hs ? SystemInformation.HorizontalScrollBarHeight : 0);
                if (sc.Width != wantW || sc.Height != wantH) Fit();
                Read();
                if (Visible != vOn) Visible = vOn;
                if (pos != lastPos || max != lastMax || page != lastPage) { lastPos = pos; lastMax = max; lastPage = page; Invalidate(); }
            };
            tick.Start();
            Disposed += delegate { tick.Stop(); tick.Dispose(); };
        }

        Rectangle Thumb()
        {
            int range = Math.Max(1, max - min + 1);
            int track = Height - 4;
            int h = Math.Max(28, (int)((long)track * page / range));
            int span = Math.Max(1, range - page);
            int y = 2 + (int)((long)(track - h) * Math.Max(0, Math.Min(span, pos - min)) / span);
            return new Rectangle(3, y, 5, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (!vOn) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = Thumb();
            Color c = dragging || hover ? Look.Muted : ThumbColor;
            using (GraphicsPath p = new GraphicsPath())
            using (SolidBrush b = new SolidBrush(c))
            {
                int d = r.Width;
                p.AddArc(r.X, r.Y, d, d, 180, 180);
                p.AddArc(r.X, r.Bottom - d, d, d, 0, 180);
                p.CloseFigure();
                e.Graphics.FillPath(b, p);
            }
        }

        void ScrollTo(int val)
        {
            Read();
            val = Math.Max(min, Math.Min(max - page + 1, val));
            if (val == pos) return;
            ScrollableControl s2 = sc as ScrollableControl;
            ListView lv = sc as ListView;
            if (s2 != null) s2.AutoScrollPosition = new Point(-s2.AutoScrollPosition.X, val);
            else if (lv != null)
            {
                int rowH = lv.Items.Count > 0 ? lv.GetItemRect(0).Height : 17;
                SendMessage(lv.Handle, 0x1014, IntPtr.Zero, (IntPtr)((val - pos) * rowH));
            }
            else
            {
                SendMessage(sc.Handle, 0x115, (IntPtr)(4 | (val << 16)), IntPtr.Zero);
                SendMessage(sc.Handle, 0x115, (IntPtr)8, IntPtr.Zero);
            }
            Read();
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Read();
            Rectangle r = Thumb();
            if (e.Y >= r.Top && e.Y <= r.Bottom) { dragging = true; dragY = e.Y; dragVal = pos; Capture = true; }
            else ScrollTo(pos + (e.Y < r.Top ? -page : page));
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                Rectangle r = Thumb();
                int track = Math.Max(1, Height - 4 - r.Height);
                int span = Math.Max(1, max - min + 1 - page);
                ScrollTo(dragVal + (int)((long)(e.Y - dragY) * span / track));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Capture = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Read();
            int step = sc is ScrollableControl ? 60 : 3;
            ScrollTo(pos - e.Delta / 120 * step);
            base.OnMouseWheel(e);
        }
    }

    // a slider inside a right-click menu, like Discord's User Volume
    class VolumeSlider : Control
    {
        int val, max; bool drag; readonly string title;
        public event Action<int> Changed;
        public VolumeSlider(string title, int value, int max)
        {
            this.title = title; this.max = max; val = Math.Max(0, Math.Min(max, value));
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Size = new Size(220, 48);
            BackColor = Look.Card;
            Font = new Font("Segoe UI", 9f);
        }
        Rectangle Track { get { return new Rectangle(12, 32, Width - 24, 6); } }
        void SetFrom(int x)
        {
            Rectangle t = Track;
            int v = (int)Math.Round((double)(x - t.X) * max / t.Width);
            v = Math.Max(0, Math.Min(max, v));
            if (Math.Abs(v - 100) <= 4) v = 100;   // snaps to 100%
            if (v != val) { val = v; Invalidate(); if (Changed != null) Changed(val); }
        }
        protected override void OnMouseDown(MouseEventArgs e) { drag = true; Capture = true; SetFrom(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { if (drag) SetFrom(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { drag = false; Capture = false; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            TextRenderer.DrawText(g, title, Font, new Rectangle(10, 4, Width - 20, 20), Look.Soft, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, val + "%", Font, new Rectangle(10, 4, Width - 20, 20), Look.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Rectangle t = Track;
            Look.FillRound(g, t, 3, Look.Line);
            int fx = t.X + t.Width * val / max;
            Look.FillRound(g, new Rectangle(t.X, t.Y, Math.Max(6, fx - t.X), t.Height), 3, Color.FromArgb(88, 101, 242));
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, fx - 7, t.Y - 4, 14, 14);
        }
    }

    static class Look
    {
        public static readonly bool Midnight = Config.Get("ui.theme", "dark") == "midnight";
        public static readonly Color Rail = Midnight ? Color.FromArgb(0, 0, 0) : Color.FromArgb(5, 9, 15);
        public static readonly Color Side = Midnight ? Color.FromArgb(10, 11, 13) : Color.FromArgb(17, 24, 35);
        public static readonly Color Main = Midnight ? Color.FromArgb(0, 0, 0) : Color.FromArgb(11, 17, 26);
        public static readonly Color Card = Midnight ? Color.FromArgb(17, 18, 21) : Color.FromArgb(21, 29, 41);
        public static readonly Color Line = Color.FromArgb(38, 50, 68);
        public static readonly Color Sel = Color.FromArgb(26, 43, 64);
        public static readonly Color Hover = Color.FromArgb(22, 32, 46);
        public static readonly Color Text = Color.FromArgb(232, 238, 246);
        public static readonly Color Soft = Color.FromArgb(185, 196, 211);
        public static readonly Color Muted = Color.FromArgb(139, 152, 170);
        public static readonly Color Accent = Color.FromArgb(116, 200, 255);
        public static readonly Color Green = Color.FromArgb(105, 216, 163);
        public static readonly Color GreenBtn = Color.FromArgb(29, 127, 80);
        public static readonly Color Red = Color.FromArgb(240, 138, 122);
        public static readonly Color BadgeRed = Color.FromArgb(194, 65, 12);

        static readonly Color[] Palette = {
            Color.FromArgb(45, 74, 107), Color.FromArgb(91, 58, 107), Color.FromArgb(107, 74, 45), Color.FromArgb(45, 107, 91),
            Color.FromArgb(107, 45, 66), Color.FromArgb(74, 90, 40), Color.FromArgb(40, 84, 120), Color.FromArgb(110, 60, 30) };

        public static Color AvatarColor(string name)
        {
            int h = 0;
            foreach (char c in (name ?? "").ToLowerInvariant()) h = h * 31 + c;
            return Palette[(h & 0x7fffffff) % Palette.Length];
        }

        public static void Avatar(Graphics g, string name, Rectangle r, bool speaking)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(AvatarColor(name))) g.FillEllipse(b, r);
            if (speaking && Config.Get("ui.speakring", "1") == "0") speaking = false;
            if (speaking)
                using (Pen p = new Pen(Green, 2.5f)) g.DrawEllipse(p, r.X - 1, r.Y - 1, r.Width + 1, r.Height + 1);
            string init = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            using (Font f = new Font("Segoe UI", Math.Max(7f, r.Height * 0.36f), FontStyle.Bold))
                Look.DT(g, init, f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        public static void Dot(Graphics g, Rectangle avatar, Color c, Color ring)
        {
            Rectangle d = new Rectangle(avatar.Right - 10, avatar.Bottom - 10, 12, 12);
            using (SolidBrush b = new SolidBrush(ring)) g.FillEllipse(b, d);
            d.Inflate(-2, -2);
            using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, d);
        }

        public static void Pill(Graphics g, Rectangle r, Color c)
        {
            using (GraphicsPath p = Round(r, r.Height / 2))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = rad * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        // dark scroll bars (Windows 10 1809+; older Windows just keeps the light ones)
        public static void DarkScroll(Control c)
        {
            if (c.IsHandleCreated) { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch (Exception) { } }
            else c.HandleCreated += delegate { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch (Exception) { } };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        // Pop-up windows in the app's own style: no Windows title bar, a thin border, an X in the corner,
        // drag anywhere on the background to move it, Esc closes.
        public static void Modal(Form f, Color bg, params Control[] dragSurfaces)
        {
            f.FormBorderStyle = FormBorderStyle.None;
            f.ShowInTaskbar = false;
            f.Padding = new Padding(1);
            f.BackColor = Line;
            f.KeyPreview = true;
            f.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { e.Handled = true; f.Close(); } };
            Label x = new Label();
            x.Text = "\uE711";
            x.Font = new Font("Segoe MDL2 Assets", 10f);
            x.ForeColor = Soft;
            x.BackColor = bg;
            x.TextAlign = ContentAlignment.MiddleCenter;
            x.Size = new Size(32, 32);
            x.Cursor = Cursors.Hand;
            x.Location = new Point(f.ClientSize.Width - 40, 8);
            x.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            x.MouseEnter += delegate { x.ForeColor = Color.White; };
            x.MouseLeave += delegate { x.ForeColor = Soft; };
            x.Click += delegate { f.Close(); };
            f.Controls.Add(x);
            x.BringToFront();
            MouseEventHandler drag = delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(f.Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            };
            f.MouseDown += drag;
            foreach (Control c in dragSurfaces) c.MouseDown += drag;
            foreach (Control c in f.Controls) if (c is Label && c != x) c.MouseDown += drag;
        }

        // text that follows the list's scroll offset (plain TextRenderer ignores it)
        const TextFormatFlags Keep = TextFormatFlags.PreserveGraphicsTranslateTransform;
        public static void DT(Graphics g, string t, Font f, Rectangle r, Color c) { TextRenderer.DrawText(g, t, f, r, c, Keep | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
        public static void DT(Graphics g, string t, Font f, Rectangle r, Color c, TextFormatFlags fl) { TextRenderer.DrawText(g, t, f, r, c, fl | Keep); }
        public static void DT(Graphics g, string t, Font f, Point p, Color c) { TextRenderer.DrawText(g, t, f, p, c, Keep); }
        public static void DT(Graphics g, string t, Font f, Point p, Color c, TextFormatFlags fl) { TextRenderer.DrawText(g, t, f, p, c, fl | Keep); }

        public static void FillRound(Graphics g, Rectangle r, int rad, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(r, rad))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void Badge(Graphics g, Rectangle at, int n, Color c)
        {
            string t = n > 99 ? "99+" : n.ToString();
            int w = Math.Max(18, 8 + t.Length * 7);
            Rectangle r = new Rectangle(at.Right - w, at.Y + (at.Height - 18) / 2, w, 18);
            Pill(g, r, c);
            using (Font f = new Font("Segoe UI", 8f, FontStyle.Bold))
                Look.DT(g, t, f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        public static void Speaker(Graphics g, int x, int y, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillRectangle(b, x, y + 5, 4, 6);
                g.FillPolygon(b, new Point[] { new Point(x + 4, y + 5), new Point(x + 9, y + 1), new Point(x + 9, y + 15), new Point(x + 4, y + 11) });
            }
            using (Pen p = new Pen(c, 1.6f)) g.DrawArc(p, x + 8, y + 3, 6, 10, -60, 120);
        }

        public static Color PresenceColor(string p)
        {
            switch (p)
            {
                case "online": return Green;
                case "idle": return Color.FromArgb(240, 178, 50);
                case "dnd": return Color.FromArgb(242, 63, 67);
            }
            return Color.FromArgb(110, 118, 132);
        }

        public static string PresenceName(string p)
        {
            switch (p)
            {
                case "online": return "Online";
                case "idle": return "Idle";
                case "dnd": return "Do Not Disturb";
                case "invisible": return "Invisible";
            }
            return "Offline";
        }

        public static string When(DateTime t)
        {
            DateTime now = DateTime.Now;
            string clock = Config.Get("ui.24h", "0") == "1" ? "HH:mm" : "h:mm tt";
            if (t.Date == now.Date) return "Today at " + t.ToString(clock);
            if (t.Date == now.Date.AddDays(-1)) return "Yesterday at " + t.ToString(clock);
            return t.ToString("M/d/yyyy " + clock);
        }

        public static string GameTitle(string id)
        {
            switch (id)
            {
                case "q3": return "Quake III Arena";
                case "oa": return "OpenArena";
                case "ut": return "Unreal Tournament";
                case "mf": return "Metal Fatigue";
            }
            return string.IsNullOrEmpty(id) ? "Game" : id;
        }

        public static Color GameColor(string id)
        {
            switch (id)
            {
                case "q3": return Color.FromArgb(110, 46, 16);
                case "oa": return Color.FromArgb(18, 88, 104);
                case "ut": return Color.FromArgb(34, 56, 130);
                case "mf": return Color.FromArgb(84, 58, 30);
            }
            return Color.FromArgb(45, 74, 107);
        }
    }

    // ---------------------------------------------------------------- sidebar list

    enum NavKind { Header, Channel, Voice, VoiceMember, Game, Dm, Link, Gap }

    class NavItem
    {
        public NavKind Kind;
        public string Text = "", Key = "", Sub = "";
        public int Unread, Mentions;
        public bool Online, Speaking, Mute, Deaf, Active, Collapsed, Muted;
        public string Presence = "";
        public object Tag;
        public Rectangle Bounds;
    }

    class NavList : Control
    {
        public readonly List<NavItem> Items = new List<NavItem>();
        public string Selected = "";
        public event Action<NavItem> Picked;
        public event Action<NavItem, Point> Menu;
        int scroll, hover = -1, total;

        public NavList()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Look.Side;
        }

        static int HeightOf(NavItem it)
        {
            switch (it.Kind)
            {
                case NavKind.Header: return 32;
                case NavKind.VoiceMember: return 28;
                case NavKind.Gap: return 8;
                case NavKind.Dm: return 42;
            }
            return 34;
        }

        public void Rebuild()
        {
            int y = 8;
            foreach (NavItem it in Items)
            {
                int h = HeightOf(it);
                it.Bounds = new Rectangle(8, y, Width - 16, h);
                y += h + (it.Kind == NavKind.Header || it.Kind == NavKind.VoiceMember ? 0 : 2);
            }
            total = y + 8;
            ClampScroll();
            Invalidate();
        }

        void ClampScroll()
        {
            int max = Math.Max(0, total - Height);
            if (scroll > max) scroll = max;
            if (scroll < 0) scroll = 0;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Rebuild(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll -= e.Delta / 120 * 40;
            ClampScroll();
            Invalidate();
        }

        int Hit(Point p)
        {
            Point q = new Point(p.X, p.Y + scroll);
            for (int i = 0; i < Items.Count; i++)
                if ((Items[i].Kind != NavKind.Header || Items[i].Key.Length > 0) && Items[i].Kind != NavKind.Gap && Items[i].Bounds.Contains(q)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h < 0) return;
            if (e.Button == MouseButtons.Left && Picked != null) Picked(Items[h]);
            else if (e.Button == MouseButtons.Right && Menu != null) Menu(Items[h], e.Location);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Look.Side);
            g.TranslateTransform(0, -scroll);
            TextFormatFlags tf = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            using (Font fHead = new Font("Segoe UI", 7.75f, FontStyle.Bold))
            using (Font fItem = new Font("Segoe UI", 10f))
            using (Font fBold = new Font("Segoe UI Semibold", 10f))
            using (Font fSmall = new Font("Segoe UI", 8.25f))
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    NavItem it = Items[i];
                    Rectangle b = it.Bounds;
                    if (b.Bottom < scroll || b.Top > scroll + Height) continue;
                    bool sel = it.Key.Length > 0 && it.Key == Selected;
                    if (it.Kind == NavKind.Header)
                    {
                        Color hc = i == hover ? Look.Text : Look.Muted;
                        string t = it.Text.ToUpperInvariant();
                        Look.DT(g, t, fHead, new Rectangle(b.X + 4, b.Y + 10, b.Width - 16, 20), hc, tf);
                        if (it.Key.Length > 0)
                        {
                            int tw = TextRenderer.MeasureText(t, fHead).Width;
                            int cx = b.X + 4 + tw + 2, cy = b.Y + 19;
                            using (Pen cp = new Pen(hc, 1.4f))
                            {
                                if (it.Collapsed) { g.DrawLine(cp, cx, cy - 4, cx + 3, cy - 1); g.DrawLine(cp, cx + 3, cy - 1, cx, cy + 2); }
                                else { g.DrawLine(cp, cx - 1, cy - 2, cx + 2, cy + 1); g.DrawLine(cp, cx + 2, cy + 1, cx + 5, cy - 2); }
                            }
                        }
                        continue;
                    }
                    if (it.Kind == NavKind.Gap) continue;
                    if (sel) Look.FillRound(g, b, 6, Look.Sel);
                    else if (i == hover) Look.FillRound(g, b, 6, Look.Hover);
                    bool strong = sel || ((it.Unread > 0 || it.Mentions > 0) && !it.Muted);
                    Color tc = sel ? Color.White : strong ? Color.White : it.Muted ? Color.FromArgb(90, 100, 116) : Look.Soft;
                    int tx = b.X + 10;
                    switch (it.Kind)
                    {
                        case NavKind.Channel:
                            using (Font fh = new Font("Segoe UI", 13f))
                                Look.DT(g, "#", fh, new Rectangle(b.X + 8, b.Y, 18, b.Height), Look.Muted, tf);
                            tx = b.X + 30;
                            break;
                        case NavKind.Voice:
                            Look.Speaker(g, b.X + 10, b.Y + 9, it.Active ? Look.Green : Look.Muted);
                            tx = b.X + 32;
                            if (it.Active) tc = Look.Green;
                            break;
                        case NavKind.VoiceMember:
                        {
                            Rectangle av = new Rectangle(b.X + 30, b.Y + 4, 20, 20);
                            Look.Avatar(g, it.Text, av, it.Speaking && !it.Mute);
                            tx = b.X + 58;
                            tc = it.Speaking ? Color.White : Look.Soft;
                            string tag = it.Deaf ? "deafened" : it.Mute ? "muted" : "";
                            if (tag.Length > 0)
                                Look.DT(g, tag, fSmall, new Rectangle(b.X, b.Y, b.Width - 6, b.Height), Look.Red, tf | TextFormatFlags.Right);
                            break;
                        }
                        case NavKind.Dm:
                        {
                            Rectangle av = new Rectangle(b.X + 6, b.Y + 5, 32, 32);
                            Look.Avatar(g, it.Text, av, false);
                            Look.Dot(g, av, Look.PresenceColor(it.Online ? (it.Presence.Length > 0 ? it.Presence : "online") : "offline"), sel ? Look.Sel : i == hover ? Look.Hover : Look.Side);
                            Look.DT(g, it.Text, strong ? fBold : fItem, new Rectangle(b.X + 46, b.Y + 3, b.Width - 80, 20), tc, tf);
                            Look.DT(g, it.Sub, fSmall, new Rectangle(b.X + 46, b.Y + 21, b.Width - 80, 16), it.Sub.StartsWith("Playing") ? Look.Accent : Look.Muted, tf);
                            if (it.Mentions > 0) Look.Badge(g, new Rectangle(b.X, b.Y, b.Width - 6, b.Height), it.Mentions, Look.BadgeRed);
                            continue;
                        }
                        case NavKind.Game:
                        {
                            Rectangle sq = new Rectangle(b.X + 8, b.Y + 7, 20, 20);
                            Look.FillRound(g, sq, 5, Look.GameColor(it.Key.Replace("game:", "")));
                            tx = b.X + 36;
                            break;
                        }
                        case NavKind.Link:
                            break;
                    }
                    if (it.Kind != NavKind.VoiceMember)
                        Look.DT(g, it.Text, strong ? fBold : fItem, new Rectangle(tx, b.Y, b.Width - (tx - b.X) - 30, b.Height), tc, tf);
                    else
                        Look.DT(g, it.Text, fItem, new Rectangle(tx, b.Y, b.Width - (tx - b.X) - 60, b.Height), tc, tf);
                    if (it.Mentions > 0) Look.Badge(g, new Rectangle(b.X, b.Y, b.Width - 6, b.Height), it.Mentions, Look.BadgeRed);
                    else if (it.Kind == NavKind.Voice && it.Sub.Length > 0)
                        Look.DT(g, it.Sub, fSmall, new Rectangle(b.X, b.Y, b.Width - 8, b.Height), Look.Muted, tf | TextFormatFlags.Right);
                    else if (it.Unread > 0 && !sel && !it.Muted)
                    {
                        using (SolidBrush wb = new SolidBrush(Color.White)) g.FillEllipse(wb, b.X - 6, b.Y + b.Height / 2 - 4, 8, 8);
                    }
                }
            }
            g.ResetTransform();
        }
    }

    // ---------------------------------------------------------------- chat view

    class ChatView : Control
    {
        class Row
        {
            public ChatMsg Msg;
            public bool Head, Mention, DateLine;
            public int Y, H, TextY, TextH, TopY;
            public Rectangle Card, Button, Avatar, NameRect, TextRect;
            public List<KeyValuePair<string, Rectangle>> Pills = new List<KeyValuePair<string, Rectangle>>();
            public string Url = "";
        }

        readonly List<ChatMsg> msgs = new List<ChatMsg>();
        readonly List<Row> rows = new List<Row>();
        public string Me = "";
        public bool MeOwner;
        public string WelcomeTitle = "", WelcomeText = "";
        public bool IsChannel = true;
        public string Filter = "";
        public HashSet<string> Ignored;
        bool IsIgnored(ChatMsg m) { return Ignored != null && m.User != null && Ignored.Contains(m.User); }
        public event Action<ChatMsg> JoinInvite;
        public event Action<ChatMsg, string, bool> React;        // message, emoji, on
        public event Action<ChatMsg, Point> PickReaction;       // open the emoji picker at a screen point
        public event Action<ChatMsg> Reply, Edit, Delete;
        public event Action<string, Point> PersonClicked;       // name, screen point
        int scroll, total;
        bool stick = true;
        int hoverRow = -1, hoverTool = -1, hoverBtn = -1;
        readonly List<KeyValuePair<string, Rectangle>> tools = new List<KeyValuePair<string, Rectangle>>();

        static readonly Font FBody = new Font("Segoe UI", ChatSize());
        static float ChatSize() { float f; return float.TryParse(Config.Get("ui.chatsize", "10.25"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) && f >= 8 && f <= 16 ? f : 10.25f; }
        static readonly Font FName = new Font("Segoe UI Semibold", 10.25f);
        static readonly Font FTime = new Font("Segoe UI", 8.25f);
        static readonly Font FIcon = new Font("Segoe MDL2 Assets", 11f);
        static readonly Font FEmoji = new Font("Segoe UI Emoji", 10.5f);
        const int TL = 72;
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        static readonly System.Text.RegularExpressions.Regex UrlRe = new System.Text.RegularExpressions.Regex(@"https?://[^\s<>""]+");

        public ChatView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Look.Main;
        }

        public void SetMessages(List<ChatMsg> list)
        {
            msgs.Clear();
            if (list != null) msgs.AddRange(list);
            stick = true;
            Relayout();
        }

        public void Add(ChatMsg m) { msgs.Add(m); Relayout(); }

        public void Replace(ChatMsg m)
        {
            for (int i = 0; i < msgs.Count; i++) if (msgs[i].Id == m.Id && m.Id != 0) msgs[i] = m;
            Relayout();
        }

        public void Remove(long id)
        {
            msgs.RemoveAll(delegate(ChatMsg x) { return x.Id == id && id != 0; });
            Relayout();
        }

        public ChatMsg LastOwn()
        {
            for (int i = msgs.Count - 1; i >= 0; i--)
                if (string.Equals(msgs[i].User, Me, StringComparison.OrdinalIgnoreCase) && !msgs[i].IsInvite && msgs[i].Id != 0) return msgs[i];
            return null;
        }

        bool Mentions(ChatMsg m)
        {
            if (Me.Length == 0 || string.Equals(m.User, Me, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(m.ReplyUser, Me, StringComparison.OrdinalIgnoreCase)) return true;
            string t = (m.Text ?? "").ToLowerInvariant();
            return t.Contains("@" + Me.ToLowerInvariant()) || t.Contains("@everyone") || t.Contains("@here");
        }

        void Relayout()
        {
            rows.Clear();
            int w = Math.Max(200, Width - TL - 40);
            int y = 24 + 150;
            ChatMsg p = null;
            string f = Filter.Trim().ToLowerInvariant();
            foreach (ChatMsg m in msgs)
            {
                if (f.Length > 0 && (m.Text + " " + m.User + " " + m.Title).ToLowerInvariant().IndexOf(f) < 0) continue;
                if (p == null || p.Time.Date != m.Time.Date)
                {
                    Row d = new Row(); d.DateLine = true; d.Msg = m; d.Y = y + 8; d.H = 28;
                    rows.Add(d);
                    y += 40;
                    p = null;
                }
                Row r = new Row();
                r.Msg = m;
                r.Head = p == null || p.User != m.User || (m.Time - p.Time).TotalMinutes > 7 || m.IsInvite || p.IsInvite || m.ReplyId != 0;
                r.Mention = Mentions(m);
                if (r.Head) y += 14;
                r.TopY = y;
                int h = 0;
                if (m.ReplyId != 0) h += 20;
                r.Y = y + h;
                if (r.Head) h += 22;
                r.TextY = y + h;
                r.TextH = string.IsNullOrEmpty(m.Text) ? 0 : TextRenderer.MeasureText(m.Text, FBody, new Size(w, int.MaxValue), Wrap).Height;
                r.TextRect = new Rectangle(TL, r.TextY, Math.Min(w, TextRenderer.MeasureText(m.Text ?? "", FBody, new Size(w, int.MaxValue), Wrap).Width + 4), r.TextH);
                h += r.TextH;
                System.Text.RegularExpressions.Match um = UrlRe.Match(m.Text ?? "");
                if (um.Success) r.Url = um.Value;
                if (m.IsInvite)
                {
                    h += r.TextH > 0 ? 6 : 0;
                    r.Card = new Rectangle(TL, y + h, Math.Min(440, w), 110);
                    r.Button = new Rectangle(r.Card.Right - 100, r.Card.Y + 64, 88, 34);
                    h += 114;
                }
                if (m.Reactions.Count > 0)
                {
                    int px = TL, py = y + h + 4;
                    foreach (KeyValuePair<string, List<string>> kv in m.Reactions)
                    {
                        int pw = 24 + TextRenderer.MeasureText(kv.Key, FEmoji).Width + TextRenderer.MeasureText(kv.Value.Count.ToString(), FTime).Width;
                        if (px + pw > TL + w) { px = TL; py += 30; }
                        r.Pills.Add(new KeyValuePair<string, Rectangle>(kv.Key, new Rectangle(px, py, pw, 26)));
                        px += pw + 6;
                    }
                    h = py + 26 - y + 2;
                }
                if (r.Head)
                {
                    r.Avatar = new Rectangle(16, r.Y + 2, 40, 40);
                    r.NameRect = new Rectangle(TL, r.Y, TextRenderer.MeasureText(m.User, FName, Size.Empty, TextFormatFlags.NoPadding).Width, 20);
                }
                r.H = Math.Max(h, r.Head ? 42 + (m.ReplyId != 0 ? 20 : 0) : 20);
                y += r.H + 2;
                rows.Add(r);
                p = m;
            }
            total = y + 24;
            int max = Math.Max(0, total - Height);
            if (stick) scroll = max;
            if (scroll > max) scroll = max;
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, total - Height);
            scroll -= e.Delta / 120 * 60;
            if (scroll < 0) scroll = 0;
            if (scroll > max) scroll = max;
            stick = scroll >= max - 4;
            Invalidate();
        }

        Point Doc(Point p) { return new Point(p.X, p.Y + scroll); }

        int RowAt(Point q)
        {
            for (int i = 0; i < rows.Count; i++)
                if (!rows[i].DateLine && q.Y >= rows[i].TopY - 4 && q.Y < rows[i].TopY + rows[i].H + 2) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point q = Doc(e.Location);
            int t = -1;
            for (int i = 0; i < tools.Count; i++) if (tools[i].Value.Contains(q)) t = i;
            int r = t >= 0 ? hoverRow : RowAt(q);
            int b = -1;
            for (int i = 0; i < rows.Count; i++) if (rows[i].Msg != null && rows[i].Msg.IsInvite && rows[i].Button.Contains(q)) b = i;
            if (r != hoverRow || t != hoverTool || b != hoverBtn) { hoverRow = r; hoverTool = t; hoverBtn = b; Invalidate(); }
            bool hand = t >= 0 || b >= 0;
            if (!hand && r >= 0)
            {
                Row row = rows[r];
                if (row.Avatar.Contains(q) || row.NameRect.Contains(q) || (row.Url.Length > 0 && row.TextRect.Contains(q))) hand = true;
                foreach (KeyValuePair<string, Rectangle> pill in row.Pills) if (pill.Value.Contains(q)) hand = true;
            }
            Cursor = hand ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { hoverRow = hoverTool = hoverBtn = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            Point q = Doc(e.Location);
            if (e.Button == MouseButtons.Left)
            {
                for (int i = 0; i < tools.Count; i++)
                    if (tools[i].Value.Contains(q) && hoverRow >= 0) { Tool(rows[hoverRow].Msg, tools[i].Key, tools[i].Value); return; }
                int r = RowAt(q);
                if (r < 0) return;
                Row row = rows[r];
                if (row.Msg.IsInvite && row.Button.Contains(q)) { if (JoinInvite != null) JoinInvite(row.Msg); return; }
                foreach (KeyValuePair<string, Rectangle> pill in row.Pills)
                    if (pill.Value.Contains(q) && React != null)
                    {
                        List<string> who;
                        bool mine = row.Msg.Reactions.TryGetValue(pill.Key, out who) && who.Exists(delegate(string x) { return string.Equals(x, Me, StringComparison.OrdinalIgnoreCase); });
                        React(row.Msg, pill.Key, !mine);
                        return;
                    }
                if ((row.Avatar.Contains(q) || row.NameRect.Contains(q)) && PersonClicked != null) { PersonClicked(row.Msg.User, PointToScreen(new Point(row.NameRect.Right + 8, row.Y - scroll))); return; }
                if (row.Url.Length > 0 && row.TextRect.Contains(q)) OpenUrl(row.Url);
            }
            else if (e.Button == MouseButtons.Right)
            {
                int r = RowAt(q);
                if (r < 0) return;
                ChatMsg m = rows[r].Msg;
                string url = rows[r].Url;
                ContextMenuStrip cm = new ContextMenuStrip();
                DarkMenu.Apply(cm);
                if (IsChannel && m.Id != 0)
                {
                    FlowLayoutPanel quick = new FlowLayoutPanel();
                    quick.BackColor = Look.Card; quick.Size = new Size(196, 40); quick.Padding = new Padding(4, 2, 0, 0);
                    foreach (string qe in new string[] { "\U0001F44D", "\u2764\uFE0F", "\U0001F602", "\U0001F525" })
                    {
                        string emo = qe;
                        Label b = new Label();
                        b.Text = emo; b.Font = new Font("Segoe UI Emoji", 13f); b.ForeColor = Look.Text;
                        b.Size = new Size(42, 34); b.TextAlign = ContentAlignment.MiddleCenter; b.Cursor = Cursors.Hand;
                        b.BackColor = Color.FromArgb(26, 36, 51); b.Margin = new Padding(3);
                        b.Click += delegate { cm.Close(); bool on = !(m.Reactions != null && m.Reactions.ContainsKey(emo) && m.Reactions[emo].Exists(x => string.Equals(x, Me, StringComparison.OrdinalIgnoreCase))); if (React != null) React(m, emo, on); };
                        quick.Controls.Add(b);
                    }
                    ToolStripControlHost qh = new ToolStripControlHost(quick);
                    qh.Margin = Padding.Empty;
                    cm.Items.Add(qh);
                    cm.Items.Add(new ToolStripSeparator());
                    cm.Items.Add("Add Reaction", null, delegate { if (PickReaction != null) PickReaction(m, Cursor.Position); });
                    cm.Items.Add("Reply", null, delegate { if (Reply != null) Reply(m); });
                }
                if (Mine(m) && IsChannel && !m.IsInvite && m.Id != 0) cm.Items.Add("Edit Message", null, delegate { if (Edit != null) Edit(m); });
                if (!string.IsNullOrEmpty(m.Text)) cm.Items.Add("Copy Text", null, delegate { try { Clipboard.SetText(m.Text); } catch (Exception) { } });
                if (url.Length > 0)
                {
                    cm.Items.Add("Open Link", null, delegate { OpenUrl(url); });
                    cm.Items.Add("Copy Link", null, delegate { try { Clipboard.SetText(url); } catch (Exception) { } });
                }
                if (IsChannel && m.Id != 0 && (Mine(m) || MeOwner))
                {
                    cm.Items.Add(new ToolStripSeparator());
                    ToolStripItem del = cm.Items.Add("Delete Message", null, delegate { if (Delete != null) Delete(m); });
                    del.ForeColor = Look.Red; del.Tag = "danger";
                }
                foreach (ToolStripItem it in cm.Items) if (it.ForeColor != Look.Red) it.ForeColor = Look.Text;
                if (cm.Items.Count > 0) cm.Show(this, e.Location);
            }
        }

        bool Mine(ChatMsg m) { return string.Equals(m.User, Me, StringComparison.OrdinalIgnoreCase); }

        static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
        }

        void Tool(ChatMsg m, string key, Rectangle r)
        {
            Point at = PointToScreen(new Point(r.X, r.Bottom - scroll));
            switch (key)
            {
                case "react": if (PickReaction != null) PickReaction(m, at); break;
                case "reply": if (Reply != null) Reply(m); break;
                case "edit": if (Edit != null) Edit(m); break;
                case "delete": if (Delete != null) Delete(m); break;
                default:
                    if (React != null)
                    {
                        List<string> who;
                        bool on = !(m.Reactions.TryGetValue(key, out who) && who.Exists(delegate(string x) { return string.Equals(x, Me, StringComparison.OrdinalIgnoreCase); }));
                        React(m, key, on);
                    }
                    break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Look.Main);
            g.TranslateTransform(0, -scroll);
            TextFormatFlags one = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

            Rectangle circ = new Rectangle(20, 24, 68, 68);
            Look.FillRound(g, circ, 34, Look.Card);
            using (Font fh = new Font("Segoe UI", 28f))
                Look.DT(g, IsChannel ? "#" : "@", fh, circ, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (Font ft = new Font("Segoe UI Semibold", 20f))
                Look.DT(g, Filter.Length > 0 ? "Search: " + Filter : WelcomeTitle, ft, new Rectangle(20, 100, Width - 40, 40), Color.White, one);
            Look.DT(g, Filter.Length > 0 ? (rows.Count == 0 ? "No messages match." : "Showing matching messages.") : WelcomeText,
                FBody, new Rectangle(20, 140, Width - 40, 24), Look.Muted, one);

            int w = Math.Max(200, Width - TL - 40);
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (r.TopY + r.H < scroll - 30 || r.TopY - 30 > scroll + Height) continue;
                ChatMsg m = r.Msg;
                if (r.DateLine)
                {
                    string d = m.Time.ToString("MMMM d, yyyy");
                    Size ds = TextRenderer.MeasureText(d, FTime);
                    int cy = r.Y + 14, cx = Width / 2;
                    using (Pen lp = new Pen(Look.Line)) { g.DrawLine(lp, 16, cy, cx - ds.Width / 2 - 8, cy); g.DrawLine(lp, cx + ds.Width / 2 + 8, cy, Width - 16, cy); }
                    Look.DT(g, d, FTime, new Point(cx - ds.Width / 2, cy - ds.Height / 2), Look.Muted);
                    continue;
                }
                Rectangle band = new Rectangle(0, r.TopY - 4, Width, r.H + 6);
                if (r.Mention)
                {
                    using (SolidBrush mb = new SolidBrush(Color.FromArgb(40, 34, 18))) g.FillRectangle(mb, band);
                    using (SolidBrush bar = new SolidBrush(Color.FromArgb(240, 178, 50))) g.FillRectangle(bar, 0, band.Y, 3, band.Height);
                }
                else if (i == hoverRow)
                    using (SolidBrush hb = new SolidBrush(Color.FromArgb(15, 23, 34))) g.FillRectangle(hb, band);

                if (m.ReplyId != 0)
                {
                    using (Pen rp = new Pen(Color.FromArgb(70, 84, 104), 2f))
                    {
                        g.DrawLine(rp, 36, r.TopY + 12, 36, r.TopY + 6);
                        g.DrawLine(rp, 36, r.TopY + 6, TL - 8, r.TopY + 6);
                    }
                    Look.Avatar(g, m.ReplyUser, new Rectangle(TL - 4, r.TopY, 16, 16), false);
                    string rt = "@" + m.ReplyUser + "  " + m.ReplyText;
                    Look.DT(g, rt, FTime, new Rectangle(TL + 16, r.TopY, w - 20, 16), Look.Soft, one);
                }
                if (r.Head)
                {
                    Look.Avatar(g, m.User, r.Avatar, false);
                    Look.DT(g, m.User, FName, new Point(TL, r.Y), Color.White, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    Look.DT(g, Look.When(m.Time) + (m.Edited ? "  (edited)" : ""), FTime, new Point(TL + r.NameRect.Width + 8, r.Y + 3), Look.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                }
                else if (i == hoverRow)
                    Look.DT(g, m.Time.ToString("h:mm"), FTime, new Rectangle(8, r.TextY + 2, 56, 16), Look.Muted, TextFormatFlags.HorizontalCenter);
                if (r.TextH > 0)
                {
                    Color tc = r.Url.Length > 0 && r.Url == (m.Text ?? "").Trim() ? Look.Accent : Color.FromArgb(211, 219, 230);
                    if (IsIgnored(m)) Look.DT(g, "Ignored message", FBody, new Rectangle(TL, r.TextY, w, r.TextH), Look.Muted, Wrap);
                    else Look.DT(g, m.Text, FBody, new Rectangle(TL, r.TextY, w, r.TextH), tc, Wrap);
                    if (m.Edited && !r.Head)
                        Look.DT(g, "(edited)", FTime, new Rectangle(Width - 80, r.TextY + 2, 70, 16), Look.Muted, TextFormatFlags.Right);
                }
                if (m.IsInvite) DrawCard(g, r, i == hoverBtn);
                foreach (KeyValuePair<string, Rectangle> pill in r.Pills)
                {
                    List<string> who = m.Reactions[pill.Key];
                    bool mine = who.Exists(delegate(string x) { return string.Equals(x, Me, StringComparison.OrdinalIgnoreCase); });
                    Look.FillRound(g, pill.Value, 8, mine ? Color.FromArgb(28, 52, 82) : Look.Card);
                    using (GraphicsPath pp = Look.Round(pill.Value, 8))
                    using (Pen pen = new Pen(mine ? Look.Accent : Look.Line)) g.DrawPath(pen, pp);
                    Look.DT(g, pill.Key, FEmoji, new Point(pill.Value.X + 6, pill.Value.Y + 2), Color.White, TextFormatFlags.NoPadding);
                    int ex = pill.Value.X + 10 + TextRenderer.MeasureText(pill.Key, FEmoji).Width;
                    Look.DT(g, who.Count.ToString(), FTime, new Point(ex, pill.Value.Y + 5), mine ? Look.Accent : Look.Soft, TextFormatFlags.NoPadding);
                }
            }

            // the hover toolbar, like Discord's
            tools.Clear();
            if (hoverRow >= 0 && hoverRow < rows.Count && !rows[hoverRow].DateLine)
            {
                Row r = rows[hoverRow];
                ChatMsg m = r.Msg;
                List<string> keys = new List<string>();
                if (IsChannel && m.Id != 0) { keys.Add("\U0001F44D"); keys.Add("❤️"); keys.Add("\U0001F602"); keys.Add("react"); keys.Add("reply"); }
                if (IsChannel && m.Id != 0 && Mine(m) && !m.IsInvite) keys.Add("edit");
                if (IsChannel && m.Id != 0 && (Mine(m) || MeOwner)) keys.Add("delete");
                if (keys.Count > 0)
                {
                    int bw = 32, tw = keys.Count * bw + 8;
                    Rectangle bar = new Rectangle(Width - 24 - tw, Math.Max(scroll + 2, r.TopY - 18), tw, 34);
                    Look.FillRound(g, bar, 6, Look.Card);
                    using (GraphicsPath bp = Look.Round(bar, 6))
                    using (Pen pen = new Pen(Look.Line)) g.DrawPath(pen, bp);
                    for (int k = 0; k < keys.Count; k++)
                    {
                        Rectangle b = new Rectangle(bar.X + 4 + k * bw, bar.Y + 2, bw, 30);
                        tools.Add(new KeyValuePair<string, Rectangle>(keys[k], b));
                        if (k == hoverTool) Look.FillRound(g, b, 4, Look.Hover);
                        string glyph; Font f = FIcon; Color c = Look.Soft;
                        switch (keys[k])
                        {
                            case "react": glyph = ""; break;
                            case "reply": glyph = ""; break;
                            case "edit": glyph = ""; break;
                            case "delete": glyph = ""; c = Look.Red; break;
                            default: glyph = keys[k]; f = FEmoji; c = Color.White; break;
                        }
                        Look.DT(g, glyph, f, b, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                }
            }
            g.ResetTransform();

            int max = Math.Max(0, total - Height);
            if (max > 0)
            {
                int bh = Math.Max(30, Height * Height / total);
                int by = (Height - bh) * scroll / max;
                Look.FillRound(g, new Rectangle(Width - 8, by + 2, 5, bh - 4), 2, PillScroll.ThumbColor);
            }
        }

        void DrawCard(Graphics g, Row r, bool hot)
        {
            ChatMsg m = r.Msg;
            Rectangle c = r.Card;
            Look.FillRound(g, c, 10, Look.Card);
            using (GraphicsPath p = Look.Round(c, 10))
            using (Pen pen = new Pen(Look.Line)) g.DrawPath(pen, p);
            Rectangle band = new Rectangle(c.X + 1, c.Y + 1, c.Width - 2, 46);
            using (GraphicsPath clip = Look.Round(c, 10)) g.SetClip(clip, CombineMode.Intersect);
            using (LinearGradientBrush lb = new LinearGradientBrush(band, Look.GameColor(m.Game), Color.FromArgb(30, 22, 40), 0f))
                g.FillRectangle(lb, band);
            g.ResetClip();
            using (Font fg = new Font("Segoe UI Semibold", 12f))
                Look.DT(g, Look.GameTitle(m.Game), fg, new Rectangle(c.X + 14, c.Y + 10, c.Width - 28, 26), Color.White, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            Look.DT(g, m.Title, FName, new Rectangle(c.X + 14, c.Y + 58, c.Width - 130, 20), Color.White, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            Look.DT(g, m.Detail, FTime, new Rectangle(c.X + 14, c.Y + 80, c.Width - 130, 20), Look.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            Look.FillRound(g, r.Button, 6, hot ? Color.FromArgb(36, 150, 96) : Look.GreenBtn);
            using (Font fb = new Font("Segoe UI Semibold", 10f))
                Look.DT(g, "Join", fb, r.Button, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // A small grid of emoji, used for reactions and for the message box.
    class EmojiPicker : ToolStripDropDown
    {
        public static readonly string[] Set = {
            "\U0001F44D", "\U0001F44E", "❤️", "\U0001F602", "\U0001F62E", "\U0001F622", "\U0001F621", "\U0001F525",
            "\U0001F389", "\U0001F44F", "\U0001F64F", "\U0001F480", "\U0001F914", "\U0001F60E", "\U0001F923", "\U0001F62D",
            "\U0001F3AE", "\U0001F3AF", "\U0001F4AF", "✅", "❌", "⭐", "\U0001F44B", "\U0001F91D",
            "\U0001F60D", "\U0001F634", "\U0001F92F", "\U0001F608", "\U0001F47B", "\U0001F680", "\U0001F37A", "\U0001F355" };
        public event Action<string> Picked;

        public EmojiPicker()
        {
            Padding = new Padding(6);
            BackColor = Look.Card;
            Renderer = new ToolStripProfessionalRenderer(new DarkColors());
            Panel grid = new Panel();
            grid.Size = new Size(8 * 38 + 4, 4 * 38 + 4);
            grid.BackColor = Look.Card;
            for (int i = 0; i < Set.Length; i++)
            {
                Button b = new Button();
                b.Text = Set[i];
                b.Font = new Font("Segoe UI Emoji", 14f);
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Look.Hover;
                b.BackColor = Look.Card;
                b.ForeColor = Color.White;
                b.SetBounds(2 + (i % 8) * 38, 2 + (i / 8) * 38, 36, 36);
                string e = Set[i];
                b.Click += delegate { Close(); if (Picked != null) Picked(e); };
                grid.Controls.Add(b);
            }
            ToolStripControlHost host = new ToolStripControlHost(grid);
            host.Margin = Padding.Empty;
            host.Padding = Padding.Empty;
            Items.Add(host);
        }
    }

    // Click a name: a small profile card.
    class ProfileCard : ToolStripDropDown
    {
        public event Action<string> Message, AddFriend;

        public ProfileCard(Friend f, bool isFriend, bool isMe)
        {
            BackColor = Look.Card;
            Padding = new Padding(0);
            Renderer = new ToolStripProfessionalRenderer(new DarkColors());
            Panel p = new Panel();
            p.Size = new Size(300, 250);
            p.BackColor = Look.Card;
            string name = f == null ? "" : f.Name;
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (SolidBrush b = new SolidBrush(Look.AvatarColor(name))) g.FillRectangle(b, 0, 0, 300, 70);
                Rectangle av = new Rectangle(16, 30, 76, 76);
                using (SolidBrush ring = new SolidBrush(Look.Card)) g.FillEllipse(ring, av.X - 5, av.Y - 5, av.Width + 10, av.Height + 10);
                Look.Avatar(g, name, av, false);
                Look.Dot(g, new Rectangle(av.X + 4, av.Y + 4, av.Width, av.Height), Look.PresenceColor(f == null ? "offline" : f.Presence), Look.Card);
                using (Font fn = new Font("Segoe UI Semibold", 15f))
                    Look.DT(g, name, fn, new Point(16, 114), Color.White, TextFormatFlags.NoPrefix);
                if (f != null && f.Owner)
                    using (Font fc = new Font("Segoe UI Symbol", 11f))
                        Look.DT(g, "♛ group owner", fc, new Point(18, 144), Color.FromArgb(240, 178, 50), TextFormatFlags.NoPrefix);
                string st = f == null ? "" : !f.Online ? "Offline" : f.Game.Length > 0 ? f.Status : Look.PresenceName(f.Presence) + (f.Status.Length > 0 && f.Status != "Online" ? " · " + f.Status : "");
                using (Font fs = new Font("Segoe UI", 9.5f))
                    Look.DT(g, st, fs, new Rectangle(16, 166, 270, 20), f != null && f.Game.Length > 0 ? Look.Accent : Look.Soft, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            };
            if (!isMe && f != null)
            {
                Button b = new Button();
                b.Text = isFriend ? "Message @" + name : "Add Friend";
                b.Tag = "primary";
                b.SetBounds(16, 200, 268, 36);
                b.Click += delegate
                {
                    Close();
                    if (isFriend) { if (Message != null) Message(name); }
                    else if (AddFriend != null) AddFriend(name);
                };
                p.Controls.Add(b);
                Theme.Style(b);
            }
            else p.Height = 200;
            ToolStripControlHost host = new ToolStripControlHost(p);
            host.Margin = Padding.Empty;
            host.Padding = Padding.Empty;
            Items.Add(host);
        }
    }

    // ---------------------------------------------------------------- people lists (members, friends)

    class PersonRow
    {
        public bool Header;
        public string Name = "", Sub = "", Key = "";
        public bool Online, Playing, Dim, Owner;
        public string Presence = "";
        public string[] Actions = new string[0];      // button labels drawn on the right
        public Rectangle Bounds;
        public Rectangle[] ActionRects = new Rectangle[0];
        public object Tag;
    }

    class PeopleList : Control
    {
        public readonly List<PersonRow> Rows = new List<PersonRow>();
        public int RowHeight = 44;
        public event Action<PersonRow, string> Clicked;     // row, action label ("" = the row itself)
        public event Action<PersonRow, Point> Menu;
        int scroll, total, hover = -1;

        public PeopleList()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Look.Side;
        }

        public void Rebuild()
        {
            int y = 10;
            foreach (PersonRow r in Rows)
            {
                int h = r.Header ? 30 : RowHeight;
                r.Bounds = new Rectangle(8, y, Width - 16, h);
                r.ActionRects = new Rectangle[r.Actions.Length];
                int ax = r.Bounds.Right - 6;
                for (int i = r.Actions.Length - 1; i >= 0; i--)
                {
                    int aw = Math.Max(64, TextRenderer.MeasureText(r.Actions[i], Font).Width + 22);
                    ax -= aw;
                    r.ActionRects[i] = new Rectangle(ax, r.Bounds.Y + (h - 30) / 2, aw, 30);
                    ax -= 6;
                }
                y += h + 2;
            }
            total = y + 10;
            int max = Math.Max(0, total - Height);
            if (scroll > max) scroll = max;
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Rebuild(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, total - Height);
            scroll -= e.Delta / 120 * 40;
            if (scroll < 0) scroll = 0; if (scroll > max) scroll = max;
            Invalidate();
        }

        int Hit(Point p, out string action)
        {
            action = "";
            Point q = new Point(p.X, p.Y + scroll);
            for (int i = 0; i < Rows.Count; i++)
            {
                if (Rows[i].Header || !Rows[i].Bounds.Contains(q)) continue;
                for (int a = 0; a < Rows[i].ActionRects.Length; a++)
                    if (Rows[i].ActionRects[a].Contains(q)) action = Rows[i].Actions[a];
                return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            string a;
            int h = Hit(e.Location, out a);
            if (h != hover) { hover = h; Invalidate(); }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            string a;
            int h = Hit(e.Location, out a);
            if (h < 0) return;
            if (e.Button == MouseButtons.Left && Clicked != null) Clicked(Rows[h], a);
            else if (e.Button == MouseButtons.Right && Menu != null) Menu(Rows[h], e.Location);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.TranslateTransform(0, -scroll);
            TextFormatFlags tf = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            using (Font fh = new Font("Segoe UI", 7.75f, FontStyle.Bold))
            using (Font fn = new Font("Segoe UI Semibold", 10f))
            using (Font fs = new Font("Segoe UI", 8.25f))
            using (Font fa = new Font("Segoe UI Semibold", 9f))
            {
                for (int i = 0; i < Rows.Count; i++)
                {
                    PersonRow r = Rows[i];
                    Rectangle b = r.Bounds;
                    if (r.Header)
                    {
                        Look.DT(g, r.Name.ToUpperInvariant(), fh, new Rectangle(b.X + 6, b.Y + 10, b.Width, 18), Look.Muted, tf);
                        continue;
                    }
                    if (i == hover) Look.FillRound(g, b, 6, Look.Hover);
                    int av = Math.Min(32, b.Height - 10);
                    Rectangle ar = new Rectangle(b.X + 6, b.Y + (b.Height - av) / 2, av, av);
                    Look.Avatar(g, r.Name, ar, false);
                    Look.Dot(g, ar, Look.PresenceColor(r.Online ? (r.Presence.Length > 0 ? r.Presence : "online") : "offline"), i == hover ? Look.Hover : BackColor);
                    int textW = (r.ActionRects.Length > 0 ? r.ActionRects[0].X : b.Right) - (ar.Right + 10) - 4;
                    Color nc = r.Dim ? Look.Muted : Color.White;
                    Look.DT(g, r.Name, fn, new Rectangle(ar.Right + 10, b.Y + b.Height / 2 - 18, textW, 20), nc, tf);
                    if (r.Owner)
                    {
                        int nw = Math.Min(textW, TextRenderer.MeasureText(r.Name, fn).Width);
                        using (Font fc = new Font("Segoe UI Symbol", 9f))
                            Look.DT(g, "\u265B", fc, new Point(ar.Right + 8 + nw, b.Y + b.Height / 2 - 17), Color.FromArgb(240, 178, 50));
                    }
                    Look.DT(g, r.Sub, fs, new Rectangle(ar.Right + 10, b.Y + b.Height / 2 + 1, textW, 18), r.Playing ? Look.Accent : Look.Muted, tf);
                    for (int a = 0; a < r.ActionRects.Length; a++)
                    {
                        string lab = r.Actions[a];
                        Color bg = lab == "Accept" || lab == "Message" ? Look.GreenBtn : lab == "Decline" || lab == "Remove" || lab == "Cancel" ? Color.FromArgb(60, 30, 34) : Color.FromArgb(28, 38, 54);
                        Look.FillRound(g, r.ActionRects[a], 6, bg);
                        Look.DT(g, lab, fa, r.ActionRects[a], Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    }
                }
            }
            g.ResetTransform();
        }
    }

    // ---------------------------------------------------------------- the group rail on the far left

    class GroupRail : Control
    {
        public class Entry { public string Key = "", Label = "", Tip = ""; public bool Home, Add, Games; public int Mentions; public bool Unread; public Rectangle R; }
        public readonly List<Entry> Entries = new List<Entry>();
        public string Selected = "";
        public event Action<Entry> Picked;
        public event Action<Entry, Point> Menu;
        readonly ToolTip tip = new ToolTip();
        int hover = -1;
        string tipFor = "";

        public GroupRail()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Look.Rail;
        }

        public void Rebuild()
        {
            int y = 12;
            foreach (Entry e in Entries)
            {
                e.R = new Rectangle((Width - 48) / 2, y, 48, 48);
                y += 56 + (e.Home ? 12 : 0);
            }
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Rebuild(); }

        int Hit(Point p) { for (int i = 0; i < Entries.Count; i++) if (Entries[i].R.Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            string t = h >= 0 ? Entries[h].Tip : "";
            if (t != tipFor) { tipFor = t; if (t.Length > 0) tip.Show(t, this, Width - 4, e.Y, 2000); else tip.Hide(this); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; tipFor = ""; tip.Hide(this); Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h < 0) return;
            if (e.Button == MouseButtons.Left && Picked != null) Picked(Entries[h]);
            else if (e.Button == MouseButtons.Right && Menu != null) Menu(Entries[h], e.Location);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Look.Rail);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry en = Entries[i];
                bool sel = en.Key == Selected && !en.Add;
                Rectangle r = en.R;
                Color bg = en.Home ? (sel ? Look.Accent : Color.FromArgb(21, 29, 41))
                    : en.Games ? (sel || i == hover ? Look.GreenBtn : Color.FromArgb(21, 29, 41))
                    : en.Add ? Color.FromArgb(21, 29, 41)
                    : sel ? Look.GreenBtn : i == hover ? Look.GreenBtn : Look.AvatarColor(en.Label);
                Look.FillRound(g, r, sel || i == hover ? 16 : 24, bg);
                Color fg = en.Home && sel ? Look.Rail : en.Games ? (sel || i == hover ? Color.White : Look.Green) : en.Add ? Look.Green : Color.White;
                if (en.Games)
                    using (Font f = new Font("Segoe MDL2 Assets", 17f))
                        Look.DT(g, "\uE7FC", f, r, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                else
                using (Font f = new Font("Segoe UI", en.Add ? 18f : 11f, en.Add ? FontStyle.Regular : FontStyle.Bold))
                    Look.DT(g, en.Label, f, r, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (sel)
                    using (SolidBrush pb = new SolidBrush(Color.White)) g.FillRectangle(pb, 0, r.Y + 8, 4, r.Height - 16);
                else if (en.Unread)
                    using (SolidBrush pb = new SolidBrush(Color.White)) g.FillRectangle(pb, 0, r.Y + 20, 4, 8);
                if (en.Mentions > 0) Look.Badge(g, new Rectangle(r.X, r.Bottom - 18, r.Width + 4, 18), en.Mentions, Look.BadgeRed);
                if (en.Home)
                    using (Pen sp = new Pen(Look.Line, 2f)) g.DrawLine(sp, r.X + 8, r.Bottom + 10, r.Right - 8, r.Bottom + 10);
            }
        }
    }

    // Discord-style on/off switch. With SwitchRight the switch sits at the right edge of a full-width row.
    class ToggleCheck : CheckBox
    {
        public bool SwitchRight;
        public ToggleCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }
        public override Size GetPreferredSize(Size proposed)
        {
            Size s = TextRenderer.MeasureText(Text, Font);
            return new Size(s.Width + 56, Math.Max(26, s.Height + 6));
        }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle sw = SwitchRight ? new Rectangle(Width - 42, 2, 40, 22) : new Rectangle(0, 2, 40, 22);
            Look.FillRound(g, sw, 11, Checked ? (Enabled ? Look.GreenBtn : Color.FromArgb(40, 80, 60)) : Color.FromArgb(72, 82, 98));
            Rectangle knob = Checked ? new Rectangle(sw.Right - 20, sw.Y + 2, 18, 18) : new Rectangle(sw.X + 2, sw.Y + 2, 18, 18);
            using (SolidBrush b = new SolidBrush(Enabled ? Color.White : Color.FromArgb(160, 160, 160))) g.FillEllipse(b, knob);
            Rectangle tr = SwitchRight ? new Rectangle(0, 0, Width - 56, Height) : new Rectangle(52, 0, Width - 52, Height);
            TextRenderer.DrawText(g, Text, Font, tr, Enabled ? ForeColor : Look.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.Top);
            if (Focused && ShowFocusCues) using (Pen p = new Pen(Look.Accent)) g.DrawRectangle(p, sw.X - 1, sw.Y - 1, sw.Width + 1, sw.Height + 1);
        }
    }

    // The app's icon, drawn in code (no image files): "GS" in a dark circle, with the voice state
    // around it like Discord's tray icon, and a red dot for unread mentions.
    static class TrayArt
    {
        public enum State { Idle, InVoice, Talking, Muted, Deafened }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public static Icon Make(State st, bool badge, int size)
        {
            int S = 64;
            using (Bitmap b = new Bitmap(S, S))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    Color ring = st == State.InVoice || st == State.Talking ? Color.FromArgb(35, 165, 90)
                        : st == State.Muted || st == State.Deafened ? Color.FromArgb(218, 55, 60) : Color.Empty;
                    Color fill = st == State.Talking ? Color.FromArgb(35, 165, 90) : Color.FromArgb(21, 29, 41);
                    using (SolidBrush br = new SolidBrush(fill)) g.FillEllipse(br, 3, 3, S - 6, S - 6);
                    if (!ring.IsEmpty) using (Pen p = new Pen(ring, 6f)) g.DrawEllipse(p, 4, 4, S - 8, S - 8);
                    else using (Pen p = new Pen(Color.FromArgb(116, 200, 255), 3f)) g.DrawEllipse(p, 4, 4, S - 8, S - 8);
                    using (Font f = new Font("Segoe UI", 22f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    using (SolidBrush tb = new SolidBrush(st == State.Talking ? Color.White : Color.FromArgb(116, 200, 255)))
                        g.DrawString("GS", f, tb, new RectangleF(0, 1, S, S), sf);
                    if (st == State.Muted || st == State.Deafened)
                        using (Pen p = new Pen(Color.FromArgb(218, 55, 60), 7f)) g.DrawLine(p, 14, 50, 50, 14);
                    if (badge)
                    {
                        using (SolidBrush rb = new SolidBrush(Color.FromArgb(218, 55, 60))) g.FillEllipse(rb, S - 26, S - 26, 24, 24);
                        using (Pen p = new Pen(Color.FromArgb(5, 9, 15), 3f)) g.DrawEllipse(p, S - 26, S - 26, 24, 24);
                    }
                }
                using (Bitmap small = new Bitmap(size, size))
                {
                    using (Graphics sg = Graphics.FromImage(small))
                    {
                        sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        sg.Clear(Color.Transparent);
                        sg.DrawImage(b, new Rectangle(0, 0, size, size));
                    }
                    IntPtr h = small.GetHicon();
                    Icon tmp = Icon.FromHandle(h);
                    Icon copy = (Icon)tmp.Clone();
                    tmp.Dispose();
                    DestroyIcon(h);
                    return copy;
                }
            }
        }

        public static void Free(Icon i) { try { if (i != null) i.Dispose(); } catch (Exception) { } }
    }
}
