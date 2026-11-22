using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Script.Serialization;

namespace GgsWifi
{
    public sealed class Reply
    {
        public int Status;
        public string Body = "";
        public string Location = "";
        public string Refresh = "";
    }

    public interface ITransport
    {
        Reply Get(Uri url, string referer);
    }

    // Never automatically follow a redirect: an authentication URL may contain credentials.
    public sealed class HttpTransport : ITransport
    {
        private readonly CookieContainer cookies = new CookieContainer();
        public Reply Get(Uri url, string referer)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.AllowAutoRedirect = false;
            request.CookieContainer = cookies;
            request.Proxy = null;
            request.Timeout = 6000;
            request.ReadWriteTimeout = 6000;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 GGS-WiFi/1.1";
            request.Accept = "application/json,text/html,text/plain,*/*";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (!String.IsNullOrEmpty(referer)) request.Referer = referer;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var buffer = new char[4096];
                    var body = new StringBuilder();
                    int count;
                    while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        body.Append(buffer, 0, count);
                        if (body.Length > 1024 * 1024) throw new IOException("服务器返回内容过大。");
                    }
                    return new Reply { Status = (int)response.StatusCode,
                        Body = body.ToString(), Location = response.Headers["Location"] ?? "", Refresh = response.Headers["Refresh"] ?? "" };
                }
            }
            catch (WebException)
            {
                // WebException.Message can contain the full request URL and password.
                throw new IOException("网络请求失败，请检查校园网连接、DNS 或系统时间。");
            }
        }
    }

    public sealed class ManualActionException : Exception
    {
        public ManualActionException(string message) : base(message) { }
    }

    public sealed class PortalContext
    {
        public Uri Page;
        public Dictionary<string, string> Query;
        public Dictionary<string, object> Config;
    }

    public sealed class PortalClient
    {
        public const string Host = "net.gzgs.edu.cn";
        public const string Gateway = "10.20.16.5";
        public const string Home = "https://net.gzgs.edu.cn/portal.do";
        public const string GatewayHome = "http://10.20.16.5/portal.do";
        // Controller identity from the user's official portal URL, confirmed by serverForm.servername.
        // This is a bootstrap hint, never a cached client IP, MAC or VLAN.
        public const string Controller = "NFV-BASE-01";
        private readonly ITransport transport;
        private readonly Func<HashSet<string>> localAddresses;
        public static readonly string[] ProbeUrls = {
            "http://www.msftconnecttest.com/connecttest.txt",
            "https://www.apple.com/library/test/success.html",
            "https://www.msftconnecttest.com/connecttest.txt"
        };

        public PortalClient(ITransport transport, Func<HashSet<string>> localAddresses)
        {
            this.transport = transport;
            this.localAddresses = localAddresses;
        }

        public static HashSet<string> GetLocalAddresses()
        {
            var result = new HashSet<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    (nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                     nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                     nic.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet)) continue;
                foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !ip.Address.ToString().StartsWith("169.254.") && !IPAddress.IsLoopback(ip.Address))
                        result.Add(ip.Address.ToString());
                }
            }
            return result;
        }

        public bool IsOnline()
        {
            for (int i = 0; i < ProbeUrls.Length; i++)
            {
                try
                {
                    var probe = new Uri(ProbeUrls[i]);
                    var r = transport.Get(probe, null);
                    if (r.Status != 200) continue;
                    if (probe.Host == "www.msftconnecttest.com" && r.Body.Trim() == "Microsoft Connect Test") return true;
                    if (probe.Host == "www.apple.com" && Regex.IsMatch(r.Body,
                        @"\A\s*(?:<!DOCTYPE[^>]*>\s*)?<html>\s*<head>\s*<title>\s*Success\s*</title>\s*</head>\s*<body>\s*Success\s*</body>\s*</html>\s*\z",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
                }
                catch (IOException) { }
            }
            return false;
        }

        public static bool IsPortal(Uri uri)
        {
            return uri != null && (uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) || uri.Host == Gateway) &&
                (uri.Scheme == "https" || uri.Scheme == "http") && uri.IsDefaultPort &&
                String.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath.Equals("/portal.do", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPortalEntry(Uri uri)
        {
            if (uri == null) return false;
            if (IsPortal(uri)) return true;
            // Success/logout pages carry the current connection parameters, but are never login targets.
            return (uri.Host == Host || uri.Host == Gateway) &&
                (uri.Scheme == "https" || uri.Scheme == "http") && uri.IsDefaultPort &&
                String.IsNullOrEmpty(uri.UserInfo) && Regex.IsMatch(uri.AbsolutePath,
                    @"^/portal/usertemp_(?:computer|mobile)/guangzhougs-(?:pc|mobile)(?:-v2)?/(?:login|index|logout)\.html$",
                    RegexOptions.IgnoreCase);
        }

        private static Uri LoginPage(Uri entry)
        {
            var query = ParseQuery(entry);
            var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "wlanuserip", "wlanacname", "wlanacip", "wlanuseripv6", "vlan", "mac", "wlanstamac", "usermac", "ssid", "version", "hostname" })
                if (query.ContainsKey(key)) clean[key] = query[key];
            return new UriBuilder(entry.Host == Gateway ? GatewayHome : Home) { Query = QueryString(clean) }.Uri;
        }

        public static Dictionary<string, string> ParseQuery(Uri uri)
        {
            var parsed = HttpUtility.ParseQueryString(uri.Query);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in parsed.AllKeys)
                if (!String.IsNullOrEmpty(key)) result[key] = parsed[key];
            return result;
        }

        public static string QueryString(IDictionary<string, string> values)
        {
            return String.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value ?? "")));
        }

        private Uri Discover()
        {
            // HTTP is used only to obtain a fresh captive-portal redirect, never for credentials.
            foreach (var seed in new[] { "http://www.msftconnecttest.com/redirect", "http://captive.apple.com/hotspot-detect.html" })
            {
                var next = new Uri(seed);
                for (int i = 0; i < 5; i++)
                {
                    if (IsPortalEntry(next)) return LoginPage(next);
                    try
                    {
                        var reply = transport.Get(next, null);
                        string target = reply.Location;
                        if (String.IsNullOrWhiteSpace(target) && !String.IsNullOrWhiteSpace(reply.Refresh))
                        {
                            var refresh = Regex.Match(reply.Refresh, @"url\s*=\s*['"" ]*(?<url>[^'""\s]+)", RegexOptions.IgnoreCase);
                            if (refresh.Success) target = refresh.Groups["url"].Value;
                        }
                        if (String.IsNullOrWhiteSpace(target))
                        {
                            // Covers common meta refresh and JS redirects; extracted data is never executed.
                            var match = Regex.Match(WebUtility.HtmlDecode(reply.Body),
                                @"https?://(?:net\.gzgs\.edu\.cn|10\.20\.16\.5)/(?:portal\.do|portal/usertemp_(?:computer|mobile)/guangzhougs-(?:pc|mobile)(?:-v2)?/(?:login|index|logout)\.html)[^\s""'<>]*", RegexOptions.IgnoreCase);
                            if (match.Success) target = match.Value;
                            else
                            {
                                var script = Regex.Match(WebUtility.HtmlDecode(reply.Body),
                                    @"(?:location(?:\.href)?\s*=|location\.(?:replace|assign)\s*\()\s*['""](?<url>[^'""]+)['""]", RegexOptions.IgnoreCase);
                                var refresh = Regex.Match(WebUtility.HtmlDecode(reply.Body),
                                    @"<meta\b[^>]*\bcontent\s*=\s*['""][^'""]*?url\s*=\s*(?<url>[^'""\s>]+)", RegexOptions.IgnoreCase);
                                if (script.Success) target = script.Groups["url"].Value;
                                else if (refresh.Success) target = refresh.Groups["url"].Value;
                            }
                        }
                        Uri candidate;
                        if (String.IsNullOrEmpty(target) || !Uri.TryCreate(next, target, out candidate) ||
                            (candidate.Scheme != "http" && candidate.Scheme != "https") ||
                            !String.IsNullOrEmpty(candidate.UserInfo)) break;
                        next = candidate;
                    }
                    catch (IOException) { break; }
                }
            }
            // No saved IP/MAC/VLAN: portal config must identify this machine below.
            return new Uri(Home);
        }

        public PortalContext LoadContext()
        {
            var locals = localAddresses();
            if (locals.Count == 0) throw new IOException("尚未连接 Wi-Fi 或网线，等待网络就绪。");
            var page = Discover();
            var discoveredIp = Value(ParseQuery(page), "wlanuserip");
            if (!String.IsNullOrEmpty(discoveredIp) && !locals.Contains(discoveredIp))
                throw new IOException("校园网重定向中的 IP 不属于本机，请重新获取当前认证页（不会使用旧 IP）。");
            IOException failure;
            try { return LoadContext(page, locals); }
            catch (IOException e)
            {
                failure = e;
                if (page.Host != Gateway)
                {
                    try { return LoadContext(new UriBuilder(GatewayHome) { Query = page.Query.TrimStart('?') }.Uri, locals); }
                    catch (IOException gatewayError) { failure = gatewayError; }
                }
            }
            // TUN/proxy routes can bypass captive redirects. A bare portal then returns portalForm:{}.
            // Ask the known official controller about a CURRENT local campus address instead.
            foreach (string ip in locals.OrderBy(s => s, StringComparer.Ordinal))
            {
                IPAddress address;
                if (!IPAddress.TryParse(ip, out address) || address.AddressFamily != AddressFamily.InterNetwork || address.GetAddressBytes()[0] != 10) continue;
                var bootstrap = new UriBuilder(GatewayHome) { Query = QueryString(new Dictionary<string, string> {
                    { "wlanuserip", ip }, { "wlanacname", Controller }
                }) }.Uri;
                try
                {
                    var context = LoadContext(bootstrap, locals);
                    var server = Map(context.Config, "serverForm");
                    if (Text(server, "servername") != Controller || Map(context.Config, "portalconfig").Count == 0 ||
                        Text(Map(context.Config, "portalForm"), "wlanuserip") != ip)
                        throw new IOException("校内网关未确认当前网卡对应的认证配置，请打开当前校园网认证页检查。");
                    return context;
                }
                catch (IOException e) { failure = e; }
            }
            throw failure;
        }

        private PortalContext LoadContext(Uri page, HashSet<string> locals)
        {
            transport.Get(page, null); // Establish the same cookie session as the official page.
            var query = ParseQuery(page);
            var configQuery = new Dictionary<string, string>(query);
            configQuery["viewStatus"] = "1";
            var raw = transport.Get(new Uri(page, "/PortalJsonAction.do?" + QueryString(configQuery)), page.AbsoluteUri);
            var config = ReadJson(raw);
            var form = Map(config, "portalForm");
            string ip = First(Text(form, "wlanuserip"), Value(query, "wlanuserip"), Text(config, "wlanuserip"), Text(config, "remoteAddr"));
            // The config endpoint is reachable on other networks too. A matching local client IP
            // is required before we send credentials; old redirect values are rejected as well.
            if (String.IsNullOrWhiteSpace(ip))
                throw new IOException("校园网配置未返回客户端 IP，且未能从当前校园网网卡补全参数，请检查校园网连接。");
            if (!locals.Contains(ip))
                throw new IOException("校园网返回的客户端 IP 与本机网卡不匹配，请检查 VPN/TUN 和校园网连接。");
            query["wlanuserip"] = ip;
            query["wlanacname"] = First(Text(form, "wlanacname"), Value(query, "wlanacname"));
            if (String.IsNullOrEmpty(query["wlanacname"]))
                throw new IOException("校园网未返回接入控制器信息，请先打开官方认证页确认连接。");
            foreach (var key in new[] { "vlan", "mac", "ssid" })
                query[key] = First(Text(form, key), Value(query, key));
            var server = Map(config, "serverForm");
            query["version"] = First(Text(server, "portalVer"), Value(query, "version"));
            query["wlanacip"] = First(Text(server, "serverip"), Value(query, "wlanacip"));
            query["mac"] = First(query["mac"], Value(query, "wlanstamac"), Value(query, "usermac"));
            query["hostname"] = First(Value(query, "hostname"), Environment.MachineName);
            // The portal's #casAuth handler reads ONLY the page query, unlike quickauth.
            // Rebuild it from this request's config so bare-portal fallback works for CAS too.
            page = new UriBuilder(page) { Query = QueryString(query) }.Uri;
            return new PortalContext { Page = page, Query = query, Config = config };
        }

        private static Dictionary<string, object> ReadJson(Reply reply)
        {
            if (reply.Status != 200) throw new IOException("认证服务暂不可用，稍后重试。");
            try
            {
                var result = new JavaScriptSerializer().DeserializeObject(reply.Body) as Dictionary<string, object>;
                if (result == null) throw new ArgumentException();
                return result;
            }
            catch (Exception e)
            {
                if (!(e is ArgumentException) && !(e is InvalidOperationException)) throw;
                throw new IOException("认证服务返回格式异常，请打开官方页面检查。");
            }
        }

        public static Dictionary<string, object> Map(Dictionary<string, object> d, string key)
        {
            object value;
            return d.TryGetValue(key, out value) && value is Dictionary<string, object>
                ? (Dictionary<string, object>)value : new Dictionary<string, object>();
        }
        public static string Text(Dictionary<string, object> d, string key)
        {
            object value;
            return d.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }
        public static string Value(Dictionary<string, string> d, string key)
        { string value; return d.TryGetValue(key, out value) ? value : ""; }
        private static string First(params string[] values)
        { return values.FirstOrDefault(s => !String.IsNullOrWhiteSpace(s)) ?? ""; }
    }
}
