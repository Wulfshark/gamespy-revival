// GameSpy Revival - "Add a group" wizard, like Discord's Create Your Server.
// Create my own: name the group, make your owner account, start hosting, invite friends.
// Join a friend's: hands over to the normal sign-in window.
// Free software under the GPL-3.0 (see LICENSE.txt).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Revival
{
    class GroupWizard : Form
    {
        readonly LobbyClient lobby;
        readonly Panel body = new Panel();
        public bool JoinChosen;
        bool waiting;
        Label msg;
        Button btnCreate;

        string groupName = "", channels = "", userName = "", password = "";
        bool fresh, autostart = true;

        const string Gaming = "general, looking-for-game, metal-fatigue, quake, unreal, clips-and-screens";
        const string Chill = "general, clips-and-screens";

        public GroupWizard(LobbyClient lc, bool firstRun)
        {
            lobby = lc;
            Text = "GameSpy Revival";
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 640);
            ForeColor = Look.Text;
            body.Dock = DockStyle.Fill;
            body.BackColor = Look.Card;
            Controls.Add(body);
            Look.Modal(this, Look.Card, body);
            groupName = Config.Get("host.name", "");
            userName = Config.Get("lobby.user", "");
            lobby.Changed += OnChanged;
            lobby.Failed += OnFailed;
            FormClosed += delegate { lobby.Changed -= OnChanged; lobby.Failed -= OnFailed; };
            StartPage(firstRun);
        }

        // ---- building blocks
        int y;

        void Clear() { body.Controls.Clear(); y = 26; AcceptButton = null; }

        void Title(string t, string sub)
        {
            Label a = new Label();
            a.Text = t;
            a.Font = new Font("Segoe UI Semibold", 17f);
            a.ForeColor = Color.White;
            a.TextAlign = ContentAlignment.MiddleCenter;
            a.SetBounds(20, y, 420, 36);
            body.Controls.Add(a);
            y += 40;
            Label b = new Label();
            b.Text = sub;
            b.ForeColor = Look.Soft;
            b.TextAlign = ContentAlignment.TopCenter;
            b.SetBounds(30, y, 400, 54);
            body.Controls.Add(b);
            y += 62;
        }

        void Choice(string glyph, string title, string sub, Action click)
        {
            Panel c = new Panel();
            c.SetBounds(24, y, 412, 56);
            c.Cursor = Cursors.Hand;
            bool hot = false;
            c.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Look.Card);
                Look.FillRound(g, new Rectangle(0, 0, c.Width - 1, c.Height - 1), 8, hot ? Color.FromArgb(32, 44, 62) : Color.FromArgb(22, 31, 44));
                Rectangle ic = new Rectangle(12, 10, 36, 36);
                Look.FillRound(g, ic, 20, Look.GreenBtn);
                using (Font f = new Font("Segoe MDL2 Assets", 15f))
                    TextRenderer.DrawText(g, glyph, f, ic, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                using (Font f = new Font("Segoe UI Semibold", 11f))
                    TextRenderer.DrawText(g, title, f, new Point(60, 8), Color.White, TextFormatFlags.NoPrefix);
                using (Font f = new Font("Segoe UI", 8.75f))
                    TextRenderer.DrawText(g, sub, f, new Rectangle(60, 30, c.Width - 94, 20), Look.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                using (Pen p = new Pen(Look.Soft, 1.8f))
                {
                    int cx = c.Width - 22, cy = c.Height / 2;
                    g.DrawLine(p, cx - 3, cy - 5, cx + 2, cy);
                    g.DrawLine(p, cx + 2, cy, cx - 3, cy + 5);
                }
            };
            c.MouseEnter += delegate { hot = true; c.Invalidate(); };
            c.MouseLeave += delegate { hot = false; c.Invalidate(); };
            c.MouseUp += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) click(); };
            body.Controls.Add(c);
            y += 64;
        }

        TextBox Field(string label, string value, bool secret)
        {
            Label l = new Label();
            l.Text = label.ToUpperInvariant();
            l.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            l.ForeColor = Look.Soft;
            l.SetBounds(30, y, 400, 18);
            body.Controls.Add(l);
            y += 20;
            TextBox t = new TextBox();
            t.Text = value;
            t.Font = new Font("Segoe UI", 11f);
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = Look.Rail;
            t.ForeColor = Look.Text;
            t.UseSystemPasswordChar = secret;
            t.SetBounds(30, y, 400, 28);
            body.Controls.Add(t);
            y += 40;
            return t;
        }

        CheckBox Check(string text, bool on)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = on;
            c.ForeColor = Look.Text;
            c.SetBounds(30, y, 400, 40);
            body.Controls.Add(c);
            y += 42;
            return c;
        }

        Label Note(string text, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = color;
            l.SetBounds(30, y, 400, 40);
            body.Controls.Add(l);
            y += 44;
            return l;
        }

        Button Footer(string back, Action onBack, string next, Action onNext)
        {
            Panel f = new Panel();
            f.SetBounds(0, ClientSize.Height - 64, ClientSize.Width, 64);
            f.BackColor = Color.FromArgb(17, 24, 35);
            body.Controls.Add(f);
            f.BringToFront();
            if (back != null)
            {
                Label b = new Label();
                b.Text = back;
                b.ForeColor = Look.Soft;
                b.Cursor = Cursors.Hand;
                b.TextAlign = ContentAlignment.MiddleLeft;
                b.SetBounds(24, 16, 120, 32);
                b.Click += delegate { onBack(); };
                f.Controls.Add(b);
            }
            if (next == null) return null;
            Button n = new Button();
            n.Text = next;
            n.SetBounds(ClientSize.Width - 24 - 150, 14, 150, 36);
            Theme.Primary(n);
            n.Click += delegate { onNext(); };
            f.Controls.Add(n);
            AcceptButton = n;
            return n;
        }

        // ---- pages (same flow as Discord's Create Your Server)
        const string FriendsSet = "general, clips-and-screens";
        const string MfSet = "general, looking-for-game, metal-fatigue, clips-and-screens";
        const string ArenaSet = "general, looking-for-game, quake, unreal, clips-and-screens";
        bool community;

        Label SmallHead(string t)
        {
            Label l = new Label();
            l.Text = t;
            l.Font = new Font("Segoe UI Semibold", 9f);
            l.ForeColor = Look.Soft;
            l.SetBounds(26, y, 400, 20);
            body.Controls.Add(l);
            y += 24;
            return l;
        }

        void StartPage(bool firstRun)
        {
            Clear();
            Title(firstRun ? "Welcome to GameSpy Revival" : "Create Your Group",
                "Your group is where you and your friends hang out and set up games. It runs on your own PC.");
            Choice("\uE710", "Create My Own", "Pick your own channels", delegate { channels = Gaming; TypePage(); });
            SmallHead("Start from a template");
            Choice("\uE7FC", "Gaming", "Every game: Quake, Unreal, Metal Fatigue", delegate { channels = Gaming; TypePage(); });
            Choice("\uE7FC", "Metal Fatigue night", "Lobby and channels for Metal Fatigue matches", delegate { channels = MfSet; TypePage(); });
            Choice("\uE7FC", "Arena shooters", "Quake III, OpenArena and Unreal Tournament", delegate { channels = ArenaSet; TypePage(); });
            Choice("\uE716", "Friends", "Just chat and voice", delegate { channels = FriendsSet; TypePage(); });
            // footer: Have an address already?
            Panel f = new Panel();
            f.SetBounds(0, ClientSize.Height - 92, ClientSize.Width, 92);
            f.BackColor = Color.FromArgb(17, 24, 35);
            Label h = new Label();
            h.Text = "Have an address already?";
            h.Font = new Font("Segoe UI Semibold", 12f);
            h.ForeColor = Color.White;
            h.TextAlign = ContentAlignment.MiddleCenter;
            h.SetBounds(0, 8, ClientSize.Width, 28);
            f.Controls.Add(h);
            Button j = new Button();
            j.Text = "Join a Group";
            j.SetBounds(24, 42, ClientSize.Width - 48, 36);
            Theme.Style(j);
            j.Click += delegate { JoinChosen = true; DialogResult = DialogResult.Cancel; Close(); };
            f.Controls.Add(j);
            body.Controls.Add(f);
            f.BringToFront();
        }

        void TypePage()
        {
            Clear();
            if (LobbyHost.Running && Config.Get("lobby.host", "0") == "1")
            {
                Title("You're already hosting", "This PC already runs " + Config.Get("host.name", "a group")
                    + ". One PC hosts one group. Change its name and channels in Settings > Host this group.");
                Footer("Back", delegate { StartPage(false); }, "OK", delegate { Close(); });
                return;
            }
            Title("Tell Us More About Your Group", "Is your new group for just a few friends or a larger community?");
            Choice("\uE716", "For me and my friends", "A small crew", delegate { community = false; CustomizePage(); });
            Choice("\uE774", "For a club or community", "Adds #announcements and #rules", delegate { community = true; CustomizePage(); });
            Label skip = new Label();
            skip.Text = "Not sure? You can skip this question for now.";
            skip.ForeColor = Look.Accent;
            skip.Cursor = Cursors.Hand;
            skip.TextAlign = ContentAlignment.MiddleCenter;
            skip.SetBounds(30, y, 400, 24);
            skip.Click += delegate { community = false; CustomizePage(); };
            body.Controls.Add(skip);
            Footer("Back", delegate { StartPage(false); }, null, null);
        }

        static bool HasOldData()
        {
            try
            {
                string a = Path.Combine(LobbyHost.Folder, "accounts.json");
                return File.Exists(a) && new FileInfo(a).Length > 30;
            }
            catch (Exception) { return false; }
        }

        void CustomizePage()
        {
            Clear();
            Title("Customize Your Group", "Give your new group a name. You can always change it later.");
            TextBox gname = Field("Group name", groupName.Length > 0 && groupName != "Game Night" ? groupName
                : (userName.Length > 0 ? userName + "'s group" : "Game Night"), false);
            TextBox name = Field("Your name in this group", userName, false);
            CheckBox clean = null;
            if (HasOldData())
                clean = Check("Start fresh: set aside the old accounts and messages on this PC (kept as a backup), so you're the owner", false);
            CheckBox auto = Check("Keep the group running when Windows starts", autostart);
            msg = Note("", Look.Red);
            btnCreate = Footer("Back", delegate { TypePage(); }, "Create", delegate
            {
                if (waiting) return;
                string g = gname.Text.Trim().Replace("\"", "'");
                string n = name.Text.Trim();
                if (g.Length == 0) { msg.Text = "Give your group a name."; return; }
                if (!Regex.IsMatch(n, "^[A-Za-z0-9_\\-]{3,20}$")) { msg.Text = "Your name: 3 to 20 letters, numbers, - or _."; return; }
                groupName = g.Length > 40 ? g.Substring(0, 40) : g;
                if (community && !channels.StartsWith("announcements")) channels = "announcements, rules, " + channels;
                userName = n; password = LobbyClient.DeviceSecret;   // this PC is your key: no password to remember
                fresh = clean != null && clean.Checked;
                autostart = auto.Checked;
                Finish();
            });
            ActiveControl = name;
        }

        void Finish()
        {
            msg.ForeColor = Look.Soft;
            msg.Text = "Starting your group server...";
            Application.DoEvents();
            Config.Set("host.name", groupName);
            Config.Set("host.channels", channels);
            LobbyHost.Stop();
            if (fresh)
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                foreach (string f in new string[] { "accounts.json", "chat.json" })
                {
                    string p = Path.Combine(LobbyHost.Folder, f);
                    try { if (File.Exists(p)) File.Move(p, p + ".backup-" + stamp); } catch (Exception) { }
                }
            }
            LobbyHost.StartWithWindows = autostart;
            string err = LobbyHost.Start(Shell.HostPort(), 27900, groupName, channels);
            if (err != null) { msg.ForeColor = Look.Red; msg.Text = err; return; }
            Config.Set("lobby.host", "1");
            msg.Text = "Creating your account...";
            waiting = true;
            btnCreate.Enabled = false;
            lobby.SignIn("127.0.0.1:" + Shell.HostPort(), userName, password, true, true);
        }

        void OnChanged()
        {
            if (waiting && lobby.State == LobbyState.SignedIn) { waiting = false; InvitePage(); }
        }

        void OnFailed(string why)
        {
            if (!waiting) return;
            waiting = false;
            if (btnCreate != null) btnCreate.Enabled = true;
            if (msg != null)
            {
                msg.ForeColor = Look.Red;
                msg.Text = why.IndexOf("taken", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "That name is already taken in this group. Pick another, or tick Start fresh." : why;
            }
        }

        static string LanAddress()
        {
            try
            {
                using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("10.255.255.255", 9);   // no packet is sent; this just picks the PC's main network address
                    return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch (Exception) { return ""; }
        }

        void InvitePage()
        {
            Clear();
            Title(groupName + " is ready!", "Send your friends the invite link. If they have GameSpy Revival, clicking it "
                + "opens their app; otherwise they press + > Join a Group and paste it.");
            string lan = LanAddress();
            string port = Shell.HostPort().ToString();
            Label pub = AddressRow("Invite link", "making your link...");
            if (lan.Length > 0) AddressRow("For friends on your Wi-Fi or LAN", Invite.Link(lan + ":" + port));
            Note("For internet friends, your router must forward TCP 27901 and 27900 to this PC. "
                + "If Windows Firewall asks about gamespy-lobby.exe, allow it.", Look.Muted).Height = 60;
            Footer(null, null, "Done", delegate { DialogResult = DialogResult.OK; Close(); });
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string ip = "";
                ip = Invite.PublicIp();
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        string my = Invite.MyLink(ip, Shell.HostPort());
                        pub.Text = my.Length > 0 ? my : "No internet address found yet";
                    });
                }
                catch (Exception) { }
            });
        }

        Label AddressRow(string label, string value)
        {
            Label l = new Label();
            l.Text = label.ToUpperInvariant();
            l.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            l.ForeColor = Look.Soft;
            l.SetBounds(30, y, 400, 18);
            body.Controls.Add(l);
            y += 20;
            Label v = new Label();
            v.Text = value;
            v.Font = new Font("Segoe UI Semibold", 12f);
            v.ForeColor = Color.White;
            v.BackColor = Look.Rail;
            v.TextAlign = ContentAlignment.MiddleLeft;
            v.Padding = new Padding(8, 0, 0, 0);
            v.SetBounds(30, y, 310, 34);
            body.Controls.Add(v);
            Button c = new Button();
            c.Text = "Copy";
            c.SetBounds(348, y, 82, 34);
            Theme.Style(c);
            c.Click += delegate
            {
                if (v.Text.IndexOf(':') < 0 || v.Text.Contains(" ")) return;
                try { Clipboard.SetText(v.Text); c.Text = "Copied"; } catch (Exception) { }
            };
            body.Controls.Add(c);
            y += 50;
            return v;
        }
    }
}
