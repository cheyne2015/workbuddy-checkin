using System.Globalization;

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
    public string? TotalCredits { get; init; }
    public string? TotalCreditsSource { get; init; }
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
    public Dictionary<string, string>? GrowthModules { get; init; }
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
        var growth = status.GrowthUpdatedAt.HasValue
            ? $"\n成长中心：本轮 +{status.GrowthCreditsGained} 积分 · {status.GrowthUpdatedAt.Value.LocalDateTime:MM-dd HH:mm}" +
              (status.GrowthNeedsAttention ? " · 需要处理" : "")
            : "";
        var route = string.IsNullOrWhiteSpace(status.RouteDiscoveryMessage) ? "" :
            $"\n接口路由（{status.RouteDiscoveryUpdatedAt?.LocalDateTime:MM-dd HH:mm}）：{status.RouteDiscoveryMessage}";
        var update = string.IsNullOrWhiteSpace(status.UpdateMessage) ? "" :
            $"\n程序更新（{status.UpdateCheckedAt?.LocalDateTime:MM-dd HH:mm}）：{status.UpdateMessage}";
        return new DashboardStatusView(title, balance,
            $"{balanceDetail}{reward}{streak} · {attempts}{fallback}\n{mode} · {channel}：{status.Message}{growth}{route}{update}",
            status.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
    }
}

internal sealed record GrowthModuleView(string Key, string Title, string Detail);

internal sealed record GrowthCenterDashboardView(
    string TotalCredits,
    string TotalCreditsSource,
    string StreakDays,
    string Energy,
    string CreditsGained,
    string UpdatedAt,
    string Summary,
    IReadOnlyList<GrowthModuleView> Modules,
    bool NeedsAttention)
{
    private static readonly (string Key, string Title)[] ModuleOrder =
    [
        ("travel", "旅行礼物"),
        ("tasks", "任务"),
        ("makeup", "补登"),
        ("redeem", "连登奖励"),
        ("lottery", "抽奖"),
        ("buddy", "Buddy 盲盒")
    ];

    internal static GrowthCenterDashboardView From(RunStatus? status)
    {
        if (status is null)
            return Empty("等待首次执行");

        var rawTotal = status.TotalCredits ?? status.AfterBalance ?? status.BeforeBalance;
        var total = FormatCredits(rawTotal);
        var source = status.TotalCreditsSource switch
        {
            "Api" => "接口实时",
            "Ocr" => "OCR 读取",
            "Cached" or "CachedOcr" => "最近缓存",
            _ when !string.IsNullOrWhiteSpace(status.TotalCredits) => "已记录",
            _ when !string.IsNullOrWhiteSpace(rawTotal) && status.BalanceFresh => "OCR 读取",
            _ when !string.IsNullOrWhiteSpace(rawTotal) => "最近缓存",
            _ => "尚未读取"
        };
        var modules = ModuleOrder.Select(module => new GrowthModuleView(
            module.Key,
            module.Title,
            status.GrowthModules?.TryGetValue(module.Key, out var detail) == true && !string.IsNullOrWhiteSpace(detail)
                ? detail
                : "暂无记录")).ToArray();
        var streak = status.StreakDays ?? status.GrowthStreakDays;
        return new GrowthCenterDashboardView(
            total,
            source,
            streak.HasValue ? $"{streak.Value} 天" : "--",
            status.GrowthEnergy?.ToString(CultureInfo.InvariantCulture) ?? "--",
            $"+{status.GrowthCreditsGained}",
            status.GrowthUpdatedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "--",
            !status.GrowthUpdatedAt.HasValue
                ? "暂无成长中心执行记录"
                : status.GrowthNeedsAttention
                    ? "部分项目需要处理，请查看下方明细。"
                    : status.GrowthCreditsGained > 0
                        ? $"本轮已完成，共获得 {status.GrowthCreditsGained} 积分。"
                        : "本轮已完成，暂无新增积分。",
            modules,
            status.GrowthNeedsAttention);
    }

    private static GrowthCenterDashboardView Empty(string summary) =>
        new("--", "尚未读取", "--", "--", "+0", "--", summary,
            ModuleOrder.Select(module => new GrowthModuleView(module.Key, module.Title, "暂无记录")).ToArray(), false);

    private static string FormatCredits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "--";
        return decimal.TryParse(value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString("#,0.##", CultureInfo.InvariantCulture)
            : value;
    }
}
