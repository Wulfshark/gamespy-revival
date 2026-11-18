// GameSpy Revival - the game library home, laid out like Steam's library shelf.
// Tall tiles with art drawn in code (no publisher artwork), the game's name and its status.
// Free software under the GPL-3.0 (see LICENSE.txt).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Revival
{
    class LibraryPage : Panel
    {
        public class Tile
        {
            public string Key, Title, Kind, Year;
            public Color A, B;
            public Func<string> Status;   // "Installed", "Not set up yet", ...
            public Rectangle R;
        }

        public readonly List<Tile> Tiles = new List<Tile>();
        public event Action<string> Picked;
        int hover = -1;
        const int TW = 168, TH = 236, Gap = 18, Top = 92;
        static readonly Font FTitle = new Font("Segoe UI Semibold", 11f);
        static readonly Font FSmall = new Font("Segoe UI", 8.5f);
        static readonly Font FHead = new Font("Segoe UI Semibold", 20f);
        static readonly Font FShelf = new Font("Segoe UI", 9f, FontStyle.Bold);

        public LibraryPage()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Look.Main;
            AutoScroll = true;
        }

        void LayoutTiles()
        {
            int cols = Math.Max(1, (ClientSize.Width - 40 + Gap) / (TW + Gap));
            for (int i = 0; i < Tiles.Count; i++)
                Tiles[i].R = new Rectangle(28 + (i % cols) * (TW + Gap), Top + (i / cols) * (TH + 64), TW, TH);
            int rows = (Tiles.Count + cols - 1) / cols;
            AutoScrollMinSize = new Size(0, Top + rows * (TH + 64) + 20);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point p = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
            int h = -1;
            for (int i = 0; i < Tiles.Count; i++) if (Tiles[i].R.Contains(p)) h = i;
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && hover >= 0 && Picked != null) Picked(Tiles[hover].Key);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            LayoutTiles();
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            TextRenderer.DrawText(g, "Library", FHead, new Point(24, 18), Color.White, TextFormatFlags.PreserveGraphicsTranslateTransform);
            TextRenderer.DrawText(g, "ALL GAMES (" + Tiles.Count + ")", FShelf, new Point(28, 62), Look.Muted, TextFormatFlags.PreserveGraphicsTranslateTransform);
            for (int i = 0; i < Tiles.Count; i++)
            {
                Tile t = Tiles[i];
                Rectangle r = t.R;
                if (i == hover) r.Inflate(4, 4);   // Steam's tiles grow a little on hover
                using (GraphicsPath path = Round(r, 6))
                {
                    using (LinearGradientBrush br = new LinearGradientBrush(r, t.A, t.B, 70f)) g.FillPath(br, path);
                    g.SetClip(path);
                    using (SolidBrush sh = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
                    {
                        g.FillEllipse(sh, r.Right - 120, r.Y - 50, 170, 170);
                        g.FillPolygon(sh, new Point[] { new Point(r.X + 40, r.Bottom), new Point(r.X + 90, r.Bottom), new Point(r.Right, r.Y + 70), new Point(r.Right, r.Y + 20) });
                    }
                    Rectangle fade = new Rectangle(r.X, r.Bottom - 110, r.Width, 110);
                    using (LinearGradientBrush fb = new LinearGradientBrush(fade, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(210, 5, 8, 12), 90f)) g.FillRectangle(fb, fade);
                    g.ResetClip();
                    g.TranslateTransform(0, 0);
                    TextRenderer.DrawText(g, t.Year + "  ·  " + t.Kind, FSmall, new Rectangle(r.X + 10, r.Bottom - 74, r.Width - 16, 16), Color.FromArgb(205, 215, 228),
                        TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsTranslateTransform);
                    TextRenderer.DrawText(g, t.Title, FTitle, new Rectangle(r.X + 10, r.Bottom - 56, r.Width - 16, 46), Color.White,
                        TextFormatFlags.WordBreak | TextFormatFlags.PreserveGraphicsTranslateTransform);
                    using (Pen p = new Pen(i == hover ? Look.Accent : Color.FromArgb(40, 255, 255, 255), i == hover ? 2f : 1f)) g.DrawPath(p, path);
                }
                string st = t.Status != null ? t.Status() : "";
                Color sc = st.StartsWith("Installed") || st.StartsWith("Ready") ? Look.Green : Look.Muted;
                TextRenderer.DrawText(g, st, FSmall, new Rectangle(t.R.X, t.R.Bottom + 10, t.R.Width, 18), sc,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsTranslateTransform);
            }
        }

        static GraphicsPath Round(Rectangle r, int rad)
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
    }
}
