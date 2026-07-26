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
    private const string SingletonName = "WorkBuddyAutoClaim.Singleton";
    private const string ManualTestRequestName = "WorkBuddyAutoClaim.ManualTestRequest";
    private static readonly TimeSpan ManualTestHandoffTimeout = TimeSpan.FromSeconds(90);
    private static readonly string BaseDir = AppContext.BaseDirectory;
    private static readonly string ConfigPath = Path.Combine(BaseDir, "config.json");
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim", "state.json");
    private static Mutex? _mutex;

    [STAThread]
    private static int Main(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "--daemon";
        if (command is "--run-now" or "--manual-test") return RunManualTest();

        _mutex = new Mutex(true, SingletonName, out bool firstInstance);
        if (!firstInstance) return 0;

        try
        {
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
                "--dry-run" => DryRun(config),
                "--verify-layout" => VerifyLayout(config),
                "--verify-card" => VerifyBuddyCard(args.Skip(1).FirstOrDefault()),
                "--ocr-screenshot" => OcrScreenshot(args.Skip(1).FirstOrDefault()),
                "--verify-claim-ocr" => VerifyClaimOcr(args.Skip(1).FirstOrDefault(), config),
                "--test-buddy-card" => TestBuddyCard(config),
                "--test-menu" => TestMenuClick(config),
                "--test-personal-center" => TestPersonalCenter(config),
                "--probe-checkin-entry" => ProbeCheckInEntry(config),
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
        using var manualTestRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ManualTestRequestName);
        while (true)
        {
            try
            {
                if (WaitForManualTestRequest(manualTestRequest, TimeSpan.Zero)) return 0;
                var now = DateTime.Now;
                var state = LoadState();
                var scheduledToday = now.Date.Add(claimTime);
                if (state.SuccessDate == DateOnly.FromDateTime(now))
                {
                    if (SleepUntilOrManualTestRequest(NextClaimTime(now, claimTime), "今天已成功领取", manualTestRequest)) return 0;
                    continue;
                }
                if (state.TerminalFailureDate == DateOnly.FromDateTime(now))
                {
                    if (SleepUntilOrManualTestRequest(NextClaimTime(now, claimTime), "今天已完成 5 次领取尝试，等待明天", manualTestRequest)) return 0;
                    continue;
                }

                if (now < scheduledToday)
                {
                    if (SleepUntilOrManualTestRequest(scheduledToday, "尚未到领取时间", manualTestRequest)) return 0;
                    continue;
                }

                if (!IsInteractiveDesktop())
                {
                    Log($"桌面已锁定；将在 {retryDelay.TotalSeconds:0} 秒后重试。");
                }
                else
                {
                    int exitCode = RunOnce(config, ClaimRunMode.Automatic);
                    if (exitCode == 0)
                    {
                        if (SleepUntilOrManualTestRequest(NextClaimTime(now, claimTime), "今天已成功领取", manualTestRequest)) return 0;
                        continue;
                    }
                    if (exitCode == 3)
                    {
                        Log($"领取过程中桌面锁定；将在 {retryDelay.TotalSeconds:0} 秒后重试。");
                    }
                    else
                    {
                        // RunOnce has already completed the configured five consecutive
                        // attempts and sent the one terminal-failure notification. Do not
                        // start another five-attempt batch every minute for the rest of today.
                        state.TerminalFailureDate = DateOnly.FromDateTime(now);
                        SaveState(state);
                        if (SleepUntilOrManualTestRequest(NextClaimTime(now, claimTime), "今天领取失败，已停止重复尝试", manualTestRequest)) return 0;
                        continue;
                    }
                }
            }
            catch (Exception ex) { Log("守护错误: " + ex.Message); }
            if (WaitForManualTestRequest(manualTestRequest, retryDelay)) return 0;
        }
    }

    private static DateTime NextClaimTime(DateTime now, TimeSpan claimTime)
    {
        var scheduledToday = now.Date.Add(claimTime);
        return now < scheduledToday ? scheduledToday : scheduledToday.AddDays(1);
    }

    private static bool SleepUntilOrManualTestRequest(DateTime wakeAt, string reason, WaitHandle? interrupt = null)
    {
        while (true)
        {
            var remaining = wakeAt - DateTime.Now;
            if (remaining <= TimeSpan.Zero) return false;

            Log($"{reason}；休眠至 {wakeAt:yyyy-MM-dd HH:mm:ss}。");
            var milliseconds = Math.Min(remaining.TotalMilliseconds, int.MaxValue);
            if (interrupt is null)
            {
                Thread.Sleep((int)Math.Ceiling(milliseconds));
                continue;
            }
            if (interrupt.WaitOne((int)Math.Ceiling(milliseconds)))
            {
                LogManualTestYield();
                return true;
            }
        }
    }

    private static bool WaitForManualTestRequest(WaitHandle request, TimeSpan timeout)
    {
        if (!request.WaitOne(timeout)) return false;
        LogManualTestYield();
        return true;
    }

    private static void LogManualTestYield() =>
        Log("收到手动测试请求，后台守护暂停并交出执行权。");

    private static int RunManualTest()
    {
        bool ownsMutex = false;
        bool restartDaemon = false;
        try
        {
            var config = LoadConfig();
            using var request = new EventWaitHandle(false, EventResetMode.AutoReset, ManualTestRequestName);
            _mutex = new Mutex(true, SingletonName, out bool noOtherInstance);
            ownsMutex = noOtherInstance;
            if (!noOtherInstance)
            {
                Log("手动测试请求已发出，等待后台守护暂停。");
                request.Set();
                if (!_mutex.WaitOne(ManualTestHandoffTimeout))
                    throw new TimeoutException("后台守护未能在 90 秒内暂停，手动测试未执行。");
                ownsMutex = true;
                restartDaemon = true;
            }

            Log("开始手动测试：仅执行一次，不修改自动领取状态。");
            return RunOnce(config, ClaimRunMode.ManualTest);
        }
        catch (Exception ex)
        {
            Log("手动测试错误: " + ex);
            Notify("WorkBuddy 手动测试失败", "测试未执行或发生错误，已停止并等待确认。", ToolTipIcon.Error);
            return 1;
        }
        finally
        {
            if (ownsMutex) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;
            if (restartDaemon)
            {
                try { StartDaemonAfterManualTest(); }
                catch (Exception ex)
                {
                    Log("手动测试结束后恢复后台守护失败: " + ex);
                    Notify("WorkBuddy 手动测试", "测试结束，但后台守护恢复失败，请手动启动工具。", ToolTipIcon.Error);
                }
            }
        }
    }

    private static void StartDaemonAfterManualTest()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法解析程序路径。");
        Process.Start(new ProcessStartInfo(exe, "--daemon") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        Log("手动测试结束，已恢复后台守护。");
    }

    private enum ClaimRunMode { Automatic, ManualTest }

    private static int GetAttemptLimit(Config config, ClaimRunMode mode) =>
        mode == ClaimRunMode.ManualTest ? 1 : config.MaxAttempts;

    private static bool ShouldPersistDailyState(ClaimRunMode mode) => mode == ClaimRunMode.Automatic;

    private static int RunOnce(Config config, ClaimRunMode mode)
    {
        bool isManualTest = mode == ClaimRunMode.ManualTest;
        int maxAttempts = GetAttemptLimit(config, mode);
        if (!IsInteractiveDesktop())
        {
            Log("桌面已锁定，跳过本次尝试。");
            if (isManualTest)
                NotifyManualTestFailure("桌面已锁定，测试未执行");
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

            for (int attempt = 1; attempt <= maxAttempts && !succeeded; attempt++)
            {
                try
                {
                    Log($"开始{(isManualTest ? "手动测试" : "领取")}，第 {attempt}/{maxAttempts} 次。");
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
            if (ShouldPersistDailyState(mode)) SaveState(new State { SuccessDate = DateOnly.FromDateTime(DateTime.Today) });
            Log("完成: " + result);
            Notify(isManualTest ? "WorkBuddy 手动测试" : "WorkBuddy 自动领取", result, ToolTipIcon.Info);
            return 0;
        }

        Log("领取失败: " + result);
        if (isManualTest)
            NotifyManualTestFailure(result);
        else
            Notify("WorkBuddy 自动领取失败", "已连续尝试 5 次仍未成功，请手动领取。", ToolTipIcon.Error);
        return 1;
    }

    private static void NotifyManualTestFailure(string result) =>
        Notify("WorkBuddy 手动测试失败", $"{result}。已停止测试并等待确认。", ToolTipIcon.Error);

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
        var beforeBalance = evidence.Balance ?? throw new InvalidOperationException("领取前丢失了积分余额 OCR 锚点。");
        var immediate = evidence.Actions.FirstOrDefault(action => action.Kind == ClaimActionKind.Immediate);
        if (immediate is not null)
            return ClickImmediateClaimAndVerify(window, config, immediate, beforeBalance, out result);

        // “签到”只被视为进入领取流程的入口，不把它本身当作领取成功。
        // 每个入口最多点一次；只有随后真实出现“立即领取”才会继续。
        var triedCandidateIds = new HashSet<string>(StringComparer.Ordinal);
        // A final button that was already visible is never attributed to the entry click.
        // This makes an entry->final transition explicit and prevents a same-word control
        // elsewhere in WorkBuddy from becoming a target after the screen changes.
        var preExistingImmediateCandidateIds = evidence.Actions
            .Where(action => action.Kind == ClaimActionKind.Immediate)
            .Select(action => action.CandidateId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var checkIn in evidence.Actions.Where(action => action.Kind == ClaimActionKind.CheckIn))
        {
            if (!triedCandidateIds.Add(checkIn.CandidateId)) continue;
            if (!TryGetStableBalanceBeforeAction(window, config, beforeBalance, out var stableBeforeEntry))
            {
                result = "签到入口点击前未能稳定读取积分余额；为避免误报，本次未点击入口";
                continue;
            }
            Log($"OCR 识别签到入口：{checkIn.Keyword}，文本={checkIn.Text}，位置=({checkIn.CenterX},{checkIn.CenterY})。");
            SaveBuddyDiagnosticCapture(window, "before-checkin");
            ClickWindowPoint(window, checkIn.CenterX, checkIn.CenterY);
            if (TryFindImmediateClaimAfterCheckIn(window, config, stableBeforeEntry, preExistingImmediateCandidateIds,
                    out var discoveredImmediate, out var balanceBeforeImmediate, out bool popupRoute) &&
                discoveredImmediate is not null && balanceBeforeImmediate is not null)
                return ClickImmediateClaimAndVerify(window, config, discoveredImmediate, balanceBeforeImmediate,
                    out result, balanceAlreadyStabilized: popupRoute);
        }

        result = "未识别到立即领取；已点击签到入口但未出现立即领取按钮";
        return false;
    }

    private static bool ClickImmediateClaimAndVerify(
        IntPtr window, Config config, ClaimAction immediate, BalanceReading beforeBalance,
        out string result, bool balanceAlreadyStabilized = false)
    {
        var stableBeforeBalance = beforeBalance;
        if (!balanceAlreadyStabilized && !TryGetStableBalanceBeforeAction(window, config, beforeBalance, out stableBeforeBalance))
        {
            result = "立即领取出现，但点击前未能稳定读取积分余额；为避免误报，本次未点击最终领取按钮";
            return false;
        }
        if (!balanceAlreadyStabilized) beforeBalance = stableBeforeBalance;
        Log($"OCR 识别最终立即领取：文本={immediate.Text}，位置=({immediate.CenterX},{immediate.CenterY})，点击前积分余额={beforeBalance.RawText}。");
        SaveBuddyDiagnosticCapture(window, "before-claim");
        ClickWindowPoint(window, immediate.CenterX, immediate.CenterY);
        var verification = WaitForClaimResult(window, config, beforeBalance, TimeSpan.FromSeconds(20));
        bool claimed = verification != ClaimVerification.NotConfirmed;
        SaveBuddyDiagnosticCapture(window, claimed ? "after-claim-success" : "after-claim-failure");
        result = verification switch
        {
            ClaimVerification.ClaimedText => "领取成功，OCR 检测到“今日已领”状态",
            ClaimVerification.BalanceChanged => "领取成功，点击立即领取后 OCR 识别到积分余额变化",
            ClaimVerification.NotConfirmed => "点击立即领取后未识别到积分余额变化或“今日已领”状态",
            _ => "点击立即领取后未识别到积分余额变化或“今日已领”状态"
        };
        return claimed;
    }

    private static bool TryFindImmediateClaimAfterCheckIn(
        IntPtr window,
        Config config,
        BalanceReading expectedBeforeBalance,
        IReadOnlySet<string> preExistingImmediateCandidateIds,
        out ClaimAction? immediate,
        out BalanceReading? balanceBeforeImmediate,
        out bool popupRoute)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        do
        {
            using var image = CaptureWindow(window);
            if (image is not null)
            {
                var evidence = ReadMenuEvidence(image, config);
                // The final button must be new and must remain tied to the same personal-center
                // balance anchor. A popup that hides that anchor is deliberately not clicked.
                var found = SelectNewImmediateAction(
                    evidence, expectedBeforeBalance.Bounds, preExistingImmediateCandidateIds, config);
                if (found is not null)
                {
                    immediate = found;
                    balanceBeforeImmediate = evidence.Balance;
                    popupRoute = false;
                    return true;
                }
                // WorkBuddy v5.3 opens the Buddy 加油站 mini card after clicking the
                // personal-center entry. It has no visible balance row, so it is accepted
                // only when its own card markers and its enlarged OCR “立即领取” are present.
                if (TryFindBuddyPopupImmediateClaim(image, config, out var popupImmediate) && popupImmediate is not null)
                {
                    immediate = popupImmediate;
                    balanceBeforeImmediate = expectedBeforeBalance;
                    popupRoute = true;
                    return true;
                }
            }
            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < until);
        immediate = null;
        balanceBeforeImmediate = null;
        popupRoute = false;
        return false;
    }

    private static ClaimAction? SelectNewImmediateAction(
        MenuEvidence evidence,
        OcrBounds expectedBalanceBounds,
        IReadOnlySet<string> preExistingImmediateCandidateIds,
        Config config)
    {
        if (evidence.Balance is null || !IsSameBalanceAnchor(expectedBalanceBounds, evidence.Balance.Bounds, config))
            return null;
        return evidence.Actions.FirstOrDefault(action =>
            action.Kind == ClaimActionKind.Immediate &&
            !preExistingImmediateCandidateIds.Contains(action.CandidateId));
    }

    private static bool TryFindBuddyPopupImmediateClaim(Bitmap bitmap, Config config, out ClaimAction? immediate)
    {
        immediate = null;
        var pageOcr = ReadOcr(bitmap);
        var anchor = pageOcr.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line), Text: NormalizeOcrText(line.Text)))
            .FirstOrDefault(item => item.Line.Words.Count > 0 &&
                                    item.Bounds.Left < bitmap.Width / 2 &&
                                    item.Bounds.Top > bitmap.Height / 2 &&
                                    item.Text.Contains("本期", StringComparison.Ordinal));
        if (anchor.Line is null) return false;

        int sourceLeft = Math.Max(0, anchor.Bounds.Left - config.PopupCardAnchorLeftOffsetPixels);
        int sourceTop = Math.Max(0, anchor.Bounds.Top - config.PopupCardAnchorTopOffsetPixels);
        int sourceWidth = Math.Min(config.PopupCardWidthPixels, bitmap.Width - sourceLeft);
        int sourceHeight = Math.Min(config.PopupCardHeightPixels, bitmap.Height - sourceTop);
        if (sourceWidth <= 0 || sourceHeight <= 0) return false;
        using var enlarged = CreateScaledCrop(bitmap, new Rectangle(sourceLeft, sourceTop, sourceWidth, sourceHeight),
            config.PopupCardOcrScale);
        var popupOcr = ReadOcr(enlarged);
        bool hasPopupIdentity = popupOcr.Lines.Any(line =>
            NormalizeOcrText(line.Text).Contains("Buddy加油站", StringComparison.Ordinal) ||
            NormalizeOcrText(line.Text).Contains("8uddy加油站", StringComparison.Ordinal)) &&
            popupOcr.Lines.Any(line => NormalizeOcrText(line.Text).Contains("本期", StringComparison.Ordinal));
        if (!hasPopupIdentity) return false;

        var immediateKeywords = GetNormalizedKeywords(config.ImmediateClaimKeywords, Config.DefaultImmediateClaimKeywords);
        var button = popupOcr.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line), Text: NormalizeOcrText(line.Text)))
            .FirstOrDefault(item => item.Line.Words.Count > 0 &&
                                    immediateKeywords.Any(keyword => item.Text.Contains(keyword, StringComparison.Ordinal)));
        if (button.Line is null) return false;

        int scale = Math.Max(1, config.PopupCardOcrScale);
        var sourceBounds = new OcrBounds(
            sourceLeft + button.Bounds.Left / scale,
            sourceTop + button.Bounds.Top / scale,
            sourceLeft + button.Bounds.Right / scale,
            sourceTop + button.Bounds.Bottom / scale);
        immediate = new ClaimAction("立即领取", button.Line.Text, sourceBounds.CenterX, sourceBounds.CenterY,
            GetCandidateId(sourceBounds, config.ClaimCandidatePositionTolerancePixels), ClaimActionKind.Immediate);
        Log($"Buddy 加油站弹层 OCR 识别最终立即领取：文本={button.Line.Text}，位置=({sourceBounds.CenterX},{sourceBounds.CenterY})。");
        return true;
    }

    private static bool TryOpenPersonalCenterAndReadEvidence(IntPtr window, Config config, out MenuEvidence evidence)
    {
        using (var current = CaptureWindow(window))
        {
            if (current is not null)
            {
                evidence = ReadMenuEvidence(current, config);
                if (evidence.IsPersonalCenter) return true;
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
                if (evidence.IsPersonalCenter) return true;
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
        BalanceReading? lastChangedBalance = null;
        int consecutiveChangedFrames = 0;
        do
        {
            bool hasPersonalCenter = false;
            using (var image = CaptureWindow(window))
            {
                if (image is not null)
                {
                    var evidence = ReadMenuEvidence(image, config);
                    hasPersonalCenter = evidence.IsPersonalCenter;
                    if (evidence.HasSuccessText)
                    {
                        Log("OCR 检测到“今日已领”状态。");
                        return ClaimVerification.ClaimedText;
                    }
                    if (evidence.Balance is not null && IsBalanceChanged(beforeBalance, evidence.Balance, config))
                    {
                        consecutiveChangedFrames = lastChangedBalance is not null &&
                                                   AreSameBalance(lastChangedBalance, evidence.Balance, config)
                            ? consecutiveChangedFrames + 1
                            : 1;
                        lastChangedBalance = evidence.Balance;
                        // Numeric OCR is already an exact value. A visual fallback needs two
                        // matching post-click frames so a repaint or OCR-anchor jitter cannot win.
                        if (!beforeBalance.IsVisualFingerprint || consecutiveChangedFrames >= 2)
                        {
                            Log($"OCR 积分余额变化：{beforeBalance.RawText} -> {evidence.Balance.RawText}。");
                            return ClaimVerification.BalanceChanged;
                        }
                    }
                    else { lastChangedBalance = null; consecutiveChangedFrames = 0; }
                }
            }
            if (!hasPersonalCenter && !reopenedMenu && DateTime.UtcNow >= reopenMenuAfter)
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

    private static bool TryGetStableBalanceBeforeAction(
        IntPtr window, Config config, BalanceReading observed, out BalanceReading stable)
    {
        stable = observed;
        // Text/numeric OCR is an exact number. The visual fallback intentionally asks for
        // two additional compatible captures before a non-reversible click.
        if (!observed.IsVisualFingerprint) return true;

        for (int sample = 0; sample < 2; sample++)
        {
            Thread.Sleep(250);
            using var image = CaptureWindow(window);
            if (image is null) return false;
            var current = ReadMenuEvidence(image, config).Balance;
            if (current is null || !IsSameBalanceAnchor(observed.Bounds, current.Bounds, config) ||
                !AreSameBalance(observed, current, config))
                return false;
            stable = current;
        }
        return true;
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
    private sealed record BalanceReading(
        string RawText,
        string Fingerprint,
        OcrBounds Bounds,
        bool IsVisualFingerprint = false,
        string? VisualSignature = null);
    private enum ClaimActionKind { CheckIn, Immediate }
    private enum ClaimVerification { NotConfirmed, BalanceChanged, ClaimedText }

    // OCR wording can become more or less specific after a re-render. The relative
    // position is the stable identity, so two same-text buttons may still both run.
    private sealed record ClaimAction(
        string Keyword, string Text, int CenterX, int CenterY, string CandidateId, ClaimActionKind Kind);
    private sealed record MenuEvidence(
        BalanceReading? Balance,
        IReadOnlyList<ClaimAction> Actions,
        bool HasSuccessText,
        bool IsPersonalCenter)
    {
        public static readonly MenuEvidence Empty = new(null, [], false, false);
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

    private static OcrSnapshot ReadOcr(Bitmap bitmap, string language = "profile")
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
            start.ArgumentList.Add("-Language");
            start.ArgumentList.Add(language);
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
        var balance = TryReadBalance(bitmap, ocr, config);
        var actions = balance is null ? [] : FindClaimActions(ocr, balance, config);
        bool hasSuccessText = balance is not null && HasClaimSuccessText(ocr, balance, config);
        bool isPersonalCenter = balance is not null && HasPersonalCenterMarker(ocr, balance, config);
        return new MenuEvidence(balance, actions, hasSuccessText, isPersonalCenter);
    }

    private sealed record BalanceLabel(OcrLine Line, OcrBounds Bounds);

    private static BalanceReading? TryReadBalance(Bitmap bitmap, OcrSnapshot snapshot, Config config)
    {
        var direct = TryReadBalance(snapshot, config);
        // Preserve a visual signature even when the text OCR succeeds. This lets a
        // one-character visual baseline (for example 0) be compared safely with a
        // later multi-digit balance that OCR can transcribe.
        if (direct is not null)
            return direct with { VisualSignature = CreateVisualBalanceFingerprint(bitmap, direct.Bounds, config) };

        var label = FindBalanceLabel(snapshot);
        if (label is null) return null;

        var broadValue = TryReadBalanceFromCrop(bitmap, label, config, leftOffset: -4,
            width: config.BalanceValueCropWidthPixels, scale: config.BalanceValueCropScale,
            above: config.BalanceValueCropAbovePixels, below: config.BalanceValueCropBelowPixels, highContrast: false);
        if (broadValue is not null) return broadValue;

        // The value can be a single small digit at the far right of the row. A focused,
        // higher-resolution retry prevents a visible 0 balance from being treated as missing.
        return TryReadBalanceFromCrop(bitmap, label, config,
            config.BalanceValueFocusedCropLeftOffsetPixels,
            config.BalanceValueFocusedCropWidthPixels,
            config.BalanceValueFocusedCropScale,
            config.BalanceValueFocusedCropAbovePixels,
            config.BalanceValueFocusedCropBelowPixels,
            highContrast: true)
            ?? CreateVisualBalanceReading(bitmap, label.Bounds, config);
    }

    private static BalanceReading? TryReadBalance(OcrSnapshot snapshot, Config config)
    {
        var label = FindBalanceLabel(snapshot);
        if (label is null) return null;

        // The left-side icon is sometimes OCR'd as 0, so only accept digits that
        // occur after the “积分余额” text on the same line.
        var normalizedLabelLine = NormalizeOcrText(label.Line.Text);
        const string labelText = "积分余额";
        int labelTextIndex = normalizedLabelLine.IndexOf(labelText, StringComparison.Ordinal);
        var sameLineDigits = labelTextIndex < 0
            ? string.Empty
            : new string(normalizedLabelLine[(labelTextIndex + labelText.Length)..].Where(char.IsDigit).ToArray());
        if (sameLineDigits.Length >= 1)
            return new BalanceReading(label.Line.Text, sameLineDigits, label.Bounds);

        var candidate = snapshot.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line)))
            .Where(item => !ReferenceEquals(item.Line, label.Line) && item.Line.Words.Count > 0 &&
                           item.Line.Text.Count(char.IsDigit) >= 1 &&
                           ((item.Bounds.Left >= label.Bounds.Right - 12 &&
                             item.Bounds.Left <= label.Bounds.Right + config.BalanceValueDirectRightPixels &&
                             Math.Abs(item.Bounds.CenterY - label.Bounds.CenterY) <= config.BalanceValueSameRowTolerance) ||
                            (item.Bounds.Top >= label.Bounds.Bottom &&
                             item.Bounds.Left <= label.Bounds.Right + config.BalanceValueDirectRightPixels &&
                             item.Bounds.Top - label.Bounds.Bottom <= config.BalanceValueVerticalTolerance)))
            .OrderBy(item => Math.Abs(item.Bounds.CenterY - label.Bounds.CenterY))
            .ThenBy(item => item.Bounds.Left)
            .FirstOrDefault();
        if (candidate.Line is null) return null;

        var fingerprint = new string(candidate.Line.Text.Where(char.IsDigit).ToArray());
        return fingerprint.Length >= 1 ? new BalanceReading(candidate.Line.Text, fingerprint, label.Bounds) : null;
    }

    private static BalanceLabel? FindBalanceLabel(OcrSnapshot snapshot)
    {
        var label = snapshot.Lines
            .Select(line => new BalanceLabel(line, GetOcrBounds(line)))
            .FirstOrDefault(item => NormalizeOcrText(item.Line.Text).Contains("积分余额", StringComparison.Ordinal));
        return label?.Line is null ? null : label;
    }

    private static BalanceReading? TryReadBalanceFromCrop(
        Bitmap bitmap, BalanceLabel label, Config config, int leftOffset, int width, int scale, int above, int below,
        bool highContrast)
    {
        using var enlargedValueArea = CreateBalanceValueCrop(bitmap, label.Bounds, leftOffset, width, scale, above, below);
        if (highContrast) IncreaseOcrContrast(enlargedValueArea);
        var enlargedOcr = ReadOcr(enlargedValueArea, "en-US");
        var value = enlargedOcr.Lines
            .Where(line => line.Words.Count > 0 && line.Text.Count(char.IsDigit) >= 1)
            .OrderBy(line => GetOcrBounds(line).Top)
            .FirstOrDefault();
        if (value is null) return null;
        var fingerprint = new string(value.Text.Where(char.IsDigit).ToArray());
        return fingerprint.Length >= 1 ? new BalanceReading(value.Text, fingerprint, label.Bounds) : null;
    }

    private static Bitmap CreateBalanceValueCrop(
        Bitmap bitmap, OcrBounds labelBounds, int leftOffset, int width, int scale, int above, int below)
    {
        int sourceLeft = Math.Max(0, labelBounds.Right + leftOffset);
        int sourceTop = Math.Max(0, labelBounds.Top - above);
        int sourceRight = Math.Min(bitmap.Width, sourceLeft + width);
        int sourceBottom = Math.Min(bitmap.Height, labelBounds.Bottom + below);
        return CreateScaledCrop(bitmap, new Rectangle(sourceLeft, sourceTop,
            Math.Max(1, sourceRight - sourceLeft), Math.Max(1, sourceBottom - sourceTop)), scale);
    }

    private static Bitmap CreateScaledCrop(Bitmap bitmap, Rectangle source, int scale)
    {
        int sourceLeft = Math.Clamp(source.Left, 0, Math.Max(0, bitmap.Width - 1));
        int sourceTop = Math.Clamp(source.Top, 0, Math.Max(0, bitmap.Height - 1));
        int sourceRight = Math.Clamp(source.Right, sourceLeft + 1, bitmap.Width);
        int sourceBottom = Math.Clamp(source.Bottom, sourceTop + 1, bitmap.Height);
        int sourceWidth = sourceRight - sourceLeft;
        int sourceHeight = sourceBottom - sourceTop;
        int safeScale = Math.Max(1, scale);
        var enlarged = new Bitmap(sourceWidth * safeScale, sourceHeight * safeScale, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(enlarged);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(bitmap, new Rectangle(0, 0, enlarged.Width, enlarged.Height),
            new Rectangle(sourceLeft, sourceTop, sourceWidth, sourceHeight), GraphicsUnit.Pixel);
        return enlarged;
    }

    private static void IncreaseOcrContrast(Bitmap bitmap)
    {
        const int threshold = 220;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            int luma = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
            bitmap.SetPixel(x, y, luma < threshold ? Color.Black : Color.White);
        }
    }

    private static BalanceReading CreateVisualBalanceReading(Bitmap bitmap, OcrBounds labelBounds, Config config)
    {
        // WorkBuddy v5.3 can render a one-character balance that Windows OCR refuses to
        // transcribe. The crop is anchored to “积分余额” and produces a local visual
        // fingerprint, so a displayed-number change remains verifiable without guessing.
        // Average cells make the fingerprint tolerant of a one-pixel OCR-label drift;
        // confirmation still requires a later, stable post-click change.
        return new BalanceReading("视觉余额指纹", CreateVisualBalanceFingerprint(bitmap, labelBounds, config),
            labelBounds, IsVisualFingerprint: true);
    }

    private static string CreateVisualBalanceFingerprint(Bitmap bitmap, OcrBounds labelBounds, Config config)
    {
        using var crop = CreateBalanceValueCrop(bitmap, labelBounds,
            config.BalanceValueFocusedCropLeftOffsetPixels,
            config.BalanceValueFocusedCropWidthPixels,
            1,
            config.BalanceValueFocusedCropAbovePixels,
            config.BalanceValueFocusedCropBelowPixels);
        const int columns = 20;
        const int rows = 8;
        var bytes = new byte[columns * rows];
        int index = 0;
        for (int cellY = 0; cellY < rows; cellY++)
        for (int cellX = 0; cellX < columns; cellX++)
        {
            int left = cellX * crop.Width / columns;
            int right = Math.Max(left + 1, (cellX + 1) * crop.Width / columns);
            int top = cellY * crop.Height / rows;
            int bottom = Math.Max(top + 1, (cellY + 1) * crop.Height / rows);
            long lumaTotal = 0;
            int pixels = 0;
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                var color = crop.GetPixel(x, y);
                lumaTotal += color.R * 299 + color.G * 587 + color.B * 114;
                pixels++;
            }
            bytes[index++] = (byte)Math.Clamp((int)(lumaTotal / Math.Max(1, pixels) / 1_000 / 32), 0, 7);
        }
        return "V:" + Convert.ToHexString(bytes);
    }

    private static IReadOnlyList<ClaimAction> FindClaimActions(
        OcrSnapshot snapshot, BalanceReading balance, Config config)
    {
        // Every executable word is explicitly classified: “立即领取” is final, and
        // “签到…” is only an entry. Generic words such as “领取” are intentionally not
        // acted on, because a version update can place them in an unrelated card.
        var keywords = GetNormalizedKeywords(config.ImmediateClaimKeywords, Config.DefaultImmediateClaimKeywords)
            .Concat(GetNormalizedKeywords(config.CheckInKeywords, Config.DefaultCheckInKeywords))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(keyword => keyword.Length)
            .ToArray();
        var exclusions = (config.ClaimActionExclusions ?? [.. Config.DefaultClaimActionExclusions])
            .Select(NormalizeOcrText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToHashSet(StringComparer.Ordinal);
        var actionRegion = GetBalanceRegion(balance, config, config.ClaimActionAboveBalancePixels,
            config.ClaimActionBelowBalancePixels);
        return snapshot.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line), Normalized: NormalizeOcrText(line.Text)))
            .Where(item => item.Line.Words.Count > 0 && actionRegion.Contains(item.Bounds) &&
                           !item.Normalized.Contains("已领", StringComparison.Ordinal) &&
                           !item.Normalized.Contains("成功", StringComparison.Ordinal) &&
                           !exclusions.Contains(item.Normalized))
            .Select(item =>
            {
                var keyword = keywords.FirstOrDefault(value => item.Normalized.Contains(value, StringComparison.Ordinal));
                return string.IsNullOrWhiteSpace(keyword)
                    ? null
                    : new ClaimAction(keyword, item.Line.Text, item.Bounds.CenterX, item.Bounds.CenterY,
                        GetCandidateId(item.Bounds, config.ClaimCandidatePositionTolerancePixels),
                        ClassifyClaimAction(item.Normalized, config));
            })
            .Where(action => action is not null)
            .Select(action => action!)
            .GroupBy(action => action.CandidateId)
            .Select(group => group.First())
            .ToArray();
    }

    private static ClaimActionKind ClassifyClaimAction(string normalizedText, Config config)
    {
        if (GetNormalizedKeywords(config.ImmediateClaimKeywords, Config.DefaultImmediateClaimKeywords)
            .Any(keyword => normalizedText.Contains(keyword, StringComparison.Ordinal)))
            return ClaimActionKind.Immediate;
        if (GetNormalizedKeywords(config.CheckInKeywords, Config.DefaultCheckInKeywords)
            .Any(keyword => normalizedText.Contains(keyword, StringComparison.Ordinal)))
            return ClaimActionKind.CheckIn;
        throw new InvalidOperationException($"未分类的领取关键词: {normalizedText}");
    }

    private static string[] GetNormalizedKeywords(IEnumerable<string>? configured, IEnumerable<string> fallback) =>
        (configured ?? fallback)
            .Select(NormalizeOcrText)
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .OrderByDescending(keyword => keyword.Length)
            .ToArray();

    private static bool IsSameBalanceAnchor(OcrBounds expected, OcrBounds actual, Config config) =>
        Math.Abs(expected.CenterX - actual.CenterX) <= config.BalanceAnchorDriftPixels &&
        Math.Abs(expected.CenterY - actual.CenterY) <= config.BalanceAnchorDriftPixels;

    private static bool AreSameBalance(BalanceReading expected, BalanceReading actual, Config config)
    {
        if (!expected.IsVisualFingerprint && !actual.IsVisualFingerprint)
            return StringComparer.Ordinal.Equals(expected.Fingerprint, actual.Fingerprint);
        return GetVisualBalanceDifference(GetVisualSignature(expected), GetVisualSignature(actual)) <=
               config.VisualBalanceSameFrameMaxChangedCells;
    }

    private static bool IsBalanceChanged(BalanceReading before, BalanceReading after, Config config)
    {
        if (!before.IsVisualFingerprint && !after.IsVisualFingerprint)
            return !StringComparer.Ordinal.Equals(before.Fingerprint, after.Fingerprint);
        return GetVisualBalanceDifference(GetVisualSignature(before), GetVisualSignature(after)) >=
               config.VisualBalanceChangeMinimumChangedCells;
    }

    private static string GetVisualSignature(BalanceReading reading) =>
        reading.IsVisualFingerprint ? reading.Fingerprint : reading.VisualSignature ?? string.Empty;

    private static int GetVisualBalanceDifference(string left, string right)
    {
        if (!left.StartsWith("V:", StringComparison.Ordinal) || !right.StartsWith("V:", StringComparison.Ordinal))
            return int.MaxValue;
        byte[] leftBytes;
        byte[] rightBytes;
        try
        {
            leftBytes = Convert.FromHexString(left[2..]);
            rightBytes = Convert.FromHexString(right[2..]);
        }
        catch (FormatException) { return int.MaxValue; }
        if (leftBytes.Length != rightBytes.Length) return int.MaxValue;
        int changedCells = 0;
        for (int index = 0; index < leftBytes.Length; index++)
            if (Math.Abs(leftBytes[index] - rightBytes[index]) >= 1) changedCells++;
        return changedCells;
    }

    private static bool HasClaimSuccessText(OcrSnapshot snapshot, BalanceReading balance, Config config)
    {
        var region = GetBalanceRegion(balance, config, config.ClaimActionAboveBalancePixels, config.ClaimActionBelowBalancePixels);
        return snapshot.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line), Normalized: NormalizeOcrText(line.Text)))
            .Where(item => item.Line.Words.Count > 0 && region.Contains(item.Bounds))
            .Any(item => item.Normalized.Contains("今日已领", StringComparison.Ordinal) ||
                         item.Normalized.Contains("本期已领", StringComparison.Ordinal));
    }

    private static bool HasPersonalCenterMarker(OcrSnapshot snapshot, BalanceReading balance, Config config)
    {
        var region = GetBalanceRegion(balance, config, config.PersonalCenterEvidenceAboveBalancePixels,
            config.PersonalCenterEvidenceBelowBalancePixels);
        return snapshot.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line), Normalized: NormalizeOcrText(line.Text)))
            .Any(item => item.Line.Words.Count > 0 && region.Contains(item.Bounds) &&
                         (item.Normalized.Contains("设置", StringComparison.Ordinal) ||
                          item.Normalized.Contains("外观", StringComparison.Ordinal) ||
                          item.Normalized.Contains("浅色", StringComparison.Ordinal) ||
                          item.Normalized.Contains("深色", StringComparison.Ordinal)));
    }

    private readonly record struct OcrRegion(int Left, int Top, int Right, int Bottom)
    {
        public bool Contains(OcrBounds bounds) => bounds.Left >= Left && bounds.Right <= Right &&
                                                   bounds.CenterY >= Top && bounds.CenterY <= Bottom;
    }

    private static OcrRegion GetBalanceRegion(BalanceReading balance, Config config, int above, int below) =>
        new(Math.Max(0, balance.Bounds.Left - config.ClaimActionLeftPixels),
            Math.Max(0, balance.Bounds.Top - above),
            balance.Bounds.Right + config.ClaimActionRightPixels,
            balance.Bounds.Bottom + below);

    private static string GetCandidateId(OcrBounds bounds, int tolerancePixels)
    {
        int tolerance = Math.Max(1, tolerancePixels);
        return $"{bounds.CenterX / tolerance}:{bounds.CenterY / tolerance}";
    }

    private static OcrBounds GetOcrBounds(OcrLine line)
    {
        if (line.Words.Count == 0) return default;
        return new OcrBounds(line.Words.Min(word => word.X), line.Words.Min(word => word.Y),
            line.Words.Max(word => word.X + word.Width), line.Words.Max(word => word.Y + word.Height));
    }

    private static string NormalizeOcrText(string text) =>
        Regex.Replace(text, "[\\s\\p{P}\\p{S}]", string.Empty)
            // Windows OCR sometimes emits the Traditional glyphs from the same Simplified UI.
            // Normalize the action words before routing; raw OCR is retained in diagnostics.
            .Replace('領', '领')
            .Replace('簽', '签');

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
        Native.ShowWindow(window, Native.SW_SHOWNOACTIVATE);
        Thread.Sleep(800);
        if (!TryOpenPersonalCenterAndReadEvidence(window, config, out var evidence))
            throw new InvalidOperationException("个人中心未完成加载或 OCR 未识别到积分余额。");
        using var image = CaptureWindow(window) ?? throw new InvalidOperationException("无法捕获个人中心。");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
        Directory.CreateDirectory(folder);
        image.Save(Path.Combine(folder, "workbuddy-personal-center-test.png"), ImageFormat.Png);
        Log($"个人中心测试完成：余额={evidence.Balance!.RawText}，已领状态={evidence.HasSuccessText}，未执行领取点击。");
        return 0;
    }

    // Controlled compatibility probe: it clicks one OCR-confirmed entry once, captures
    // the resulting layer, and never clicks any “立即领取” control.
    private static int ProbeCheckInEntry(Config config)
    {
        var window = EnsureWorkBuddyWindow(config);
        if (window == IntPtr.Zero) throw new InvalidOperationException("未找到 WorkBuddy 主窗口。");
        bool wasForeground = Native.GetForegroundWindow() == window;
        try
        {
            Native.ShowWindow(window, Native.SW_SHOWNOACTIVATE);
            if (!TryOpenPersonalCenterAndReadEvidence(window, config, out var before))
                throw new InvalidOperationException("探测前未能打开个人中心。");
            var entry = before.Actions.FirstOrDefault(action => action.Kind == ClaimActionKind.CheckIn)
                        ?? throw new InvalidOperationException("探测前未识别到签到入口。");
            SaveBuddyDiagnosticCapture(window, "probe-before-checkin");
            Log($"签到入口探测：仅点击一次 {entry.Text}，位置=({entry.CenterX},{entry.CenterY})，不会点击最终领取。");
            ClickWindowPoint(window, entry.CenterX, entry.CenterY);
            Thread.Sleep(1_200);
            using var afterImage = CaptureWindow(window) ?? throw new InvalidOperationException("入口点击后无法捕获 WorkBuddy。");
            var afterEvidence = ReadMenuEvidence(afterImage, config);
            bool popupImmediate = TryFindBuddyPopupImmediateClaim(afterImage, config, out var popupAction) && popupAction is not null;
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
            Directory.CreateDirectory(folder);
            var output = Path.Combine(folder, "workbuddy-probe-after-checkin.png");
            afterImage.Save(output, ImageFormat.Png);
            Log($"签到入口探测完成：余额锚点={(afterEvidence.Balance is null ? "无" : afterEvidence.Balance.RawText)}，" +
                $"个人中心={afterEvidence.IsPersonalCenter}，弹层立即领取={popupImmediate}，截图={output}；未执行最终领取点击。");
            return 0;
        }
        finally
        {
            if (!wasForeground) Native.ShowWindow(window, Native.SW_MINIMIZE);
        }
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
        if (GetAttemptLimit(config, ClaimRunMode.ManualTest) != 1 ||
            GetAttemptLimit(config, ClaimRunMode.Automatic) != config.MaxAttempts)
            throw new InvalidOperationException("手动测试必须只尝试一次，自动领取必须使用配置次数。");
        if (ShouldPersistDailyState(ClaimRunMode.ManualTest) || !ShouldPersistDailyState(ClaimRunMode.Automatic))
            throw new InvalidOperationException("手动测试不得写入每日状态，自动领取必须写入每日状态。");
        var nextAfterTerminalFailure = NextClaimTime(new DateTime(2026, 7, 26, 12, 0, 0), TimeSpan.Zero);
        if (nextAfterTerminalFailure != new DateTime(2026, 7, 27, 0, 0, 0))
            throw new InvalidOperationException("领取失败后应休眠至下一天领取时间。");
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

        var selfTestConfig = new Config();
        var ocr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "积分余额", Words = [new OcrWord { Text = "积分余额", X = 30, Y = 350, Width = 80, Height = 18 }] },
                new OcrLine { Text = "1324.67", Words = [new OcrWord { Text = "1324.67", X = 198, Y = 350, Width = 58, Height = 18 }] },
                new OcrLine { Text = "签到领积分", Words = [new OcrWord { Text = "签到领积分", X = 52, Y = 292, Width = 98, Height = 22 }] },
                new OcrLine { Text = "体验版", Words = [new OcrWord { Text = "体验版", X = 52, Y = 145, Width = 42, Height = 18 }] },
                new OcrLine { Text = "领取说明", Words = [new OcrWord { Text = "领取说明", X = 52, Y = 100, Width = 90, Height = 18 }] }
            ]
        };
        var balance = TryReadBalance(ocr, selfTestConfig) ?? throw new InvalidOperationException("积分余额 OCR 锚点校验失败。");
        if (balance.Fingerprint != "132467") throw new InvalidOperationException("积分余额 OCR 指纹校验失败。");
        var actions = FindClaimActions(ocr, balance, selfTestConfig);
        if (actions.Count != 1 || actions[0].Keyword != "签到领积分" || actions[0].Kind != ClaimActionKind.CheckIn)
            throw new InvalidOperationException("动态领取文字 OCR 路由校验失败。");
        var immediateOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "积分余额", Words = [new OcrWord { Text = "积分余额", X = 30, Y = 350, Width = 80, Height = 18 }] },
                new OcrLine { Text = "1324.67", Words = [new OcrWord { Text = "1324.67", X = 198, Y = 350, Width = 58, Height = 18 }] },
                new OcrLine { Text = "立即领取", Words = [new OcrWord { Text = "立即领取", X = 60, Y = 290, Width = 74, Height = 22 }] }
            ]
        };
        var immediateBalance = TryReadBalance(immediateOcr, selfTestConfig)
                               ?? throw new InvalidOperationException("立即领取余额锚点校验失败。");
        var immediateAction = FindClaimActions(immediateOcr, immediateBalance, selfTestConfig)
            .FirstOrDefault(action => action.Kind == ClaimActionKind.Immediate);
        if (immediateAction is null)
            throw new InvalidOperationException("立即领取 OCR 优先路由校验失败。");
        var immediateEvidence = new MenuEvidence(immediateBalance, [immediateAction], false, true);
        if (SelectNewImmediateAction(immediateEvidence, immediateBalance.Bounds,
                new HashSet<string>([immediateAction.CandidateId]), selfTestConfig) is not null ||
            SelectNewImmediateAction(immediateEvidence, immediateBalance.Bounds,
                new HashSet<string>(), selfTestConfig) is null)
            throw new InvalidOperationException("签到后仅接受新出现立即领取的校验失败。");
        var visualBefore = new BalanceReading("视觉余额指纹", "V:000000000000", immediateBalance.Bounds, true);
        var visualSame = new BalanceReading("视觉余额指纹", "V:000100000000", immediateBalance.Bounds, true);
        var visualChanged = new BalanceReading("视觉余额指纹", "V:010101010101", immediateBalance.Bounds, true);
        if (!AreSameBalance(visualBefore, visualSame, selfTestConfig) ||
            !IsBalanceChanged(visualBefore, visualChanged, selfTestConfig))
            throw new InvalidOperationException("视觉余额稳定性校验失败。");
        var claimedOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "今日已领", Words = [new OcrWord { Text = "今日已领", X = 45, Y = 278, Width = 68, Height = 18 }] },
                new OcrLine { Text = "+100", Words = [new OcrWord { Text = "+100", X = 70, Y = 314, Width = 45, Height = 18 }] }
            ]
        };
        if (!HasClaimSuccessText(claimedOcr, balance, selfTestConfig))
            throw new InvalidOperationException("今日已领 OCR 文本校验失败。");
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
    internal static readonly string[] DefaultClaimActionExclusions = ["体验版"];
    internal static readonly string[] DefaultImmediateClaimKeywords = ["立即领取"];
    internal static readonly string[] DefaultCheckInKeywords = ["签到领积分", "去签到", "签到"];
    public string WorkBuddyPath { get; set; } = @"D:\Program Files\WorkBuddy\WorkBuddy.exe";
    public string ClaimTime { get; set; } = "00:00";
    public int RetryIntervalSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
    public int LaunchWaitSeconds { get; set; } = 20;
    public int CardReadyTimeoutSeconds { get; set; } = 30;
    public List<string> ClaimActionExclusions { get; set; } = [.. DefaultClaimActionExclusions];
    public List<string> ImmediateClaimKeywords { get; set; } = [.. DefaultImmediateClaimKeywords];
    public List<string> CheckInKeywords { get; set; } = [.. DefaultCheckInKeywords];
    public int BalanceValueVerticalTolerance { get; set; } = 80;
    public int BalanceValueSameRowTolerance { get; set; } = 32;
    public int BalanceValueDirectRightPixels { get; set; } = 360;
    public int BalanceValueCropWidthPixels { get; set; } = 300;
    public int BalanceValueCropAbovePixels { get; set; } = 30;
    public int BalanceValueCropBelowPixels { get; set; } = 34;
    public int BalanceValueCropScale { get; set; } = 3;
    public int BalanceValueFocusedCropLeftOffsetPixels { get; set; } = 80;
    public int BalanceValueFocusedCropWidthPixels { get; set; } = 140;
    public int BalanceValueFocusedCropScale { get; set; } = 8;
    public int BalanceValueFocusedCropAbovePixels { get; set; } = 8;
    public int BalanceValueFocusedCropBelowPixels { get; set; } = 12;
    public int ClaimActionLeftPixels { get; set; } = 60;
    public int ClaimActionRightPixels { get; set; } = 360;
    public int ClaimActionAboveBalancePixels { get; set; } = 220;
    public int ClaimActionBelowBalancePixels { get; set; } = 100;
    public int ClaimCandidatePositionTolerancePixels { get; set; } = 24;
    public int BalanceAnchorDriftPixels { get; set; } = 48;
    public int VisualBalanceSameFrameMaxChangedCells { get; set; } = 4;
    public int VisualBalanceChangeMinimumChangedCells { get; set; } = 5;
    public int PopupCardAnchorLeftOffsetPixels { get; set; } = 90;
    public int PopupCardAnchorTopOffsetPixels { get; set; } = 35;
    public int PopupCardWidthPixels { get; set; } = 340;
    public int PopupCardHeightPixels { get; set; } = 235;
    public int PopupCardOcrScale { get; set; } = 4;
    public int PersonalCenterEvidenceAboveBalancePixels { get; set; } = 80;
    public int PersonalCenterEvidenceBelowBalancePixels { get; set; } = 300;
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
internal sealed class State
{
    public DateOnly? SuccessDate { get; set; }
    public DateOnly? TerminalFailureDate { get; set; }
}

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
