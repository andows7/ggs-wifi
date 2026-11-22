using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace GgsWifi
{
    internal sealed class FakeTransport : ITransport
    {
        public readonly List<Uri> Requests = new List<Uri>();
        public Func<Uri, Reply> Handler;
        public Reply Get(Uri url, string referer) { Requests.Add(url); return Handler(url); }
    }

    internal static class Tests
    {
        private static int passed;
        private static string root;
        private static void Assert(bool value, string name)
        { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        private static void Throws<T>(Action action, string name) where T : Exception
        { try { action(); } catch (T) { Assert(true, name); return; } throw new Exception("FAIL: " + name); }
        private static string Config(string ip)
        { return "{\"portalForm\":{\"wlanuserip\":\"" + ip + "\",\"wlanacname\":\"CURRENT-AC\",\"mac\":\"aa:bb:cc:dd:ee:ff\"},\"portalconfig\":{\"id\":3}}"; }
        private static PortalClient Client(FakeTransport transport)
        { return new PortalClient(transport, () => new HashSet<string> { "10.2.3.4" }); }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                root = Path.GetFullPath(args[0]);
                Directory.CreateDirectory(root);
                if (args.Length > 1 && args[1] == "--login-once")
                {
                    var store = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GGS_WIFI"));
                    var saved = store.Load();
                    if (!saved.AuthPaused) throw new IOException("Live verification skipped: the normal app is not paused.");
                    var client = new PortalClient(new HttpTransport(), PortalClient.GetLocalAddresses);
                    if (client.IsOnline()) { Console.WriteLine("Internet is already reachable; no login submitted."); return 0; }
                    var context = client.LoadContext();
                    var browser = new BrowserLogin(Path.Combine(root, "live-validation-profile"), Console.WriteLine, () => false);
                    var temporary = new LoginAccount(saved.TemporaryUsername, SettingsStore.Unprotect(saved.ProtectedTemporaryPassword));
                    var unified = new LoginAccount(saved.Username, SettingsStore.Unprotect(saved.ProtectedPassword));
                    Console.WriteLine("Starting one live flow with saved credentials; no settings or startup entries are changed.");
                    string result = AuthenticationFlow.Run(saved.PreferTemporaryLogin, temporary, unified,
                        account => browser.LoginTemporary(context.Page, account.Username, account.Password),
                        account => browser.Login(client.LoadContext().Page, account.Username, account.Password),
                        () => { for (int i = 0; i < 3; i++) { Thread.Sleep(2000); if (client.IsOnline()) return true; } return false; },
                        () => { }, Console.WriteLine);
                    Console.WriteLine("Verified internet restored by " + result + " login."); return 0;
                }
                if (args.Length > 1 && args[1] == "--inspect")
                {
                    var context = new PortalClient(new HttpTransport(), PortalClient.GetLocalAddresses).LoadContext();
                    new BrowserLogin(Path.Combine(root, "inspect-browser-profile"), Console.WriteLine, () => false).Inspect(context.Page, Console.WriteLine);
                    return 0;
                }
                if (args.Length > 1 && args[1] == "--diagnose")
                {
                    var context = new PortalClient(new HttpTransport(), PortalClient.GetLocalAddresses).LoadContext();
                    Console.WriteLine("Read-only portal diagnostic: host=" + context.Page.Host + ", local-IP=" + context.Query["wlanuserip"] +
                        ", controller=" + context.Query["wlanacname"] + ", version=" + context.Query["version"] + ", VLAN-returned=" + !String.IsNullOrEmpty(context.Query["vlan"]));
                    return 0;
                }
                Protocol(); AuthenticationTests(); SettingsTests(); BrowserTests(); Preview();
                Console.WriteLine("All " + passed + " assertions passed.");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        }

        private static void Protocol()
        {
            var fake = new FakeTransport();
            fake.Handler = u => new Reply { Status = 200, Body = "<html>Login</html>" };
            Assert(!Client(fake).IsOnline(), "Captive login HTML cannot count as internet connectivity");
            fake.Handler = u => new Reply { Status = 302, Location = PortalClient.Home, Body = "Microsoft Connect Test" };
            Assert(!Client(fake).IsOnline(), "Redirect cannot count as internet connectivity");
            fake.Handler = u => new Reply { Status = 200, Body = "Microsoft Connect Test\r\n" };
            Assert(Client(fake).IsOnline(), "Exact connectivity probe success is recognized");
            fake.Handler = u => {
                if (u.Host == "www.msftconnecttest.com") throw new IOException("timeout");
                return new Reply { Status = 200, Body = "<html><head><title>Success</title></head><body>Success</body></html>" };
            };
            Assert(Client(fake).IsOnline(), "Second provider works when first probe fails");
            fake.Handler = u => {
                if (u.Host == "www.msftconnecttest.com") throw new IOException("HTTPS endpoint/certificate unavailable");
                return new Reply { Status = 200, Body = "<HTML><HEAD><TITLE>Success</TITLE></HEAD><BODY>Success</BODY></HTML>" };
            };
            Assert(Client(fake).IsOnline(), "Apple's real uppercase success document is recognized");
            fake.Handler = u => u.Scheme == "http" ? new Reply { Status = 200, Body = "Microsoft Connect Test" } :
                new Reply { Status = 200, Body = "<html>certificate/login page</html>" };
            Assert(Client(fake).IsOnline(), "The standard HTTP connectivity probe works when HTTPS probes fail");
            fake.Handler = u => new Reply { Status = 200, Body = "<HTML><HEAD><TITLE>Success</TITLE></HEAD><BODY>Success<form>校园网登录</form></BODY></HTML>" };
            Assert(!Client(fake).IsOnline(), "A captive page containing success text cannot count as internet access");
            fake.Requests.Clear();
            fake.Handler = u => {
                if (u.Scheme == "http") return new Reply { Status = 302,
                    Location = "http://net.gzgs.edu.cn/portal.do?wlanuserip=10.2.3.4&wlanacname=CURRENT-AC&vlan=77&mac=aa%3Abb&hostname=ThisPC" };
                return new Reply { Status = 200, Body = u.AbsolutePath == "/PortalJsonAction.do" ? Config("10.2.3.4") : "<html></html>" };
            };
            var context = Client(fake).LoadContext();
            Assert(context.Page.Scheme == "https", "Portal HTTP redirect is upgraded to HTTPS");
            Assert(context.Query["wlanuserip"] == "10.2.3.4" && context.Query["vlan"] == "77" && context.Query["mac"] == "aa:bb:cc:dd:ee:ff",
                "Current redirect IP and VLAN are retained, with live config MAC preferred");
            Assert(fake.Requests.Any(u => u.AbsolutePath == "/PortalJsonAction.do" && u.Query.Contains("viewStatus=1")),
                "Fresh portal config is requested");
            Assert(!fake.Requests.Any(u => u.Query.Contains("10.9.9.9") || u.Query.Contains("passwd")),
                "No example IP or credential is sent during discovery");
            fake.Handler = u => {
                if (u.Scheme == "http") return new Reply { Status = 200, Body = "<meta content='0;url=https://net.gzgs.edu.cn/portal.do?wlanuserip=10.2.3.4&amp;wlanacname=CURRENT-AC'>" };
                return new Reply { Status = 200, Body = u.AbsolutePath == "/PortalJsonAction.do" ? Config("10.2.3.4") : "" };
            };
            Assert(Client(fake).LoadContext().Query["wlanacname"] == "CURRENT-AC", "HTML redirect entities are decoded");
            foreach (string redirect in new[] {
                "<script>window.location.href='/portal.do?wlanuserip=10.2.3.4&wlanacname=CURRENT-AC';</script>",
                "<meta http-equiv='refresh' content='0;url=/portal.do?wlanuserip=10.2.3.4&amp;wlanacname=CURRENT-AC'>" })
            {
                fake.Handler = u => {
                    if (u.Host == "www.msftconnecttest.com") return new Reply { Status = 302, Location = "http://10.20.16.5/entry" };
                    if (u.AbsolutePath == "/entry") return new Reply { Status = 200, Body = redirect };
                    return new Reply { Status = 200, Body = u.AbsolutePath == "/PortalJsonAction.do" ? Config("10.2.3.4") : "" };
                };
                Assert(Client(fake).LoadContext().Query["wlanacname"] == "CURRENT-AC", "Relative script/meta portal redirects preserve current connection parameters");
            }
            fake.Handler = u => {
                if (u.Host == "www.msftconnecttest.com") return new Reply { Status = 302, Location = "http://10.20.16.5/entry" };
                if (u.AbsolutePath == "/entry") return new Reply { Status = 200, Refresh = "0; url=/portal.do?wlanuserip=10.2.3.4&wlanacname=CURRENT-AC" };
                return new Reply { Status = 200, Body = u.AbsolutePath == "/PortalJsonAction.do" ? Config("10.2.3.4") : "" };
            };
            Assert(Client(fake).LoadContext().Page.Host == PortalClient.Gateway, "HTTP Refresh headers can lead to the official gateway");
            fake.Handler = u => {
                if (u.Scheme == "http" && u.Host != PortalClient.Gateway) return new Reply { Status = 302, Location = "http://10.20.16.5/portal.do?wlanuserip=10.2.3.4&wlanacname=CURRENT-AC&vlan=77" };
                return new Reply { Status = 200, Body = u.AbsolutePath == "/PortalJsonAction.do" ? Config("10.2.3.4") : "" };
            };
            Assert(Client(fake).LoadContext().Page.Host == PortalClient.Gateway, "Internal-IP portal redirects retain the working campus gateway origin");
            fake.Handler = u => {
                if (u.Host != PortalClient.Gateway) throw new IOException("school DNS unavailable");
                return new Reply { Status = 200, Body = u.AbsolutePath == "/PortalJsonAction.do" ? Config("10.2.3.4") : "" };
            };
            context = Client(fake).LoadContext();
            Assert(context.Page.Host == PortalClient.Gateway && context.Query["wlanuserip"] == "10.2.3.4",
                "Unavailable school DNS falls back to the gateway with a validated local IP");
            fake.Requests.Clear();
            fake.Handler = u => {
                var q = PortalClient.ParseQuery(u);
                if (u.AbsolutePath == "/PortalJsonAction.do" && PortalClient.Value(q, "wlanacname") == PortalClient.Controller)
                    return new Reply { Status = 200, Body = "{\"portalForm\":{\"wlanuserip\":\"10.2.3.4\",\"wlanacname\":\"NFV-BASE-01\"},\"portalconfig\":{\"id\":8},\"serverForm\":{\"servername\":\"NFV-BASE-01\",\"portalVer\":4}}" };
                return new Reply { Status = 200, Body = u.AbsolutePath == "/PortalJsonAction.do" ? "{\"portalForm\":{}}" : "" };
            };
            context = Client(fake).LoadContext();
            Assert(context.Query["wlanuserip"] == "10.2.3.4" && context.Query["wlanacname"] == PortalClient.Controller && context.Query["version"] == "4",
                "A TUN-bypassed redirect and empty portalForm recover using the current campus address and confirmed controller");
            Assert(!fake.Requests.Any(u => u.Query.Contains("passwd") || u.Query.Contains("10.9.9.9") || u.Query.Contains("9999999")),
                "Bootstrap sends no credentials, example IP or old VLAN");
            var nonCampus = new PortalClient(fake, () => new HashSet<string> { "192.168.150.1" });
            fake.Requests.Clear();
            Throws<IOException>(() => nonCampus.LoadContext(), "Virtual private adapters are not used for campus bootstrap");
            Assert(!fake.Requests.Any(u => u.Query.Contains("192.168.150.1")), "Bootstrap never selects a VMware-style non-campus address");
            fake.Handler = u => {
                if (u.AbsolutePath == "/PortalJsonAction.do" && u.Query.Contains("wlanacname"))
                    return new Reply { Status = 200, Body = "{\"portalForm\":{\"wlanuserip\":\"10.2.3.4\",\"wlanacname\":\"NFV-BASE-01\"},\"portalconfig\":{\"id\":8},\"serverForm\":{\"servername\":\"OTHER-CONTROLLER\"}}" };
                return new Reply { Status = 200, Body = u.AbsolutePath == "/PortalJsonAction.do" ? "{\"portalForm\":{}}" : "" };
            };
            Throws<IOException>(() => Client(fake).LoadContext(), "Bootstrap rejects an unconfirmed controller identity");
            fake.Handler = u => {
                if (u.Host != PortalClient.Gateway) return new Reply { Status = 302,
                    Location = "http://10.20.16.5/portal/usertemp_computer/guangzhougs-pc-v2/logout.html?wlanuserip=10.2.3.4&wlanacname=CURRENT-AC&version=4&vlan=77&msg=OLD&selfTicket=OLD" };
                return new Reply { Status = 200, Body = u.AbsolutePath == "/PortalJsonAction.do" ? Config("10.2.3.4") : "" };
            };
            context = Client(fake).LoadContext();
            Assert(context.Page.AbsolutePath == "/portal.do" && context.Query["version"] == "4" && context.Query["vlan"] == "77",
                "Logout redirect is converted to a login page while retaining fresh connection parameters");
            Assert(!context.Query.ContainsKey("msg") && !context.Query.ContainsKey("selfTicket"), "Logout messages and session tickets are not reused for login");
            Assert(!PortalClient.IsPortalEntry(new Uri("http://10.20.16.5.evil.example/portal.do")) &&
                !PortalClient.IsPortalEntry(new Uri("http://10.20.16.5:8080/portal.do")), "Portal discovery rejects lookalike hosts and unexpected ports");
            fake.Handler = u => {
                if (u.Scheme == "http") throw new IOException("no redirect");
                return new Reply { Status = 200, Body = u.AbsolutePath == "/PortalJsonAction.do" ? Config("10.2.3.4") : "" };
            };
            context = Client(fake).LoadContext();
            Assert(PortalClient.ParseQuery(context.Page)["wlanuserip"] == "10.2.3.4" &&
                PortalClient.ParseQuery(context.Page)["wlanacname"] == "CURRENT-AC", "Bare portal fallback populates the CAS button's page query");
            Assert(PortalClient.ParseQuery(context.Page)["mac"] == "aa:bb:cc:dd:ee:ff", "Fresh config MAC reaches the CAS service callback");
            fake.Handler = u => {
                if (u.Scheme == "http") return new Reply { Status = 302, Location = "https://net.gzgs.edu.cn/portal.do?wlanuserip=10.9.9.9" };
                return new Reply { Status = 200, Body = u.AbsolutePath == "/PortalJsonAction.do" ? Config("10.9.9.9") : "" };
            };
            Throws<IOException>(() => Client(fake).LoadContext(), "Stale client IP is rejected");
            fake.Handler = u => {
                if (u.Scheme == "http") throw new IOException("no redirect");
                return new Reply { Status = 200, Body = u.AbsolutePath == "/PortalJsonAction.do" ? Config("218.1.2.3") : "" };
            };
            Throws<IOException>(() => Client(fake).LoadContext(), "Public portal on another network is rejected");
            var absent = new PortalClient(fake, () => new HashSet<string>());
            fake.Requests.Clear();
            Throws<IOException>(() => absent.LoadContext(), "Disconnected adapters skip authentication");
            Assert(fake.Requests.Count == 0, "No portal requests occur without a connected adapter");
            Assert(BrowserLogin.TrustedSchoolPage("https://auth.gzgs.edu.cn/authserver/login"), "School HTTPS subdomain is trusted");
            Assert(!BrowserLogin.TrustedSchoolPage("https://auth.gzgs.edu.cn.evil.example/login") &&
                !BrowserLogin.TrustedSchoolPage("http://auth.gzgs.edu.cn/login") &&
                !BrowserLogin.TrustedSchoolPage("https://user:secret@auth.gzgs.edu.cn/login"), "Lookalike, plaintext and userinfo URLs are rejected");
            Assert(BrowserLogin.IsPortalCallback("http://10.20.16.5/portalCasAuth.do?ticket=TEST") &&
                !BrowserLogin.IsPortalCallback("http://10.20.16.5.evil.example/portalCasAuth.do"), "Known internal CAS callback is accepted without trusting lookalikes");
        }

        private static void AuthenticationTests()
        {
            var calls = new List<string>();
            Action temp = () => calls.Add("temporary"), cas = () => calls.Add("unified");
            Action check = () => { };
            Action<string> status = s => calls.Add("status");
            string result = AuthenticationFlow.Run(true, temp, cas, () => true, check, status);
            Assert(result == "temporary" && !calls.Contains("unified"), "Verified temporary login skips unified authentication");
            calls.Clear(); int verifies = 0;
            result = AuthenticationFlow.Run(true, temp, cas, () => ++verifies == 2, check, status);
            Assert(result == "unified" && calls.Count(c => c == "temporary") == 1 && calls.Count(c => c == "unified") == 1,
                "A temporary success page without internet falls back to unified login exactly once");
            calls.Clear();
            result = AuthenticationFlow.Run(true, () => { throw new IOException("temporary rejected"); }, cas, () => true, check, status);
            Assert(result == "unified" && calls.Contains("unified"), "Temporary request failure still attempts unified authentication");
            result = AuthenticationFlow.Run(true, () => { throw new ManualActionException("temporary captcha"); }, cas, () => true, check, status);
            Assert(result == "unified", "Temporary verification requirements can fall back to unified authentication");
            calls.Clear();
            result = AuthenticationFlow.Run(false, temp, cas, () => true, check, status);
            Assert(result == "unified" && !calls.Contains("temporary"), "Unified-only mode never submits temporary credentials");
            calls.Clear();
            Throws<OperationCanceledException>(() => AuthenticationFlow.Run(true, () => { throw new OperationCanceledException(); }, cas,
                () => true, check, status), "Cancelling temporary login propagates cancellation");
            Assert(!calls.Contains("unified"), "Cancellation does not trigger fallback login");
            bool cancelled = false;
            Throws<OperationCanceledException>(() => AuthenticationFlow.Run(true, () => { cancelled = true; throw new IOException(); }, cas,
                () => true, () => { if (cancelled) throw new OperationCanceledException(); }, status), "Cancellation after a temporary failure stops fallback");
            Throws<IOException>(() => AuthenticationFlow.Run(true, temp, cas, () => false, check, status),
                "Neither login can report success without internet access");
            Throws<ManualActionException>(() => AuthenticationFlow.Run(true, () => { throw new IOException(); },
                () => { throw new ManualActionException("CAS captcha"); }, () => false, check, status), "Unified verification requirements retain the normal pause behavior");
            var temporaryAccount = new LoginAccount("temporary-account", "temporary-secret");
            var unifiedAccount = new LoginAccount("school-account", "school-secret");
            LoginAccount temporaryReceived = null, unifiedReceived = null;
            result = AuthenticationFlow.Run(true, temporaryAccount, unifiedAccount,
                account => { temporaryReceived = account; throw new IOException(); }, account => unifiedReceived = account,
                () => true, check, status);
            Assert(result == "unified" && temporaryReceived.Username == "temporary-account" && temporaryReceived.Password == "temporary-secret" &&
                unifiedReceived.Username == "school-account" && unifiedReceived.Password == "school-secret", "Fallback dispatches each login's own independent credentials");
            temporaryReceived = null;
            result = AuthenticationFlow.Run(true, new LoginAccount("", ""), unifiedAccount,
                account => temporaryReceived = account, account => unifiedReceived = account, () => true, check, status);
            Assert(result == "unified" && temporaryReceived == null, "An empty temporary account goes directly to unified login without reusing its credentials");
            Throws<ManualActionException>(() => AuthenticationFlow.Run(true, temporaryAccount, new LoginAccount("", ""),
                account => { }, account => { }, () => true, check, status), "Unified fallback credentials must be configured before authentication");
        }

        private static void SettingsTests()
        {
            string password = "Test-only!中文+&%";
            string encrypted = SettingsStore.Protect(password);
            Assert(encrypted != password && SettingsStore.Unprotect(encrypted) == password, "DPAPI round-trip preserves password");
            var store = new SettingsStore(Path.Combine(root, "settings-test"));
            string temporaryPassword = "Temporary-only!Different+密码";
            var settings = new Settings { Username = "test-only-student", ProtectedPassword = encrypted, AuthPaused = true,
                TemporaryUsername = "test-only-temporary", ProtectedTemporaryPassword = SettingsStore.Protect(temporaryPassword) };
            store.Save(settings);
            settings.IntervalSeconds = 45; store.Save(settings);
            var loaded = store.Load();
            Assert(loaded.IntervalSeconds == 45 && loaded.AuthPaused && SettingsStore.Unprotect(loaded.ProtectedPassword) == password,
                "Atomic settings replacement preserves pause and encrypted credentials");
            Assert(!File.ReadAllText(Path.Combine(store.DirectoryPath, "settings.json")).Contains(password), "Settings do not store plaintext password");
            Assert(loaded.TemporaryUsername == "test-only-temporary" && SettingsStore.Unprotect(loaded.ProtectedTemporaryPassword) == temporaryPassword &&
                SettingsStore.Unprotect(loaded.ProtectedPassword) == password, "Two separate account/password pairs survive encrypted storage");
            Assert(!File.ReadAllText(Path.Combine(store.DirectoryPath, "settings.json")).Contains(temporaryPassword), "Temporary passwords are not stored as plaintext");
            Assert(loaded.PreferTemporaryLogin, "Existing settings default to temporary-first authentication");
            loaded.PreferTemporaryLogin = false; store.Save(loaded);
            Assert(!store.Load().PreferTemporaryLogin, "Unified-only preference survives a settings round trip");
            var legacy = new JavaScriptSerializer().Deserialize<Settings>("{\"Username\":\"legacy\",\"ProtectedPassword\":" + new JavaScriptSerializer().Serialize(encrypted) + ",\"IntervalSeconds\":30}");
            Assert(legacy.PreferTemporaryLogin, "Older settings without the new option migrate to temporary-first mode");
            Assert(legacy.Username == "legacy" && SettingsStore.Unprotect(legacy.ProtectedPassword) == password &&
                legacy.TemporaryUsername == "" && legacy.ProtectedTemporaryPassword == "", "Migration keeps legacy CAS credentials and leaves temporary credentials empty");
            Assert(Startup.Command(@"D:\Some Folder\助手.exe") == "\"D:\\Some Folder\\助手.exe\" --background", "Startup command quotes paths with spaces");
        }

        private static void BrowserTests()
        {
            string browser = BrowserLogin.FindBrowser();
            Assert(browser != null, "Installed Edge or Chrome is found");
            string profile = Path.Combine(root, "test-browser-profile");
            Directory.CreateDirectory(profile);
            var active = Path.Combine(profile, "DevToolsActivePort");
            if (File.Exists(active)) File.Delete(active);
            var start = new ProcessStartInfo(browser, "--headless=new --remote-debugging-port=0 --remote-debugging-address=127.0.0.1 --user-data-dir=\"" +
                profile + "\" --no-first-run --no-default-browser-check --disable-extensions --disable-background-networking about:blank") {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (var process = Process.Start(start))
            {
                Cdp cdp = null;
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (!File.Exists(active) && DateTime.UtcNow < deadline) Thread.Sleep(200);
                    Assert(File.Exists(active), "Local headless browser starts");
                    int port = Int32.Parse(File.ReadAllLines(active)[0]);
                    var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/json/list");
                    request.Proxy = null; request.Timeout = 5000;
                    object[] pages;
                    using (var response = request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                        pages = (object[])new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd());
                    var page = pages.OfType<Dictionary<string, object>>().First(p => PortalClient.Text(p, "type") == "page");
                    cdp = new Cdp(new Uri(PortalClient.Text(page, "webSocketDebuggerUrl")));
                    cdp.Call("Page.enable", new { });
                    string fixture = "<form id='form' action='https://cas.gzgs.edu.cn/authserver/login'><input name='execution' type='hidden' value='fresh-token'><input id='username'><input id='password' type='password'>" +
                        "<button type='button' id='loginButton'>登录</button></form><script>window.inputCount=0;document.querySelector('#username').addEventListener('input',()=>window.inputCount++);" +
                        "document.querySelector('#loginButton').onclick=()=>{window.captured={user:document.querySelector('#username').value,password:'official-encrypted:'+document.querySelector('#password').value,token:document.querySelector('[name=execution]').value};};</script>";
                    SetPage(cdp, fixture);
                    string script = BrowserLogin.FillScript.Replace("__USER__", "'test-student'").Replace("__PASS__", "'test-secret'");
                    Assert(cdp.Eval(script) == "地址未受信任", "Live form guard prevents credentials on non-school pages");
                    // Test the rest of the exact script on a local-only fixture with a simulated trusted location.
                    string localScript = "(()=>{const location=new URL('https://cas.gzgs.edu.cn/authserver/login');return " + script + ";})()";
                    Assert(cdp.Eval(localScript) == "submitted", "CAS form is filled and native login click is triggered");
                    Assert(cdp.Eval("JSON.stringify(window.captured)").Contains("official-encrypted:test-secret"), "Website's own login handler performs password processing");
                    Assert(cdp.Eval("JSON.stringify(window.captured)").Contains("fresh-token"), "Hidden CAS token is preserved");
                    Assert(cdp.Eval("String(window.inputCount)") == "1", "Input event reaches website listeners");
                    cdp.Eval("document.querySelector('#form').insertAdjacentHTML('beforeend','<input type=password>');'ok'");
                    Assert(cdp.Eval(localScript) == "密码框不唯一", "Ambiguous password forms are not submitted");
                    SetPage(cdp, fixture + "<input id='captcha' placeholder='验证码'>");
                    var snapshot = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(cdp.Eval(BrowserLogin.SnapshotScript));
                    Assert(PortalClient.Text(snapshot, "captcha") == "True", "Visible verification code is detected");
                    SetPage(cdp, fixture + "<input id='captcha' style='display:none'>");
                    snapshot = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(cdp.Eval(BrowserLogin.SnapshotScript));
                    Assert(PortalClient.Text(snapshot, "captcha") == "False", "Hidden inactive captcha does not block login");
                    SetPage(cdp, fixture + "<a>短信验证码登录</a><p>二次验证使用说明，扫描二维码完成验证的帮助文档</p>");
                    snapshot = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(cdp.Eval(BrowserLogin.SnapshotScript));
                    Assert(PortalClient.Text(snapshot, "captcha") == "False", "SMS login tabs and verification help text do not falsely pause password login");
                    foreach (string inactive in new[] {
                        "<input id='captcha' style='visibility:hidden'>",
                        "<div style='opacity:0'><input id='captcha'></div>",
                        "<div aria-hidden='true'><input id='captcha'></div>",
                        "<input id='captcha' disabled>", "<input id='captcha' readonly>" })
                    {
                        SetPage(cdp, fixture + inactive);
                        snapshot = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(cdp.Eval(BrowserLogin.SnapshotScript));
                        Assert(PortalClient.Text(snapshot, "captcha") == "False", "Non-interactive verification controls do not falsely pause login");
                    }
                    SetPage(cdp, fixture + "<iframe src='about:blank#captcha'></iframe>");
                    snapshot = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(cdp.Eval(BrowserLogin.SnapshotScript));
                    Assert(PortalClient.Text(snapshot, "captcha") == "True" && PortalClient.Text(snapshot, "challengeSource") == "challenge-frame",
                        "An actual visible CAPTCHA frame still requires user verification");
                    SetPage(cdp, fixture + "<div role='alert'>请输入短信验证码</div>");
                    snapshot = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(cdp.Eval(BrowserLogin.SnapshotScript));
                    Assert(PortalClient.Text(snapshot, "captcha") == "True", "An explicit active verification error still requires user verification");
                    SetPage(cdp, fixture + "<button id='casAuth'>统一身份认证登录</button>");
                    Assert(cdp.Eval(localScript) == "仍在临时用户页面", "CAS credentials never go into temporary-user login");
                    ExactSchoolForm(cdp, localScript);
                    TemporarySchoolForm(cdp);
                    string observer = "(()=>{const location=new URL('http://10.20.16.5/portal.do');return " + BrowserLogin.AuthObserverScript + ";})()";
                    cdp.Eval(observer + ";'installed'");
                    cdp.Eval(@"(()=>{const xhr=new XMLHttpRequest();xhr.open('GET','http://10.20.16.5/quickauth.do');
                        Object.defineProperty(xhr,'responseText',{value:JSON.stringify({code:'1',message:'账号或密码错误 secret-must-not-appear'})});
                        xhr.dispatchEvent(new Event('load'));return 'recorded';})()");
                    string responseSummary = cdp.Eval("JSON.stringify(window.__ggsAuthResult)");
                    Assert(responseSummary.Contains("credentials") && responseSummary.Contains("1") && !responseSummary.Contains("secret-must-not-appear"),
                        "Temporary response diagnostics retain only the result code and safe failure category");
                    Assert(!BrowserLogin.TemporaryRejection("secret-must-not-appear", "credentials").Contains("secret-must-not-appear"),
                        "Untrusted response codes cannot put server text or credentials in the log");
                    Assert(cdp.Eval("alert('非法接入');'dismissed'") == "dismissed" && cdp.IllegalAccess && cdp.DialogOpened,
                        "Illegal-access native alerts are detected and dismissed without hanging the browser");
                    Assert(cdp.Eval("String(confirm('需要手动确认'))") == "false" && cdp.RequiresInteraction,
                        "Native confirmation dialogs are declined and flagged for manual handling");
                }
                finally
                {
                    if (cdp != null) { try { cdp.Call("Browser.close", new { }); } catch { } cdp.Dispose(); }
                    if (!process.WaitForExit(3000)) process.Kill();
                }
            }
        }
        private static void SetPage(Cdp cdp, string html)
        {
            string payload = new JavaScriptSerializer().Serialize(html);
            cdp.Eval("document.open();document.write(" + payload + ");document.close();'ok'");
        }
        private static void ExactSchoolForm(Cdp cdp, string localScript)
        {
            string fixtures = Path.Combine(Path.GetDirectoryName(root), "tests", "fixtures");
            var json = new JavaScriptSerializer();
            SetPage(cdp, File.ReadAllText(Path.Combine(fixtures, "cas-form.html"), Encoding.UTF8));
            cdp.Eval(File.ReadAllText(Path.Combine(fixtures, "jquery.js"), Encoding.UTF8) + ";'ok'");
            string casScript = File.ReadAllText(Path.Combine(fixtures, "cas-site.js"), Encoding.UTF8);
            // Only the fixture's location is substituted; production uses the real fresh service URL.
            casScript = casScript.Replace("self.document.location", "new URL('https://cas.gzgs.edu.cn/authserver/login?service=http%3A%2F%2F10.20.16.5%2FportalCasAuth.do%3Fwlanuserip%3D10.2.3.4')");
            cdp.Eval(casScript + ";disableEmptyInputFormSubmission();preserveAnchorTagOnForm();'ok'");
            cdp.Eval("document.querySelector('#fm1').addEventListener('submit',e=>{e.preventDefault();window.actualCaptured={action:e.target.action,user:document.querySelector('#username').value,pass:document.querySelector('#password').value,token:document.querySelector('[name=execution]').value};});'ok'");
            Assert(cdp.Eval(localScript) == "submitted", "Exact supplied CAS form and submit input are recognized");
            string captured = cdp.Eval("JSON.stringify(window.actualCaptured)");
            Assert(captured.Contains("test-student") && captured.Contains("test-secret"), "Actual school input-enable handlers receive filled credentials");
            Assert(captured.Contains("fresh-fixture-token") && captured.Contains("service=http%3A"), "Actual CAS handler retains fresh execution and service callback");
            var snapshot = json.Deserialize<Dictionary<string, object>>(cdp.Eval(BrowserLogin.SnapshotScript));
            Assert(PortalClient.Text(snapshot, "captcha") == "False", "School's hidden SMS controls do not falsely require verification");
            cdp.Eval("document.querySelector('#codeContent').style.display='block';'ok'");
            snapshot = json.Deserialize<Dictionary<string, object>>(cdp.Eval(BrowserLogin.SnapshotScript));
            Assert(PortalClient.Text(snapshot, "captcha") == "True", "School's active SMS verification pauses unattended login");
            cdp.Eval("document.querySelector('#fm1').action='https://example.com/collect';'ok'");
            Assert(cdp.Eval(localScript) == "表单提交地址不正确", "Unexpected form destination cannot receive credentials");
        }
        private static void TemporarySchoolForm(Cdp cdp)
        {
            string fixtures = Path.Combine(Path.GetDirectoryName(root), "tests", "fixtures");
            string fixture = File.ReadAllText(Path.Combine(fixtures, "temporary-form.html"), Encoding.UTF8);
            SetPage(cdp, fixture);
            cdp.Eval(File.ReadAllText(Path.Combine(fixtures, "jquery.js"), Encoding.UTF8) + ";'ok'");
            cdp.Eval(File.ReadAllText(Path.Combine(fixtures, "temporary-site.js"), Encoding.UTF8) + ";'ok'");
            // All network calls are replaced by an in-memory capture. Use the school's exact checkSubmit/encryptParam.
            cdp.Eval(@"window.portalconfig={username:'#userid',password:'#passwd',submit:'#loginsubmit',read:'#read',validateCode:'#validateCode',
                portaltype:'#portaltype',portaltypeDefaultVal:'0',validateType:'0',authByRas:true,rsa:{modulus:'fixture-key',publicExponent:'fixture-exponent'},
                portalForm:{wlanuserip:'10.2.3.4',wlanacname:'CURRENT-AC',vlan:'77',mac:'aa:bb:cc:dd:ee:ff'},serverForm:{portalVer:'4',serverip:'10.20.16.5'},
                portalconfig:{id:3,timestamp:'fresh-timestamp',uuid:'fresh-uuid'}};
                window.local={readProtocol:'请同意协议'};window.tryrelogintime=0;window.setMaxDigits=()=>{};
                window.RSAKeyPair=function(){};window.encryptedString=(key,text)=>'official-encrypted:'+text;
                window.showLoading=window.hideLoading=window.debug=()=>{};
                window._alert=msg=>{$('#_alert_msg').text(msg).show();};
                $.ajax=options=>{window.temporaryCaptured={url:options.url,data:options.data};options.success({code:'1',message:'密码错误'});};
                $('#loginsubmit').on('click',checkSubmit);'ok'");
            string script = BrowserLogin.TemporaryFillScript.Replace("__USER__", "'test-student'").Replace("__PASS__", "'test-secret'");
            Assert(cdp.Eval(script) == "地址未受信任", "Temporary credentials cannot be filled into an untrusted live origin");
            string localScript = "(()=>{const location=new URL('http://10.20.16.5/portal.do?wlanuserip=10.2.3.4&wlanacname=CURRENT-AC&vlan=77');return " + script + ";})()";
            // A real page's empty form action resolves against its own trusted location.
            cdp.Eval("document.querySelector('form').action='http://10.20.16.5/portal.do';'ok'");
            Assert(cdp.Eval(localScript) == "submitted", "Official temporary form and native login handler are supported");
            string captured = cdp.Eval("JSON.stringify(window.temporaryCaptured)");
            Assert(captured.Contains("/quickauth.do") && captured.Contains("official-encrypted:test-student") && captured.Contains("official-encrypted:test-secret"),
                "Temporary login uses official request construction and RSA processing");
            Assert(captured.Contains("fresh-uuid") && captured.Contains("fresh-timestamp") && captured.Contains("10.2.3.4") && captured.Contains("aa:bb:cc:dd:ee:ff"),
                "Temporary login retains current UUID, timestamp, client IP and MAC");
            Assert(cdp.Eval("String(document.querySelector('#read').checked)") == "true", "Temporary login checks the required usage agreement");
            var snapshot = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(cdp.Eval(BrowserLogin.SnapshotScript));
            Assert(PortalClient.Text(snapshot, "rejected") == "True" && PortalClient.Text(snapshot, "notice") == "True",
                "Official inline login failures are detected for fallback");
            cdp.Eval("portalconfig.portalForm.wlanuserip='10.9.9.9';'ok'");
            Assert(cdp.Eval(localScript) == "接入参数不匹配", "Temporary submission rejects a changed client IP");
            cdp.Eval("portalconfig.portalForm.wlanuserip='10.2.3.4';document.querySelector('form').action='https://example.com/collect';'ok'");
            Assert(cdp.Eval(localScript) == "表单提交地址不正确", "Temporary submission rejects a foreign form action");
            string lookalikeScript = localScript.Replace("http://10.20.16.5/portal.do?", "http://10.20.16.5.evil.example/portal.do?");
            Assert(cdp.Eval(lookalikeScript) == "地址未受信任", "Temporary form rejects gateway lookalike hosts");
            string plaintextSchoolScript = localScript.Replace("http://10.20.16.5/portal.do?", "http://net.gzgs.edu.cn/portal.do?");
            Assert(cdp.Eval(plaintextSchoolScript) == "地址未受信任", "Temporary form requires HTTPS for the public school hostname");
            SetPage(cdp, "<h1>认证成功</h1><button id='logoutsubmit'>下线</button>");
            string successSnapshot = "(()=>{const location=new URL('http://10.20.16.5/portal/usertemp_computer/guangzhougs-pc-v2/logout.html');return " + BrowserLogin.SnapshotScript + ";})()";
            snapshot = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(cdp.Eval(successSnapshot));
            Assert(PortalClient.Text(snapshot, "success") == "True", "Logout page starts internet verification without clicking the disconnect button");
        }

        private static void Preview()
        {
            Application.EnableVisualStyles();
            using (var configured = new MainForm(new SettingsStore(Path.Combine(root, "preview-settings")),
                new Settings { Username = "CAS-test-account", ProtectedPassword = SettingsStore.Protect("CAS-test-secret"),
                    TemporaryUsername = "temporary-test-account", ProtectedTemporaryPassword = SettingsStore.Protect("temporary-test-secret"), Enabled = false }, false))
            {
                var groups = Descendants(configured).OfType<GroupBox>().ToArray();
                Assert(groups.Length == 2 && Descendants(groups[0]).OfType<TextBox>().Any(t => t.Text == "temporary-test-account") &&
                    Descendants(groups[1]).OfType<TextBox>().Any(t => t.Text == "CAS-test-account"), "UI loads separate accounts into the correct login groups");
                Assert(Descendants(groups[0]).OfType<TextBox>().Any(t => t.UseSystemPasswordChar && t.Text == "temporary-test-secret") &&
                    Descendants(groups[1]).OfType<TextBox>().Any(t => t.UseSystemPasswordChar && t.Text == "CAS-test-secret"), "UI loads and masks both independent saved passwords");
            }
            using (var form = new MainForm(new SettingsStore(Path.Combine(root, "preview-settings")), new Settings(), false))
            {
                form.Show();
                form.Update();
                Application.DoEvents();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(Path.Combine(root, "preview.png"));
                }
                Assert(Descendants(form).Count() > 15, "Settings form renders successfully");
                var detail = (Label)form.Controls.Find("detail", true)[0];
                detail.Text = "校园网配置未返回客户端 IP，且未能从当前校园网网卡补全参数，请检查校园网连接。请打开认证页查看是否需要额外验证，处理后点击立即检查。300 秒后重试。";
                form.ClientSize = new Size(560, 520);
                form.PerformLayout(); Application.DoEvents();
                Assert(detail.Height > detail.Font.Height * 2 && detail.Height >= detail.GetPreferredSize(new Size(detail.Width, 0)).Height,
                    "Long error details grow to multiple complete lines in a narrow window");
                var scroll = (Panel)form.Controls[0];
                Assert(scroll.VerticalScroll.Visible && scroll.DisplayRectangle.Height > scroll.ClientSize.Height,
                    "A short window scrolls to content instead of clipping it");
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(Path.Combine(root, "preview-narrow.png"));
                }
                var controls = Descendants(form).ToArray();
                var fonts = controls.Select(c => c.Font).ToArray();
                for (int i = 0; i < controls.Length; i++) controls[i].Font = new Font(fonts[i].FontFamily, fonts[i].Size * 1.5F, fonts[i].Style);
                form.ClientSize = new Size(800, 960); form.PerformLayout(); Application.DoEvents();
                Assert(detail.Height >= detail.GetPreferredSize(new Size(detail.Width, 0)).Height,
                    "Larger text keeps the entire error detail visible");
                Assert(Descendants(form).OfType<GroupBox>().All(g => g.ClientSize.Width >= TextRenderer.MeasureText(g.Text, g.Font).Width),
                    "Account groups reflow so enlarged captions are never truncated");
                scroll.AutoScrollPosition = Point.Empty;
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(Path.Combine(root, "preview-large.png"));
                }
            }
        }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                yield return control;
                foreach (Control child in Descendants(control)) yield return child;
            }
        }
    }
}
