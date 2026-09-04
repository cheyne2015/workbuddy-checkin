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
            _ => "领取失败"
        };
        var balanceDetail = status.Outcome == "Claimed" &&
                            !string.IsNullOrWhiteSpace(status.BeforeBalance) &&
                            !string.IsNullOrWhiteSpace(status.AfterBalance)
            ? $"{status.BeforeBalance} → {status.AfterBalance}"
            : $"当前余额：{balance}";
        var attempts = status.MaxAttempts > 0
            ? $"尝试 {status.AttemptsPerformed}/{status.MaxAttempts}"
            : "尚未尝试";
        var mode = status.Mode == "Manual" ? "手动重试" : "每日自动领取";
        return new DashboardStatusView(title, balance,
            $"{balanceDetail} · {attempts}\n{mode}：{status.Message}",
            status.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
    }
}
