// GameSpy Revival - window shell, theme and custom-drawn controls.
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
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(8, 13, 20);
        public static readonly Color Panel = Color.FromArgb(17, 24, 35);
        public static readonly Color Panel2 = Color.FromArgb(21, 29, 41);
        public static readonly Color Line = Color.FromArgb(38, 50, 68);
        public static readonly Color Text = Color.FromArgb(232, 238, 246);
        public static readonly Color Muted = Color.FromArgb(139, 152, 170);
        public static readonly Color Accent = Color.FromArgb(116, 200, 255);
        public static readonly Color Green = Color.FromArgb(105, 216, 163);
        public static readonly Color GreenBtn = Color.FromArgb(34, 150, 96);
        public static readonly Color Sel = Color.FromArgb(24, 44, 68);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        public static void Cue(TextBox t, string text)
        {
            t.HandleCreated += delegate { SendMessage(t.Handle, 0x1501, (IntPtr)1, text); };
            if (t.IsHandleCreated) SendMessage(t.Handle, 0x1501, (IntPtr)1, text);
        }

        public static void Primary(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = GreenBtn;
            b.ForeColor = Color.White;
            b.Font = new Font("Segoe UI Semibold", 10f);
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(44, 172, 110);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(28, 128, 82);
            b.Cursor = Cursors.Hand;
        }

        public static void Style(Control c)
        {
            if (c is Hero) { StyleKids(c); return; }
            if (c is TabControl) { c.BackColor = Panel; }
            else if (c is TabPage) { c.BackColor = Panel; c.ForeColor = Text; }
            else if (c is Button)
            {
                Button b = (Button)c;
                if ("primary".Equals(b.Tag)) Primary(b);
                else if ("danger".Equals(b.Tag))
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = Color.FromArgb(40, 24, 28);
                    b.ForeColor = Color.FromArgb(240, 120, 110);
                    b.FlatAppearance.BorderColor = Color.FromArgb(110, 50, 50);
                    b.UseVisualStyleBackColor = false;
                }
                else
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = Color.FromArgb(28, 38, 54);
                    b.ForeColor = Text;
                    b.FlatAppearance.BorderColor = Line;
                    b.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 52, 74);
                    b.UseVisualStyleBackColor = false;
                }
            }
            else if (c is TextBox || c is NumericUpDown || c is ComboBox)
            {
                c.BackColor = Color.FromArgb(12, 18, 28);
                c.ForeColor = Text;
                TextBox t = c as TextBox;
                if (t != null) t.BorderStyle = BorderStyle.FixedSingle;
                ComboBox cb = c as ComboBox;
                if (cb != null) cb.FlatStyle = FlatStyle.Flat;
            }
            else if (c is ListView || c is ListBox)
            {
                if (!(c is ServerList)) { c.BackColor = Color.FromArgb(12, 18, 28); c.ForeColor = Text; }
            }
            else if (c is LinkLabel)
            {
                LinkLabel l = (LinkLabel)c;
                l.LinkColor = Accent; l.ActiveLinkColor = Green; l.VisitedLinkColor = Accent;
                l.BackColor = Color.Transparent;
            }
            else if (c is Label)
            {
                int fc = c.ForeColor.ToArgb();
                if ("muted".Equals(c.Tag) || fc == SystemColors.GrayText.ToArgb()) c.ForeColor = Muted;
                else if (fc == SystemColors.ControlText.ToArgb()) c.ForeColor = Text;
                c.BackColor = Color.Transparent;
            }
            else if (c is CheckBox || c is RadioButton || c is GroupBox)
            {
                c.ForeColor = Text;
                c.BackColor = Color.Transparent;
                CheckBox cx = c as CheckBox;
                if (cx != null && cx.Appearance == Appearance.Button) { cx.BackColor = Color.FromArgb(28, 38, 54); }
            }
            else if (c is Panel || c is UserControl)
            {
                if ("card".Equals(c.Tag)) c.BackColor = Panel2;
                else if (c.BackColor == SystemColors.Control) c.BackColor = Bg;
                if (c.ForeColor.ToArgb() == SystemColors.ControlText.ToArgb()) c.ForeColor = Text;
            }
            StyleKids(c);
        }

        static void StyleKids(Control c)
        {
            foreach (Control k in c.Controls) Style(k);
        }
    }

    class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Color.FromArgb(38, 52, 74); } }
        public override Color MenuItemBorder { get { return Theme.Line; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Panel2; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Panel2; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Panel2; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Panel2; } }
        public override Color SeparatorDark { get { return Theme.Line; } }
        public override Color SeparatorLight { get { return Theme.Line; } }
        public override Color MenuBorder { get { return Theme.Line; } }
    }

    static class DarkMenu
    {
        public static void Apply(ContextMenuStrip m)
        {
            m.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
            m.BackColor = Theme.Panel2;
            m.ForeColor = Theme.Text;
            m.ShowImageMargin = false;
            m.Opening += delegate { foreach (ToolStripItem i in m.Items) i.ForeColor = (i.Tag as string) == "danger" ? Color.FromArgb(240, 138, 122) : Theme.Text; };
        }
    }

    // The banner at the top of a game's page.
    class Hero : Panel
    {
        readonly string title, sub;
        readonly Color a, b;

        public Hero(string t, string s, Color ca, Color cb)
        {
            title = t; sub = s;
            a = ca.IsEmpty ? Color.FromArgb(120, 62, 28) : ca;
            b = cb.IsEmpty ? Color.FromArgb(30, 22, 40) : cb;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;
            using (LinearGradientBrush br = new LinearGradientBrush(r, a, b, 25f)) g.FillRectangle(br, r);
            int w = r.Width, h = r.Height;
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(20, 255, 255, 255)))
            {
                g.FillPolygon(sh, new Point[] { new Point(w / 2 + 40, 0), new Point(w / 2 + 170, 0), new Point(w / 2 - 20, h), new Point(w / 2 - 150, h) });
                g.FillPolygon(sh, new Point[] { new Point(w - 190, 0), new Point(w - 130, 0), new Point(w - 260, h), new Point(w - 320, h) });
            }
            using (SolidBrush ring = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
                g.FillEllipse(ring, w - 250, -70, 230, 230);
            Rectangle fr = new Rectangle(0, h - 100, w, 100);
            using (LinearGradientBrush fb = new LinearGradientBrush(fr, Color.FromArgb(0, 8, 13, 20), Color.FromArgb(225, 8, 13, 20), 90f))
                g.FillRectangle(fb, fr);
            using (Font f1 = new Font("Segoe UI Semibold", 23f))
                TextRenderer.DrawText(g, title, f1, new Point(16, h - 74), Color.White);
            using (Font f2 = new Font("Segoe UI", 10f))
                TextRenderer.DrawText(g, sub, f2, new Point(18, h - 34), Color.FromArgb(200, 214, 230));
            using (Pen p = new Pen(Theme.Line)) g.DrawRectangle(p, 0, 0, w - 1, h - 1);
        }
    }

    // Server list drawn as cards: name and map on the left, players and ping on the right.
    class ServerList : ListBox
    {
        public List<string> Favs = new List<string>();

        public ServerList()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 54;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Bg;
            IntegralHeight = false;
            DoubleBuffered = true;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            Graphics g = e.Graphics;
            using (SolidBrush clear = new SolidBrush(Theme.Bg)) g.FillRectangle(clear, e.Bounds);
            ServerInfo s = Items[e.Index] as ServerInfo;
            if (s == null) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Rectangle r = new Rectangle(e.Bounds.X, e.Bounds.Y + 3, e.Bounds.Width - 2, e.Bounds.Height - 7);
            using (SolidBrush bg = new SolidBrush(sel ? Theme.Sel : Theme.Panel2)) g.FillRectangle(bg, r);
            using (Pen p = new Pen(sel ? Theme.Accent : Theme.Line)) g.DrawRectangle(p, r);

            string name = (Favs.Contains(s.Key) ? "\u2605 " : "") + s.Name + (s.Locked ? "   (password)" : "");
            string map = s.Map + (s.Mod.Length > 0 ? " \u00b7 " + s.Mod : "") + " \u00b7 " + s.Key;
            TextFormatFlags f = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left;
            using (Font fb = new Font("Segoe UI Semibold", 10.5f))
                TextRenderer.DrawText(g, name, fb, new Rectangle(r.X + 14, r.Y + 6, r.Width - 120, 22), s.Players <= 0 ? Theme.Muted : Theme.Text, f);
            using (Font fs = new Font("Segoe UI", 9f))
                TextRenderer.DrawText(g, map, fs, new Rectangle(r.X + 14, r.Y + 27, r.Width - 120, 18), Theme.Muted, f);
            TextFormatFlags rf = TextFormatFlags.NoPrefix | TextFormatFlags.Right;
            using (Font fp = new Font("Segoe UI Semibold", 10.5f))
                TextRenderer.DrawText(g, s.Players + "/" + s.Max, fp, new Rectangle(r.Right - 104, r.Y + 6, 92, 22), s.Players <= 0 ? Theme.Muted : Theme.Text, rf);
            using (Font fs2 = new Font("Segoe UI", 9f))
                TextRenderer.DrawText(g, s.Ping + " ms", fs2, new Rectangle(r.Right - 104, r.Y + 27, 92, 18), Theme.Muted, rf);
        }
    }

    // A plain page of text with optional buttons (Home, Library, Downloads and so on).
    class InfoPage : Panel
    {
        public Action OnShow;
        readonly FlowLayoutPanel flow = new FlowLayoutPanel();
        readonly Label body = new Label();

        public InfoPage(string title, string text)
        {
            Dock = DockStyle.Fill;
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            flow.AutoScroll = true;
            flow.Padding = new Padding(26, 22, 20, 10);
            Controls.Add(flow);
            Label head = new Label();
            head.Text = title;
            head.Font = new Font("Segoe UI Semibold", 20f);
            head.ForeColor = Color.White;
            head.AutoSize = true;
            head.Margin = new Padding(0, 0, 0, 10);
            flow.Controls.Add(head);
            body.Text = text;
            body.Font = new Font("Segoe UI", 10.5f);
            body.ForeColor = Theme.Muted;
            body.AutoSize = true;
            body.MaximumSize = new Size(660, 0);
            body.Margin = new Padding(0, 0, 0, 18);
            flow.Controls.Add(body);
        }

        public void SetBody(string t) { body.Text = t; }

        public void AddAction(string text, Action a)
        {
            Button b = new Button();
            b.Text = text;
            b.Size = new Size(300, 34);
            b.Margin = new Padding(0, 0, 0, 8);
            b.Click += delegate { a(); };
            flow.Controls.Add(b);
        }
    }

    class NavButton : Button
    {
        public string Target;
        public bool Soon;

        public NavButton(string text, string target)
        {
            Text = text;
            Target = target;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TextAlign = ContentAlignment.MiddleLeft;
            Padding = new Padding(14, 0, 0, 0);
            Font = new Font("Segoe UI", 9.5f);
            Cursor = Cursors.Hand;
            Mark(false);
        }

        public void Mark(bool on)
        {
            BackColor = on ? Theme.Sel : Theme.Panel;
            ForeColor = on ? Theme.Accent : (Soon ? Theme.Muted : Theme.Text);
            FlatAppearance.MouseOverBackColor = on ? Theme.Sel : Theme.Panel2;
            FlatAppearance.MouseDownBackColor = Theme.Sel;
            UseVisualStyleBackColor = false;
        }
    }
}
