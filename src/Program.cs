using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace GgsWifi
{
    public sealed class Settings
    {
        public string Username { get; set; }
        public string ProtectedPassword { get; set; }
        public string TemporaryUsername { get; set; }
        public string ProtectedTemporaryPassword { get; set; }
        public bool Enabled { get; set; }
        public bool StartWithWindows { get; set; }
        public int IntervalSeconds { get; set; }
        public bool AuthPaused { get; set; }
        public bool PreferTemporaryLogin { get; set; }
        public Settings() { Username = ""; ProtectedPassword = ""; TemporaryUsername = ""; ProtectedTemporaryPassword = "";
            Enabled = true; StartWithWindows = true; IntervalSeconds = 30; PreferTemporaryLogin = true; }
    }

    public sealed class SettingsStore
    {
        public readonly string DirectoryPath;
        private string FilePath { get { return Path.Combine(DirectoryPath, "settings.json"); } }
        public SettingsStore(string directory) { DirectoryPath = directory; }
        public Settings Load()
        {
            if (!File.Exists(FilePath)) return new Settings();
            var settings = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8));
            if (settings == null || settings.IntervalSeconds < 15 || settings.IntervalSeconds > 300)
                throw new IOException("配置文件无效，请先备份并移走 settings.json 后重新设置。");
            return settings;
        }
        public void Save(Settings settings)
        {
            Directory.CreateDirectory(DirectoryPath);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(settings), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }
        public static string Protect(string plaintext)
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext),
                Encoding.UTF8.GetBytes("GGS_WIFI_v1"), DataProtectionScope.CurrentUser));
        }
        public static string Unprotect(string ciphertext)
        {
            if (String.IsNullOrEmpty(ciphertext)) return "";
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ciphertext),
                Encoding.UTF8.GetBytes("GGS_WIFI_v1"), DataProtectionScope.CurrentUser));
        }
    }

    public static class Startup
    {
        public const string Name = "GgsWifiAutoAuth";
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static string Command(string exe) { return "\"" + Path.GetFullPath(exe) + "\" --background"; }
        public static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (enabled) key.SetValue(Name, Command(Application.ExecutablePath), RegistryValueKind.String);
                else key.DeleteValue(Name, false);
            }
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
        internal static readonly uint ShowMessage = RegisterWindowMessage("GGS_WIFI_SHOW_V1");

        [STAThread]
        private static void Main(string[] args)
        {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool owned;
            string sid = WindowsIdentity.GetCurrent().User.Value;
            using (var mutex = new Mutex(true, @"Local\GGS_WIFI_" + sid, out owned))
            {
                if (!owned) { PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero); return; }
                var store = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GGS_WIFI"));
                try
                {
                    var settings = store.Load();
                    Application.Run(new MainForm(store, settings, Array.IndexOf(args, "--background") >= 0));
                }
                catch (Exception)
                {
                    MessageBox.Show("程序未能启动。请检查配置文件或密码是否属于当前 Windows 用户。\n配置目录：" +
                        store.DirectoryPath + "\n如需重新设置，请先备份并移走 settings.json。", "校园网助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    public sealed class MainForm : Form
    {
        private readonly SettingsStore store;
        private Settings settings;
        private readonly TextBox username = new TextBox();
        private readonly TextBox password = new TextBox();
        private readonly TextBox temporaryUsername = new TextBox();
        private readonly TextBox temporaryPassword = new TextBox();
        private readonly CheckBox enabled = new CheckBox();
        private readonly CheckBox startup = new CheckBox();
        private readonly CheckBox preferTemporary = new CheckBox();
        private readonly NumericUpDown interval = new NumericUpDown();
        private readonly Label state = new Label();
        private readonly Label detail = new Label();
        private readonly TextBox history = new TextBox();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly Icon applicationIcon;
        private readonly ToolTip tooltip = new ToolTip();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private bool exiting;
        private bool busy;
        private volatile int generation;
        private int failures;
        private int offlineSamples;
        private DateTime nextCheck = DateTime.MinValue;
        private string savedPassword;
        private string savedTemporaryPassword;
        private string lastLog = "";
        private readonly bool background;
        private Button check;

        public MainForm(SettingsStore store, Settings settings, bool background)
        {
            this.store = store; this.settings = settings; this.background = background;
            savedPassword = SettingsStore.Unprotect(settings.ProtectedPassword);
            savedTemporaryPassword = SettingsStore.Unprotect(settings.ProtectedTemporaryPassword);
            Text = "广工商 · 校园网助手";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(680, Math.Min(800, Screen.FromControl(this).WorkingArea.Height - 80));
            MinimumSize = new Size(580, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 10F);
            BackColor = Color.FromArgb(245, 247, 251);
            using (var resource = typeof(MainForm).Assembly.GetManifestResourceStream("GgsWifi.AppIcon"))
            {
                if (resource == null) throw new IOException("程序图标资源缺失，请重新下载完整程序。");
                using (var embeddedIcon = new Icon(resource)) applicationIcon = (Icon)embeddedIcon.Clone();
            }
            Icon = applicationIcon;
            BuildUi();
            username.Text = settings.Username;
            password.Text = savedPassword;
            temporaryUsername.Text = settings.TemporaryUsername;
            temporaryPassword.Text = savedTemporaryPassword;
            enabled.Checked = settings.Enabled;
            startup.Checked = settings.StartWithWindows;
            preferTemporary.Checked = settings.PreferTemporaryLogin;
            interval.Value = settings.IntervalSeconds;
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开设置", null, (s, e) => ShowMain());
            menu.Items.Add("立即检查", null, (s, e) => CheckNow());
            menu.Items.Add("暂停 / 恢复监测", null, (s, e) => TogglePause());
            menu.Items.Add("退出", null, (s, e) => ExitApp());
            tray.Icon = applicationIcon;
            tray.Text = "广工商校园网助手";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowMain();
            tray.Visible = true;
            timer.Interval = 1000;
            timer.Tick += (s, e) => { if (!busy && DateTime.UtcNow >= nextCheck) RunCheck(false); };
            timer.Start();
            Shown += (s, e) => {
                if (background && savedPassword.Length > 0) Hide();
                nextCheck = DateTime.UtcNow.AddSeconds(background ? 12 : 2);
                if (settings.AuthPaused) SetStatus("已暂停认证", "上次认证需要处理，请检查后点击“立即检查 / 恢复”。");
                else if (savedPassword.Length == 0) SetStatus("等待首次设置", "输入学号和统一身份认证密码，保存后开始监测。");
                else SetStatus("监测已就绪", "连通时保持待机，连续两次断网后自动尝试认证。");
            };
            FormClosing += (s, e) => {
                if (!exiting && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true; Hide();
                    tray.ShowBalloonTip(2000, "校园网助手", "已转入后台，右下角托盘可打开或退出。", ToolTipIcon.Info);
                }
            };
        }

        private void BuildUi()
        {
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var content = Stack();
            content.Padding = new Padding(24, 16, 24, 16);
            scroll.Controls.Add(content); Controls.Add(scroll);
            var header = new Label { Text = "校园网，自动续连", Font = new Font(Font.FontFamily, 22F, FontStyle.Bold),
                ForeColor = Color.FromArgb(26, 43, 70), AutoSize = true };
            AddWrapped(content, header);
            AddWrapped(content, new Label { Text = "广州工商学院  /  临时登录优先 · 统一认证兜底", AutoSize = true,
                ForeColor = Color.FromArgb(100, 112, 132) });
            var panel = Stack(); panel.BackColor = Color.White; panel.Padding = new Padding(16, 10, 16, 10);
            panel.Margin = new Padding(0, 6, 0, 8);
            state.Name = "state"; state.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
            state.ForeColor = Color.FromArgb(29, 99, 187); state.Text = "等待首次设置";
            detail.Name = "detail"; detail.Font = new Font(Font.FontFamily, 9F);
            detail.ForeColor = Color.FromArgb(100, 112, 132); detail.Text = "输入学号和统一身份认证密码，保存后开始监测。";
            AddWrapped(panel, state); AddWrapped(panel, detail); content.Controls.Add(panel);
            var accounts = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
            accounts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            accounts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            var temporaryGroup = AccountGroup("临时用户登录（选填）", "临时账号", temporaryUsername, temporaryPassword);
            var unifiedGroup = AccountGroup("统一身份认证", "学号 / 工号", username, password);
            accounts.Controls.Add(temporaryGroup, 0, 0); accounts.Controls.Add(unifiedGroup, 1, 0);
            bool columns = true;
            EventHandler reflow = (s, e) => {
                int minimum = Math.Max(TextRenderer.MeasureText(temporaryGroup.Text, temporaryGroup.Font).Width,
                    TextRenderer.MeasureText(unifiedGroup.Text, unifiedGroup.Font).Width) + 40;
                bool sideBySide = accounts.ClientSize.Width >= minimum * 2;
                if (columns == sideBySide) return;
                columns = sideBySide; accounts.SuspendLayout();
                if (sideBySide) accounts.ColumnCount = 2;
                accounts.SetCellPosition(unifiedGroup, new TableLayoutPanelCellPosition(sideBySide ? 1 : 0, sideBySide ? 0 : 1));
                if (!sideBySide) accounts.ColumnCount = 1;
                accounts.ColumnStyles.Clear();
                accounts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, sideBySide ? 50F : 100F));
                if (sideBySide) accounts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
                accounts.ResumeLayout(true);
            };
            accounts.SizeChanged += reflow; temporaryGroup.FontChanged += reflow; unifiedGroup.FontChanged += reflow;
            content.Controls.Add(accounts);
            preferTemporary.Text = "优先临时用户登录，失败后使用统一认证账号登录";
            AddWrapped(content, preferTemporary);
            tooltip.SetToolTip(preferTemporary, "两组账号密码分别保存。临时账号留空或取消勾选时，直接使用统一认证账号。");
            var options = Flow();
            enabled.Text = "断网后自动认证"; enabled.AutoSize = true; enabled.Margin = new Padding(0, 0, 20, 4); options.Controls.Add(enabled);
            startup.Text = "开机自启（登录 Windows 后）"; startup.AutoSize = true; options.Controls.Add(startup);
            content.Controls.Add(options);
            var timing = Flow();
            timing.Controls.Add(new Label { Text = "检查间隔", AutoSize = true, Margin = new Padding(0, 5, 12, 4) });
            interval.Width = 85; interval.Minimum = 15; interval.Maximum = 300; interval.Increment = 5; timing.Controls.Add(interval);
            timing.Controls.Add(new Label { Text = "秒 · 连续两次断网才认证", AutoSize = true, Margin = new Padding(10, 5, 0, 4) });
            content.Controls.Add(timing);
            var buttons = Flow();
            var save = AddButton(buttons, "保存并开始");
            save.BackColor = Color.FromArgb(35, 103, 214); save.ForeColor = Color.White;
            save.Click += (s, e) => SaveSettings();
            check = AddButton(buttons, "立即检查 / 恢复"); check.Click += (s, e) => CheckNow();
            var open = AddButton(buttons, "打开认证页"); open.Click += (s, e) => OpenUrl("http://www.msftconnecttest.com/redirect");
            content.Controls.Add(buttons);
            var logHeader = Flow();
            logHeader.Controls.Add(new Label { Text = "运行记录", AutoSize = true, Margin = new Padding(0, 0, 24, 4) });
            var logs = new LinkLabel { Text = "打开日志目录", AutoSize = true };
            logs.LinkClicked += (s, e) => { Directory.CreateDirectory(store.DirectoryPath); OpenUrl(store.DirectoryPath); }; logHeader.Controls.Add(logs);
            content.Controls.Add(logHeader);
            history.Name = "history"; history.Dock = DockStyle.Top; history.Height = 96;
            history.Multiline = true; history.ReadOnly = true; history.WordWrap = true;
            history.ScrollBars = ScrollBars.Vertical; history.BackColor = Color.White; history.BorderStyle = BorderStyle.FixedSingle;
            history.Font = new Font("Microsoft YaHei UI", 9F); history.Margin = new Padding(0, 0, 0, 8); content.Controls.Add(history);
            AddWrapped(content, new Label { Text = "密码由 Windows 加密保存 · 关闭窗口后继续在托盘运行",
                AutoSize = true, ForeColor = Color.FromArgb(110, 120, 136), Font = new Font(Font.FontFamily, 9F) });
        }
        private static TableLayoutPanel Stack()
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, Margin = Padding.Empty };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            return table;
        }
        private static FlowLayoutPanel Flow()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 6) };
        }
        private static void AddWrapped(TableLayoutPanel parent, Control control)
        {
            control.AutoSize = true; control.Dock = DockStyle.Top; control.Margin = new Padding(0, 0, 0, 2);
            parent.Controls.Add(control);
            EventHandler resize = (s, e) => {
                int width = Math.Max(80, parent.ClientSize.Width - parent.Padding.Horizontal - control.Margin.Horizontal);
                if (control.MaximumSize.Width != width) control.MaximumSize = new Size(width, 0);
            };
            parent.SizeChanged += resize; resize(parent, EventArgs.Empty);
        }
        private static GroupBox AccountGroup(string title, string accountLabel, TextBox account, TextBox secret)
        {
            var group = new GroupBox { Text = title, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(12, 8, 12, 12), Margin = new Padding(0, 0, 10, 0) };
            var fields = Stack(); fields.Dock = DockStyle.Fill;
            AddWrapped(fields, new Label { Text = accountLabel });
            account.Dock = DockStyle.Top; account.Margin = new Padding(0, 0, 0, 8); fields.Controls.Add(account);
            AddWrapped(fields, new Label { Text = "密码" });
            secret.Dock = DockStyle.Top; secret.Margin = Padding.Empty; secret.UseSystemPasswordChar = true; fields.Controls.Add(secret);
            group.Controls.Add(fields); return group;
        }
        private Button AddButton(FlowLayoutPanel parent, string text)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(170, 42), Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0, 0, 12, 4),
                FlatStyle = FlatStyle.Flat, BackColor = Color.White, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Color.FromArgb(212, 221, 234); parent.Controls.Add(b); return b;
        }

        private void SaveSettings()
        {
            if (String.IsNullOrWhiteSpace(username.Text) || String.IsNullOrEmpty(password.Text))
            { MessageBox.Show(this, "请填写学号和统一身份认证密码。", Text); return; }
            if (String.IsNullOrWhiteSpace(temporaryUsername.Text) != String.IsNullOrEmpty(temporaryPassword.Text))
            { MessageBox.Show(this, "临时登录的账号和密码请成对填写，或同时留空以直接使用统一认证。", Text); return; }
            if (busy) { MessageBox.Show(this, "正在检查，请完成后再保存设置。", Text); return; }
            try
            {
                var updated = new Settings { Username = username.Text.Trim(), ProtectedPassword = SettingsStore.Protect(password.Text),
                    TemporaryUsername = temporaryUsername.Text.Trim(),
                    ProtectedTemporaryPassword = String.IsNullOrEmpty(temporaryPassword.Text) ? "" : SettingsStore.Protect(temporaryPassword.Text),
                    Enabled = enabled.Checked, StartWithWindows = startup.Checked, IntervalSeconds = (int)interval.Value,
                    PreferTemporaryLogin = preferTemporary.Checked, AuthPaused = false };
                // Save credentials first. If startup registration fails, make that explicit.
                store.Save(updated);
                settings = updated; savedPassword = password.Text; savedTemporaryPassword = temporaryPassword.Text;
                generation++; failures = 0; offlineSamples = 0;
                Startup.Set(settings.StartWithWindows);
                Log("设置已保存；开机自启" + (settings.StartWithWindows ? "已开启。" : "已关闭。"));
                nextCheck = DateTime.MinValue;
                if (!settings.Enabled) SetStatus("自动认证已关闭", "需要时可以点击“立即检查 / 恢复”进行一次检查。");
                else RunCheck(true);
            }
            catch (Exception) { MessageBox.Show(this, "保存设置或写入开机自启失败，请检查配置目录和 Windows 启动项权限。", Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void CheckNow()
        {
            if (busy) return;
            if (savedPassword.Length == 0)
            { ShowMain(); MessageBox.Show(this, "请先填写账号密码，并点击“保存并开始”。", Text); return; }
            if (username.Text.Trim() != settings.Username || password.Text != savedPassword || preferTemporary.Checked != settings.PreferTemporaryLogin ||
                temporaryUsername.Text.Trim() != (settings.TemporaryUsername ?? "") || temporaryPassword.Text != savedTemporaryPassword)
            { ShowMain(); MessageBox.Show(this, "账号密码或登录方式有未保存的修改，请先点击“保存并开始”。", Text); return; }
            settings.AuthPaused = false;
            try { store.Save(settings); } catch { Log("暂停状态未能写入配置，请检查目录权限。"); }
            failures = 0; nextCheck = DateTime.MinValue;
            RunCheck(true);
        }

        private void TogglePause()
        {
            generation++;
            settings.Enabled = !settings.Enabled;
            settings.AuthPaused = false;
            enabled.Checked = settings.Enabled;
            try { store.Save(settings); } catch { Log("监测状态未能写入配置，请检查目录权限。"); }
            nextCheck = DateTime.MinValue;
            SetStatus(settings.Enabled ? "监测已恢复" : "监测已暂停", settings.Enabled ? "等待下一次连通性检查。" : "可从托盘菜单恢复监测。");
        }

        private async void RunCheck(bool manual)
        {
            if (busy || exiting || (!manual && (!settings.Enabled || settings.AuthPaused)) || savedPassword.Length == 0) return;
            busy = true; check.Enabled = false;
            int run = generation;
            var unifiedAccount = new LoginAccount(settings.Username, savedPassword);
            var temporaryAccount = new LoginAccount(settings.TemporaryUsername, savedTemporaryPassword);
            bool useTemporary = settings.PreferTemporaryLogin && temporaryAccount.IsConfigured;
            SetStatus("正在检查网络", "检测互联网连通性…");
            try
            {
                string outcome = await Task.Factory.StartNew(() => {
                    var client = new PortalClient(new HttpTransport(), PortalClient.GetLocalAddresses);
                    if (client.IsOnline()) return "online";
                    if (!manual && offlineSamples < 1) return "confirm";
                    if (run != generation) throw new OperationCanceledException();
                    PostStatus("正在获取校园网参数", "确认当前 IP 与校园网认证入口…");
                    var context = client.LoadContext();
                    if (run != generation) throw new OperationCanceledException();
                    var browser = new BrowserLogin(Path.Combine(store.DirectoryPath, "BrowserProfile"),
                        PostAuthStatus, () => exiting || run != generation);
                    Action checkCancelled = () => { if (run != generation || exiting) throw new OperationCanceledException(); };
                    Func<bool> verify = () => {
                        PostStatus("正在验证网络", "检查认证后是否可以访问互联网…");
                        for (int i = 0; i < 3; i++)
                        {
                            checkCancelled(); Thread.Sleep(2000); checkCancelled();
                            if (client.IsOnline()) return true;
                        }
                        return false;
                    };
                    return AuthenticationFlow.Run(useTemporary, temporaryAccount, unifiedAccount,
                        account => browser.LoginTemporary(context.Page, account.Username, account.Password),
                        account => {
                            checkCancelled();
                            var portal = useTemporary ? client.LoadContext().Page : context.Page;
                            checkCancelled();
                            browser.Login(portal, account.Username, account.Password);
                        }, verify, checkCancelled,
                        PostAuthStatus);
                });
                if (run != generation || exiting) return;
                if (outcome == "confirm")
                {
                    offlineSamples++;
                    SetStatus("网络暂时不可用", "等待再次确认，避免网络短暂波动时重复认证。");
                }
                else
                {
                    offlineSamples = 0; failures = 0;
                    string auth = outcome == "temporary" ? "临时用户登录" : "统一身份认证";
                    SetStatus("网络已连通", outcome == "online" ? "互联网可访问，继续后台监测。" : auth + "后已验证互联网可访问。");
                    Log(outcome == "online" ? "互联网连接正常。" : auth + "完成，互联网连通检查通过。");
                }
                nextCheck = DateTime.UtcNow.AddSeconds(settings.IntervalSeconds);
            }
            catch (ManualActionException e)
            {
                if (run != generation || exiting) return;
                settings.AuthPaused = true;
                try { store.Save(settings); } catch { Log("暂停状态未能写入配置，请检查目录权限。"); }
                SetStatus("需要处理 · 已暂停认证", e.Message); Log(e.Message);
                tray.ShowBalloonTip(5000, "校园网助手需要处理", e.Message, ToolTipIcon.Warning);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (run != generation || exiting) return;
                failures++;
                int wait = Math.Min(300, Math.Max(settings.IntervalSeconds, 30) * (1 << Math.Min(failures - 1, 4)));
                nextCheck = DateTime.UtcNow.AddSeconds(wait);
                // Only internally generated IOException messages are shown. Browser/OS exceptions
                // may contain request payloads and are deliberately not written to disk.
                string message = e is IOException ? e.Message : "本次检查未完成，请确认校园网和浏览器可用。";
                SetStatus("等待网络恢复", message + " " + wait + " 秒后重试。"); Log(message + " " + wait + " 秒后重试。");
            }
            finally { busy = false; if (!IsDisposed) { check.Enabled = true; if (exiting) Close(); } }
        }

        private void PostAuthStatus(string message)
        {
            if (exiting || IsDisposed) return;
            try { BeginInvoke((Action)(() => { if (!exiting) { SetStatus("正在校园网认证", message); Log(message); } })); }
            catch (InvalidOperationException) { }
        }
        private void PostStatus(string title, string message)
        {
            if (exiting || IsDisposed) return;
            try { BeginInvoke((Action)(() => { if (!exiting) SetStatus(title, message); })); }
            catch (InvalidOperationException) { }
        }
        private void SetStatus(string title, string message)
        { state.Text = title; detail.Text = message; tooltip.SetToolTip(detail, message); tray.Text = ("校园网助手 · " + title).Substring(0, Math.Min(63, ("校园网助手 · " + title).Length)); }
        private void Log(string message)
        {
            if (lastLog == message) return;
            lastLog = message;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
            if (history.TextLength > 18000) history.Clear();
            history.AppendText(line);
            try
            {
                Directory.CreateDirectory(store.DirectoryPath);
                var path = Path.Combine(store.DirectoryPath, "activity.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                {
                    var backup = path + ".1";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(path, backup);
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { MessageBox.Show(this, "未能打开，请手动访问：" + url, Text); }
        }
        private void ShowMain() { Show(); WindowState = FormWindowState.Normal; Activate(); }
        private void ExitApp()
        {
            exiting = true; generation++; timer.Stop();
            if (busy) SetStatus("正在退出", "正在停止认证并关闭本程序创建的后台浏览器…");
            else Close();
        }
        protected override void WndProc(ref Message m)
        { if (m.Msg == Program.ShowMessage) ShowMain(); base.WndProc(ref m); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { exiting = true; timer.Dispose(); tray.Dispose(); tooltip.Dispose(); applicationIcon.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
