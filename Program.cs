using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WorkBuddyAutoClaim;

internal static class Program
{
    private const string TaskName = "WorkBuddy Auto Claim";
    private static readonly string BaseDir = AppContext.BaseDirectory;
    private static readonly string ConfigPath = Path.Combine(BaseDir, "config.json");
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim", "state.json");
    private static Mutex? _mutex;

    [STAThread]
    private static int Main(string[] args)
    {
        _mutex = new Mutex(true, "WorkBuddyAutoClaim.Singleton", out bool firstInstance);
        if (!firstInstance) return 0;

        try
        {
            var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "--daemon";
            var config = LoadConfig();
            if (command == "--install")
            {
                var exitCode = Install(config);
                _mutex.ReleaseMutex();
                _mutex = null;
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法解析程序路径。");
                Process.Start(new ProcessStartInfo(exe, "--daemon") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                return exitCode;
            }
            return command switch
            {
                "--uninstall" => Uninstall(),
                "--run-now" => RunOnce(config, notify: true),
                "--dry-run" => DryRun(config),
                "--verify-layout" => VerifyLayout(config),
                "--verify-card" => VerifyBuddyCard(args.Skip(1).FirstOrDefault()),
                "--ocr-screenshot" => OcrScreenshot(args.Skip(1).FirstOrDefault()),
                "--verify-claim-ocr" => VerifyClaimOcr(args.Skip(1).FirstOrDefault(), config),
                "--test-buddy-card" => TestBuddyCard(config),
                "--test-menu" => TestMenuClick(config),
                "--test-personal-center" => TestPersonalCenter(config),
                "--self-test" => SelfTest(config),
                "--daemon" => RunDaemon(config),
                _ => 2
            };
        }
        catch (Exception ex)
        {
            Log("致命错误: " + ex);
            Notify("WorkBuddy 自动领取", "工具发生错误，请查看日志。", ToolTipIcon.Error);
            return 1;
        }
        finally { _mutex?.ReleaseMutex(); }
    }

    private static int RunDaemon(Config config)
    {
        Log("后台守护已启动，领取时间: " + config.ClaimTime);
        if (!TimeSpan.TryParse(config.ClaimTime, out var claimTime))
            throw new InvalidOperationException("ClaimTime 必须是 HH:mm。");

        var retryDelay = TimeSpan.FromSeconds(Math.Max(10, config.RetryIntervalSeconds));
        while (true)
        {
            try
            {
                var now = DateTime.Now;
                var state = LoadState();
                var scheduledToday = now.Date.Add(claimTime);
                if (state.SuccessDate == DateOnly.FromDateTime(now))
                {
                    SleepUntil(scheduledToday.AddDays(1), "今天已成功领取");
                    continue;
                }

                if (now < scheduledToday)
                {
                    SleepUntil(scheduledToday, "尚未到领取时间");
                    continue;
                }

                if (!IsInteractiveDesktop())
                {
                    Log($"桌面已锁定；将在 {retryDelay.TotalSeconds:0} 秒后重试。");
                }
                else if (RunOnce(config, notify: true) != 0)
                {
                    Log($"领取未成功；将在 {retryDelay.TotalSeconds:0} 秒后重试。");
                }
            }
            catch (Exception ex) { Log("守护错误: " + ex.Message); }
            Thread.Sleep(retryDelay);
        }
    }

    private static void SleepUntil(DateTime wakeAt, string reason)
    {
        while (true)
        {
            var remaining = wakeAt - DateTime.Now;
            if (remaining <= TimeSpan.Zero) return;

            Log($"{reason}；休眠至 {wakeAt:yyyy-MM-dd HH:mm:ss}。");
            var milliseconds = Math.Min(remaining.TotalMilliseconds, int.MaxValue);
            Thread.Sleep((int)Math.Ceiling(milliseconds));
        }
    }

    private static int RunOnce(Config config, bool notify)
    {
        if (!IsInteractiveDesktop())
        {
            Log("桌面已锁定，跳过本次尝试。");
            return 3;
        }

        var originalWindow = FindWorkBuddyWindow();
        bool wasRunning = originalWindow != IntPtr.Zero;
        bool wasForeground = wasRunning && Native.GetForegroundWindow() == originalWindow;
        bool launchedByTool = false;
        IntPtr window = IntPtr.Zero;
        bool succeeded = false;
        string result = "未能确认领取成功";
        try
        {
            window = EnsureWorkBuddyWindow(config, out launchedByTool);
            if (window == IntPtr.Zero) throw new InvalidOperationException("未找到 WorkBuddy 主窗口。");
            if (launchedByTool)
            {
                // 最小化的 Electron 窗口会返回纯白截图；恢复为无焦点窗口才能读取卡片，
                // 不激活、不抢前台，完成后仍会自动关闭。
                Native.ShowWindow(window, Native.SW_SHOWNOACTIVATE);
                Log("WorkBuddy 由工具启动，已无焦点恢复以读取领取卡片。");
            }
            else if (wasForeground)
            {
                Log("WorkBuddy 原本在前台，保留前台状态领取。");
            }
            else
            {
                Native.ShowWindow(window, Native.SW_SHOWNOACTIVATE);
                Log("WorkBuddy 原本在后台，领取后将恢复最小化状态。");
            }

            for (int attempt = 1; attempt <= config.MaxAttempts && !succeeded; attempt++)
            {
                try
                {
                    Log($"开始领取，第 {attempt}/{config.MaxAttempts} 次。");
                    succeeded = TryClaimFromPersonalCenter(window, config, out result);
                }
                catch (Exception ex)
                {
                    result = ex.Message;
                    Log($"第 {attempt} 次失败: {ex.Message}");
                }
            }
        }
        finally
        {
            if (window != IntPtr.Zero)
            {
                if (launchedByTool)
                {
                    CloseWorkBuddy();
                    Log("WorkBuddy 由工具启动，领取流程结束后已关闭。");
                }
                else if (!wasForeground)
                {
                    Native.ShowWindow(window, Native.SW_MINIMIZE);
                    Log("WorkBuddy 原本在后台，领取流程结束后已最小化。");
                }
            }
        }

        if (succeeded)
        {
            SaveState(new State { SuccessDate = DateOnly.FromDateTime(DateTime.Today) });
            Log("完成: " + result);
            if (notify) Notify("WorkBuddy 自动领取", result, ToolTipIcon.Info);
            return 0;
        }

        Log("领取失败: " + result);
        if (notify) Notify("WorkBuddy 自动领取失败", "已连续尝试 5 次仍未成功，请手动领取。", ToolTipIcon.Error);
        return 1;
    }

    private static IntPtr EnsureWorkBuddyWindow(Config config)
    {
        return EnsureWorkBuddyWindow(config, out _);
    }

    private static IntPtr EnsureWorkBuddyWindow(Config config, out bool launchedByTool)
    {
        var existing = FindWorkBuddyWindow();
        if (existing != IntPtr.Zero)
        {
            launchedByTool = false;
            return existing;
        }
        launchedByTool = true;
        if (!File.Exists(config.WorkBuddyPath)) throw new FileNotFoundException("找不到 WorkBuddy.exe", config.WorkBuddyPath);
        Process.Start(new ProcessStartInfo(config.WorkBuddyPath) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
        var until = DateTime.UtcNow.AddSeconds(config.LaunchWaitSeconds);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(500);
            var window = FindWorkBuddyWindow();
            if (window != IntPtr.Zero) return window;
        }
        return IntPtr.Zero;
    }

    // Chromium/Electron 会把内容放在子窗口。领取按钮需要完整地处理按下和松开事件，
    // 因此同步发送到渲染窗口；不移动真实鼠标、不激活 WorkBuddy。
    private static void ClickWindowPoint(IntPtr topWindow, int windowX, int windowY)
    {
        var target = FindChromeChild(topWindow);
        if (target == IntPtr.Zero) target = topWindow;
        Native.GetWindowRect(topWindow, out var rect);
        var point = new Native.POINT { X = rect.Left + windowX, Y = rect.Top + windowY };
        Native.ScreenToClient(target, ref point);
        var lParam = (IntPtr)((point.Y << 16) | (point.X & 0xffff));
        Native.SendMessageTimeout(target, Native.WM_MOUSEMOVE, IntPtr.Zero, lParam,
            Native.SMTO_ABORTIFHUNG | Native.SMTO_BLOCK, Native.ClickMessageTimeoutMilliseconds, out _);
        Thread.Sleep(40);
        if (Native.SendMessageTimeout(target, Native.WM_LBUTTONDOWN, (IntPtr)1, lParam,
                Native.SMTO_ABORTIFHUNG | Native.SMTO_BLOCK, Native.ClickMessageTimeoutMilliseconds, out _) == IntPtr.Zero)
            throw new InvalidOperationException("向 WorkBuddy 发送鼠标按下事件超时。");
        Thread.Sleep(80);
        if (Native.SendMessageTimeout(target, Native.WM_LBUTTONUP, IntPtr.Zero, lParam,
                Native.SMTO_ABORTIFHUNG | Native.SMTO_BLOCK, Native.ClickMessageTimeoutMilliseconds, out _) == IntPtr.Zero)
            throw new InvalidOperationException("向 WorkBuddy 发送鼠标松开事件超时。");
    }

    private static bool LooksClaimed(IntPtr window, Config config)
    {
        int height = GetWindowHeight(window);
        if (height <= config.ClaimBottomOffset) return false;
        using var bitmap = CaptureWindow(window);
        if (bitmap is null || config.ClaimStatusX >= bitmap.Width || height - config.ClaimBottomOffset >= bitmap.Height)
        {
            Log("无法在后台捕获 WorkBuddy 窗口，拒绝把领取结果判为成功。");
            return false;
        }
        int y = height - config.ClaimBottomOffset;
        // 取按钮左右留白，避开“今日已领”文字本身的深灰抗锯齿像素。
        var samples = new[] { config.ClaimStatusX, config.ClaimStatusX + 80, config.ClaimStatusX + 88 }
            .Where(x => x < bitmap.Width)
            .Select(x => bitmap.GetPixel(x, y))
            .ToArray();
        // 已领取按钮背景在本机为 RGB(242,242,242)。菜单关闭后的背景是纯白(255,255,255)，
        // 因此必须由三个采样点同时落在窄灰阶范围内，不能只判断“颜色接近灰色”。
        bool darkTheme = IsDarkPersonalCenter(bitmap, config);
        bool claimed = IsClaimedButtonBackground(samples, darkTheme);
        Log($"状态像素 {string.Join(", ", samples.Select(c => $"RGB({c.R},{c.G},{c.B})"))}，主题={(darkTheme ? "深色" : "浅色")}，已领取判定: {claimed}");
        return claimed;
    }

    private static bool LooksPopupClaimed(IntPtr window, Config config)
    {
        return InspectPopupButton(window, config, expectDisabled: true);
    }

    private static bool TryClaimFromPersonalCenter(IntPtr window, Config config, out string result)
    {
        // 以“积分余额”文字为个人中心锚点。这样页面的卡片颜色、尺寸和按钮位置改变时，
        // 仍然只会在确认个人中心已经打开后，点击 OCR 实际读到的领取文字。
        if (!TryOpenPersonalCenterAndReadEvidence(window, config, out var evidence))
        {
            result = "未能在左下个人中心识别到“积分余额”，拒绝猜测点击";
            return false;
        }
        if (evidence.HasSuccessText)
        {
            result = "WorkBuddy 今日已领取";
            return true;
        }
        if (evidence.Actions.Count == 0)
        {
            result = "OCR 未识别到 签到/领取/体验 等可领取按钮文字";
            return false;
        }

        var triedActions = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var action = evidence.Actions.FirstOrDefault(candidate =>
                !triedActions.Contains(candidate.DeduplicationKey));
            if (action is null) break;
            triedActions.Add(action.DeduplicationKey);

            // 每一次点击都使用该次点击前刚读到的余额指纹，绝不把上一次截图当作本次基线。
            var beforeBalance = evidence.Balance ?? throw new InvalidOperationException("领取前丢失了积分余额 OCR 锚点。");
            Log($"OCR 识别领取候选：{action.Keyword}，文本={action.Text}，位置=({action.CenterX},{action.CenterY})，积分余额记录={beforeBalance.RawText}。");
            SaveBuddyDiagnosticCapture(window, "before-claim");
            ClickWindowPoint(window, action.CenterX, action.CenterY);
            var verification = WaitForClaimResult(window, config, beforeBalance, TimeSpan.FromSeconds(20));
            bool claimed = verification != ClaimVerification.NotConfirmed;
            SaveBuddyDiagnosticCapture(window, claimed ? "after-claim-success" : "after-claim-failure");
            if (claimed)
            {
                result = verification switch
                {
                    ClaimVerification.ClaimedText => "领取成功，OCR 已识别今日已领或领取成功文字",
                    ClaimVerification.BalanceChanged => "领取成功，OCR 识别到积分余额变化",
                    _ => "领取成功"
                };
                return true;
            }

            // 失败后重新从个人中心和余额锚点读取当前页面，再处理还未点击过的文字候选。
            if (!TryOpenPersonalCenterAndReadEvidence(window, config, out evidence)) break;
            if (evidence.HasSuccessText)
            {
                result = "领取成功，OCR 已识别今日已领或领取成功文字";
                return true;
            }
        }

        result = "已依次点击 OCR 识别到的领取候选，但未识别到成功状态或积分余额变化";
        return false;
    }

