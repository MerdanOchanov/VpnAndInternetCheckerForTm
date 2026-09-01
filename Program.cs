// InternetChecker - tray/widget app for Windows 7/10/11 (.NET Framework 4.x)
// Three independent checks, each pinned to a specific network adapter:
//  1) Router / gateway  - ICMP ping to the physical adapter's gateway (home router).
//  2) Provider (bypass VPN) - reach astu.tm / telecom.tm THROUGH the physical channel,
//     with interface-bound DNS so it works even when the VPN tunnel is dead.
//  3) Internet via VPN - reach youtube.com THROUGH the VPN adapter.
// Starts by showing the window, runs the tests immediately, then lives in the tray.
// Built with the in-box .NET Framework compiler (csc.exe), C# 5 syntax only.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InternetChecker
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }
    }

    // ------------------------- Config -------------------------
    class Config
    {
        public string ProviderTargets = "astu.tm,telecom.tm"; // provider check (bypass VPN)
        public string VpnTarget = "youtube.com";              // VPN check (through tunnel)
        public int IntervalSec = 15;
        public int TimeoutMs = 3000;
        public string VpnHints = "TAP,WireGuard,Wintun,OpenVPN,VPN,NordLynx,Mullvad,ProtonVPN,tun,utun,ppp,Clash,sing-box,singbox,v2ray,xray,outline,warp,hysteria,tun2socks,hiddify,nekoray";
        // Local proxy ports probed when a proxy-VPN (v2ray/xray/clash) runs without a system proxy.
        public string ProxyProbePorts = "10808,10809,1080,1081,7890,7891,2080,8889,1087";
        public bool Autostart = false;

        public static string PathCfg()
        {
            string dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
            return System.IO.Path.Combine(dir, "internetchecker.cfg");
        }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                string p = PathCfg();
                if (File.Exists(p))
                {
                    string[] lines = File.ReadAllLines(p);
                    foreach (string line in lines)
                    {
                        string s = line.Trim();
                        if (s.Length == 0 || s.StartsWith("#")) continue;
                        int eq = s.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = s.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = s.Substring(eq + 1).Trim();
                        if (k == "providertargets") c.ProviderTargets = v;
                        else if (k == "vpntarget") c.VpnTarget = v;
                        else if (k == "intervalsec") int.TryParse(v, out c.IntervalSec);
                        else if (k == "timeoutms") int.TryParse(v, out c.TimeoutMs);
                        else if (k == "vpnhints") c.VpnHints = v;
                        else if (k == "proxyprobeports") c.ProxyProbePorts = v;
                        else if (k == "autostart") bool.TryParse(v, out c.Autostart);
                    }
                }
            }
            catch { }
            if (c.IntervalSec < 3) c.IntervalSec = 3;
            if (c.TimeoutMs < 500) c.TimeoutMs = 500;
            return c;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# InternetChecker config");
                sb.AppendLine("# providerTargets: узлы туркм. провайдера (через запятую) для проверки МИМО VPN");
                sb.AppendLine("providerTargets=" + ProviderTargets);
                sb.AppendLine("# vpnTarget: узел для проверки ЧЕРЕЗ VPN");
                sb.AppendLine("vpnTarget=" + VpnTarget);
                sb.AppendLine("intervalSec=" + IntervalSec.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("timeoutMs=" + TimeoutMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("vpnHints=" + VpnHints);
                sb.AppendLine("# локальные порты прокси (v2ray/xray/clash), если системный прокси не задан");
                sb.AppendLine("proxyProbePorts=" + ProxyProbePorts);
                sb.AppendLine("autostart=" + (Autostart ? "true" : "false"));
                File.WriteAllText(PathCfg(), sb.ToString());
            }
            catch { }
        }
    }

    // ------------------------- Native (ICMP with source binding) -------------------------
    static class Native
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern IntPtr IcmpCreateFile();
        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern bool IcmpCloseHandle(IntPtr handle);
        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint IcmpSendEcho2Ex(
            IntPtr icmpHandle, IntPtr Event, IntPtr apcRoutine, IntPtr apcContext,
            uint sourceAddress, uint destinationAddress,
            byte[] requestData, ushort requestSize, IntPtr requestOptions,
            byte[] replyBuffer, uint replySize, uint timeout);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
    }

    // ------------------------- Reachability primitives -------------------------
    static class Probe
    {
        const int IP_UNICAST_IF = 31;

        static uint ToUInt(IPAddress ip) { return BitConverter.ToUInt32(ip.GetAddressBytes(), 0); }

        public static int IfIndex(NetworkInterface ni)
        {
            try { return ni.GetIPProperties().GetIPv4Properties().Index; }
            catch { return -1; }
        }

        // ICMP echo, forced out of the interface owning 'source'.
        public static bool Icmp(IPAddress source, IPAddress dest, int timeoutMs)
        {
            if (source == null || dest == null) return false;
            IntPtr handle = Native.IcmpCreateFile();
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            try
            {
                byte[] data = Encoding.ASCII.GetBytes("InternetChecker");
                byte[] reply = new byte[256];
                uint ret = Native.IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    ToUInt(source), ToUInt(dest), data, (ushort)data.Length, IntPtr.Zero,
                    reply, (uint)reply.Length, (uint)timeoutMs);
                if (ret == 0) return false;
                return BitConverter.ToUInt32(reply, 4) == 0; // Status offset 4, IP_SUCCESS = 0
            }
            catch { return false; }
            finally { Native.IcmpCloseHandle(handle); }
        }

        // TCP connect, pinned to a specific interface (source IP + IP_UNICAST_IF).
        public static bool Tcp(NetworkInterface ni, IPAddress localIp, int ifIndex, IPAddress dest, int port, int timeoutMs)
        {
            if (dest == null || localIp == null) return false;
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (ifIndex > 0)
                    s.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IP_UNICAST_IF,
                        IPAddress.HostToNetworkOrder(ifIndex));
                s.Bind(new IPEndPoint(localIp, 0));
                IAsyncResult ar = s.BeginConnect(dest, port, null, null);
                bool done = ar.AsyncWaitHandle.WaitOne(timeoutMs, false);
                if (done && s.Connected) { s.EndConnect(ar); return true; }
                return false;
            }
            catch { return false; }
            finally { try { s.Close(); } catch { } }
        }

        // Plain TCP connect over the default route (no interface binding, system DNS).
        public static bool TcpDirect(string host, int port, int timeoutMs)
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                s.ReceiveTimeout = timeoutMs; s.SendTimeout = timeoutMs;
                IAsyncResult ar = s.BeginConnect(host, port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(timeoutMs, false) && s.Connected;
                if (ok) s.EndConnect(ar);
                return ok;
            }
            catch { return false; }
            finally { try { s.Close(); } catch { } }
        }

        // Is host reachable directly (443, then 80)? Used to detect that the internet
        // already arrives through an upstream/router VPN even without a local adapter/proxy.
        public static bool ReachableDirect(string host, int timeoutMs)
        {
            return TcpDirect(host, 443, timeoutMs) || TcpDirect(host, 80, timeoutMs);
        }

        // Minimal DNS/A resolver over UDP, pinned to a specific interface.
        public static IPAddress DnsQuery(IPAddress localIp, int ifIndex, IPAddress server, string host, int timeoutMs)
        {
            Socket u = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                if (ifIndex > 0)
                    u.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IP_UNICAST_IF,
                        IPAddress.HostToNetworkOrder(ifIndex));
                u.Bind(new IPEndPoint(localIp, 0));
                u.ReceiveTimeout = timeoutMs;
                byte[] q = BuildQuery(host);
                u.SendTo(q, new IPEndPoint(server, 53));
                byte[] buf = new byte[512];
                EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                int n = u.ReceiveFrom(buf, ref any);
                return ParseAnswer(buf, n);
            }
            catch { return null; }
            finally { try { u.Close(); } catch { } }
        }

        static byte[] BuildQuery(string host)
        {
            MemoryStream ms = new MemoryStream();
            byte[] id = new byte[2];
            new Random().NextBytes(id);
            ms.Write(id, 0, 2);
            ms.Write(new byte[] { 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, 0, 10);
            string[] labels = host.Split('.');
            foreach (string lab in labels)
            {
                byte[] lb = Encoding.ASCII.GetBytes(lab);
                ms.WriteByte((byte)lb.Length);
                ms.Write(lb, 0, lb.Length);
            }
            ms.WriteByte(0);
            ms.Write(new byte[] { 0x00, 0x01, 0x00, 0x01 }, 0, 4); // A, IN
            return ms.ToArray();
        }

        static IPAddress ParseAnswer(byte[] r, int len)
        {
            if (len < 12) return null;
            int ancount = (r[6] << 8) | r[7];
            if (ancount < 1) return null;
            int pos = 12;
            // skip question name
            while (pos < len && r[pos] != 0)
            {
                if ((r[pos] & 0xC0) == 0xC0) { pos += 2; goto qend; }
                pos += r[pos] + 1;
            }
            pos += 1; // zero byte
        qend:
            pos += 4; // qtype + qclass
            for (int i = 0; i < ancount && pos + 12 <= len; i++)
            {
                if ((r[pos] & 0xC0) == 0xC0) pos += 2;
                else { while (pos < len && r[pos] != 0) pos += r[pos] + 1; pos += 1; }
                if (pos + 10 > len) break;
                int type = (r[pos] << 8) | r[pos + 1]; pos += 2;
                pos += 2;     // class
                pos += 4;     // ttl
                int rdlen = (r[pos] << 8) | r[pos + 1]; pos += 2;
                if (type == 1 && rdlen == 4 && pos + 4 <= len)
                {
                    byte[] ip = new byte[4];
                    Array.Copy(r, pos, ip, 0, 4);
                    return new IPAddress(ip);
                }
                pos += rdlen;
            }
            return null;
        }
    }

    // ------------------------- Network adapter discovery -------------------------
    static class Nets
    {
        public static NetworkInterface FindVpn(string[] hints)
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                bool isVpn = ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel || MatchHint(ni, hints);
                if (!isVpn) continue;
                if (GetRoutableIPv4(ni) != null) return ni; // skip dead APIPA TAP/VPN adapters
            }
            return null;
        }

        public static NetworkInterface FindPhysical(string[] hints)
        {
            NetworkInterface eth = null, wifi = null, other = null;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (MatchHint(ni, hints)) continue;
                if (GetRoutableIPv4(ni) == null) continue; // skip disconnected APIPA adapters
                // NOTE: no gateway requirement - when a VPN is up it often strips the
                // physical adapter's gateway record; we still want that adapter.
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet && eth == null) eth = ni;
                else if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && wifi == null) wifi = ni;
                else if (other == null) other = ni;
            }
            if (eth != null) return eth;
            if (wifi != null) return wifi;
            return other;
        }

        static bool MatchHint(NetworkInterface ni, string[] hints)
        {
            string a = (ni.Name + " " + ni.Description).ToLowerInvariant();
            foreach (string h in hints)
            {
                string hh = h.Trim().ToLowerInvariant();
                if (hh.Length > 0 && a.Contains(hh)) return true;
            }
            return false;
        }

        public static bool IsRoutableV4(IPAddress a)
        {
            if (a == null || a.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = a.GetAddressBytes();
            if (b[0] == 127) return false;               // loopback
            if (b[0] == 169 && b[1] == 254) return false; // APIPA / link-local
            if (b[0] == 0) return false;
            return true;
        }

        // Routable IPv4 only (skips APIPA) - used to tell a live adapter from a dead TAP/VPN.
        public static IPAddress GetRoutableIPv4(NetworkInterface ni)
        {
            foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                if (IsRoutableV4(ua.Address)) return ua.Address;
            return null;
        }

        public static IPAddress GetIPv4(NetworkInterface ni)
        {
            IPAddress first = null;
            foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (first == null) first = ua.Address;
                if (IsRoutableV4(ua.Address)) return ua.Address;
            }
            return first;
        }

        public static IPAddress GetGatewayV4(NetworkInterface ni)
        {
            foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
                if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                    return g.Address;
            return null;
        }

        static UnicastIPAddressInformation GetV4Info(NetworkInterface ni)
        {
            foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork) return ua;
            return null;
        }

        // Best guess of the on-link router: network address + 1 (e.g. 172.16.2.17/24 -> 172.16.2.1).
        static IPAddress GuessGateway(NetworkInterface ni)
        {
            try
            {
                UnicastIPAddressInformation info = GetV4Info(ni);
                if (info == null || info.IPv4Mask == null) return null;
                byte[] ip = info.Address.GetAddressBytes();
                byte[] m = info.IPv4Mask.GetAddressBytes();
                uint ipu = (uint)((ip[0] << 24) | (ip[1] << 16) | (ip[2] << 8) | ip[3]);
                uint mu = (uint)((m[0] << 24) | (m[1] << 16) | (m[2] << 8) | m[3]);
                if (mu == 0) return null;
                uint gw = (ipu & mu) + 1;
                byte[] gb = new byte[] { (byte)(gw >> 24), (byte)(gw >> 16), (byte)(gw >> 8), (byte)gw };
                return new IPAddress(gb);
            }
            catch { return null; }
        }

        // Router IP even when the VPN stripped the gateway record: real gateway,
        // else the guessed on-link .1, else the adapter's first DNS server.
        public static IPAddress RouterIp(NetworkInterface ni)
        {
            IPAddress gw = GetGatewayV4(ni);
            if (gw != null) return gw;
            gw = GuessGateway(ni);
            if (gw != null) return gw;
            try
            {
                foreach (IPAddress d in ni.GetIPProperties().DnsAddresses)
                    if (d.AddressFamily == AddressFamily.InterNetwork) return d;
            }
            catch { }
            return null;
        }

        // DNS servers for this adapter (IPv4), plus sensible fallbacks.
        public static System.Collections.Generic.List<IPAddress> DnsServers(NetworkInterface ni, bool physical)
        {
            System.Collections.Generic.List<IPAddress> list = new System.Collections.Generic.List<IPAddress>();
            try
            {
                foreach (IPAddress d in ni.GetIPProperties().DnsAddresses)
                    if (d.AddressFamily == AddressFamily.InterNetwork) list.Add(d);
            }
            catch { }
            if (physical)
            {
                IPAddress gw = RouterIp(ni);
                if (gw != null && !list.Contains(gw)) list.Add(gw); // routers usually serve DNS
            }
            list.Add(IPAddress.Parse("8.8.8.8"));   // last resort
            list.Add(IPAddress.Parse("1.1.1.1"));
            return list;
        }

        // Resolve a host to IPv4, forcing DNS out of the given interface.
        public static IPAddress ResolveVia(NetworkInterface ni, bool physical, string host, int timeoutMs)
        {
            IPAddress direct;
            if (IPAddress.TryParse(host, out direct)) return direct;
            IPAddress local = GetIPv4(ni);
            int idx = Probe.IfIndex(ni);
            foreach (IPAddress srv in DnsServers(ni, physical))
            {
                IPAddress ip = Probe.DnsQuery(local, idx, srv, host, timeoutMs);
                if (ip != null) return ip;
            }
            return null;
        }

        public static bool Reachable(NetworkInterface ni, IPAddress ip, int timeoutMs)
        {
            IPAddress local = GetIPv4(ni);
            int idx = Probe.IfIndex(ni);
            if (Probe.Tcp(ni, local, idx, ip, 443, timeoutMs)) return true;
            if (Probe.Tcp(ni, local, idx, ip, 80, timeoutMs)) return true;
            return Probe.Icmp(local, ip, timeoutMs);
        }
    }

    // ------------------------- Proxy-based VPN support -------------------------
    class ProxyInfo
    {
        public bool Enabled;
        public string Type;   // "socks" | "http"
        public string Host;
        public int Port;
        public string Pac;    // AutoConfigURL, if any (not tunneled here)
    }

    static class Proxy
    {
        // Reads the per-user WinINET proxy (what most proxy-VPN clients set).
        public static ProxyInfo Read()
        {
            ProxyInfo pi = new ProxyInfo();
            try
            {
                RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings");
                if (k == null) return pi;
                object en = k.GetValue("ProxyEnable");
                object srv = k.GetValue("ProxyServer");
                object pac = k.GetValue("AutoConfigURL");
                k.Close();
                pi.Pac = pac == null ? null : pac.ToString();
                pi.Enabled = en != null && Convert.ToInt32(en) != 0;
                string s = srv == null ? "" : srv.ToString();
                if (s.Length > 0) ParseServer(s, pi);
            }
            catch { }
            return pi;
        }

        // ProxyServer can be "host:port" or "socks=h:p;http=h:p;https=h:p".
        static void ParseServer(string s, ProxyInfo pi)
        {
            string socks = null, http = null, https = null, generic = null;
            string[] parts = s.Split(';');
            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                int eq = p.IndexOf('=');
                if (eq > 0)
                {
                    string scheme = p.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = p.Substring(eq + 1).Trim();
                    if (scheme == "socks") socks = val;
                    else if (scheme == "https") https = val;
                    else if (scheme == "http") http = val;
                }
                else generic = p;
            }
            string pick; string type;
            if (socks != null) { pick = socks; type = "socks"; }
            else if (https != null) { pick = https; type = "http"; }
            else if (http != null) { pick = http; type = "http"; }
            else if (generic != null) { pick = generic; type = "http"; }
            else return;
            int c = pick.LastIndexOf(':');
            if (c <= 0) return;
            pi.Type = type;
            pi.Host = pick.Substring(0, c).Trim();
            int port; int.TryParse(pick.Substring(c + 1).Trim(), out port);
            pi.Port = port;
        }

        // Detects a local proxy-VPN (v2ray/xray/clash) that is NOT set as system proxy:
        // probes common loopback ports and returns the first that can tunnel to host:443.
        public static ProxyInfo DetectLocal(string portsCsv, string host, int timeoutMs)
        {
            if (portsCsv == null) return null;
            string[] parts = portsCsv.Split(',');
            foreach (string raw in parts)
            {
                int port;
                if (!int.TryParse(raw.Trim(), out port) || port <= 0) continue;
                if (!PortOpen("127.0.0.1", port, 400)) continue; // fast reject if nothing listens
                if (Socks5("127.0.0.1", port, host, 443, timeoutMs))
                    return Make("socks", "127.0.0.1", port);
                if (HttpConnect("127.0.0.1", port, host, 443, timeoutMs))
                    return Make("http", "127.0.0.1", port);
            }
            return null;
        }

        static ProxyInfo Make(string type, string host, int port)
        {
            ProxyInfo pi = new ProxyInfo();
            pi.Enabled = true; pi.Type = type; pi.Host = host; pi.Port = port;
            return pi;
        }

        static bool PortOpen(string host, int port, int timeoutMs)
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                IAsyncResult ar = s.BeginConnect(host, port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(timeoutMs, false) && s.Connected;
                if (ok) s.EndConnect(ar);
                return ok;
            }
            catch { return false; }
            finally { try { s.Close(); } catch { } }
        }

        // Tests reaching host:port THROUGH the proxy (proves the proxy-VPN tunnel works).
        public static bool TestConnect(ProxyInfo pi, string host, int port, int timeoutMs)
        {
            if (pi == null || pi.Host == null || pi.Port <= 0) return false;
            return pi.Type == "socks"
                ? Socks5(pi.Host, pi.Port, host, port, timeoutMs)
                : HttpConnect(pi.Host, pi.Port, host, port, timeoutMs);
        }

        static Socket Dial(string host, int port, int timeoutMs)
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.ReceiveTimeout = timeoutMs; s.SendTimeout = timeoutMs;
            IAsyncResult ar = s.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false) || !s.Connected) { try { s.Close(); } catch { } return null; }
            s.EndConnect(ar);
            return s;
        }

        static bool Socks5(string ph, int pp, string host, int port, int t)
        {
            Socket s = null;
            try
            {
                s = Dial(ph, pp, t);
                if (s == null) return false;
                s.Send(new byte[] { 0x05, 0x01, 0x00 }); // ver5, no-auth
                byte[] r = new byte[2];
                if (s.Receive(r) < 2 || r[0] != 0x05 || r[1] != 0x00) return false;
                byte[] hb = Encoding.ASCII.GetBytes(host);
                byte[] req = new byte[7 + hb.Length];
                req[0] = 0x05; req[1] = 0x01; req[2] = 0x00; req[3] = 0x03; req[4] = (byte)hb.Length;
                Array.Copy(hb, 0, req, 5, hb.Length);
                req[5 + hb.Length] = (byte)(port >> 8);
                req[6 + hb.Length] = (byte)(port & 0xFF);
                s.Send(req);
                byte[] resp = new byte[10];
                int m = s.Receive(resp);
                return m >= 2 && resp[1] == 0x00; // 0 = succeeded
            }
            catch { return false; }
            finally { if (s != null) try { s.Close(); } catch { } }
        }

        static bool HttpConnect(string ph, int pp, string host, int port, int t)
        {
            Socket s = null;
            try
            {
                s = Dial(ph, pp, t);
                if (s == null) return false;
                string req = "CONNECT " + host + ":" + port + " HTTP/1.1\r\nHost: " + host + ":" + port + "\r\n\r\n";
                s.Send(Encoding.ASCII.GetBytes(req));
                byte[] buf = new byte[256];
                int m = s.Receive(buf);
                if (m <= 0) return false;
                string line = Encoding.ASCII.GetString(buf, 0, m);
                return line.StartsWith("HTTP/1.") && line.IndexOf(" 200") > 0;
            }
            catch { return false; }
            finally { if (s != null) try { s.Close(); } catch { } }
        }
    }

    // ------------------------- System helpers (elevation, temporary routes) -------------------------
    static class Sys
    {
        public static bool IsElevated()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static bool Route(string args)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo("route.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                System.Diagnostics.Process pr = System.Diagnostics.Process.Start(psi);
                pr.StandardOutput.ReadToEnd();
                pr.StandardError.ReadToEnd();
                pr.WaitForExit(4000);
                return pr.HasExited && pr.ExitCode == 0;
            }
            catch { return false; }
        }

        // Adds a temporary host route (dest/32) via the physical gateway on the physical
        // interface, so an off-subnet provider host can be reached bypassing the VPN.
        public static bool AddHostRoute(IPAddress dest, IPAddress gw, int ifIndex)
        {
            if (dest == null || gw == null) return false;
            string args = "add " + dest + " mask 255.255.255.255 " + gw + " metric 1";
            if (ifIndex > 0) args += " if " + ifIndex;
            return Route(args);
        }

        public static void DelHostRoute(IPAddress dest)
        {
            if (dest == null) return;
            Route("delete " + dest);
        }

        public static void Relaunch(bool asAdmin)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                if (asAdmin) psi.Verb = "runas";
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }
    }

    enum Res { Ok, Fail, Off, Unknown }

    // ------------------------- Tray application -------------------------
    class TrayContext : ApplicationContext
    {
        NotifyIcon tray;
        System.Windows.Forms.Timer timer;
        Config cfg;
        PopupForm popup;
        Control sync;
        IntPtr lastHicon = IntPtr.Zero;
        MenuItem autostartItem;

        Res gwRes = Res.Unknown, provRes = Res.Unknown, vpnRes = Res.Unknown;
        string gwText = "проверка...", provText = "проверка...", vpnText = "проверка...";
        DateTime lastCheck = DateTime.MinValue;
        volatile bool busy = false;

        public TrayContext()
        {
            cfg = Config.Load();

            sync = new Control();
            IntPtr force = sync.Handle;

            tray = new NotifyIcon();
            SetIcon(Color.Gray);
            tray.Text = "InternetChecker";
            tray.Visible = true;
            tray.MouseClick += TrayClick;

            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("Проверить сейчас", delegate(object s, EventArgs e) { RunChecks(); });
            menu.MenuItems.Add("Открыть виджет", delegate(object s, EventArgs e) { ShowPopup(); });
            autostartItem = new MenuItem("Автозапуск с Windows", ToggleAutostart);
            autostartItem.Checked = cfg.Autostart;
            menu.MenuItems.Add(autostartItem);
            menu.MenuItems.Add("Ярлык на рабочий стол", delegate(object s, EventArgs e) { CreateDesktopShortcut(); });
            if (!Sys.IsElevated())
                menu.MenuItems.Add("Перезапустить от администратора (точный пинг)",
                    delegate(object s, EventArgs e) { Sys.Relaunch(true); ExitApp(); });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Настройки (файл cfg)", delegate(object s, EventArgs e) { OpenConfig(); });
            menu.MenuItems.Add("Выход", delegate(object s, EventArgs e) { ExitApp(); });
            tray.ContextMenu = menu;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = cfg.IntervalSec * 1000;
            timer.Tick += delegate(object s, EventArgs e) { RunChecks(); };
            timer.Start();

            // Show the window first, then it tucks into the tray.
            ShowPopup();
            RunChecks();
        }

        void TrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (popup != null && popup.Visible) popup.Hide();
                else ShowPopup();
            }
        }

        void ShowPopup()
        {
            if (popup == null)
            {
                popup = new PopupForm();
                popup.gwBtn.Click += delegate(object s, EventArgs e) { RunChecks(); };
                popup.provBtn.Click += delegate(object s, EventArgs e) { RunChecks(); };
                popup.vpnBtn.Click += delegate(object s, EventArgs e) { RunChecks(); };
                // Close button hides to tray instead of exiting.
                popup.FormClosing += delegate(object s, FormClosingEventArgs e)
                {
                    if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; popup.Hide(); }
                };
            }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            popup.Location = new Point(wa.Right - popup.Width - 8, wa.Bottom - popup.Height - 8);
            RefreshPopup();
            popup.Show();
            popup.Activate();
        }

        void RunChecks()
        {
            if (busy) return;
            busy = true;
            Config c = cfg;
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                Res g, p, v; string gt, pt, vt;
                DoChecks(c, out g, out gt, out p, out pt, out v, out vt);
                try
                {
                    sync.BeginInvoke((MethodInvoker)delegate
                    {
                        gwRes = g; gwText = gt; provRes = p; provText = pt; vpnRes = v; vpnText = vt;
                        lastCheck = DateTime.Now;
                        ApplyUi();
                        busy = false;
                    });
                }
                catch { busy = false; }
            });
        }

        static void DoChecks(Config c,
            out Res g, out string gt, out Res p, out string pt, out Res v, out string vt)
        {
            string[] hints = c.VpnHints.Split(',');
            NetworkInterface phys = Nets.FindPhysical(hints);
            NetworkInterface vpn = Nets.FindVpn(hints);

            IPAddress router = phys == null ? null : Nets.RouterIp(phys);
            IPAddress physIp = phys == null ? null : Nets.GetIPv4(phys);
            int physIdx = phys == null ? -1 : Probe.IfIndex(phys);
            bool elevated = Sys.IsElevated();

            // 1) Router / gateway (ICMP to the physical router - on-link, works with VPN up)
            if (phys == null) { g = Res.Unknown; gt = "физ. адаптер не найден"; }
            else if (router == null) { g = Res.Unknown; gt = "шлюз не найден"; }
            else
            {
                bool ok = Probe.Icmp(physIp, router, c.TimeoutMs);
                g = ok ? Res.Ok : Res.Fail;
                gt = (ok ? "OK  " : "нет ответа  ") + router.ToString() + "  [" + phys.Name + "]";
            }

            // 2) Provider bypassing VPN (astu.tm / telecom.tm through the physical channel).
            // With a VPN up the physical default route is gone, so an off-subnet host is
            // reached by temporarily adding a host-route via the router (needs admin). Without
            // admin we fall back to on-link DNS resolution as the provider-alive signal.
            if (phys == null) { p = Res.Unknown; pt = "физ. адаптер не найден"; }
            else
            {
                bool anyOk = false;
                StringBuilder sb = new StringBuilder();
                string[] hosts = c.ProviderTargets.Split(',');
                foreach (string raw in hosts)
                {
                    string host = raw.Trim();
                    if (host.Length == 0) continue;
                    IPAddress ip = Nets.ResolveVia(phys, true, host, c.TimeoutMs);
                    string mark;
                    if (ip == null) mark = "нет DNS";
                    else if (Nets.Reachable(phys, ip, c.TimeoutMs)) { mark = "OK"; anyOk = true; }
                    else if (elevated && router != null)
                    {
                        // true bypass ping via a temporary host-route
                        bool added = Sys.AddHostRoute(ip, router, physIdx);
                        bool ok = added && (Probe.Icmp(physIp, ip, c.TimeoutMs) || Nets.Reachable(phys, ip, c.TimeoutMs));
                        if (added) Sys.DelHostRoute(ip);
                        if (ok) { mark = "OK"; anyOk = true; } else mark = "нет";
                    }
                    else
                    {
                        // no admin: DNS resolved on-link => provider chain is alive
                        mark = "DNS-OK*"; anyOk = true;
                    }
                    if (sb.Length > 0) sb.Append(",  ");
                    sb.Append(host).Append(": ").Append(mark);
                }
                p = anyOk ? Res.Ok : Res.Fail;
                sb.Append(elevated ? "  [точный пинг]" : "  [без прав админа: по DNS*]");
                pt = sb.ToString();
            }

            // 3) Internet via VPN. Two kinds of "VPN":
            //    (a) proxy-based (SOCKS/HTTP proxy, e.g. Xray/V2Ray) - test through the proxy;
            //    (b) real tunnel adapter (WireGuard/OpenVPN with a routable IP) - test bound to it.
            string vhost = c.VpnTarget.Trim();
            ProxyInfo px = Proxy.Read();
            if (px != null && px.Enabled && px.Host != null && px.Port > 0)
            {
                bool ok = Proxy.TestConnect(px, vhost, 443, c.TimeoutMs);
                v = ok ? Res.Ok : Res.Fail;
                vt = vhost + ": " + (ok ? "OK" : "нет") +
                     "  [прокси " + px.Type + " " + px.Host + ":" + px.Port + "]";
            }
            else if (vpn != null)
            {
                IPAddress ip = Nets.ResolveVia(vpn, false, vhost, c.TimeoutMs);
                if (ip == null) { v = Res.Fail; vt = vhost + ": нет DNS  [" + vpn.Name + "]"; }
                else
                {
                    bool ok = Nets.Reachable(vpn, ip, c.TimeoutMs);
                    v = ok ? Res.Ok : Res.Fail;
                    vt = vhost + ": " + (ok ? "OK" : "нет") + "  [" + vpn.Name + "]";
                }
            }
            else
            {
                // No system proxy and no tunnel adapter: probe for a local proxy-VPN
                // (v2ray/xray/clash running without setting the system proxy).
                ProxyInfo local = Proxy.DetectLocal(c.ProxyProbePorts, vhost, c.TimeoutMs);
                if (local != null)
                {
                    v = Res.Ok;
                    vt = vhost + ": OK  [локальный прокси " + local.Type + " " + local.Host + ":" + local.Port + "]";
                }
                else if (Probe.ReachableDirect(vhost, c.TimeoutMs))
                {
                    // No local adapter/proxy, yet the target is reachable directly -> the
                    // internet already arrives through an upstream/router VPN.
                    v = Res.Ok;
                    vt = vhost + ": OK  [прямой доступ — интернет уже идёт через VPN]";
                }
                else if (px != null && px.Pac != null && px.Pac.Length > 0)
                {
                    v = Res.Unknown; vt = "PAC-прокси (авто-конфиг) не поддерживается: " + px.Pac;
                }
                else { v = Res.Off; vt = "VPN выключен (ни адаптера, ни прокси)"; }
            }
        }

        void ApplyUi()
        {
            SetIcon(IconColor());
            tray.Text = Trim63("Роутер:" + Short(gwRes) + " Пров:" + Short(provRes) + " VPN:" + Short(vpnRes));
            RefreshPopup();
        }

        void RefreshPopup()
        {
            if (popup == null || !popup.IsHandleCreated) return;
            popup.gwDot.BackColor = DotColor(gwRes);
            popup.provDot.BackColor = DotColor(provRes);
            popup.vpnDot.BackColor = DotColor(vpnRes);
            popup.gwLbl.Text = "Роутер / шлюз:  " + gwText;
            popup.provLbl.Text = "Провайдер (мимо VPN):  " + provText;
            popup.vpnLbl.Text = "Интернет через VPN:  " + vpnText;
            popup.updLbl.Text = lastCheck == DateTime.MinValue
                ? "Проверка..."
                : "Обновлено: " + lastCheck.ToString("HH:mm:ss");
        }

        static string Short(Res r)
        {
            if (r == Res.Ok) return "OK";
            if (r == Res.Fail) return "нет";
            if (r == Res.Off) return "выкл";
            return "?";
        }

        static Color DotColor(Res r)
        {
            if (r == Res.Ok) return Color.LimeGreen;
            if (r == Res.Fail) return Color.Red;
            if (r == Res.Off) return Color.Gold;
            return Color.Gray;
        }

        Color IconColor()
        {
            if (provRes == Res.Unknown && gwRes == Res.Unknown) return Color.Gray;
            if (gwRes == Res.Fail) return Color.Red;      // even the router is unreachable
            if (provRes == Res.Fail) return Color.Red;    // no internet from provider
            if (provRes == Res.Ok && vpnRes == Res.Ok) return Color.LimeGreen;
            return Color.Gold;                            // provider ok, VPN off/fail
        }

        void SetIcon(Color c)
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 14, 14);
                using (Pen p = new Pen(Color.FromArgb(90, 0, 0, 0))) g.DrawEllipse(p, 1, 1, 14, 14);
            }
            IntPtr h = bmp.GetHicon();
            tray.Icon = Icon.FromHandle(h);
            bmp.Dispose();
            if (lastHicon != IntPtr.Zero) Native.DestroyIcon(lastHicon);
            lastHicon = h;
        }

        static string Trim63(string s) { return s.Length <= 63 ? s : s.Substring(0, 63); }

        void ToggleAutostart(object sender, EventArgs e)
        {
            cfg.Autostart = !cfg.Autostart;
            autostartItem.Checked = cfg.Autostart;
            SetAutostart(cfg.Autostart);
            cfg.Save();
        }

        void SetAutostart(bool on)
        {
            try
            {
                RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (k == null) return;
                if (on) k.SetValue("InternetChecker", "\"" + Application.ExecutablePath + "\"");
                else if (k.GetValue("InternetChecker") != null) k.DeleteValue("InternetChecker", false);
                k.Close();
            }
            catch { }
        }

        void CreateDesktopShortcut()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnk = System.IO.Path.Combine(desktop, "InternetChecker.lnk");
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { Application.ExecutablePath });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc,
                    new object[] { System.IO.Path.GetDirectoryName(Application.ExecutablePath) });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc,
                    new object[] { Application.ExecutablePath + ",0" });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc,
                    new object[] { "Проверка интернета и VPN" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Notify("InternetChecker", "Ярлык создан на рабочем столе");
            }
            catch (Exception ex)
            {
                Notify("InternetChecker", "Не удалось создать ярлык: " + ex.Message);
            }
        }

        void Notify(string title, string text)
        {
            try
            {
                tray.BalloonTipTitle = title;
                tray.BalloonTipText = text;
                tray.ShowBalloonTip(2500);
            }
            catch { }
        }

        void OpenConfig()
        {
            try
            {
                string p = Config.PathCfg();
                if (!File.Exists(p)) cfg.Save();
                System.Diagnostics.Process.Start("notepad.exe", p);
            }
            catch { }
        }

        void ExitApp()
        {
            try { timer.Stop(); } catch { }
            try { tray.Visible = false; } catch { }
            if (lastHicon != IntPtr.Zero) Native.DestroyIcon(lastHicon);
            ExitThread();
        }
    }

    // ------------------------- Popup widget -------------------------
    class PopupForm : Form
    {
        public Label gwDot, provDot, vpnDot, gwLbl, provLbl, vpnLbl, updLbl;
        public Button gwBtn, provBtn, vpnBtn;

        public PopupForm()
        {
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Text = "InternetChecker";
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(430, 175);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            gwDot = MakeDot(14); gwLbl = MakeLbl(36, 14); gwBtn = MakeBtn(14);
            provDot = MakeDot(58); provLbl = MakeLbl(36, 58); provBtn = MakeBtn(58);
            vpnDot = MakeDot(102); vpnLbl = MakeLbl(36, 102); vpnBtn = MakeBtn(102);

            updLbl = new Label();
            updLbl.AutoSize = false;
            updLbl.SetBounds(14, 146, 410, 20);
            updLbl.ForeColor = Color.Gray;
            updLbl.Text = "Проверка...";

            Controls.Add(gwDot); Controls.Add(gwLbl); Controls.Add(gwBtn);
            Controls.Add(provDot); Controls.Add(provLbl); Controls.Add(provBtn);
            Controls.Add(vpnDot); Controls.Add(vpnLbl); Controls.Add(vpnBtn);
            Controls.Add(updLbl);
        }

        Label MakeDot(int y)
        {
            Label d = new Label();
            d.AutoSize = false;
            d.SetBounds(14, y + 2, 14, 14);
            d.BackColor = Color.Gray;
            return d;
        }

        Label MakeLbl(int x, int y)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.SetBounds(x, y, 290, 40);
            l.Text = "...";
            return l;
        }

        Button MakeBtn(int y)
        {
            Button b = new Button();
            b.Text = "Проверить";
            b.SetBounds(336, y, 84, 28);
            return b;
        }
    }
}
