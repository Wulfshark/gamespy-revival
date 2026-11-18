// GameSpy Revival - Discord-style invites: short codes and gamespy://join/ links instead of IP addresses.
// Free software under the GPL-3.0 (see LICENSE.txt).

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Revival
{
    static class Invite
    {
        const string Abc = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford base32: no I, L, O, U
        public const string Scheme = "gamespy";

        // 203.0.113.25:27901 -> "K7Q2-M9XA-4B" (an IPv4 address and a port packed into 10 characters)
        public static string Code(string address)
        {
            string host; int port;
            if (!Split(address, out host, out port)) return "";
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return "";
            byte[] b = ip.GetAddressBytes();
            ulong v = ((ulong)b[0] << 40) | ((ulong)b[1] << 32) | ((ulong)b[2] << 24) | ((ulong)b[3] << 16) | (ulong)port;
            StringBuilder sb = new StringBuilder();
            for (int i = 9; i >= 0; i--) sb.Append(Abc[(int)((v >> (i * 5)) & 31)]);
            string c = sb.ToString();
            return c.Substring(0, 4) + "-" + c.Substring(4, 4) + "-" + c.Substring(8);
        }

        // The invite page on GitHub Pages: a normal https link that's clickable in Discord, texts and email.
        // It opens the app through gamespy://join/, or points to the download if the app isn't installed.
        public const string WebBase = "https://wulfshark.github.io/gamespy-revival/join.html#";

        // The link friends click. A host name (for example from a free dynamic-DNS service) goes in as is.
        public static string Link(string address)
        {
            string c = Code(address);
            return WebBase + (c.Length > 0 ? c : address.Trim());
        }

        // Accepts a link, a code, or a plain address; returns host:port, or "" if it isn't any of those.
        public static string Resolve(string text)
        {
            string t = (text ?? "").Trim().Trim('<', '>', '"', '\'');
            int hash = t.IndexOf('#');
            if (hash >= 0) t = t.Substring(hash + 1);   // https://.../join.html#CODE
            int k = t.IndexOf("://", StringComparison.Ordinal);
            if (k > 0) t = t.Substring(k + 3);
            if (t.StartsWith("join/", StringComparison.OrdinalIgnoreCase)) t = t.Substring(5);
            t = t.TrimEnd('/');
            string raw = t.Replace("-", "").Replace(" ", "").ToUpperInvariant();
            if (raw.Length == 10 && Regex.IsMatch(raw, "^[0-9A-HJKMNP-TV-Z]{10}$"))
            {
                ulong v = 0;
                foreach (char ch in raw) v = (v << 5) | (ulong)Abc.IndexOf(ch);
                int port = (int)(v & 0xFFFF);
                if (port > 0)
                    return ((v >> 40) & 255) + "." + ((v >> 32) & 255) + "." + ((v >> 24) & 255) + "." + ((v >> 16) & 255) + ":" + port;
            }
            string h; int p;
            if (Split(t, out h, out p)) return h + ":" + p;
            if (Regex.IsMatch(t, @"^[A-Za-z0-9.\-]+$") && t.Contains(".")) return t + ":27901";
            return "";
        }

        static bool Split(string a, out string host, out int port)
        {
            host = ""; port = 0;
            a = (a ?? "").Trim();
            int i = a.LastIndexOf(':');
            if (i <= 0) return false;
            host = a.Substring(0, i);
            return int.TryParse(a.Substring(i + 1), out port) && port > 0 && port < 65536 && Regex.IsMatch(host, @"^[A-Za-z0-9.\-]+$");
        }

        // This PC's internet address, as friends outside see it. Tries three lookup sites; "" if none answer.
        public static string PublicIp()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }   // TLS 1.2
            foreach (string url in new string[] { "https://api.ipify.org", "https://checkip.amazonaws.com", "https://icanhazip.com" })
            {
                try
                {
                    using (WebClient wc = new WebClient())
                    {
                        string ip = wc.DownloadString(url).Trim();
                        IPAddress a;
                        if (IPAddress.TryParse(ip, out a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return ip;
                    }
                }
                catch (Exception) { }
            }
            return "";
        }

        // Optional permanent address for the group you host (a free dynamic-DNS name such as mygroup.duckdns.org).
        // Invite links use it instead of your IP, so they keep working when your internet address changes.
        public static string MyName { get { return Config.Get("host.publicname", "").Trim(); } }

        public static string MyLink(string ip, int port)
        {
            if (MyName.Length > 0) return WebBase + MyName + ":" + port;
            return ip.Length > 0 ? Link(ip + ":" + port) : "";
        }

        // ---- identity backup: the per-PC key plus the list of groups/accounts you use
        public static void SaveIdentity(string path)
        {
            File.WriteAllText(path, "GameSpy Revival identity backup v1\r\n"
                + "Keep this file private: anyone who has it can sign in as you.\r\n"
                + "key=" + LobbyClient.DeviceSecret + "\r\n"
                + "accounts=" + Config.Get("accounts.recent", "") + "\r\n");
        }

        public static string LoadIdentity(string path)   // null when OK, otherwise what went wrong
        {
            string key = "", accts = "";
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith("key=")) key = line.Substring(4).Trim();
                else if (line.StartsWith("accounts=")) accts = line.Substring(9).Trim();
            }
            if (key.Length < 32) return "That file isn't a GameSpy Revival identity backup.";
            Config.Set("identity.secret", key);
            string have = Config.Get("accounts.recent", "");
            foreach (string e in accts.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                if (("|" + have + "|").IndexOf("|" + e + "|", StringComparison.OrdinalIgnoreCase) < 0) have = have.Length > 0 ? have + "|" + e : e;
            Config.Set("accounts.recent", have);
            return null;
        }

        // Makes gamespy:// links open this app (current user only, no admin rights needed).
        public static void RegisterScheme()
        {
            try
            {
                string exe = System.Windows.Forms.Application.ExecutablePath;
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme))
                {
                    k.SetValue("", "URL:GameSpy Revival invite");
                    k.SetValue("URL Protocol", "");
                    using (RegistryKey c = k.CreateSubKey(@"shell\open\command"))
                        c.SetValue("", "\"" + exe + "\" --join \"%1\"");
                }
            }
            catch (Exception) { }
        }

        // A link clicked while the app is already open is handed over through this file.
        static string PendingFile
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSpyRevival");
                Directory.CreateDirectory(d);
                return Path.Combine(d, "join" + (Config.Profile.Length > 0 ? "-" + Config.Profile : "") + ".txt");
            }
        }

        public static void SetPending(string link) { try { File.WriteAllText(PendingFile, link ?? ""); } catch (Exception) { } }

        public static string TakePending()
        {
            try
            {
                if (!File.Exists(PendingFile)) return "";
                string s = File.ReadAllText(PendingFile).Trim();
                File.Delete(PendingFile);
                return s;
            }
            catch (Exception) { return ""; }
        }
    }
}
