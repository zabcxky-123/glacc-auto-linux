using GlaccAuto.Core.Diagnostics;
using GlaccAuto.Core.Glacc;
using GlaccAuto.Core.Notify;


namespace GlaccAuto.Core.Claim;

/// <summary>
/// 无界面领取编排：登录态恢复 → 任务进度对账 → 分阶段 mobileGLTaskPush → 结果通知。
/// GUI 与 CLI 共用同一套业务语义。
/// </summary>
public sealed class ClaimService
{
    private readonly AppSettings _settings;
    private readonly GlaccCredentials _cred;
    private readonly GlaccAuthClient _auth;
    private readonly GlaccSession _session;
    private readonly GlaccGameClient _game;

    public ClaimService(AppSettings settings, GlaccCredentials cred)
    {
        _settings = settings;
        _cred = cred;
        _auth = new GlaccAuthClient(cred);
        _session = new GlaccSession(cred, _auth, () => settings.NetworkRetryCount);
        _game = new GlaccGameClient(cred, _session);
    }

    public GlaccCredentials Credentials => _cred;
    public GlaccAuthClient Auth => _auth;
    public GlaccSession Session => _session;
    public GlaccGameClient Game => _game;

    /// <summary>钱包 score → 分钟（80 score = 1 分钟）。</summary>
    public static double ScoreToMinutes(long score) => score / GlaccConstants.ScorePerMinute;

    /// <summary>失败原因 → 用户可读文案。</summary>
    public static string DescribeFailure(GlaccFailReason reason) => reason switch
    {
        GlaccFailReason.NoResponse => "网络不可用：无法连接服务器",
        GlaccFailReason.ServerError => "服务端暂时不可用（HTTP 5xx）",
        GlaccFailReason.BadResponse => "服务端响应无法解析：接口可能已变更",
        GlaccFailReason.ClientError => "本机网络组件异常：TLS 指纹库不可用",
        GlaccFailReason.NeedRelogin => "登录已过期，请重新短信登录",
        GlaccFailReason.NotLoggedIn => "尚未登录，请先完成短信登录",
        _ => "网络异常",
    };

