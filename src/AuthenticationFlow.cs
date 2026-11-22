using System;
using System.IO;

namespace GgsWifi
{
    public sealed class LoginAccount
    {
        public readonly string Username;
        public readonly string Password;
        public LoginAccount(string username, string password) { Username = username; Password = password; }
        public bool IsConfigured { get { return !String.IsNullOrWhiteSpace(Username) && !String.IsNullOrEmpty(Password); } }
    }

    public static class AuthenticationFlow
    {
        public static string Run(bool preferTemporary, LoginAccount temporaryAccount, LoginAccount unifiedAccount,
            Action<LoginAccount> temporary, Action<LoginAccount> unified, Func<bool> verify,
            Action checkCancelled, Action<string> status)
        {
            if (!unifiedAccount.IsConfigured) throw new ManualActionException("请先填写并保存统一认证账号和密码。");
            return Run(preferTemporary && temporaryAccount.IsConfigured,
                () => temporary(temporaryAccount), () => unified(unifiedAccount), verify, checkCancelled, status);
        }

        public static string Run(bool preferTemporary, Action temporary, Action unified,
            Func<bool> verify, Action checkCancelled, Action<string> status)
        {
            checkCancelled();
            if (preferTemporary)
            {
                status("正在尝试临时用户登录…");
                try
                {
                    temporary();
                    checkCancelled();
                    if (verify()) return "temporary";
                }
                catch (IOException e) { status("临时用户登录未完成：" + e.Message); }
                catch (ManualActionException e) { status("临时用户登录需要处理：" + e.Message); }
                // Cancellation is deliberately not caught: pausing/exiting must not start another login.
                checkCancelled();
                status("临时用户登录未能恢复网络，正在切换统一身份认证…");
            }
            checkCancelled();
            unified();
            checkCancelled();
            if (verify()) return "unified";
            throw new IOException("认证流程已结束，但仍未检测到互联网连接，稍后重新检查。");
        }
    }
}
