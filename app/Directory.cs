// GameSpy Revival - the community directory: the people keeping old multiplayer games alive.
// Free software under the GNU GPL, version 3 or (at your option) any later version. No warranty.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Revival
{
    class DirectoryPage : Panel
    {
        static readonly string[][] Projects =
        {
            // name, games, what they do, link
            new string[] { "333networks", "Unreal, Unreal Tournament, Deus Ex, Rune, Postal 2, Serious Sam and more",
                "Has run replacement master servers since 2005, so server lists keep working after GameSpy. This app's Unreal Tournament browser uses their masters.",
                "https://333networks.com" },
            new string[] { "OldUnreal", "Unreal, Unreal Tournament",
                "Community patches (UT v469) made with Epic's permission: modern Windows support, fixes, and working online play.",
                "https://github.com/OldUnreal/UnrealTournamentPatches" },
            new string[] { "Unreal Archive", "Unreal series",
                "A huge archive of maps, mods and skins, and hosts two 333networks master servers.",
                "https://unrealarchive.org" },
            new string[] { "TribesNext", "Tribes 2",
                "Restored Tribes 2's online play after Dynamix and Sierra were gone. Pickup CTF games still run most nights.",
                "https://www.tribesnext.com" },
            new string[] { "BF2Hub", "Battlefield 2",
                "Replacement accounts, server list and ranked stats for Battlefield 2 after the GameSpy shutdown.",
                "https://www.bf2hub.com" },
            new string[] { "Project Reality: BF2", "Battlefield 2 (standalone mod)",
                "A free, standalone realism mod with its own master server. It's been going for nearly twenty years.",
                "https://www.realitymod.com" },
            new string[] { "SWBFSpy", "Star Wars Battlefront (2004) and Battlefront II (2005)",
                "Master server replacement so the original Battlefront games can still browse and join servers.",
                "https://www.github.com/SWBFSpy" },
            new string[] { "CnCNet", "C&C, Red Alert, Tiberian Sun, Red Alert 2 / Yuri's Revenge",
                "Community-run online play, fixes for modern Windows, ladders and tournaments for the classic C&C games.",
                "https://cncnet.org/red-alert-2" },
            new string[] { "OpenSpy", "Many GameSpy games",
                "An open-source GameSpy replacement aiming at full compatibility, from server lists to logins.",
                "https://openspy.net" },
            new string[] { "GameRanger", "Hundreds of older PC games",
                "A long-running matchmaking service that took on many games GameSpy dropped.",
                "https://www.gameranger.com" },
            new string[] { "OpenArena", "OpenArena",
                "A free, open-source arena shooter on the Quake III engine. You can browse its servers in this app.",
                "https://openarena.ws" },
        };

        public DirectoryPage()
        {
            Dock = DockStyle.Fill;
            AutoScroll = true;
            BackColor = Theme.Bg;
            int y = 18, x = 26, w = 690;

            Label t = new Label();
            t.Text = "Community Projects";
            t.Font = new Font("Segoe UI Semibold", 20f);
            t.ForeColor = Color.White;
            t.SetBounds(x, y, w, 38);
            Controls.Add(t);
            y += 42;
            Label sub = new Label();
            sub.Text = "When GameSpy shut down in 2014, these groups kept old games online on their own time. Go say thanks, join their servers, and chip in if you can.";
            sub.Font = new Font("Segoe UI", 10f);
            sub.ForeColor = Theme.Muted;
            sub.SetBounds(x, y, w, 40);
            Controls.Add(sub);
            y += 52;

            foreach (string[] p in Projects)
            {
                Panel card = new Panel();
                card.Tag = "card";
                card.BackColor = Theme.Panel2;
                card.SetBounds(x, y, w, 92);
                Label n = new Label();
                n.Text = p[0];
                n.Font = new Font("Segoe UI Semibold", 12f);
                n.ForeColor = Color.White;
                n.SetBounds(14, 8, 400, 24);
                card.Controls.Add(n);
                Label g = new Label();
                g.Text = p[1];
                g.Font = new Font("Segoe UI", 8.5f);
                g.ForeColor = Theme.Accent;
                g.SetBounds(14, 32, w - 150, 16);
                g.AutoEllipsis = true;
                card.Controls.Add(g);
                Label d = new Label();
                d.Text = p[2];
                d.Font = new Font("Segoe UI", 9f);
                d.ForeColor = Theme.Text;
                d.SetBounds(14, 50, w - 150, 36);
                card.Controls.Add(d);
                Button b = new Button();
                b.Text = "Visit site";
                b.SetBounds(w - 122, 30, 106, 30);
                string url = p[3];
                b.Click += delegate { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { } };
                card.Controls.Add(b);
                Theme.Style(b);
                ToolTip tt = new ToolTip();
                tt.SetToolTip(b, url);
                Controls.Add(card);
                y += 102;
            }
            Label foot = new Label();
            foot.Text = "Know a group that belongs here? Tell whoever runs your lobby.";
            foot.Font = new Font("Segoe UI", 9f);
            foot.ForeColor = Theme.Muted;
            foot.SetBounds(x, y + 4, w, 20);
            Controls.Add(foot);
            Panel pad = new Panel(); pad.SetBounds(x, y + 30, 10, 20); pad.BackColor = Theme.Bg; Controls.Add(pad);
        }
    }
}