    public async Task<ClaimReport> RunAsync(IClaimObserver? observer = null, CancellationToken ct = default)
    {
        observer ??= NullClaimObserver.Instance;

        if (!_cred.HasToken && !_cred.HasRefreshToken)
            return Fail("尚未登录，需要先完成短信登录", needRelogin: true);

        if (_session.JwtNeedsRefresh)
        {
            observer.OnStatus("正在恢复登录态…");
            var r = await _session.EnsureJwtAsync(ct: ct);
            if (!r.Ok)
            {
                return Fail(r.Error, needRelogin: r.NeedRelogin, detail: r.Error);
            }
        }

        var stagesResult = await _game.GetTaskStagesAsync(
            onRetry: (a, w) => observer.OnRetryAsync("任务进度查询", a, w),
            ct: ct);
        if (!stagesResult.Ok)
            return FromCall(stagesResult, "无法同步任务进度");

        var stages = stagesResult.Value!;
        var total = stages.Sum(s => s.StageSum);
        var index = stages.Sum(s => s.StageCurrent);
        double? balance = await TryWalletAsync(observer, ct);

        if (total == 0)
            return Fail("服务端今日暂无任务数据", index: 0, total: 0, balance: balance);

        if (index >= total)
        {
            DiagLog.Info("对账显示今日任务已完成，无需领取");
            return Ok("今日任务已全部完成", index, total, balance);
        }

        DiagLog.Info($"开始领取：共 {total} 次，已完成 {index} 次");

        for (var si = 0; si < stages.Count; si++)
        {
            var firstPush = true;
            while (stages[si].StageCurrent < stages[si].StageSum && index < total)
            {
                ct.ThrowIfCancellationRequested();
                if (!firstPush) await Task.Delay(NextInterval(), ct);
                firstPush = false;
                var stage = stages[si];

                var push = await _game.PushTaskAsync(stage.TaskId,
                    onRetry: (a, w) => observer.OnRetryAsync($"{stage.Name} 推送", a, w),
                    ct: ct);
                if (!push.Ok)
                    return FromCall(push, "已中断", stages.Sum(s => s.StageCurrent), total, balance);

                var result = push.Value!;
                if (result.Code == 0)
                {
                    stages[si] = stage with { StageCurrent = stage.StageCurrent + 1 };
                    index = stages.Sum(s => s.StageCurrent);
                    var addMinutes = ScoreToMinutes(result.AddScore);
                    var msg = addMinutes > 0
                        ? $"已领取 {stage.Name}（+{addMinutes:0.#} 分钟）"
                        : $"已领取 {stage.Name}";
                    observer.OnStatus(msg);
                    DiagLog.Info($"已领取 {stage.Name}，+{addMinutes:0.#} 分钟（taskId={stage.TaskId}）");
                }
                else
                {
                    observer.OnStatus($"服务端返回 code={result.Code}（{stage.Name} 暂不可推），跳下一阶段", isError: true);
                    DiagLog.Warn($"推送被服务端拒绝：code={result.Code}，taskId={stage.TaskId}");
                    break;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            var server = await _game.GetTaskStagesAsync(accept: s => s is { Count: > 0 },
                onRetry: (a, w) => observer.OnRetryAsync("任务进度对账", a, w),
                ct: ct);
            if (!server.Ok)
                return FromCall(server, "已中断", index, total, balance);

            var serverStages = server.Value!;
            var serverCount = serverStages.Sum(s => s.StageCurrent);
            var localCount = index;
            if (localCount > serverCount)
            {
                DiagLog.Error($"进度对账异常：本地 {localCount} 次 / 服务端 {serverCount} 次，已停止领取");
                return Fail($"进度对账异常（本地 {localCount} 次 / 服务端 {serverCount} 次）",
                    detail: "服务端进度少于本地已确认的领取次数，发放链路可能已失效",
                    index: serverCount, total: serverStages.Sum(s => s.StageSum), balance: balance);
            }
            stages = serverStages;
            total = stages.Sum(s => s.StageSum);
            index = stages.Sum(s => s.StageCurrent);
        }

        balance = await TryWalletAsync(observer, ct) ?? balance;
        if (index >= total)
        {
            DiagLog.Info($"领取完成：{index} / {total} 次，余额 {FormatBalance(balance)}");
            return Ok("领取完成", index, total, balance);
        }

        DiagLog.Warn($"领取结束但未跑满：{index} / {total} 次");
        return Fail($"领取未跑满：{index} / {total} 次（部分阶段被服务端拒绝）",
            index: index, total: total, balance: balance);
    }

    public async Task NotifyAsync(ClaimReport report)
    {
        var key = _settings.ServerKey;
        if (!ServerChanClient.IsConfigured(key))
        {
            DiagLog.Info("未配置 Server酱通知，跳过结果推送");
            return;
        }
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var desp = report.Success
            ? $"**定时领取完成**\n\n- 时间: {now}\n- 本次进度: {report.ClaimIndex} / {report.TotalClaims} 次\n- 当前余额: {report.BalanceText}"
            : $"**定时领取未完成**\n\n- 时间: {now}\n- 原因: {report.Message}";
        var sent = await ServerChanClient.SendAsync(key.Trim(),
            report.Success ? "glacc-auto 定时领取成功" : "glacc-auto 定时领取失败", desp);
        DiagLog.Info(sent ? "定时领取结果通知已发送" : "定时领取结果通知未送达");
    }

    private async Task<double?> TryWalletAsync(IClaimObserver observer, CancellationToken ct)
    {
        var score = await _game.GetWalletScoreAsync(
            onRetry: (a, w) => observer.OnRetryAsync("余额查询", a, w), ct: ct);
        if (score.Ok) return ScoreToMinutes(score.Value);
        DiagLog.Warn($"查询余额失败：{score.Reason}｜{score.Detail}");
        return null;
    }

    private TimeSpan NextInterval()
    {
        var min = Math.Clamp(_settings.IntervalMinSec, 1, 3600);
        var max = Math.Clamp(_settings.IntervalMaxSec, min, 3600);
        return TimeSpan.FromSeconds(Random.Shared.Next(min, max + 1));
    }

    private static string FormatBalance(double? minutes) => minutes is { } m
        ? $"{(int)Math.Round(m) / 60} 小时 {(int)Math.Round(m) % 60:00} 分"
        : "未知";

    private static ClaimReport Ok(string message, int index, int total, double? balance) => new()
    {
        Success = true,
        Message = message,
        ClaimIndex = index,
        TotalClaims = total,
        BalanceMinutes = balance,
    };

    private static ClaimReport Fail(string message, bool needRelogin = false, string detail = "",
        int index = 0, int total = 0, double? balance = null) => new()
    {
        Success = false,
        NeedRelogin = needRelogin,
        Message = message,
        Detail = detail,
        ClaimIndex = index,
        TotalClaims = total,
        BalanceMinutes = balance,
    };

    private static ClaimReport FromCall<T>(GlaccCallResult<T> result, string context,
        int index = 0, int total = 0, double? balance = null) =>
        Fail($"{DescribeFailure(result.Reason)}，{context}",
            needRelogin: result.Reason is GlaccFailReason.NeedRelogin or GlaccFailReason.NotLoggedIn,
            detail: result.Detail, index: index, total: total, balance: balance);
}
