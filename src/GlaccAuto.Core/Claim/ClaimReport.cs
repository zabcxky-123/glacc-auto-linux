namespace GlaccAuto.Core.Claim;

/// <summary>一次领取（或状态查询）的结果，供 CLI / 通知使用。</summary>
public sealed class ClaimReport
{
    public bool Success { get; init; }
    public bool NeedRelogin { get; init; }
    public string Message { get; init; } = "";
    public string Detail { get; init; } = "";
    public int ClaimIndex { get; init; }
    public int TotalClaims { get; init; }
    public double? BalanceMinutes { get; init; }

    public string BalanceText => BalanceMinutes is { } m
        ? $"{(int)Math.Round(m) / 60} 小时 {(int)Math.Round(m) % 60:00} 分"
        : "未知";
}

/// <summary>领取过程观察者（CLI 打日志、GUI 可忽略）。</summary>
public interface IClaimObserver
{
    void OnStatus(string message, bool isError = false);
    Task OnRetryAsync(string what, int attempt, TimeSpan wait);
}

/// <summary>空观察者。</summary>
public sealed class NullClaimObserver : IClaimObserver
{
    public static readonly NullClaimObserver Instance = new();
    public void OnStatus(string message, bool isError = false) { }
    public Task OnRetryAsync(string what, int attempt, TimeSpan wait) => Task.CompletedTask;
}