    private static bool TryOpenPersonalCenterAndReadEvidence(IntPtr window, Config config, out MenuEvidence evidence)
    {
        using (var current = CaptureWindow(window))
        {
            if (current is not null)
            {
                evidence = ReadMenuEvidence(current, config);
                if (evidence.Balance is not null) return true;
            }
        }

        int height = GetWindowHeight(window);
        if (height <= config.ProfileBottomOffset)
        {
            evidence = MenuEvidence.Empty;
            return false;
        }

        Log("未发现积分余额；打开左下个人中心并等待 OCR 锚点加载。");
        ClickWindowPoint(window, config.ProfileX, height - config.ProfileBottomOffset);
        var until = DateTime.UtcNow.AddSeconds(config.CardReadyTimeoutSeconds);
        do
        {
            using var image = CaptureWindow(window);
            if (image is not null)
            {
                evidence = ReadMenuEvidence(image, config);
                if (evidence.Balance is not null) return true;
            }
            Thread.Sleep(650);
        }
        while (DateTime.UtcNow < until);

        evidence = MenuEvidence.Empty;
        Log("打开个人中心后仍未通过 OCR 识别到积分余额。");
        return false;
    }

    // 领取统一从左下个人菜单进行。主界面检查只用于避免菜单已经打开时被再次点击关闭，
    // 领取按钮、今日已领状态和积分余额均必须来自菜单内的 Buddy 加油站卡片。
    private static bool TryFindBuddyCardInPersonalMenu(IntPtr window, Config config, out BuddyCard card)
    {
        int windowHeight = GetWindowHeight(window);
        if (TryFindBuddyCard(window, out card) && IsPersonalMenuCard(card, windowHeight)) return true;

        if (windowHeight <= config.ProfileBottomOffset)
        {
            card = default;
            return false;
        }

        Log("打开左下个人菜单后等待 Buddy 加油站卡片加载。");
        ClickWindowPoint(window, config.ProfileX, windowHeight - config.ProfileBottomOffset);
        var until = DateTime.UtcNow.AddSeconds(config.CardReadyTimeoutSeconds);
        do
        {
            if (TryFindBuddyCard(window, out card, logMissing: false) && IsPersonalMenuCard(card, windowHeight)) return true;
            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < until);

        card = default;
        Log("打开个人菜单后仍未识别到 Buddy 加油站卡片。");
        return false;
    }

