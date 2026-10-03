namespace WorkBuddyAutoClaim;

internal sealed record RunStatus
{
    public DateTimeOffset UpdatedAt { get; init; }
    public string Mode { get; init; } = "Automatic";
    public string Outcome { get; init; } = "None";
    public string Message { get; init; } = "暂无领取记录";
    public string? BeforeBalance { get; init; }
    public string? AfterBalance { get; init; }
    public int AttemptsPerformed { get; init; }
    public int MaxAttempts { get; init; }
    public string? ExecutionChannel { get; init; }
    public string? ApiResult { get; init; }
    public string? CreditGained { get; init; }
    public int? StreakDays { get; init; }
    public string? EndpointHost { get; init; }
    public string? FallbackReason { get; init; }
    public bool BalanceFresh { get; init; } = true;
    public string? PendingEndpointHost { get; init; }
    public string? GrowthMessage { get; init; }
    public DateTimeOffset? GrowthUpdatedAt { get; init; }
    public int GrowthCreditsGained { get; init; }
    public int? GrowthEnergy { get; init; }
    public int? GrowthStreakDays { get; init; }
    public bool GrowthNeedsAttention { get; init; }
    public string? RouteDiscoveryMessage { get; init; }
    public DateTimeOffset? RouteDiscoveryUpdatedAt { get; init; }
    public string? UpdateMessage { get; init; }
    public DateTimeOffset? UpdateCheckedAt { get; init; }
    public string? AvailableVersion { get; init; }
}

internal sealed record DashboardStatusView(string Title, string Balance, string Detail, string UpdatedAt)
{
    internal static DashboardStatusView From(RunStatus? status)
    {
        if (status is null)
            return new DashboardStatusView("暂无领取记录", "--", "守护程序正在等待首次执行。", "--");

        var balance = status.AfterBalance ?? status.BeforeBalance ?? "未读取到";
        var title = status.Outcome switch
        {
            "Claimed" => "领取成功",
            "AlreadyClaimed" => "今日已领取",
            "Running" => "正在领取",
            "Queued" => "等待重试",
            "Pending" => "领取结果待确认",
            _ => "领取失败"
        };
        var balanceDetail = status.Outcome == "Claimed" && status.BalanceFresh &&
                            !string.IsNullOrWhiteSpace(status.BeforeBalance) &&
                            !string.IsNullOrWhiteSpace(status.AfterBalance)
            ? $"{status.BeforeBalance} → {status.AfterBalance}"
            : $"当前余额：{balance}{(status.BalanceFresh ? "" : "（接口领取后未刷新）")}";
        var attempts = status.MaxAttempts > 0
            ? $"尝试 {status.AttemptsPerformed}/{status.MaxAttempts}"
            : "尚未尝试";
        var mode = status.Mode == "Manual" ? "手动重试" : "每日自动领取";
        var channel = status.ExecutionChannel switch
        {
            "Api" => "接口快速通道",
            "OcrFallback" => "OCR 保底通道",
            _ => "传统 OCR 通道"
        };
        var reward = string.IsNullOrWhiteSpace(status.CreditGained) ? "" : $" · 本次 +{status.CreditGained}";
        var streak = status.StreakDays.HasValue ? $" · 连签 {status.StreakDays} 天" : "";
        var fallback = string.IsNullOrWhiteSpace(status.FallbackReason) ? "" : $" · 接口回退：{status.FallbackReason}";
        var growth = string.IsNullOrWhiteSpace(status.GrowthMessage) ? "" :
            $"\n成长中心（{status.GrowthUpdatedAt?.LocalDateTime:MM-dd HH:mm}）：{status.GrowthMessage}";
        var route = string.IsNullOrWhiteSpace(status.RouteDiscoveryMessage) ? "" :
            $"\n接口路由（{status.RouteDiscoveryUpdatedAt?.LocalDateTime:MM-dd HH:mm}）：{status.RouteDiscoveryMessage}";
        var update = string.IsNullOrWhiteSpace(status.UpdateMessage) ? "" :
            $"\n程序更新（{status.UpdateCheckedAt?.LocalDateTime:MM-dd HH:mm}）：{status.UpdateMessage}";
        return new DashboardStatusView(title, balance,
            $"{balanceDetail}{reward}{streak} · {attempts}{fallback}\n{mode} · {channel}：{status.Message}{growth}{route}{update}",
            status.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
    }
}
