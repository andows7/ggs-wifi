using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace GgsWifi
{
    public sealed class BrowserLogin
    {
        private readonly string profile;
        private readonly Action<string> status;
        private readonly Func<bool> cancelled;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public BrowserLogin(string profile, Action<string> status, Func<bool> cancelled)
        { this.profile = profile; this.status = status; this.cancelled = cancelled; }

        public static string FindBrowser()
        {
            var candidates = new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        public static bool TrustedSchoolPage(string address)
        {
            Uri uri;
            return Uri.TryCreate(address, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
                uri.IsDefaultPort && String.IsNullOrEmpty(uri.UserInfo) &&
                (uri.Host.Equals("gzgs.edu.cn", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.EndsWith(".gzgs.edu.cn", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsPortalCallback(string address)
        {
            Uri uri;
            return Uri.TryCreate(address, UriKind.Absolute, out uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https") && uri.IsDefaultPort &&
                String.IsNullOrEmpty(uri.UserInfo) && (uri.Host == PortalClient.Gateway || uri.Host == PortalClient.Host);
        }

        public void Login(Uri portal, string username, string password)
        { LoginCore(portal, username, password, false); }

        public void LoginTemporary(Uri portal, string username, string password)
        { LoginCore(portal, username, password, true); }

        public void Inspect(Uri portal, Action<string> diagnostic)
        {
            LoginCore(portal, "", "", false, cdp => {
                string previous = ""; bool clicked = false;
                var until = DateTime.UtcNow.AddSeconds(35);
                while (DateTime.UtcNow < until)
                {
                    CheckCancelled(); Thread.Sleep(600);
                    try
                    {
                        var snapshot = Json.Deserialize<Dictionary<string, object>>(cdp.Eval(SnapshotScript));
                        string url = PortalClient.Text(snapshot, "url");
                        Uri page;
                        string safeUrl = Uri.TryCreate(url, UriKind.Absolute, out page) ? page.GetLeftPart(UriPartial.Path) : "unknown";
                        string summary = safeUrl + " ready=" + PortalClient.Text(snapshot, "ready") + " captcha=" + PortalClient.Text(snapshot, "captcha") +
                            " cas=" + PortalClient.Text(snapshot, "cas") + " password=" + PortalClient.Text(snapshot, "password");
                        string candidates = cdp.Eval(@"JSON.stringify({inputs:[...document.querySelectorAll('input')].filter(e=>/captcha|validatecode|verifycode|verification|otp|smscode|动态码|验证码/i.test((e.name||'')+' '+e.id+' '+e.placeholder)).map(e=>({id:e.id,name:e.name,type:e.type,display:getComputedStyle(e).display,visibility:getComputedStyle(e).visibility,hasBox:!!(e.offsetWidth||e.offsetHeight||e.getClientRects().length)})),textMatches:['短信验证码','二次验证','扫描二维码完成验证'].filter(t=>(document.body?document.body.innerText:'').includes(t))})");
                        if (summary + candidates != previous) { diagnostic(summary + " " + candidates); previous = summary + candidates; }
                        if (!clicked && PortalClient.Text(snapshot, "ready") == "complete" && PortalClient.Text(snapshot, "cas") == "True")
                        { cdp.Eval("document.querySelector('#casAuth').click();'clicked'"); clicked = true; until = DateTime.UtcNow.AddSeconds(40); }
                    }
                    catch (IOException) { }
                }
            });
        }

        private void LoginCore(Uri portal, string username, string password, bool temporary, Action<Cdp> inspect = null)
        {
            if (!PortalClient.IsPortal(portal) || (portal.Scheme != "https" && portal.Host != PortalClient.Gateway))
                throw new IOException("认证入口不正确。");
            var browser = FindBrowser();
            if (browser == null) throw new ManualActionException("没有找到 Edge 或 Chrome，请安装其中一个浏览器后恢复监测。");
            Directory.CreateDirectory(profile);
            string activePort = Path.Combine(profile, "DevToolsActivePort");
            if (File.Exists(activePort)) File.Delete(activePort);
            // Dedicated profile: never attach to or change the user's normal browser session.
            var start = new ProcessStartInfo(browser,
                "--headless=new --remote-debugging-address=127.0.0.1 --remote-debugging-port=0 " +
                "--user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check " +
                "--disable-extensions --disable-sync --disable-background-networking --no-proxy-server about:blank");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            using (var process = Process.Start(start))
            {
                Cdp cdp = null;
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (!File.Exists(activePort) && DateTime.UtcNow < deadline)
                    { CheckCancelled(); Thread.Sleep(200); }
                    if (!File.Exists(activePort)) throw new IOException("后台浏览器未能启动，请确认 Edge/Chrome 可以正常打开。");
                    var lines = File.ReadAllLines(activePort);
                    int port;
                    if (lines.Length == 0 || !Int32.TryParse(lines[0], out port) || port < 1 || port > 65535)
                        throw new IOException("后台浏览器连接失败。");
                    var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/json/list");
                    request.Proxy = null; request.Timeout = 5000; request.AllowAutoRedirect = false;
                    object[] targets;
                    using (var response = request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                        targets = Json.DeserializeObject(reader.ReadToEnd()) as object[];
                    var page = targets == null ? null : targets.OfType<Dictionary<string, object>>()
                        .FirstOrDefault(t => PortalClient.Text(t, "type") == "page");
                    if (page == null) throw new IOException("无法连接后台认证标签页。");
                    var socketUri = new Uri(PortalClient.Text(page, "webSocketDebuggerUrl"));
                    if (socketUri.Scheme != "ws" || socketUri.Host != "127.0.0.1" || socketUri.Port != port)
                        throw new IOException("后台浏览器地址无效。");
                    cdp = new Cdp(socketUri);
                    cdp.Call("Page.enable", new { });
                    cdp.Call("Page.addScriptToEvaluateOnNewDocument", new { source = AuthObserverScript });
                    cdp.Call("Page.navigate", new { url = portal.AbsoluteUri });
                    if (inspect != null) { inspect(cdp); return; }
                    if (temporary)
                    {
                        LoginTemporaryPage(cdp, username, password);
                        return;
                    }
                    status("正在打开统一身份认证…");
                    bool clickedCas = false;
                    bool submitted = false;
                    DateTime submittedAt = DateTime.MinValue;
                    deadline = DateTime.UtcNow.AddSeconds(75);
                    while (DateTime.UtcNow < deadline)
                    {
                        CheckCancelled();
                        Thread.Sleep(500);
                        Dictionary<string, object> state;
                        try { state = Json.DeserializeObject(cdp.Eval(SnapshotScript)) as Dictionary<string, object>; }
                        catch (IOException) { continue; } // Execution context can disappear during navigation.
                        if (state == null) continue;
                        string url = PortalClient.Text(state, "url");
                        if (PortalClient.Text(state, "ready") != "complete") continue;
                        if (cdp.IllegalAccess || PortalClient.Text(state, "illegal") == "True")
                            throw new IOException("校园网提示非法接入，请重新获取当前网络的认证参数。");
                        if (cdp.RequiresInteraction)
                            throw new ManualActionException("认证页面需要手动确认，请打开官方认证页处理后恢复监测。");
                        if (cdp.Rejected || (submitted && PortalClient.Text(state, "rejected") == "True"))
                            throw new ManualActionException("统一认证未通过，请核对学号和密码或到官方页面查看提示，已暂停重试。");
                        // The supplied CAS service returns to the campus gateway over HTTP.
                        // No credentials are inserted there; only the final connectivity check follows.
                        if (clickedCas && PortalClient.Text(state, "cas") != "True" &&
                            PortalClient.Text(state, "password") != "True" && IsPortalCallback(url)) return;
                        // All credential insertion is guarded again inside the same JS invocation.
                        if (!TrustedSchoolPage(url) && !IsPortalCallback(url))
                        {
                            if (url == "about:blank") continue;
                            throw new ManualActionException("统一认证跳转到了尚未适配的地址，请打开官方页面确认，并提供登录页网址。");
                        }
                        if (PortalClient.Text(state, "captcha") == "True")
                            throw new ManualActionException("统一认证页显示了需要操作的验证码控件，请打开认证页完成验证，再恢复监测。");
                        if (!clickedCas && PortalClient.Text(state, "cas") == "True")
                        {
                            cdp.Eval("document.querySelector('#casAuth').click(); 'clicked'");
                            clickedCas = true;
                            continue;
                        }
                        if (!submitted && PortalClient.Text(state, "password") == "True" &&
                            PortalClient.Text(state, "cas") != "True")
                        {
                            if (PortalClient.Text(state, "ready") != "complete") continue;
                            string script = FillScript.Replace("__USER__", Json.Serialize(username)).Replace("__PASS__", Json.Serialize(password));
                            string outcome = cdp.Eval(script);
                            if (outcome != "submitted")
                                throw new ManualActionException("未识别统一认证表单（" + outcome + "），请提供该登录页源码以便适配。");
                            submitted = true; submittedAt = DateTime.UtcNow;
                            status("已提交统一认证，正在确认结果…");
                            continue;
                        }
                        // Network verification is authoritative and happens in the monitor afterwards.
                        if ((submitted || clickedCas) && url.IndexOf("/portalCasAuth", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            PortalClient.Text(state, "password") != "True" && DateTime.UtcNow > submittedAt.AddSeconds(3)) return;
                        if (PortalClient.Text(state, "rejected") == "True" && submitted)
                            throw new ManualActionException("统一认证未通过，请核对学号和密码或到官方页面查看提示，已暂停重试。");
                        if (submitted && DateTime.UtcNow > submittedAt.AddSeconds(10))
                        {
                            if (PortalClient.Text(state, "password") == "True")
                                throw new ManualActionException("统一认证仍停留在登录页，请在官方页面检查账号、验证码或额外提示，已暂停重试。");
                            return;
                        }
                        // Existing SSO cookies may authenticate without presenting a password form.
                        if (clickedCas && !submitted && new Uri(url).Host == PortalClient.Host &&
                            PortalClient.Text(state, "cas") != "True" && PortalClient.Text(state, "password") != "True") return;
                    }
                    throw new ManualActionException("统一认证页面等待超时，请打开官方页面确认是否需要验证码，或提供该页面源码。");
                }
                finally
                {
                    if (cdp != null)
                    {
                        try { cdp.Call("Browser.close", new { }); } catch { }
                        cdp.Dispose();
                    }
                    // Only terminate the exact browser root process we created.
                    try { if (!process.WaitForExit(3000)) process.Kill(); } catch (InvalidOperationException) { }
                }
            }
        }

        private void LoginTemporaryPage(Cdp cdp, string username, string password)
        {
            bool submitted = false;
            DateTime submittedAt = DateTime.MinValue;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                CheckCancelled();
                Thread.Sleep(400);
                Dictionary<string, object> state;
                try { state = Json.DeserializeObject(cdp.Eval(SnapshotScript)) as Dictionary<string, object>; }
                catch (IOException) { continue; }
                if (state == null) continue;
                string url = PortalClient.Text(state, "url");
                if (url == "about:blank") continue;
                if (PortalClient.Text(state, "ready") != "complete") continue;
                if (!IsPortalCallback(url)) throw new IOException("临时登录跳转地址不正确。");
                string authCode = PortalClient.Text(state, "authCode");
                if (submitted && !String.IsNullOrEmpty(authCode) && authCode != "0")
                    throw new IOException(TemporaryRejection(authCode, PortalClient.Text(state, "authReason")));
                if (cdp.DialogOpened || PortalClient.Text(state, "illegal") == "True" ||
                    PortalClient.Text(state, "captcha") == "True" ||
                    (submitted && (PortalClient.Text(state, "rejected") == "True" || PortalClient.Text(state, "notice") == "True")))
                    throw new IOException("临时用户登录未通过或需要人工验证。");
                // A success page is only a signal to start independent internet verification.
                if (PortalClient.Text(state, "success") == "True")
                { status("临时登录已到达学校成功页，正在检测互联网…"); return; }
                if (!submitted && PortalClient.Text(state, "ready") == "complete")
                {
                    string script = TemporaryFillScript.Replace("__USER__", Json.Serialize(username)).Replace("__PASS__", Json.Serialize(password));
                    string outcome = cdp.Eval(script);
                    if (outcome == "waiting") continue;
                    if (outcome != "submitted") throw new IOException("临时用户登录表单不可用。");
                    submitted = true; submittedAt = DateTime.UtcNow;
                    status("已提交临时用户登录，正在确认结果…");
                }
                if (submitted && DateTime.UtcNow > submittedAt.AddSeconds(10))
                    throw new IOException("临时用户登录未能完成。");
            }
            throw new IOException("临时用户登录等待超时。");
        }

        private void CheckCancelled() { if (cancelled()) throw new OperationCanceledException(); }

        internal static string TemporaryRejection(string code, string reason)
        {
            string description = reason == "credentials" ? "临时账号或密码被学校拒绝" :
                reason == "verification" ? "临时登录要求额外验证" :
                reason == "binding" ? "临时账号需要绑定或选择运营商服务" : "临时用户登录被学校拒绝";
            int number;
            return description + (Int32.TryParse(code, out number) ? "（代码 " + number + "）。" : "（返回格式异常）。");
        }

        internal const string AuthObserverScript = @"(()=>{
            if(window.__ggsAuthObserverInstalled)return;
            window.__ggsAuthObserverInstalled=true;
            const original=XMLHttpRequest.prototype.open;
            XMLHttpRequest.prototype.open=function(method,url){
                let target;
                try{target=new URL(url,location.href);}catch(e){}
                if(target && target.origin===location.origin && target.pathname==='/quickauth.do'){
                    this.addEventListener('load',()=>{
                        try{
                            const data=JSON.parse(this.responseText),code=String(data.code);
                            const message=String(data.message||'');
                            const reason=/密码|账号|用户不存在/.test(message)?'credentials':/验证码|二次验证|动态码/.test(message)?'verification':/绑定|运营商|选择服务/.test(message)?'binding':'rejected';
                            window.__ggsAuthResult={code:/^-?\d{1,6}$/.test(code)?code:'unknown',reason:reason};
                        }catch(e){window.__ggsAuthResult={code:'unknown',reason:'rejected'};}
                    });
                }
                return original.apply(this,arguments);
            };
        })()";

        internal const string SnapshotScript = @"JSON.stringify((()=>{
            const visible=e=>{
                if(!e || !e.isConnected || !(e.offsetWidth||e.offsetHeight||e.getClientRects().length))return false;
                for(let n=e;n && n.nodeType===1;n=n.parentElement){
                    const s=getComputedStyle(n);
                    if(n.hidden || n.getAttribute('aria-hidden')==='true' || s.display==='none' || s.visibility==='hidden' || s.visibility==='collapse' || Number(s.opacity)===0)return false;
                }
                return true;
            };
            const text=(document.body?document.body.innerText:'').slice(0,16000);
            const activeForm=document.querySelector('#fm1')||document.querySelector('form[name=loginportalform]')||document;
            const codeInput=[...activeForm.querySelectorAll('input')].some(e=>visible(e)&&!e.disabled&&!e.readOnly&&/^(text|tel|number|password)$/.test(e.type)&&(
                /captcha|validate.?code|verify.?code|verification.?code|sms.?code|^otp$|^codeVal$/i.test(e.name||'') ||
                /captcha|validate.?code|verify.?code|verification.?code|sms.?code|^otp$|^codeVal$/i.test(e.id||'') ||
                /验证码|动态码|verification code|one.time code/i.test(e.placeholder||'') || e.autocomplete==='one-time-code'));
            const challengeFrame=[...document.querySelectorAll('iframe')].some(e=>visible(e)&&/captcha|recaptcha|hcaptcha|geetest|yidun/i.test(e.src));
            const challengeWidget=[...document.querySelectorAll('.geetest_panel,.yidun_modal,#nc_1_n1z,[data-captcha-active=true]')].some(visible);
            const challengeError=[...document.querySelectorAll('.alert-danger,.error,#msg,#errorMsg,[role=alert]')].some(e=>visible(e)&&/请输入.{0,8}验证码|请完成.{0,8}(验证|滑动)|必须.{0,8}二次验证/.test(e.innerText||''));
            // Merely mentioning SMS verification, a login tab or help text is not an active challenge.
            const challengeSource=codeInput?'code-input':challengeFrame?'challenge-frame':challengeWidget?'challenge-widget':challengeError?'verification-error':'';
            const challenge=document.readyState==='complete' && !!challengeSource;
            return {url:location.href,ready:document.readyState,cas:visible(document.querySelector('#casAuth')),
              password:[...document.querySelectorAll('input[type=password]')].some(visible),captcha:challenge,challengeSource:challengeSource,
              authCode:window.__ggsAuthResult?window.__ggsAuthResult.code:'',authReason:window.__ggsAuthResult?window.__ggsAuthResult.reason:'',
              illegal:/非法接入/.test(text),notice:visible(document.querySelector('#_alert_msg')) && !!document.querySelector('#_alert_msg').innerText.trim(),
              success:/\/logout\.html$/i.test(location.pathname) && visible(document.querySelector('#logoutsubmit')),
              rejected:/账号或密码.{0,5}(错误|不正确)|用户名或密码.{0,5}(错误|不正确)|密码错误|账户.{0,5}锁定|账号.{0,5}锁定/.test(text)};
        })())";

        internal const string TemporaryFillScript = @"(()=>{
            const gateway=location.hostname==='10.20.16.5' && /^(http:|https:)$/.test(location.protocol);
            const school=location.hostname==='net.gzgs.edu.cn' && location.protocol==='https:';
            if((!gateway && !school) || location.username || location.password || (location.port && location.port!==(location.protocol==='https:'?'443':'80')) || location.pathname!=='/portal.do') return '地址未受信任';
            const visible=e=>!!e && !!(e.offsetWidth||e.offsetHeight||e.getClientRects().length);
            const form=document.querySelector('form[name=loginportalform]');
            if(!form)return 'waiting';
            const action=new URL(form.action||location.href,location.href);
            if(action.origin!==location.origin || action.pathname!=='/portal.do' || action.username || action.password)return '表单提交地址不正确';
            const user=form.querySelector('#userid'),pass=form.querySelector('#passwd'),submit=form.querySelector('#loginsubmit'),read=form.querySelector('#read');
            if(!visible(user)||!visible(pass)||pass.type!=='password'||!visible(submit))return 'waiting';
            if(typeof portalconfig==='undefined'||!portalconfig.portalForm||!portalconfig.portalconfig||typeof checkSubmit!=='function')return 'waiting';
            if(portalconfig.username!=='#userid'||portalconfig.password!=='#passwd'||portalconfig.submit!=='#loginsubmit')return '表单配置不正确';
            const query=new URLSearchParams(location.search),ip=query.get('wlanuserip'),ac=query.get('wlanacname');
            if(!ip||!ac||(portalconfig.portalForm.wlanuserip && portalconfig.portalForm.wlanuserip!==ip)||(portalconfig.portalForm.wlanacname && portalconfig.portalForm.wlanacname!==ac))return '接入参数不匹配';
            if(submit.disabled)return 'waiting';
            const set=(el,v)=>{Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,v);el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));};
            set(user,__USER__);set(pass,__PASS__);
            if(read && !read.checked)read.click();
            submit.click();return 'submitted';
        })()";

        internal const string FillScript = @"(()=>{
            if(location.protocol!=='https:' || location.hostname!=='cas.gzgs.edu.cn' || location.pathname!=='/authserver/login' || (location.port && location.port!=='443')) return '地址未受信任';
            const visible=e=>!!e && !!(e.offsetWidth||e.offsetHeight||e.getClientRects().length);
            if(visible(document.querySelector('#casAuth')))return '仍在临时用户页面';
            const passwords=[...document.querySelectorAll('input[type=password]')].filter(visible);
            if(passwords.length!==1)return '密码框不唯一';
            const pass=passwords[0], form=pass.closest('form')||document;
            const action=new URL(form.action||location.href,location.href);
            if(action.protocol!=='https:' || action.hostname!=='cas.gzgs.edu.cn' || action.pathname!=='/authserver/login' || (action.port && action.port!=='443'))return '表单提交地址不正确';
            const inputs=[...form.querySelectorAll('input')].filter(e=>visible(e)&&/^(text|email|tel|number)$/.test(e.type));
            let user=inputs.find(e=>/^(username|userName|userid|userId|account|loginName|j_username)$/.test(e.name)||/^(username|userName|userid|userId|account|loginName|j_username)$/.test(e.id));
            if(!user && inputs.length===1)user=inputs[0];
            if(!user)return '未识别学号框';
            let buttons=[...form.querySelectorAll('button,input[type=submit],input[type=button],a,[role=button]')].filter(visible);
            let submit=buttons.find(e=>/^(登\s*录|登\s*入|login|sign\s*in)$/i.test((e.innerText||e.value||'').trim()));
            if(!submit)submit=buttons.find(e=>/^(loginButton|loginBtn|login_submit|submit|login)$/.test(e.id));
            if(!submit)return '未识别登录按钮';
            const set=(el,v)=>{Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,v);el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));};
            set(user,__USER__);set(pass,__PASS__);submit.click();return 'submitted';
        })()";
    }

    internal sealed class Cdp : IDisposable
    {
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private int nextId;
        internal bool DialogOpened { get; private set; }
        internal bool IllegalAccess { get; private set; }
        internal bool Rejected { get; private set; }
        internal bool RequiresInteraction { get; private set; }
        public Cdp(Uri uri)
        {
            socket.Options.Proxy = null;
            using (var timeout = new CancellationTokenSource(8000))
                socket.ConnectAsync(uri, timeout.Token).GetAwaiter().GetResult();
        }
        public Dictionary<string, object> Call(string method, object args)
        {
            int id = ++nextId;
            var payload = Encoding.UTF8.GetBytes(json.Serialize(new { id = id, method = method, @params = args }));
            try
            {
                using (var timeout = new CancellationTokenSource(8000))
                {
                    socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();
                    var buffer = new byte[8192];
                    while (true)
                    {
                        using (var stream = new MemoryStream())
                        {
                            WebSocketReceiveResult frame;
                            do
                            {
                                frame = socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).GetAwaiter().GetResult();
                                if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("后台浏览器已关闭。");
                                stream.Write(buffer, 0, frame.Count);
                                if (stream.Length > 2 * 1024 * 1024) throw new IOException("后台浏览器响应过大。");
                            } while (!frame.EndOfMessage);
                            var message = json.DeserializeObject(Encoding.UTF8.GetString(stream.ToArray())) as Dictionary<string, object>;
                            if (message != null && PortalClient.Text(message, "method") == "Page.javascriptDialogOpening")
                            {
                                var dialog = PortalClient.Map(message, "params");
                                string text = PortalClient.Text(dialog, "message");
                                bool alert = PortalClient.Text(dialog, "type") == "alert";
                                DialogOpened = true;
                                IllegalAccess |= text.Contains("非法接入");
                                Rejected |= text.Contains("错误") || text.Contains("不正确") || text.Contains("失败") || text.Contains("锁定");
                                RequiresInteraction |= !alert || text.Contains("验证码") || text.Contains("二次验证");
                                // Dismiss alerts so Runtime.evaluate cannot hang. Never accept a confirmation/prompt.
                                var dismiss = Encoding.UTF8.GetBytes(json.Serialize(new { id = ++nextId,
                                    method = "Page.handleJavaScriptDialog", @params = new { accept = alert } }));
                                socket.SendAsync(new ArraySegment<byte>(dismiss), WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();
                                continue;
                            }
                            object value;
                            if (message == null || !message.TryGetValue("id", out value) || Convert.ToInt32(value) != id) continue;
                            if (message.ContainsKey("error")) throw new IOException("后台浏览器页面正在切换或响应异常。");
                            return PortalClient.Map(message, "result");
                        }
                    }
                }
            }
            catch (WebSocketException) { throw new IOException("后台浏览器连接中断。"); }
            catch (OperationCanceledException) { throw new IOException("后台浏览器响应超时。"); }
        }
        public string Eval(string script)
        {
            var result = Call("Runtime.evaluate", new { expression = script, returnByValue = true, awaitPromise = true });
            if (result.ContainsKey("exceptionDetails")) throw new IOException("认证页脚本暂未就绪。");
            return PortalClient.Text(PortalClient.Map(result, "result"), "value");
        }
        public void Dispose() { socket.Dispose(); }
    }
}