    private static ClaimVerification WaitForClaimResult(
        IntPtr window, Config config, BalanceReading beforeBalance, TimeSpan timeout)
    {
        var until = DateTime.UtcNow.Add(timeout);
        var reopenMenuAfter = DateTime.UtcNow.AddSeconds(2);
        bool reopenedMenu = false;
        do
        {
            bool hasBalanceAnchor = false;
            using (var image = CaptureWindow(window))
            {
                if (image is not null)
                {
                    var evidence = ReadMenuEvidence(image, config);
                    hasBalanceAnchor = evidence.Balance is not null;
                    if (evidence.HasSuccessText) return ClaimVerification.ClaimedText;
                    if (evidence.Balance is not null && !StringComparer.Ordinal.Equals(beforeBalance.Fingerprint, evidence.Balance.Fingerprint))
                    {
                        Log($"OCR 积分余额变化：{beforeBalance.RawText} -> {evidence.Balance.RawText}。");
                        return ClaimVerification.BalanceChanged;
                    }
                }
            }
            if (!hasBalanceAnchor && !reopenedMenu && DateTime.UtcNow >= reopenMenuAfter)
            {
                if (GetWindowHeight(window) > config.ProfileBottomOffset)
                {
                    Log("领取后未读到个人中心；重新打开左下个人中心核验领取结果。");
                    TryOpenPersonalCenterAndReadEvidence(window, config, out _);
                    reopenedMenu = true;
                }
            }
            Thread.Sleep(650);
        }
        while (DateTime.UtcNow < until);
        return ClaimVerification.NotConfirmed;
    }

