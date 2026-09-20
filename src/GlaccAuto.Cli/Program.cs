using System.Globalization;
using System.Reflection;
using GlaccAuto.Core;
using GlaccAuto.Core.Claim;
using GlaccAuto.Core.Diagnostics;
using GlaccAuto.Core.Glacc;
using GlaccAuto.Core.Scheduling;

namespace GlaccAuto.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        DiagLog.Prune();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cmd = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();
        if (args.Any(a => a is "-h" or "--help") && cmd is null) cmd = "help";
        if (args.Any(a => a.Equals("--scheduled", StringComparison.OrdinalIgnoreCase)))
            cmd = "claim";

        try
        {
            return cmd switch
            {
                "login" => await LoginAsync(),
                "logout" => Logout(),
                "status" => await StatusAsync(),
                "claim" => await ClaimAsync(notify: true),
                "schedule" => Schedule(args.Skip(1).ToArray()),
                "help" or "--help" or "-h" or null when args.Length == 0 => PrintHelp(),
                null or "" => PrintHelp(),
                _ => Unknown(cmd),
            };
        }
        catch (Exception ex)
        {
            DiagLog.Error("CLI 异常退出", ex);
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 2;
        }
    }

    private static int PrintHelp()
    {
        var ver = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";
        var plus = ver.IndexOf('+');
        if (plus >= 0) ver = ver[..plus];
        Console.WriteLine($"""
            glacc-auto {ver}  （Linux / 无界面）

            用法：
              glacc-auto login              短信登录（交互式）
              glacc-auto logout             退出登录，清除本机令牌
              glacc-auto status             查看余额与今日任务进度
              glacc-auto claim              立即领取今日任务
              glacc-auto --scheduled        同上（供 systemd/cron 调用，并推送 Server酱）
              glacc-auto schedule on [HH:MM]
              glacc-auto schedule off
              glacc-auto schedule status

            数据目录：{AppPaths.Root}
              可用环境变量 GLACC_HOME 覆盖。
            """);
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知命令：{cmd}");
        PrintHelp();
        return 1;
    }

    private static async Task<int> LoginAsync()
    {
        var cred = GlaccCredentials.Load();
        var auth = new GlaccAuthClient(cred);

        Console.Write("手机号：");
        var phone = (Console.ReadLine() ?? "").Trim();
        Console.WriteLine("正在发送验证码…");
        var sent = await auth.SendSmsAsync(phone);
        if (!sent.Ok)
        {
            Console.Error.WriteLine(sent.Error);
            return 1;
        }
        Console.WriteLine("验证码已发送，5 分钟内有效。");
        Console.Write("验证码：");
        var code = (Console.ReadLine() ?? "").Trim();
        var login = await auth.LoginAsync(code);
        if (!login.Ok)
        {
            Console.Error.WriteLine(login.Error);
            return 1;
        }
        Console.WriteLine($"登录成功。账号 {MaskPhone(cred.Phone)}  ID:{cred.Sub}");
        Console.WriteLine($"凭证已保存到 {AppPaths.CredentialsFile}");
        return 0;
    }

    private static int Logout()
    {
        var cred = GlaccCredentials.Load();
        cred.ClearSession();
        cred.Save();
        Console.WriteLine("已退出登录。");
        return 0;
    }

    private static async Task<int> StatusAsync()
    {
        var settings = AppSettings.Load();
        var cred = GlaccCredentials.Load();
        if (!cred.HasToken && !cred.HasRefreshToken)
        {
            Console.WriteLine("尚未登录。请先执行：glacc-auto login");
            return 1;
        }
        var svc = new ClaimService(settings, cred);
        if (svc.Session.JwtNeedsRefresh)
        {
            var r = await svc.Session.EnsureJwtAsync();
            if (!r.Ok)
            {
                Console.Error.WriteLine(r.Error);
                return r.NeedRelogin ? 10 : 1;
            }
        }
        var stages = await svc.Game.GetTaskStagesAsync();
        var wallet = await svc.Game.GetWalletScoreAsync();
        Console.WriteLine($"账号  {MaskPhone(cred.Phone)}  ID:{cred.Sub}");
        if (wallet.Ok)
        {
            var m = ClaimService.ScoreToMinutes(wallet.Value);
            Console.WriteLine($"余额  {(int)Math.Round(m) / 60} 小时 {(int)Math.Round(m) % 60:00} 分");
        }
        else
        {
            Console.WriteLine($"余额  未知（{ClaimService.DescribeFailure(wallet.Reason)}）");
        }
        if (stages.Ok)
        {
            var list = stages.Value!;
            var total = list.Sum(s => s.StageSum);
            var done = list.Sum(s => s.StageCurrent);
            Console.WriteLine($"今日  {done} / {total}");
            foreach (var s in list)
                Console.WriteLine($"  - {s.Name}  {s.StageCurrent}/{s.StageSum}  (taskId={s.TaskId})");
        }
        else
        {
            Console.WriteLine($"任务  {ClaimService.DescribeFailure(stages.Reason)}");
        }
        return 0;
    }

    private static async Task<int> ClaimAsync(bool notify)
    {
        using var gate = TryLock();
        if (gate is null)
        {
            Console.Error.WriteLine("已有领取进程在运行，本次跳过。");
            DiagLog.Warn("领取跳过：已有实例持有领取锁");
            return 0;
        }

        var settings = AppSettings.Load();
        var cred = GlaccCredentials.Load();
        var svc = new ClaimService(settings, cred);
        var observer = new ConsoleObserver();
        DiagLog.Info("CLI 开始领取");
        var report = await svc.RunAsync(observer);
        if (notify) await svc.NotifyAsync(report);

        Console.WriteLine(report.Success
            ? $"完成：{report.Message}  进度 {report.ClaimIndex}/{report.TotalClaims}  余额 {report.BalanceText}"
            : $"未完成：{report.Message}");
        if (!string.IsNullOrEmpty(report.Detail))
            Console.WriteLine($"详情：{report.Detail}");

        if (report.NeedRelogin) return 10;
        return report.Success ? 0 : 1;
    }

    private static int Schedule(string[] args)
    {
        var sub = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();
        return sub switch
        {
            "on" or "enable" => ScheduleOn(args),
            "off" or "disable" => ScheduleOff(),
            "status" or null => ScheduleStatus(),
            _ => Unknown("schedule " + sub),
        };
    }

    private static int ScheduleOn(string[] args)
    {
        var settings = AppSettings.Load();
        var timeText = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'))
                       ?? settings.ScheduledTime;
        if (!TimeSpan.TryParse(timeText, CultureInfo.InvariantCulture, out var time))
            time = new TimeSpan(8, 0, 0);
        time = new TimeSpan(time.Hours, time.Minutes, 0);
        ScheduledTaskManager.Register(time);
        settings.ScheduledTime = $"{time.Hours:00}:{time.Minutes:00}";
        settings.Save();
        Console.WriteLine($"已启用每日 {settings.ScheduledTime} 自动领取。");
        if (OperatingSystem.IsLinux())
            Console.WriteLine("已写入 systemd --user timer（glacc-auto-claim.timer）；用户总线不可用时回退 crontab。");
        return 0;
    }

    private static int ScheduleOff()
    {
        ScheduledTaskManager.Unregister();
        Console.WriteLine("已关闭定时领取。");
        return 0;
    }

    private static int ScheduleStatus()
    {
        if (!ScheduledTaskManager.TryQuery(out var snap))
        {
            Console.Error.WriteLine("无法查询定时任务状态。");
            return 1;
        }
        if (snap is null)
        {
            Console.WriteLine("定时领取：未注册");
            return 0;
        }
        Console.WriteLine($"定时领取：{(snap.Enabled ? "已启用" : "已禁用")}");
        Console.WriteLine($"时刻：{snap.StartTime.Hours:00}:{snap.StartTime.Minutes:00}");
        Console.WriteLine($"命令：{snap.Command} {snap.Arguments}");
        return 0;
    }

    private static Mutex? TryLock()
    {
        var mutex = new Mutex(true, "glacc-auto-claim", out var created);
        if (created) return mutex;
        mutex.Dispose();
        return null;
    }

    private static string MaskPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 13 && digits.StartsWith("86")) digits = digits[2..];
        return digits.Length <= 5 ? digits : digits[..2] + "******" + digits[^3..];
    }

    private sealed class ConsoleObserver : IClaimObserver
    {
        public void OnStatus(string message, bool isError = false)
        {
            if (isError) Console.Error.WriteLine(message);
            else Console.WriteLine(message);
        }

        public Task OnRetryAsync(string what, int attempt, TimeSpan wait)
        {
            Console.WriteLine($"网络波动：{what}失败，{wait.TotalSeconds:0} 秒后自动重试（第 {attempt} 次）");
            return Task.CompletedTask;
        }
    }
}