    // 领取是不可逆操作。保留最近一次点击前后的窗口截图，便于区分“未点到”、
    // “页面过渡中”与“点击后未更新”，而不把之后的状态倒推为本次点击成功。
    private static void SaveBuddyDiagnosticCapture(IntPtr window, string phase)
    {
        using var image = CaptureWindow(window);
        if (image is null) return;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, $"workbuddy-{phase}.png");
        image.Save(output, ImageFormat.Png);
        Log($"领取诊断截图已保存: {output}");
    }

    private enum ClaimVerification { NotConfirmed, ClaimedText, BalanceChanged }

    private readonly record struct BuddyCard(int Left, int HeaderTop, int Width)
    {
        public int ButtonCenterX => Left + (int)Math.Round(Width * 0.25);
        public int ButtonCenterY => HeaderTop + (int)Math.Round(Width * 0.64);
    }

    private sealed class OcrSnapshot
    {
        public string Language { get; set; } = "";
        public List<OcrLine> Lines { get; set; } = [];
    }

    private sealed class OcrLine
    {
        public string Text { get; set; } = "";
        public List<OcrWord> Words { get; set; } = [];
    }

    private sealed class OcrWord
    {
        public string Text { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    private readonly record struct OcrBounds(int Left, int Top, int Right, int Bottom)
    {
        public int CenterX => Left + (Right - Left) / 2;
        public int CenterY => Top + (Bottom - Top) / 2;
    }

    // Bounds is deliberately the “积分余额” label position, not the numeric value position:
    // action text is normally aligned with the label on the left side of the card.
    private sealed record BalanceReading(string RawText, string Fingerprint, OcrBounds Bounds);
    private sealed record ClaimAction(string Keyword, string Text, int CenterX, int CenterY)
    {
        public string DeduplicationKey => Keyword + "|" + NormalizeOcrText(Text);
    }
    private sealed record MenuEvidence(BalanceReading? Balance, IReadOnlyList<ClaimAction> Actions, bool HasSuccessText)
    {
        public static readonly MenuEvidence Empty = new(null, [], false);
    }

    private static int OcrScreenshot(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("请提供可读取的截图路径。", imagePath);
        using var bitmap = new Bitmap(imagePath);
        var ocr = ReadOcr(bitmap);
        Console.WriteLine(string.Join(Environment.NewLine, ocr.Lines.Select(line =>
            $"{string.Join(' ', line.Words.Select(word => $"{word.X},{word.Y},{word.Width},{word.Height}"))}: {line.Text}")));
        Log($"OCR 截图测试完成：语言={ocr.Language}，识别到 {ocr.Lines.Count} 行文字。");
        return 0;
    }

    private static int VerifyClaimOcr(string? imagePath, Config config)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("请提供可读取的截图路径。", imagePath);
        using var bitmap = new Bitmap(imagePath);
        var evidence = ReadMenuEvidence(bitmap, config);
        if (evidence.Balance is null)
            throw new InvalidOperationException("OCR 未读取到积分余额。\n");
        if (!evidence.HasSuccessText && evidence.Actions.Count == 0)
            throw new InvalidOperationException("OCR 未识别到成功状态或可领取文字。\n");
        Console.WriteLine($"余额={evidence.Balance.RawText}; 成功文字={evidence.HasSuccessText}; 候选={string.Join(", ", evidence.Actions.Select(action => action.Text))}");
        Log($"OCR 领取截图验证通过：余额={evidence.Balance.RawText}，成功文字={evidence.HasSuccessText}，候选数量={evidence.Actions.Count}。\n");
        return 0;
    }

    private static OcrSnapshot ReadOcr(Bitmap bitmap)
    {
        var script = Path.Combine(BaseDir, "workbuddy-ocr.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("找不到 Windows OCR 脚本。", script);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
        Directory.CreateDirectory(folder);
        var imagePath = Path.Combine(folder, $"ocr-{Environment.ProcessId}-{Guid.NewGuid():N}.png");
        bitmap.Save(imagePath, ImageFormat.Png);
        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // workbuddy-ocr.ps1 deliberately emits UTF-8 JSON. Do not let the
                // current Windows ANSI code page corrupt OCR text before JSON parsing.
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("-ImagePath");
            start.ArgumentList.Add(imagePath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Windows OCR。\n");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(outputTask, errorTask);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Windows OCR 失败: " + errorTask.Result.Trim());
            byte[] jsonBytes;
            try { jsonBytes = Convert.FromBase64String(outputTask.Result.Trim()); }
            catch (FormatException ex) { throw new InvalidOperationException("Windows OCR 返回的数据格式无效。", ex); }
            return JsonSerializer.Deserialize<OcrSnapshot>(Encoding.UTF8.GetString(jsonBytes),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException("Windows OCR 未返回可读取的结果。");
        }
        finally { try { File.Delete(imagePath); } catch { } }
    }

    private static MenuEvidence ReadMenuEvidence(Bitmap bitmap, Config config)
    {
        var ocr = ReadOcr(bitmap);
        var balance = TryReadBalance(ocr);
        var actions = balance is null ? [] : FindClaimActions(ocr, balance, config.ClaimActionKeywords);
        bool hasSuccessText = balance is not null && HasClaimSuccessText(ocr, balance);
        return new MenuEvidence(balance, actions, hasSuccessText);
    }

    private static BalanceReading? TryReadBalance(OcrSnapshot snapshot)
    {
        var label = snapshot.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line)))
            .FirstOrDefault(item => NormalizeOcrText(item.Line.Text).Contains("积分余额", StringComparison.Ordinal));
        if (label.Line is null) return null;

        var candidate = snapshot.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line)))
            .Where(item => item.Line.Words.Count > 0 && item.Bounds.Left > label.Bounds.Right &&
                           Math.Abs(item.Bounds.CenterY - label.Bounds.CenterY) <= 40 &&
                           item.Line.Text.Count(char.IsDigit) >= 2)
            .OrderBy(item => Math.Abs(item.Bounds.CenterY - label.Bounds.CenterY))
            .ThenBy(item => item.Bounds.Left)
            .FirstOrDefault();
        if (candidate.Line is null) return null;

        var fingerprint = new string(candidate.Line.Text.Where(char.IsDigit).ToArray());
        return fingerprint.Length >= 2 ? new BalanceReading(candidate.Line.Text, fingerprint, label.Bounds) : null;
    }

    private static IReadOnlyList<ClaimAction> FindClaimActions(
        OcrSnapshot snapshot, BalanceReading balance, IEnumerable<string>? configuredKeywords)
    {
        var keywords = (configuredKeywords ?? Config.DefaultClaimActionKeywords)
            .Select(NormalizeOcrText)
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .OrderByDescending(keyword => keyword.Length)
            .ToArray();
        // 按钮通常在“积分余额”同一张个人中心卡片的上方。范围是相对余额文字推导，
        // 不是任何特定 WorkBuddy 版本的绝对坐标或卡片颜色。
        int actionLeft = Math.Max(0, balance.Bounds.Left - 48);
        int actionRight = balance.Bounds.Right + 320;
        int actionTop = Math.Max(0, balance.Bounds.Top - 150);
        int actionBottom = balance.Bounds.Bottom + 56;
        return snapshot.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line), Normalized: NormalizeOcrText(line.Text)))
            .Where(item => item.Line.Words.Count > 0 && item.Bounds.Left >= actionLeft &&
                           item.Bounds.Right <= actionRight &&
                           item.Bounds.CenterY >= actionTop && item.Bounds.CenterY <= actionBottom &&
                           !item.Normalized.Contains("已领", StringComparison.Ordinal) &&
                           !item.Normalized.Contains("成功", StringComparison.Ordinal))
            .Select(item =>
            {
                var keyword = keywords.FirstOrDefault(value => item.Normalized.Contains(value, StringComparison.Ordinal));
                return string.IsNullOrWhiteSpace(keyword) ? null : new ClaimAction(keyword, item.Line.Text, item.Bounds.CenterX, item.Bounds.CenterY);
            })
            .Where(action => action is not null)
            .Select(action => action!)
            .GroupBy(action => (action.CenterX / 8, action.CenterY / 8))
            .Select(group => group.First())
            .ToArray();
    }

    private static bool HasClaimSuccessText(OcrSnapshot snapshot, BalanceReading balance)
    {
        int left = Math.Max(0, balance.Bounds.Left - 48);
        int right = balance.Bounds.Right + 320;
        int top = Math.Max(0, balance.Bounds.Top - 180);
        int bottom = balance.Bounds.Bottom + 96;
        return snapshot.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line), Normalized: NormalizeOcrText(line.Text)))
            .Where(item => item.Line.Words.Count > 0 && item.Bounds.Left >= left && item.Bounds.Right <= right &&
                           item.Bounds.CenterY >= top && item.Bounds.CenterY <= bottom)
            .Any(item => item.Normalized.Contains("今日已领", StringComparison.Ordinal) ||
                         item.Normalized.Contains("本期已领", StringComparison.Ordinal) ||
                         item.Normalized.Contains("已领取", StringComparison.Ordinal) ||
                         item.Normalized.Contains("领取成功", StringComparison.Ordinal) ||
                         item.Normalized.Contains("领成功", StringComparison.Ordinal) ||
                         item.Normalized.Contains("签到成功", StringComparison.Ordinal) ||
                         Regex.IsMatch(item.Line.Text, @"\+\s*\d{2,4}"));
    }

    private static OcrBounds GetOcrBounds(OcrLine line)
    {
        if (line.Words.Count == 0) return default;
        return new OcrBounds(line.Words.Min(word => word.X), line.Words.Min(word => word.Y),
            line.Words.Max(word => word.X + word.Width), line.Words.Max(word => word.Y + word.Height));
    }

    private static string NormalizeOcrText(string text) =>
        Regex.Replace(text, "[\\s\\p{P}\\p{S}]", string.Empty);

    private static bool IsPersonalMenuCard(BuddyCard card, int windowHeight) =>
        windowHeight > 0 && card.HeaderTop < windowHeight * 0.60;

    private static bool TryFindBuddyCard(IntPtr window, out BuddyCard card, bool logMissing = true)
    {
        using var bitmap = CaptureWindow(window);
        if (bitmap is null)
        {
            card = default;
            Log("无法在后台捕获 WorkBuddy 窗口。");
            return false;
        }
        return TryFindBuddyCard(bitmap, out card, logMissing);
    }

    private static bool TryFindBuddyCard(Bitmap bitmap, out BuddyCard card, bool logMissing = true)
    {
        const int minHeaderWidth = 120;
        const int maxGap = 12;
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            int start = -1;
            int lastGreen = -1;
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                if (IsBuddyGreen(bitmap.GetPixel(x, y)))
                {
                    if (start < 0) start = x;
                    lastGreen = x;
                    continue;
                }

                if (start >= 0 && x - lastGreen > maxGap)
                {
                    if (lastGreen - start >= minHeaderWidth)
                    {
                        card = new BuddyCard(start, y, lastGreen - start + 2);
                        Log($"已识别 Buddy 加油站卡片：x={card.Left}, y={card.HeaderTop}, width={card.Width}。");
                        return true;
                    }
                    start = -1;
                    lastGreen = -1;
                }
            }
            if (start >= 0 && lastGreen - start >= minHeaderWidth)
            {
                card = new BuddyCard(start, y, lastGreen - start + 2);
                Log($"已识别 Buddy 加油站卡片：x={card.Left}, y={card.HeaderTop}, width={card.Width}。");
                return true;
            }
        }
        card = default;
        if (logMissing) Log("未在窗口中找到 Buddy 加油站绿色卡片。");
        return false;
    }

    private static bool IsBuddyGreen(Color color) =>
        color.G >= 110 && color.G - color.R >= 40 && color.G - color.B >= 15;

    private static bool LooksBuddyCardClaimed(IntPtr window, BuddyCard card)
    {
        using var bitmap = CaptureWindow(window);
        return bitmap is not null && LooksBuddyCardClaimed(bitmap, card);
    }

    private static bool LooksBuddyCardClaimed(Bitmap bitmap, BuddyCard card)
    {
        if (!TryGetBuddyButtonSamples(bitmap, card, out var samples, out bool darkTheme)) return false;
        bool claimed = IsClaimedButtonBackground(samples, darkTheme);
        Log($"Buddy 加油站状态像素 {string.Join(", ", samples.Select(c => $"RGB({c.R},{c.G},{c.B})"))}，主题={(darkTheme ? "深色" : "浅色")}，已领取判定: {claimed}");
        return claimed;
    }

    private static bool LooksBuddyCardClaimButtonEnabled(IntPtr window, BuddyCard card)
    {
        using var bitmap = CaptureWindow(window);
        return bitmap is not null && LooksBuddyCardClaimButtonEnabled(bitmap, card);
    }

    private static bool LooksBuddyCardClaimButtonEnabled(Bitmap bitmap, BuddyCard card)
    {
        if (!TryGetBuddyButtonSamples(bitmap, card, out var samples, out bool darkTheme)) return false;
        bool enabled = samples.Length == 3 && samples.All(c =>
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            int min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max - min <= 18 && (darkTheme ? max >= 210 : max <= 140);
        });
        Log($"Buddy 加油站领取按钮: {string.Join(", ", samples.Select(c => $"RGB({c.R},{c.G},{c.B})"))}，主题={(darkTheme ? "深色" : "浅色")}，可领取判定: {enabled}");
        return enabled;
    }

    private static bool TryGetBuddyButtonSamples(Bitmap bitmap, BuddyCard card, out Color[] samples, out bool darkTheme)
    {
        // 领取按钮的居中文字会改变中心行的少量像素；从按钮上方的纯背景取样。
        int y = card.ButtonCenterY - Math.Max(6, card.Width / 25);
        var xs = new[] { 0.08, 0.38, 0.44 }
            .Select(factor => card.Left + (int)Math.Round(card.Width * factor))
            .ToArray();
        if (y < 0 || y >= bitmap.Height || xs.Any(x => x < 0 || x >= bitmap.Width))
        {
            samples = Array.Empty<Color>();
            darkTheme = false;
            return false;
        }
        samples = xs.Select(x => bitmap.GetPixel(x, y)).ToArray();
        darkTheme = IsDarkBuddyCard(bitmap, card);
        return true;
    }

    private static bool IsDarkBuddyCard(Bitmap bitmap, BuddyCard card)
    {
        var points = new[] { (0.84, 0.35), (0.84, 0.45), (0.84, 0.50) }
            .Select(point => (
                X: card.Left + (int)Math.Round(card.Width * point.Item1),
                Y: card.HeaderTop + (int)Math.Round(card.Width * point.Item2)))
            .Where(point => point.X >= 0 && point.X < bitmap.Width && point.Y >= 0 && point.Y < bitmap.Height)
            .Select(point => bitmap.GetPixel(point.X, point.Y))
            .ToArray();
        return points.Length > 0 && points.All(c => Math.Max(c.R, Math.Max(c.G, c.B)) < 100);
    }

    private static bool OpenPersonalCenter(IntPtr window, Config config)
    {
        return TryOpenPersonalCenterAndReadEvidence(window, config, out _);
    }

    private static bool IsPersonalCenterReady(IntPtr window, Config config)
    {
        using var bitmap = CaptureWindow(window);
        if (bitmap is null || config.PersonalCenterCardHeaderX >= bitmap.Width || config.PersonalCenterCardHeaderY >= bitmap.Height) return false;
        var c = bitmap.GetPixel(config.PersonalCenterCardHeaderX, config.PersonalCenterCardHeaderY);
        bool ready = c.G >= 75 && c.G - c.R >= 50 && c.G - c.B >= 15;
        Log($"个人中心签到卡片: RGB({c.R},{c.G},{c.B})，展开判定: {ready}");
        return ready;
    }

    private static bool LooksMenuClaimButtonEnabled(IntPtr window, Config config)
    {
        int height = GetWindowHeight(window);
        using var bitmap = CaptureWindow(window);
        if (bitmap is null || height <= config.ClaimBottomOffset) return false;
        int y = height - config.ClaimBottomOffset;
        var samples = new[] { 50, 130 }.Where(x => x < bitmap.Width).Select(x => bitmap.GetPixel(x, y)).ToArray();
        bool darkTheme = IsDarkPersonalCenter(bitmap, config);
        bool enabled = samples.Length == 2 && samples.All(c =>
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            int min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max - min <= 18 && (darkTheme ? max >= 210 : max <= 140);
        });
        Log($"个人中心领取按钮: {string.Join(", ", samples.Select(c => $"RGB({c.R},{c.G},{c.B})"))}，主题={(darkTheme ? "深色" : "浅色")}，可领取判定: {enabled}");
        return enabled;
    }

    private static bool TryClaimVisiblePopup(IntPtr window, Config config)
    {
        if (!InspectPopupButton(window, config, expectDisabled: false)) return false;
        ClickWindowPoint(window, config.PopupClaimX, GetWindowHeight(window) - config.PopupClaimBottomOffset);
        Thread.Sleep(1200);
        return LooksPopupClaimed(window, config);
    }

    private static bool InspectPopupButton(IntPtr window, Config config, bool expectDisabled)
    {
        int height = GetWindowHeight(window);
        using var bitmap = CaptureWindow(window);
        if (bitmap is null || height <= config.PopupHeaderBottomOffset || height <= config.PopupClaimBottomOffset) return false;
        int headerY = height - config.PopupHeaderBottomOffset;
        int buttonY = height - config.PopupClaimBottomOffset;
        if (config.PopupHeaderX >= bitmap.Width || buttonY >= bitmap.Height || headerY >= bitmap.Height) return false;
        var header = bitmap.GetPixel(config.PopupHeaderX, headerY);
        bool popupOpen = header.G >= 150 && header.R <= 90 && header.B >= 100;
        var buttonSamples = new[] { 35, 115 }.Where(x => x < bitmap.Width).Select(x => bitmap.GetPixel(x, buttonY)).ToArray();
        bool disabled = buttonSamples.Length == 2 && buttonSamples.All(c =>
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            int min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max - min <= 8 && max >= 230 && max <= 248;
        });
        bool enabled = buttonSamples.Length == 2 && buttonSamples.All(c =>
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            int min = Math.Min(c.R, Math.Min(c.G, c.B));
            // WorkBuddy 5.2.6 的“立即领取”按钮边缘实测为 RGB(121,121,121)。
            return max - min <= 18 && max <= 140;
        });
        // 标题区域会随活动文案和关闭图标变化，不能作为领取按钮的前置条件；
        // 两个按钮边缘采样点才是稳定且足够窄的实际动作判据。
        bool matched = expectDisabled ? disabled : enabled;
        Log($"领取卡片: header=RGB({header.R},{header.G},{header.B}), openHint={popupOpen}, button={string.Join(", ", buttonSamples.Select(c => $"RGB({c.R},{c.G},{c.B})"))}, disabled={disabled}, enabled={enabled}");
        return matched;
    }

    private static bool IsClaimedButtonBackground(IEnumerable<Color> samples, bool darkTheme = false)
    {
        var colors = samples.ToArray();
        if (colors.Length != 3) return false;
        return colors.All(c =>
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            int min = Math.Min(c.R, Math.Min(c.G, c.B));
            // 浅色已领按钮约 RGB(242,242,242)；深色已领按钮约 RGB(47,47,47)。
            return max - min <= 8 && (darkTheme ? max >= 38 && max <= 72 : max >= 230 && max <= 248);
        }) || colors.All(IsClaimRewardGreen);
    }

    // 2026-07-23 实际领取成功后，WorkBuddy 将按钮改为绿色“+100 ✓”，而非“今日已领”灰色按钮。
    private static bool IsClaimRewardGreen(Color color) =>
        color.G >= 125 && color.R <= 80 && color.B <= 170 && color.G - color.R >= 60 && color.G - color.B >= 20;

    private static bool IsDarkPersonalCenter(Bitmap bitmap, Config config)
    {
        if (config.PersonalCenterInfoX >= bitmap.Width || config.PersonalCenterInfoY >= bitmap.Height) return false;
        var c = bitmap.GetPixel(config.PersonalCenterInfoX, config.PersonalCenterInfoY);
        return Math.Max(c.R, Math.Max(c.G, c.B)) < 100;
    }

    private static int GetWindowHeight(IntPtr hwnd)
    {
        Native.GetWindowRect(hwnd, out var rect);
        return rect.Bottom - rect.Top;
    }

    private static IntPtr FindWorkBuddyWindow()
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            var title = Native.GetWindowText(hwnd);
            if (title.Equals("WorkBuddy", StringComparison.OrdinalIgnoreCase)) { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr FindChromeChild(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumChildWindows(parent, (hwnd, _) =>
        {
            var cls = Native.GetClassName(hwnd);
            if (cls.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)) { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void CloseWorkBuddy()
    {
        foreach (var process in Process.GetProcessesByName("WorkBuddy"))
        {
            try { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } catch { }
        }
    }

    private static bool IsInteractiveDesktop()
    {
        // GENERIC_READ 会在普通交互桌面上被拒绝；只请求读取桌面名称所需的最小权限。
        var desktop = Native.OpenInputDesktop(0, false, Native.DESKTOP_SWITCHDESKTOP | Native.DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero)
        {
            Log($"无法打开输入桌面，Win32Error={Marshal.GetLastWin32Error()}。");
            return false;
        }
        try
        {
            var name = Native.GetDesktopName(desktop);
            bool interactive = name.Equals("Default", StringComparison.OrdinalIgnoreCase);
            if (!interactive) Log($"当前输入桌面为 {name}，等待解锁。");
            return interactive;
        }
        finally { Native.CloseDesktop(desktop); }
    }

    private static int Install(Config config)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法解析程序路径。");
        var xmlPath = Path.Combine(Path.GetTempPath(), "workbuddy-auto-claim-task.xml");
        var escapedExe = SecurityElement.Escape(exe) ?? throw new InvalidOperationException("无法转义程序路径。");
        var escapedDir = SecurityElement.Escape(BaseDir) ?? throw new InvalidOperationException("无法转义工作目录。");
        var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><RunLevel>LeastPrivilege</RunLevel><LogonType>InteractiveToken</LogonType></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>
  <Actions Context=""Author""><Exec><Command>{escapedExe}</Command><Arguments>--daemon</Arguments><WorkingDirectory>{escapedDir}</WorkingDirectory></Exec></Actions>
</Task>";
        File.WriteAllText(xmlPath, xml, new System.Text.UnicodeEncoding());
        try { RunProcess("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F"); }
        finally { try { File.Delete(xmlPath); } catch { } }
        // 安装发生在当天领取时间之后时，从下一天开始，避免安装动作立刻打断正在使用的 WorkBuddy。
        if (DateTime.TryParse(config.ClaimTime, out var scheduled) && DateTime.Now.TimeOfDay >= scheduled.TimeOfDay)
            SaveState(new State { SuccessDate = DateOnly.FromDateTime(DateTime.Today) });
        Log("已安装开机自启任务。\n");
        Notify("WorkBuddy 自动领取", "已启用：每天 00:00 后自动领取。", ToolTipIcon.Info);
        return 0;
    }

    private static int Uninstall()
    {
        RunProcess("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
        Log("已删除开机自启任务。\n");
        return 0;
    }

    private static int DryRun(Config config)
    {
        var window = FindWorkBuddyWindow();
        Log(window == IntPtr.Zero ? "检查结果: WorkBuddy 未运行。" : $"检查结果: 已找到窗口 0x{window.ToInt64():X}，高度 {GetWindowHeight(window)}。");
        Log(File.Exists(config.WorkBuddyPath) ? "程序路径: 正常。" : "程序路径: 不存在。\n");
        return window == IntPtr.Zero ? 1 : 0;
    }

    private static int VerifyLayout(Config config)
    {
        var window = FindWorkBuddyWindow();
        if (window == IntPtr.Zero) throw new InvalidOperationException("WorkBuddy 未运行，无法验证布局。");
        using var image = CaptureWindow(window) ?? throw new InvalidOperationException("无法在后台捕获 WorkBuddy 窗口。");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, "workbuddy-background-capture.png");
        image.Save(output, ImageFormat.Png);
        Log($"后台截图验证成功: {image.Width}x{image.Height}，文件: {output}");
        return 0;
    }

    // 对保存的 WorkBuddy 截图校验动态卡片定位；不控制 WorkBuddy，也不会领取积分。
    private static int VerifyBuddyCard(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("请提供可读取的 WorkBuddy 截图路径。", imagePath);

        using var bitmap = new Bitmap(imagePath);
        if (!TryFindBuddyCard(bitmap, out var card))
            throw new InvalidOperationException("截图中未识别到 Buddy 加油站领取卡片。");

        bool claimed = LooksBuddyCardClaimed(bitmap, card);
        bool enabled = !claimed && LooksBuddyCardClaimButtonEnabled(bitmap, card);
        if (!claimed && !enabled)
            throw new InvalidOperationException("截图中的 Buddy 加油站按钮既非已领取也非可领取。");

        Log($"截图卡片验证通过：x={card.Left}, y={card.HeaderTop}, width={card.Width}，状态={(claimed ? "今日已领" : "可领取")}。");
        return 0;
    }

    // 只验证真实窗口中的动态卡片定位和领取状态，保存截图，但绝不点击“立即领取”。
    private static int TestBuddyCard(Config config)
    {
        var originalWindow = FindWorkBuddyWindow();
        bool wasRunning = originalWindow != IntPtr.Zero;
        bool wasForeground = wasRunning && Native.GetForegroundWindow() == originalWindow;
        bool launchedByTool = false;
        IntPtr window = IntPtr.Zero;
        try
        {
            window = EnsureWorkBuddyWindow(config, out launchedByTool);
            if (window == IntPtr.Zero) throw new InvalidOperationException("未找到 WorkBuddy 主窗口。");
            Native.ShowWindow(window, Native.SW_SHOWNOACTIVATE);
            Thread.Sleep(1200);

            if (!TryFindBuddyCardInPersonalMenu(window, config, out var card))
                throw new InvalidOperationException("未识别到 Buddy 加油站领取卡片。");
            using var image = CaptureWindow(window) ?? throw new InvalidOperationException("无法捕获 Buddy 加油站卡片。");
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
            Directory.CreateDirectory(folder);
            image.Save(Path.Combine(folder, "workbuddy-buddy-card-test.png"), ImageFormat.Png);

            bool claimed = LooksBuddyCardClaimed(image, card);
            bool enabled = !claimed && LooksBuddyCardClaimButtonEnabled(image, card);
            if (!claimed && !enabled)
                throw new InvalidOperationException("Buddy 加油站按钮既非已领取也非可领取。");
            Log($"Buddy 加油站真实窗口测试完成：状态={(claimed ? "今日已领" : "可领取")}，未执行领取点击。");
            return 0;
        }
        finally
        {
            if (window != IntPtr.Zero)
            {
                if (launchedByTool) CloseWorkBuddy();
                else if (!wasForeground) Native.ShowWindow(window, Native.SW_MINIMIZE);
            }
        }
    }

    // 仅供校准：点击账户入口、保存菜单截图、再点一次还原；不会领取积分，也不会关闭 WorkBuddy。
    private static int TestMenuClick(Config config)
    {
        var window = EnsureWorkBuddyWindow(config);
        if (window == IntPtr.Zero) throw new InvalidOperationException("未找到 WorkBuddy 主窗口。");
        ClickWindowPoint(window, config.ProfileX, GetWindowHeight(window) - config.ProfileBottomOffset);
        Thread.Sleep(900);
        using (var image = CaptureWindow(window) ?? throw new InvalidOperationException("点击后无法捕获 WorkBuddy 窗口。"))
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
            Directory.CreateDirectory(folder);
            image.Save(Path.Combine(folder, "workbuddy-menu-test.png"), ImageFormat.Png);
        }
        ClickWindowPoint(window, config.ProfileX, GetWindowHeight(window) - config.ProfileBottomOffset);
        Log("账户菜单后台点击测试完成，界面已还原，未执行领取。");
        return 0;
    }

    // 不点击领取，仅验证个人中心入口、领取状态卡片和后台截图。
    private static int TestPersonalCenter(Config config)
    {
        var window = EnsureWorkBuddyWindow(config);
        if (window == IntPtr.Zero) throw new InvalidOperationException("未找到 WorkBuddy 主窗口。");
        if (!TryOpenPersonalCenterAndReadEvidence(window, config, out var evidence))
            throw new InvalidOperationException("个人中心未完成加载或 OCR 未识别到积分余额。");
        using var image = CaptureWindow(window) ?? throw new InvalidOperationException("无法捕获个人中心。");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
        Directory.CreateDirectory(folder);
        image.Save(Path.Combine(folder, "workbuddy-personal-center-test.png"), ImageFormat.Png);
        Log($"个人中心测试完成：余额={evidence.Balance!.RawText}，已领状态={evidence.HasSuccessText}，未执行领取点击。");
        return 0;
    }

    private static Bitmap? CaptureWindow(IntPtr hwnd)
    {
        Native.GetWindowRect(hwnd, out var rect);
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            // PW_RENDERFULLCONTENT 可在窗口被遮挡时请求 Chromium/Electron 绘制自身内容。
            if (Native.PrintWindow(hwnd, hdc, Native.PW_RENDERFULLCONTENT)) return bitmap;
        }
        finally { graphics.ReleaseHdc(hdc); }
        bitmap.Dispose();
        return null;
    }

    private static int SelfTest(Config config)
    {
        if (!DateTime.TryParse(config.ClaimTime, out _)) throw new InvalidOperationException("ClaimTime 必须是 HH:mm。");
        if (config.MaxAttempts != 5) throw new InvalidOperationException("MaxAttempts 必须保持为 5。");
        if (config.RetryIntervalSeconds < 10) throw new InvalidOperationException("RetryIntervalSeconds 不能小于 10。");
        if (!IsClaimedButtonBackground(new[] { Color.FromArgb(242, 242, 242), Color.FromArgb(242, 242, 242), Color.FromArgb(242, 242, 242) }))
            throw new InvalidOperationException("已领取按钮颜色校验失败。");
        if (IsClaimedButtonBackground(new[] { Color.White, Color.White, Color.White }))
            throw new InvalidOperationException("纯白背景不能被判为已领取。");
        if (!IsClaimedButtonBackground(new[] { Color.FromArgb(47, 47, 47), Color.FromArgb(47, 47, 47), Color.FromArgb(47, 47, 47) }, darkTheme: true))
            throw new InvalidOperationException("深色模式已领取按钮颜色校验失败。");
        if (IsClaimedButtonBackground(new[] { Color.FromArgb(233, 233, 233), Color.FromArgb(233, 233, 233), Color.FromArgb(233, 233, 233) }, darkTheme: true))
            throw new InvalidOperationException("深色模式可领取按钮不能被判为已领取。");
        if (!IsClaimedButtonBackground(new[] { Color.FromArgb(16, 163, 127), Color.FromArgb(16, 163, 127), Color.FromArgb(16, 163, 127) }))
            throw new InvalidOperationException("+100 领取成功按钮颜色校验失败。");
        if (!IsPersonalMenuCard(new BuddyCard(34, 180, 216), 600) || IsPersonalMenuCard(new BuddyCard(22, 366, 218), 600))
            throw new InvalidOperationException("个人菜单卡片位置路由校验失败。");

        var ocr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "积分余额", Words = [new OcrWord { Text = "积分余额", X = 30, Y = 350, Width = 80, Height = 18 }] },
                new OcrLine { Text = "1324.67", Words = [new OcrWord { Text = "1324.67", X = 198, Y = 350, Width = 58, Height = 18 }] },
                new OcrLine { Text = "签到领积分", Words = [new OcrWord { Text = "签到领积分", X = 52, Y = 292, Width = 98, Height = 22 }] },
                new OcrLine { Text = "领取说明", Words = [new OcrWord { Text = "领取说明", X = 52, Y = 150, Width = 90, Height = 18 }] }
            ]
        };
        var balance = TryReadBalance(ocr) ?? throw new InvalidOperationException("积分余额 OCR 锚点校验失败。");
        if (balance.Fingerprint != "132467") throw new InvalidOperationException("积分余额 OCR 指纹校验失败。");
        var actions = FindClaimActions(ocr, balance, Config.DefaultClaimActionKeywords);
        if (actions.Count != 1 || actions[0].Keyword != "签到领积分")
            throw new InvalidOperationException("动态领取文字 OCR 路由校验失败。");
        var claimedOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "本期已领", Words = [new OcrWord { Text = "本期已领", X = 45, Y = 278, Width = 68, Height = 18 }] },
                new OcrLine { Text = "+100", Words = [new OcrWord { Text = "+100", X = 70, Y = 314, Width = 45, Height = 18 }] }
            ]
        };
        if (!HasClaimSuccessText(claimedOcr, balance))
            throw new InvalidOperationException("领取成功 OCR 文本校验失败。");
        Log("Self test OK.");
        return 0;
    }

    private static Config LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            var example = Path.Combine(BaseDir, "config.example.json");
            File.Copy(example, ConfigPath);
        }
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? throw new InvalidOperationException("配置文件无效。");
    }
    private static State LoadState()
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State(); }
        catch { return new State(); }
    }
    private static void SaveState(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
    }
    private static void Log(string text)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "workbuddy-auto-claim.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
    private static void Notify(string title, string text, ToolTipIcon icon)
    {
        using var tray = new NotifyIcon { Icon = SystemIcons.Information, Visible = true, BalloonTipTitle = title, BalloonTipText = text, BalloonTipIcon = icon };
        tray.ShowBalloonTip(8000);
        Application.DoEvents();
        Thread.Sleep(8500);
    }
    private static void RunProcess(string fileName, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{fileName} 失败，退出码 {p.ExitCode}。");
    }
}

internal sealed class Config
{
    internal static readonly string[] DefaultClaimActionKeywords = ["签到领积分", "立即领取", "去签到", "签到", "领积分", "领取", "体验"];
    public string WorkBuddyPath { get; set; } = @"D:\Program Files\WorkBuddy\WorkBuddy.exe";
    public string ClaimTime { get; set; } = "00:00";
    public int RetryIntervalSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
    public int LaunchWaitSeconds { get; set; } = 20;
    public int CardReadyTimeoutSeconds { get; set; } = 30;
    public List<string> ClaimActionKeywords { get; set; } = [.. DefaultClaimActionKeywords];
    public int ProfileX { get; set; } = 44;
    public int ProfileBottomOffset { get; set; } = 35;
    public int ClaimClickX { get; set; } = 92;
    public int ClaimStatusX { get; set; } = 50;
    public int ClaimBottomOffset { get; set; } = 432;
    public int PopupHeaderX { get; set; } = 210;
    public int PopupHeaderBottomOffset { get; set; } = 220;
    public int PopupClaimX { get; set; } = 77;
    public int PopupClaimBottomOffset { get; set; } = 96;
    public int PersonalCenterCardHeaderX { get; set; } = 210;
    public int PersonalCenterCardHeaderY { get; set; } = 445;
    public int PersonalCenterInfoX { get; set; } = 250;
    public int PersonalCenterInfoY { get; set; } = 500;
}
internal sealed class State { public DateOnly? SuccessDate { get; set; } }

internal static class Native
{
    internal const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    internal const uint SMTO_BLOCK = 0x0001, SMTO_ABORTIFHUNG = 0x0002;
    internal const uint ClickMessageTimeoutMilliseconds = 2_000;
    internal const int SW_SHOWNOACTIVATE = 4, SW_MINIMIZE = 6;
    internal const uint DESKTOP_READOBJECTS = 0x0001, DESKTOP_SWITCHDESKTOP = 0x0100;
    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMilliseconds, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetUserObjectInformation(IntPtr hObj, int index, IntPtr info, uint length, out uint needed);
    internal static string GetWindowText(IntPtr hwnd) { var b = new System.Text.StringBuilder(512); GetWindowText(hwnd, b, b.Capacity); return b.ToString(); }
    internal static string GetClassName(IntPtr hwnd) { var b = new System.Text.StringBuilder(512); GetClassName(hwnd, b, b.Capacity); return b.ToString(); }
    internal static string GetDesktopName(IntPtr desktop)
    {
        GetUserObjectInformation(desktop, 2, IntPtr.Zero, 0, out uint needed);
        var ptr = Marshal.AllocHGlobal((int)needed);
        try { return GetUserObjectInformation(desktop, 2, ptr, needed, out _) ? Marshal.PtrToStringUni(ptr) ?? "" : ""; }
        finally { Marshal.FreeHGlobal(ptr); }
    }
    internal const uint PW_RENDERFULLCONTENT = 2;
}
