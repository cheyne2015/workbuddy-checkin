using CommunityToolkit.WinUI.Notifications;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Windows.UI.Notifications;

namespace WorkBuddyAutoClaim;

internal static class Program
{
    private const string TaskName = "WorkBuddy Auto Claim";
    private const string SingletonName = "WorkBuddyAutoClaim.Singleton";
    private const string ManualTestRequestName = "WorkBuddyAutoClaim.ManualTestRequest";
    private const string DaemonReadyEventName = "WorkBuddyAutoClaim.DaemonReady";
    private const string LoginRequiredResultPrefix = "检测到 WorkBuddy 登录失效";
    private const int PersistentNotificationRetentionDays = 3;
    private const int LogRetentionDays = 30;
    private const int FailureDiagnosticRetentionCount = 20;
    private const int ExpectedOcrProcessTimeoutSeconds = 8;
    private const int UiFrameSignatureColumns = 12;
    private const int UiFrameSignatureRows = 8;
    private const int UiFrameLumaQuantization = 32;
    private const int UiTransitionMinimumCellDelta = 2;
    private const int UiTransitionMinimumChangedCells = 3;
    private static readonly TimeSpan ManualTestHandoffTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DaemonRestartConfirmationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OcrProcessTimeout = TimeSpan.FromSeconds(ExpectedOcrProcessTimeoutSeconds);
    private static readonly string BaseDir = AppContext.BaseDirectory;
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
    private static readonly string ConfigPath = Path.Combine(BaseDir, "config.json");
    private static readonly string StatePath = Path.Combine(DataDirectory, "state.json");
    private static readonly string StateBackupPath = StatePath + ".bak";
    private static readonly string RunStatusPath = Path.Combine(DataDirectory, "run-status.json");
    private static Mutex? _mutex;
    private static readonly SemaphoreSlim ClaimExecutionGate = new(1, 1);
    private static DateOnly? _lastRetentionMaintenanceDate;
    internal static string ConfigFilePath => ConfigPath;

    [STAThread]
    private static int Main(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "--daemon";
        if (command == "--daemon-ready-probe") return RunDaemonReadyProbe(args);
        if (command == "--self-test") return RunSelfTest();
        if (command == "--ui-smoke-test") return TrayApplication.RunSmokeTest(args.Skip(1).FirstOrDefault());
        if (command == "--startup-status") return RunStartupStatusProbe();
        if (command == "--set-startup") return RunSetStartup(args.Skip(1).FirstOrDefault());
        if (command == "--verify-claim-ocr") return RunClaimOcrVerification(args);
        if (command == "--verify-immediate-ocr") return RunImmediateOcrVerification(args.Skip(1).FirstOrDefault());
        if (command == "--verify-personal-center-ocr")
            return RunPersonalCenterOcrVerification(args.Skip(1).FirstOrDefault(), args.Skip(2).FirstOrDefault());
        if (command is "--run-now" or "--manual-test") return RunManualTest();
        if (IsInteractiveNonClaimTest(command)) return RunInteractiveNonClaimTest(command);

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
                StartDaemonAfterExclusiveOperation("安装");
                return exitCode;
            }
            return command switch
            {
                "--uninstall" => Uninstall(),
                "--dry-run" => DryRun(config),
                "--verify-layout" => VerifyLayout(config),
                "--verify-card" => VerifyBuddyCard(args.Skip(1).FirstOrDefault()),
                "--ocr-screenshot" => OcrScreenshot(args.Skip(1).FirstOrDefault()),
                "--verify-profile-entry" => VerifyProfileEntry(args.Skip(1).FirstOrDefault(), args.Skip(2).FirstOrDefault(), args.Skip(3).FirstOrDefault()),
                "--verify-profile-recovery" => VerifyProfileRecovery(args.Skip(1).FirstOrDefault()),
                "--test-buddy-card" => TestBuddyCard(config),
                "--test-menu" => TestMenuClick(config),
                "--test-personal-center" => TestPersonalCenter(config),
                "--test-notification" => TestNotification(config),
                "--probe-checkin-entry" => ProbeCheckInEntry(config),
                "--daemon" => TrayApplication.RunDaemon(config),
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

    private static bool IsInteractiveNonClaimTest(string command) =>
        command is "--test-buddy-card" or "--test-menu" or "--test-personal-center" or
            "--test-notification" or "--probe-checkin-entry";

    private static int RunInteractiveNonClaimTest(string command)
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
                Log($"Non-claim test {command} requested; waiting for daemon handoff.");
                request.Set();
                if (!_mutex.WaitOne(ManualTestHandoffTimeout))
                    throw new TimeoutException($"Daemon did not yield within {ManualTestHandoffTimeout.TotalSeconds:0} seconds; test was not executed.");
                ownsMutex = true;
                restartDaemon = true;
            }
            return command switch
            {
                "--test-buddy-card" => TestBuddyCard(config),
                "--test-menu" => TestMenuClick(config),
                "--test-personal-center" => TestPersonalCenter(config),
                "--test-notification" => TestNotification(config),
                "--probe-checkin-entry" => ProbeCheckInEntry(config),
                _ => 2
            };
        }
        catch (Exception ex)
        {
            Log($"Non-claim test {command} failed or was not executed: {ex}");
            Notify("WorkBuddy 安全测试失败", "测试未执行或发生错误；已停止并等待确认。", ToolTipIcon.Error);
            return 1;
        }
        finally
        {
            if (ownsMutex) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;
            if (restartDaemon)
            {
                try { StartDaemonAfterExclusiveOperation("安全测试"); }
                catch (Exception ex)
                {
                    Log("Failed to restore daemon after non-claim test: " + ex);
                    Notify("WorkBuddy 安全测试", "测试结束，但后台守护恢复失败，请手动启动工具。", ToolTipIcon.Error);
                }
            }
        }
    }

    internal static int RunDaemon(Config config, WaitHandle? configChanged = null)
    {
        ValidateUserConfig(config);
        using var daemonReady = new EventWaitHandle(false, EventResetMode.AutoReset, DaemonReadyEventName);
        daemonReady.Set();
        Log("后台守护已启动并发出就绪信号，领取时间: " + config.ClaimTime);

        using var manualTestRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ManualTestRequestName);
        while (true)
        {
            var retryDelay = TimeSpan.FromSeconds(Math.Clamp(config.RetryIntervalSeconds, 10, 3600));
            try
            {
                if (WaitForManualTestRequest(manualTestRequest, TimeSpan.Zero)) return 0;
                config = LoadConfig();
                var claimTime = TimeSpan.ParseExact(config.ClaimTime, @"hh\:mm", CultureInfo.InvariantCulture);
                retryDelay = TimeSpan.FromSeconds(config.RetryIntervalSeconds);
                var now = DateTime.Now;
                var stateLoad = LoadState();
                if (!stateLoad.IsUsable)
                {
                    Log("State file and backup are invalid; claim is stopped safely for today.");
                    Notify("WorkBuddy 自动领取", "状态文件与备份均无法读取；今日未执行领取，请查看诊断日志。", ToolTipIcon.Error);
                    if (SleepUntilOrManualTestRequest(NextClaimTime(now, claimTime), "状态文件损坏，等待明天", manualTestRequest, configChanged)) return 0;
                    continue;
                }
                var state = stateLoad.State!;
                if (stateLoad.Source == StateLoadSource.Backup)
                {
                    Log("State primary file was invalid; restored the last verified backup.");
                    SaveState(state);
                    if (!HasDailyTerminalState(state, DateOnly.FromDateTime(now)))
                    {
                        Log("Recovered state cannot prove today's claim status; stopped safely for today.");
                        Notify("WorkBuddy 自动领取", "状态文件已从备份恢复，但无法确认今日状态；今日未执行领取。", ToolTipIcon.Error);
                        if (SleepUntilOrManualTestRequest(NextClaimTime(now, claimTime), "状态恢复待确认，等待明天", manualTestRequest, configChanged)) return 0;
                        continue;
                    }
                }
                var scheduledToday = now.Date.Add(claimTime);
                if (state.SuccessDate == DateOnly.FromDateTime(now))
                {
                    if (SleepUntilOrManualTestRequest(NextClaimTime(now, claimTime), "今天已成功领取", manualTestRequest, configChanged)) return 0;
                    continue;
                }
                if (state.TerminalFailureDate == DateOnly.FromDateTime(now))
                {
                    if (SleepUntilOrManualTestRequest(NextClaimTime(now, claimTime), $"今天已完成 {config.MaxAttempts} 次领取尝试，等待明天", manualTestRequest, configChanged)) return 0;
                    continue;
                }

                if (now < scheduledToday)
                {
                    if (SleepUntilOrManualTestRequest(scheduledToday, "尚未到领取时间", manualTestRequest, configChanged)) return 0;
                    continue;
                }

                if (!IsInteractiveDesktop())
                {
                    retryDelay = TimeSpan.FromSeconds(60);
                    Log("桌面已锁定；将在 60 秒后重试，且不计入领取次数。");
                }
                else
                {
                    int exitCode = RunOnce(config, ClaimRunMode.Automatic);
                    if (exitCode == 0)
                    {
                        if (SleepUntilOrManualTestRequest(NextClaimTime(now, claimTime), "今天已成功领取", manualTestRequest, configChanged)) return 0;
                        continue;
                    }
                    if (exitCode == 3)
                    {
                        retryDelay = TimeSpan.FromSeconds(60);
                        Log("领取过程中桌面锁定；将在 60 秒后重试，且不计入领取次数。");
                    }
                    else
                    {
                        // RunOnce has already completed the configured consecutive
                        // attempts and sent the one terminal-failure notification. Do not
                        // start another attempt batch every minute for the rest of today.
                        state.TerminalFailureDate = DateOnly.FromDateTime(now);
                        SaveState(state);
                        if (SleepUntilOrManualTestRequest(NextClaimTime(now, claimTime), "今天领取失败，已停止重复尝试", manualTestRequest, configChanged)) return 0;
                        continue;
                    }
                }
            }
            catch (Exception ex) { Log("守护错误: " + ex.Message); }
            if (SleepUntilOrManualTestRequest(DateTime.Now.Add(retryDelay), "等待下次重试", manualTestRequest, configChanged)) return 0;
        }
    }

    private static DateTime NextClaimTime(DateTime now, TimeSpan claimTime)
    {
        var scheduledToday = now.Date.Add(claimTime);
        return now < scheduledToday ? scheduledToday : scheduledToday.AddDays(1);
    }

    private static bool HasDailyTerminalState(State state, DateOnly date) =>
        state.SuccessDate == date || state.TerminalFailureDate == date;

    private static bool SleepUntilOrManualTestRequest(
        DateTime wakeAt, string reason, WaitHandle? interrupt = null, WaitHandle? configChanged = null)
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
            var waitMilliseconds = (int)Math.Ceiling(milliseconds);
            if (configChanged is not null)
            {
                var result = WaitHandle.WaitAny([interrupt, configChanged], waitMilliseconds);
                if (result == 0)
                {
                    LogManualTestYield();
                    return true;
                }
                if (result == 1)
                {
                    Log("检测到配置已更新，重新计算守护计划。");
                    return false;
                }
                return false;
            }
            if (interrupt.WaitOne(waitMilliseconds))
            {
                LogManualTestYield();
                return true;
            }
            return false;
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

            Log($"开始手动测试：按配置最多执行 {config.ManualMaxAttempts} 次，不修改自动领取状态。");
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
        RequestDaemonRestart();
        Log("手动测试结束，已恢复后台守护。");
    }

    private static void StartDaemonAfterExclusiveOperation(string operationName)
    {
        RequestDaemonRestart();
        Log($"{operationName} ended; background daemon restarted.");
    }

    private static void RequestDaemonRestart()
    {
        using var daemonReady = new EventWaitHandle(false, EventResetMode.AutoReset, DaemonReadyEventName);
        DrainReadySignal(daemonReady);
        if (TryRequestDaemonRestartViaScheduledTask(daemonReady)) return;

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
        DrainReadySignal(daemonReady);
        using var fallback = Process.Start(new ProcessStartInfo(exe, "--daemon")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("无法直接启动后台守护。");
        if (!daemonReady.WaitOne(DaemonRestartConfirmationTimeout))
            throw new InvalidOperationException("计划任务与直接启动均未收到后台守护就绪信号。");
        Log("未能通过计划任务恢复守护；已直接启动并确认守护就绪。");
    }

    private static bool TryRequestDaemonRestartViaScheduledTask(WaitHandle daemonReady)
    {
        try
        {
            using var process = Process.Start(CreateScheduledTaskDaemonStartInfo())
                ?? throw new InvalidOperationException("无法启动 schtasks.exe。");
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Log("计划任务恢复守护超时；将回退为直接启动。");
                return false;
            }
            if (process.ExitCode == 0 && daemonReady.WaitOne(DaemonRestartConfirmationTimeout))
            {
                Log("已请求任务计划恢复后台守护，并确认守护进程已启动。");
                return true;
            }
            if (process.ExitCode == 0)
            {
                Log("任务计划已接受守护恢复请求，但未确认守护进程启动；将回退为直接启动。");
                return false;
            }
            Log($"任务计划恢复守护失败，退出码 {process.ExitCode}；将回退为直接启动。");
            return false;
        }
        catch (Exception ex)
        {
            Log("任务计划恢复守护异常；将回退为直接启动：" + ex.Message);
            return false;
        }
    }

    private static ProcessStartInfo CreateScheduledTaskDaemonStartInfo()
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/Run");
        start.ArgumentList.Add("/TN");
        start.ArgumentList.Add(TaskName);
        return start;
    }

    private static void DrainReadySignal(WaitHandle readySignal)
    {
        while (readySignal.WaitOne(0)) { }
    }

    private static ProcessStartInfo CreateDaemonReadyProbeStartInfo(string eventName)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--daemon-ready-probe");
        start.ArgumentList.Add(eventName);
        return start;
    }

    private static int RunDaemonReadyProbe(string[] args)
    {
        var eventName = args.Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(eventName)) return 2;
        using var ready = EventWaitHandle.OpenExisting(eventName);
        ready.Set();
        return 0;
    }

    private enum ClaimRunMode { Automatic, ManualTest }

    private static int GetAttemptLimit(Config config, ClaimRunMode mode) =>
        mode == ClaimRunMode.ManualTest ? config.ManualMaxAttempts : Math.Min(config.MaxAttempts, 5);

    private static bool ShouldPersistDailyState(ClaimRunMode mode) => mode == ClaimRunMode.Automatic;

    private static int RunOnce(Config config, ClaimRunMode mode)
    {
        ClaimExecutionGate.Wait();
        try { return RunOnceCore(config, mode); }
        finally { ClaimExecutionGate.Release(); }
    }

    internal static int RunManualRetryFromTray()
    {
        try
        {
            var config = LoadConfig();
            RecordRunStatus(new RunStatus
            {
                UpdatedAt = DateTimeOffset.Now,
                Mode = "Manual",
                Outcome = "Queued",
                Message = "已从守护面板提交重试请求。",
                AttemptsPerformed = 0,
                MaxAttempts = config.ManualMaxAttempts
            });
            return RunOnce(config, ClaimRunMode.ManualTest);
        }
        catch (Exception ex)
        {
            RecordRunStatus(new RunStatus
            {
                UpdatedAt = DateTimeOffset.Now,
                Mode = "Manual",
                Outcome = "Failed",
                Message = "手动重试未能启动：" + ex.Message
            });
            Log("守护面板手动重试错误: " + ex);
            Notify("WorkBuddy 手动重试失败", "重试未能启动：" + ex.Message, ToolTipIcon.Error);
            return 1;
        }
    }

    private static int RunOnceCore(Config config, ClaimRunMode mode)
    {
        bool isManualTest = mode == ClaimRunMode.ManualTest;
        int maxAttempts = GetAttemptLimit(config, mode);
        MaintainDiagnosticRetention();
        if (!IsInteractiveDesktop())
        {
            Log("桌面已锁定，跳过本次尝试。");
            RecordRunStatus(new RunStatus
            {
                UpdatedAt = DateTimeOffset.Now,
                Mode = isManualTest ? "Manual" : "Automatic",
                Outcome = "Failed",
                Message = "桌面已锁定，未执行领取。",
                AttemptsPerformed = 0,
                MaxAttempts = maxAttempts
            });
            if (isManualTest)
                NotifyManualTestFailure("桌面已锁定，测试未执行",
                    BuildClaimNotificationText(ClaimOutcomeKind.Failed, null, null));
            return 3;
        }

        var originalWindow = FindWorkBuddyWindow();
        bool wasRunning = originalWindow != IntPtr.Zero;
        bool wasForeground = wasRunning && Native.GetForegroundWindow() == originalWindow;
        bool launchedByTool = false;
        IntPtr window = IntPtr.Zero;
        bool succeeded = false;
        int attemptsPerformed = 0;
        string result = "未能确认领取成功";
        ClaimOutcomeKind outcomeKind = ClaimOutcomeKind.Failed;
        BalanceReading? notificationBeforeBalance = null;
        BalanceReading? notificationAfterBalance = null;
        RecordRunStatus(new RunStatus
        {
            UpdatedAt = DateTimeOffset.Now,
            Mode = isManualTest ? "Manual" : "Automatic",
            Outcome = "Running",
            Message = "正在打开个人中心并读取积分余额。",
            AttemptsPerformed = 0,
            MaxAttempts = maxAttempts
        });
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
                if (!IsInteractiveDesktop())
                {
                    if (isManualTest)
                    {
                        Log("手动测试开始前检测到桌面锁定；测试未执行且不计入尝试次数。");
                        return 3;
                    }
                    WaitForInteractiveDesktopWithinBatch(attemptsPerformed, maxAttempts);
                }
                attemptsPerformed = attempt;
                try
                {
                    Log($"开始{(isManualTest ? "手动测试" : "领取")}，第 {attempt}/{maxAttempts} 次。");
                    succeeded = TryClaimFromPersonalCenter(window, config, out result, out outcomeKind,
                        out notificationBeforeBalance, out notificationAfterBalance);
                }
                catch (LoginRequiredException ex)
                {
                    result = LoginRequiredResultPrefix + "；请手动登录后再试。";
                    outcomeKind = ClaimOutcomeKind.Failed;
                    notificationAfterBalance = null;
                    Log($"第 {attempt} 次安全停止: {ex.Message}");
                }
                catch (Exception ex)
                {
                    result = ex.Message;
                    outcomeKind = ClaimOutcomeKind.Failed;
                    notificationAfterBalance = null;
                    Log($"第 {attempt} 次失败: {ex.Message}");
                    if (window != IntPtr.Zero)
                        SaveFailureDiagnostic(window, config, "attempt-exception", ex.Message);
                }
                if (!succeeded && IsLoginRequiredResult(result))
                {
                    Log("检测到登录失效；停止本日后续领取尝试。");
                    break;
                }
                if (!succeeded && !IsInteractiveDesktop())
                {
                    if (isManualTest)
                    {
                        Log("手动测试过程中检测到桌面锁定；当前失败不计入测试次数。");
                        return 3;
                    }
                    attemptsPerformed = Math.Max(0, attempt - 1);
                    WaitForInteractiveDesktopWithinBatch(attemptsPerformed, maxAttempts);
                    attempt--;
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
            RecordRunStatus(new RunStatus
            {
                UpdatedAt = DateTimeOffset.Now,
                Mode = isManualTest ? "Manual" : "Automatic",
                Outcome = outcomeKind.ToString(),
                Message = result,
                BeforeBalance = FormatNotificationBalance(notificationBeforeBalance),
                AfterBalance = FormatNotificationBalance(notificationAfterBalance),
                AttemptsPerformed = attemptsPerformed,
                MaxAttempts = maxAttempts
            });
            Notify(isManualTest ? "WorkBuddy 手动测试" : "WorkBuddy 自动领取",
                BuildRunNotificationText(outcomeKind, FormatNotificationBalance(notificationBeforeBalance),
                    FormatNotificationBalance(notificationAfterBalance), attemptsPerformed, maxAttempts,
                    DescribeWorkBuddyLifecycle(launchedByTool, wasForeground), result), ToolTipIcon.Info);
            return 0;
        }

        Log("领取失败: " + result);
        RecordRunStatus(new RunStatus
        {
            UpdatedAt = DateTimeOffset.Now,
            Mode = isManualTest ? "Manual" : "Automatic",
            Outcome = "Failed",
            Message = result,
            BeforeBalance = FormatNotificationBalance(notificationBeforeBalance),
            AfterBalance = FormatNotificationBalance(notificationAfterBalance),
            AttemptsPerformed = attemptsPerformed,
            MaxAttempts = maxAttempts
        });
        var failureNotification = BuildRunNotificationText(ClaimOutcomeKind.Failed,
            FormatNotificationBalance(notificationBeforeBalance), FormatNotificationBalance(notificationAfterBalance),
            attemptsPerformed, maxAttempts, DescribeWorkBuddyLifecycle(launchedByTool, wasForeground), result);
        if (isManualTest)
            NotifyManualTestFailure(result, failureNotification);
        else
            Notify("WorkBuddy 自动领取失败", $"{result}\n{failureNotification}", ToolTipIcon.Error);
        return 1;
    }

    private static void NotifyManualTestFailure(string result, string balanceText) =>
        Notify("WorkBuddy 手动测试失败", $"{result}\n{balanceText}\n已停止测试并等待确认。", ToolTipIcon.Error);

    private static void WaitForInteractiveDesktopWithinBatch(int attemptsPerformed, int maxAttempts)
    {
        do
        {
            Log($"领取批次因桌面锁定暂停 60 秒；已完成尝试 {attemptsPerformed}/{maxAttempts}，解锁后继续剩余次数。");
            RecordRunStatus(new RunStatus
            {
                UpdatedAt = DateTimeOffset.Now,
                Mode = "Automatic",
                Outcome = "Deferred",
                Message = "桌面已锁定；60 秒后检查，解锁后继续当前领取批次。",
                AttemptsPerformed = attemptsPerformed,
                MaxAttempts = maxAttempts
            });
            Thread.Sleep(TimeSpan.FromSeconds(60));
        }
        while (!IsInteractiveDesktop());
        Log($"桌面已解锁；继续当前领取批次剩余 {maxAttempts - attemptsPerformed} 次。 ");
    }

    private static IntPtr EnsureWorkBuddyWindow(Config config)
    {
        return EnsureWorkBuddyWindow(config, out _);
    }

    private static IntPtr EnsureWorkBuddyWindow(Config config, out bool launchedByTool)
    {
        var existing = FindWorkBuddyWindow();
        bool hadExistingProcess = HasExistingWorkBuddyProcess();
        if (existing != IntPtr.Zero)
        {
            launchedByTool = false;
            return existing;
        }
        launchedByTool = ShouldTreatWorkBuddyAsToolLaunched(hadVisibleWindow: false, hadExistingProcess);
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

    private static int RunSelfTest()
    {
        try { return SelfTest(LoadConfig()); }
        catch (Exception ex)
        {
            Log("Self test failed: " + ex);
            return 1;
        }
    }

    private static int RunClaimOcrVerification(string[] args)
    {
        try { return VerifyClaimOcr(args.Skip(1).FirstOrDefault(), LoadConfig(), args.Skip(2).FirstOrDefault(), args.Skip(3).FirstOrDefault()); }
        catch (Exception ex)
        {
            Log("OCR screenshot verification failed: " + ex);
            return 1;
        }
    }

    private static int RunImmediateOcrVerification(string? imagePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                throw new FileNotFoundException("请提供可读取的 WorkBuddy 截图路径。", imagePath);
            using var bitmap = new Bitmap(imagePath);
            var directOcr = ReadClaimOcr(bitmap);
            var action = FindImmediateClaimAction(bitmap, directOcr, LoadConfig())
                         ?? throw new InvalidOperationException("整窗多路 OCR 未识别到完整的“立即领取”。");
            Console.WriteLine($"立即领取={action.Text}; X={action.CenterX}; Y={action.CenterY}");
            Log($"立即领取整窗 OCR 验证通过：文本={action.Text}，位置=({action.CenterX},{action.CenterY})。");
            return 0;
        }
        catch (Exception ex)
        {
            Log("立即领取整窗 OCR 验证失败: " + ex);
            return 1;
        }
    }

    private static int RunPersonalCenterOcrVerification(string? imagePath, string? expectedBalance)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                throw new FileNotFoundException("请提供可读取的个人中心截图路径。", imagePath);
            using var bitmap = new Bitmap(imagePath);
            var config = LoadConfig();
            var ocr = ReadClaimOcr(bitmap);
            var evidence = ReadMenuEvidence(bitmap, ocr, config);
            if (!evidence.IsPersonalCenter || !HasConfirmedNumericBalance(evidence.Balance))
                throw new InvalidOperationException("未同时识别到个人中心组合锚点和明确数字余额。");
            if (!TryFindBalanceRefreshPoint(ocr, config, out var refreshPoint))
                throw new InvalidOperationException("未识别到积分余额行中的刷新图标。");
            var balance = FormatNotificationBalance(evidence.Balance);
            if (!string.IsNullOrWhiteSpace(expectedBalance) &&
                !StringComparer.Ordinal.Equals(balance, expectedBalance))
                throw new InvalidOperationException($"积分余额 OCR 校验失败：期望 {expectedBalance}，实际 {balance}。");
            Console.WriteLine($"个人中心=true; 余额={balance}; 刷新X={refreshPoint.X}; 刷新Y={refreshPoint.Y}");
            Log($"个人中心 OCR 验证通过：余额={balance}，刷新点=({refreshPoint.X},{refreshPoint.Y})。");
            return 0;
        }
        catch (Exception ex)
        {
            Log("个人中心 OCR 验证失败: " + ex);
            return 1;
        }
    }

    private static bool ShouldTreatWorkBuddyAsToolLaunched(bool hadVisibleWindow, bool hadExistingProcess) =>
        !hadVisibleWindow && !hadExistingProcess;

    private static bool HasExistingWorkBuddyProcess()
    {
        foreach (var process in Process.GetProcessesByName("WorkBuddy"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited) return true;
                }
                catch { }
            }
        }
        return false;
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

    private static bool TryClaimFromPersonalCenter(
        IntPtr window, Config config, out string result, out ClaimOutcomeKind outcomeKind,
        out BalanceReading? beforeNotificationBalance, out BalanceReading? afterNotificationBalance)
    {
        outcomeKind = ClaimOutcomeKind.Failed;
        beforeNotificationBalance = null;
        afterNotificationBalance = null;
        using (var loginProbe = CaptureWindow(window))
        {
            if (loginProbe is not null) ThrowIfLoginRequired(ReadOcr(loginProbe));
        }
        // 以“积分余额”文字为个人中心锚点。这样页面的卡片颜色、尺寸和按钮位置改变时，
        // 仍然只会在确认个人中心已经打开后，点击 OCR 实际读到的领取文字。
        if (!TryOpenPersonalCenterAndReadEvidence(window, config, out var evidence))
        {
            result = "未能在左下个人中心识别到“积分余额”，拒绝猜测点击";
            return false;
        }
        var beforeBalance = evidence.Balance ?? throw new InvalidOperationException("领取前丢失了积分余额 OCR 锚点。");
        beforeNotificationBalance = beforeBalance;
        var firstCandidates = new HashSet<string>(StringComparer.Ordinal);
        var firstRoute = ExecuteClaimRouteInCurrentWindow(window, config, beforeBalance, firstCandidates,
            out result, out outcomeKind, out afterNotificationBalance);
        if (firstRoute == ClaimRouteExecution.Succeeded) return true;
        if (firstRoute == ClaimRouteExecution.Failed) return false;

        if (!EnsurePersonalCenterClosedForSecondScan(window, config))
        {
            result = "首次扫描无领取动作，且未能确认个人中心已经关闭。";
            return false;
        }

        Log("个人中心已关闭；开始第二次整窗领取文字扫描。");
        var secondCandidates = new HashSet<string>(StringComparer.Ordinal);
        var secondRoute = ExecuteClaimRouteInCurrentWindow(window, config, beforeBalance, secondCandidates,
            out result, out outcomeKind, out afterNotificationBalance);
        if (secondRoute == ClaimRouteExecution.Succeeded) return true;
        if (secondRoute == ClaimRouteExecution.Failed) return false;

        if (!TryOpenPersonalCenterAndReadEvidence(window, config, out var reopenedEvidence) ||
            reopenedEvidence.Balance is null)
        {
            result = "第二次扫描无领取动作，重新打开个人中心后未能刷新并确认积分余额。";
            return false;
        }

        Log($"重新打开个人中心并读取积分余额 {reopenedEvidence.Balance.RawText}；立即执行第三次整窗扫描。");
        var thirdCandidates = new HashSet<string>(StringComparer.Ordinal);
        var thirdRoute = ExecuteClaimRouteInCurrentWindow(window, config, beforeBalance, thirdCandidates,
            out result, out outcomeKind, out afterNotificationBalance);
        if (thirdRoute == ClaimRouteExecution.Succeeded) return true;
        if (thirdRoute == ClaimRouteExecution.Failed) return false;

        // A check-in click can navigate away or close the menu. Re-establish the
        // personal center only when needed; every successful balance read is followed
        // by another full-window scan before the Buddy fallback may be clicked.
        bool personalCenterReadyForBuddy = thirdCandidates.Count == 0;
        for (int recoveryScan = 1; !personalCenterReadyForBuddy && recoveryScan <= 3; recoveryScan++)
        {
            if (!TryOpenPersonalCenterAndReadEvidence(window, config, out var buddyMenuEvidence) ||
                buddyMenuEvidence.Balance is null)
            {
                result = "进入 Buddy加油站前未能重新确认个人中心和明确数字余额。";
                return false;
            }
            Log($"进入 Buddy加油站前第 {recoveryScan}/3 次重新确认积分余额 {buddyMenuEvidence.Balance.RawText}；立即执行整窗扫描。");
            int candidatesBeforeScan = thirdCandidates.Count;
            var recoveryRoute = ExecuteClaimRouteInCurrentWindow(window, config, beforeBalance, thirdCandidates,
                out result, out outcomeKind, out afterNotificationBalance);
            if (recoveryRoute == ClaimRouteExecution.Succeeded) return true;
            if (recoveryRoute == ClaimRouteExecution.Failed) return false;
            personalCenterReadyForBuddy = thirdCandidates.Count == candidatesBeforeScan;
        }
        if (!personalCenterReadyForBuddy)
        {
            result = "进入 Buddy加油站前连续出现新的签到入口，未能在个人中心稳定完成扫描。";
            return false;
        }

        if (!TryClickBuddyFuelStation(window, config, out var buddyFailure))
        {
            result = buddyFailure;
            SaveFailureDiagnostic(window, config, "buddy-fuel-station-not-found", result);
            return false;
        }

        Log("Buddy加油站已点击；等待页面加载并再次按优先级扫描整个窗口。");
        var buddyCandidates = new HashSet<string>(StringComparer.Ordinal);
        var buddyRoute = ExecuteClaimRouteUntilAction(window, config, beforeBalance, buddyCandidates,
            TimeSpan.FromSeconds(10), out result, out outcomeKind, out afterNotificationBalance);
        if (buddyRoute == ClaimRouteExecution.Succeeded) return true;
        if (buddyRoute == ClaimRouteExecution.Failed) return false;

        bool clickedCheckIn = firstCandidates.Count > 0 || secondCandidates.Count > 0 ||
                              thirdCandidates.Count > 0 || buddyCandidates.Count > 0;
        result = BuildClaimActionNotFoundResult(clickedCheckIn);
        SaveFailureDiagnostic(window, config, "claim-action-not-found", result);
        return false;
    }

    private enum ClaimRouteExecution { Succeeded, Failed, NoAction }

    private static ClaimRouteExecution ExecuteClaimRouteInCurrentWindow(
        IntPtr window, Config config, BalanceReading beforeBalance, HashSet<string> triedCandidateIds,
        out string result, out ClaimOutcomeKind outcomeKind, out BalanceReading? afterNotificationBalance)
    {
        result = "当前整窗未识别到领取动作或已领取状态";
        outcomeKind = ClaimOutcomeKind.Failed;
        afterNotificationBalance = null;
        using var currentImage = CaptureWindow(window);
        if (currentImage is null)
        {
            result = "无法读取当前 WorkBuddy 界面。";
            return ClaimRouteExecution.Failed;
        }

        var currentOcr = ReadClaimOcr(currentImage);
        var immediate = FindImmediateClaimAction(currentImage, currentOcr, config);
        bool stableClaimedText = immediate is null && HasClaimSuccessText(currentOcr) &&
                                 TryConfirmStableClaimSuccessText(window);
        var checkInActions = immediate is null && !stableClaimedText
            ? FindCheckInActions(currentImage, currentOcr, config)
            : [];
        switch (SelectClaimRoute(immediate is not null, stableClaimedText, checkInActions))
        {
            case ClaimRouteKind.Immediate:
                return ClickImmediateClaimAndVerify(window, config, immediate!, beforeBalance,
                    out result, out outcomeKind, out afterNotificationBalance)
                    ? ClaimRouteExecution.Succeeded
                    : ClaimRouteExecution.Failed;
            case ClaimRouteKind.AlreadyClaimed:
                result = "WorkBuddy 今日已领取";
                outcomeKind = ClaimOutcomeKind.AlreadyClaimed;
                afterNotificationBalance = beforeBalance;
                return ClaimRouteExecution.Succeeded;
            case ClaimRouteKind.CheckIn:
                foreach (var checkIn in checkInActions)
                {
                    if (!triedCandidateIds.Add(checkIn.CandidateId)) continue;
                    Log($"OCR 识别签到入口：{checkIn.Keyword}，文本={checkIn.Text}，位置=({checkIn.CenterX},{checkIn.CenterY})。");
                    var beforeCheckInSignature = CreateUiFrameSignature(currentImage);
                    ClickWindowPoint(window, checkIn.CenterX, checkIn.CenterY);
                    var followup = TryFindImmediateClaimAfterCheckIn(window, config, beforeCheckInSignature,
                        out var discoveredImmediate);
                    if (followup == CheckInFollowup.Immediate && discoveredImmediate is not null)
                        return ClickImmediateClaimAndVerify(window, config, discoveredImmediate, beforeBalance,
                            out result, out outcomeKind, out afterNotificationBalance, balanceAlreadyStabilized: true)
                            ? ClaimRouteExecution.Succeeded
                            : ClaimRouteExecution.Failed;
                    if (followup != CheckInFollowup.AlreadyClaimed) continue;
                    if (!TryOpenPersonalCenterAndReadEvidence(window, config, out var afterEvidence) ||
                        afterEvidence.Balance is null)
                    {
                        result = "签到后识别到今日已领取，但未能刷新并读取个人中心余额。";
                        return ClaimRouteExecution.Failed;
                    }
                    afterNotificationBalance = afterEvidence.Balance;
                    if (IsBalanceIncreased(beforeBalance, afterEvidence.Balance, config))
                    {
                        outcomeKind = ClaimOutcomeKind.Claimed;
                        result = "领取成功，签到后刷新积分余额已增加";
                        return ClaimRouteExecution.Succeeded;
                    }
                    if (AreSameBalance(beforeBalance, afterEvidence.Balance, config))
                    {
                        outcomeKind = ClaimOutcomeKind.AlreadyClaimed;
                        result = "WorkBuddy 今日已领取";
                        return ClaimRouteExecution.Succeeded;
                    }
                    result = "签到后已领取文字稳定，但刷新余额既未增加也未保持一致。";
                    return ClaimRouteExecution.Failed;
                }
                result = BuildClaimActionNotFoundResult(clickedCheckIn: true);
                return ClaimRouteExecution.NoAction;
            default:
                return ClaimRouteExecution.NoAction;
        }
    }

    private static ClaimRouteExecution ExecuteClaimRouteUntilAction(
        IntPtr window, Config config, BalanceReading beforeBalance, HashSet<string> triedCandidateIds,
        TimeSpan timeout, out string result, out ClaimOutcomeKind outcomeKind,
        out BalanceReading? afterNotificationBalance)
    {
        var until = DateTime.UtcNow.Add(timeout);
        do
        {
            var route = ExecuteClaimRouteInCurrentWindow(window, config, beforeBalance, triedCandidateIds,
                out result, out outcomeKind, out afterNotificationBalance);
            if (route != ClaimRouteExecution.NoAction) return route;
            Thread.Sleep(650);
        }
        while (DateTime.UtcNow < until);

        result = "进入 Buddy加油站后仍未识别到立即领取、稳定已领取状态或签到入口。";
        outcomeKind = ClaimOutcomeKind.Failed;
        afterNotificationBalance = null;
        return ClaimRouteExecution.NoAction;
    }

    private static bool TryClickBuddyFuelStation(IntPtr window, Config config, out string failure)
    {
        using var image = CaptureWindow(window);
        if (image is null)
        {
            failure = "重新打开个人中心后无法捕获界面，未点击 Buddy加油站。";
            return false;
        }

        var ocr = ReadClaimOcr(image);
        var buddy = FindBuddyFuelStationAction(ocr, config);
        if (FindBalanceLabel(ocr) is null || buddy is null)
        {
            failure = "点击 Buddy加油站前积分余额标签或精确 Buddy加油站入口已消失，拒绝猜测点击。";
            return false;
        }

        Log($"OCR 精确识别 Buddy加油站入口：文本={buddy.Text}，位置=({buddy.CenterX},{buddy.CenterY})。");
        ClickWindowPoint(window, buddy.CenterX, buddy.CenterY);
        Thread.Sleep(1_000);
        failure = string.Empty;
        return true;
    }

    private static bool TryConfirmStableClaimSuccessText(IntPtr window)
    {
        Thread.Sleep(650);
        using var secondImage = CaptureWindow(window);
        return secondImage is not null && HasClaimSuccessText(ReadClaimOcr(secondImage));
    }

    private static bool EnsurePersonalCenterClosedForSecondScan(IntPtr window, Config config)
    {
        int height = GetWindowHeight(window);
        if (height <= config.ProfileBottomOffset) return false;
        int profileX = config.ProfileX;
        int profileY = height - config.ProfileBottomOffset;
        using (var before = CaptureWindow(window))
        {
            if (before is not null)
            {
                var beforeOcr = ReadClaimOcr(before);
                if (FindBalanceLabel(beforeOcr) is null)
                {
                    Thread.Sleep(500);
                    using var confirmation = CaptureWindow(window);
                    if (confirmation is not null && FindBalanceLabel(ReadClaimOcr(confirmation)) is null)
                    {
                        Log("签到入口点击后个人中心已关闭；无需再次点击头像，直接开始下一次整窗扫描。");
                        return true;
                    }
                }
                if (TryFindProfileEntryPoint(before, out var profilePoint))
                {
                    profileX = profilePoint.X;
                    profileY = profilePoint.Y;
                }
            }
        }
        Log($"首次整窗扫描无领取动作；再次点击个人中心一次以关闭面板：({profileX},{profileY})。");
        ClickWindowPoint(window, profileX, profileY);

        int consecutiveAbsentFrames = 0;
        var until = DateTime.UtcNow.AddSeconds(5);
        do
        {
            Thread.Sleep(500);
            using var image = CaptureWindow(window);
            if (image is null) continue;
            var ocr = ReadClaimOcr(image);
            consecutiveAbsentFrames = FindBalanceLabel(ocr) is null ? consecutiveAbsentFrames + 1 : 0;
            if (consecutiveAbsentFrames >= 2) return true;
        }
        while (DateTime.UtcNow < until);
        return false;
    }

    private static string BuildClaimActionNotFoundResult(bool clickedCheckIn) => clickedCheckIn
        ? "未识别到立即领取；已点击签到入口但未出现立即领取按钮"
        : "未识别到立即领取；未识别到签到入口，未执行点击";

    private static bool ClickImmediateClaimAndVerify(
        IntPtr window, Config config, ClaimAction immediate, BalanceReading beforeBalance,
        out string result, out ClaimOutcomeKind outcomeKind, out BalanceReading? afterNotificationBalance,
        bool balanceAlreadyStabilized = false)
    {
        outcomeKind = ClaimOutcomeKind.Failed;
        afterNotificationBalance = null;
        var stableBeforeBalance = beforeBalance;
        if (!balanceAlreadyStabilized && !TryGetStableBalanceBeforeAction(window, config, beforeBalance, out stableBeforeBalance))
        {
            result = "立即领取出现，但点击前未能稳定读取积分余额；为避免误报，本次未点击最终领取按钮";
            return false;
        }
        if (!balanceAlreadyStabilized) beforeBalance = stableBeforeBalance;
        using var beforeClickImage = CaptureWindow(window);
        if (beforeClickImage is null)
        {
            result = "已识别立即领取，但点击前无法捕获界面以验证状态变化。";
            return false;
        }
        var beforeClickSignature = CreateUiFrameSignature(beforeClickImage);
        bool successTextWasPresentBeforeClick = HasClaimSuccessText(ReadClaimOcr(beforeClickImage));
        Log($"OCR 识别最终立即领取：文本={immediate.Text}，位置=({immediate.CenterX},{immediate.CenterY})，点击前积分余额={beforeBalance.RawText}。");
        ClickWindowPoint(window, immediate.CenterX, immediate.CenterY);
        var verification = WaitForClaimResult(window, config, beforeBalance, beforeClickSignature,
            successTextWasPresentBeforeClick, TimeSpan.FromSeconds(20),
            out afterNotificationBalance);
        bool claimed = verification != ClaimVerification.NotConfirmed;
        if (!claimed)
            SaveFailureDiagnostic(window, config, "claim-not-confirmed", "点击立即领取后未确认余额变化或今日已领取");
        result = verification switch
        {
            ClaimVerification.ClaimedText => "WorkBuddy 今日已领取",
            ClaimVerification.BalanceChanged => "领取成功，点击立即领取后 OCR 识别到积分余额变化",
            ClaimVerification.NotConfirmed => "点击立即领取后未识别到积分余额变化或“今日已领”状态",
            _ => "点击立即领取后未识别到积分余额变化或“今日已领”状态"
        };
        outcomeKind = verification switch
        {
            ClaimVerification.BalanceChanged => ClaimOutcomeKind.Claimed,
            ClaimVerification.ClaimedText => ClaimOutcomeKind.AlreadyClaimed,
            _ => ClaimOutcomeKind.Failed
        };
        return claimed;
    }

    private static CheckInFollowup TryFindImmediateClaimAfterCheckIn(
        IntPtr window,
        Config config,
        UiFrameSignature beforeCheckInSignature,
        out ClaimAction? immediate)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        bool observedUiTransition = false;
        int consecutiveClaimedFrames = 0;
        do
        {
            using var image = CaptureWindow(window);
            if (image is not null)
            {
                var ocr = ReadClaimOcr(image);
                var found = FindImmediateClaimAction(image, ocr, config);
                // “立即领取” was absent before the entry click, so its first appearance
                // is itself a concrete post-click state change even if the coarse visual
                // signature has not crossed its luminance threshold yet.
                if (found is not null)
                {
                    immediate = found;
                    return CheckInFollowup.Immediate;
                }
                if (!observedUiTransition && HasMeaningfulUiChange(beforeCheckInSignature, CreateUiFrameSignature(image)))
                {
                    observedUiTransition = true;
                    Log("已观察到签到入口点击后的界面变化，开始查找立即领取。");
                }
                if (observedUiTransition && HasClaimSuccessText(ocr))
                {
                    consecutiveClaimedFrames++;
                    if (consecutiveClaimedFrames >= 2)
                    {
                        immediate = null;
                        return CheckInFollowup.AlreadyClaimed;
                    }
                }
                else consecutiveClaimedFrames = 0;
            }
            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < until);
        SaveBuddyDiagnosticCapture(window, "after-checkin-no-immediate");
        immediate = null;
        return CheckInFollowup.NotFound;
    }

    private static bool TryOpenPersonalCenterAndReadEvidence(IntPtr window, Config config, out MenuEvidence evidence)
    {
        int profileX = config.ProfileX;
        int profileY = GetWindowHeight(window) - config.ProfileBottomOffset;
        bool profileEntryLocated = false;
        bool updateBannerDismissed = false;
        DateTime? personalCenterShellWaitUntil = null;
        Bitmap? lastCapture = null;
        OcrSnapshot? lastOcr = null;
        try
        {
            for (int clickAttempt = 0; clickAttempt <= 3; clickAttempt++)
            {
                using var current = CaptureWindow(window);
                if (current is null)
                {
                    if (clickAttempt < 3) Thread.Sleep(1_000);
                    continue;
                }

                var currentOcr = ReadClaimOcr(current);
                evidence = ReadMenuEvidence(current, currentOcr, config);
                lastCapture?.Dispose();
                lastCapture = (Bitmap)current.Clone();
                lastOcr = currentOcr;
                if (evidence.IsPersonalCenter)
                    return TryRefreshAndConfirmPersonalCenterBalance(window, config, currentOcr, out evidence);

                if (LooksLikePersonalCenterShell(currentOcr))
                {
                    personalCenterShellWaitUntil ??=
                        DateTime.UtcNow.AddSeconds(config.CardReadyTimeoutSeconds);
                    if (DateTime.UtcNow < personalCenterShellWaitUntil.Value)
                    {
                        Log("已识别到个人中心菜单外壳，但积分余额仍在加载；保持面板打开并继续等待。");
                        Thread.Sleep(1_000);
                        clickAttempt--;
                        continue;
                    }
                    Log("个人中心菜单已打开，但积分余额加载超时；将按剩余次数重新打开面板。");
                    personalCenterShellWaitUntil = null;
                }

                if (TryFindProfileEntryPoint(current, out var profilePoint))
                {
                    profileX = profilePoint.X;
                    profileY = profilePoint.Y;
                    profileEntryLocated = true;
                }
                else if (!updateBannerDismissed &&
                         TryDismissBottomLeftUpdateBanner(window, current, currentOcr, out var dismissedPoint))
                {
                    updateBannerDismissed = true;
                    Log($"已关闭遮挡个人中心入口的更新提示：({dismissedPoint.X},{dismissedPoint.Y})。");
                    Thread.Sleep(500);
                    clickAttempt--;
                    continue;
                }
                if (clickAttempt == 3) break;

                int height = GetWindowHeight(window);
                if (height <= config.ProfileBottomOffset) break;
                if (!profileEntryLocated)
                {
                    profileX = config.ProfileX;
                    profileY = height - config.ProfileBottomOffset;
                    Log($"未识别到头像图形；按已确认的左下个人中心位置尝试：({profileX},{profileY})。");
                }
                Log($"点击左下个人中心，第 {clickAttempt + 1}/3 次；随后识别个人中心组合锚点。");
                ClickWindowPoint(window, profileX, profileY);
                Thread.Sleep(1_000);
            }

            evidence = MenuEvidence.Empty;
            SavePersonalCenterFailureEvidence(window, config, lastCapture, lastOcr,
                profileEntryLocated, personalCenterAlreadyOpen: false);
            Log("三次点击个人中心后仍未识别到个人中心组合锚点与明确余额。");
            return false;
        }
        finally { lastCapture?.Dispose(); }
    }

    private static bool TryRefreshAndConfirmPersonalCenterBalance(
        IntPtr window, Config config, OcrSnapshot openingOcr, out MenuEvidence evidence)
    {
        if (!TryFindBalanceRefreshPoint(openingOcr, config, out var refreshPoint))
        {
            evidence = MenuEvidence.Empty;
            Log("个人中心已打开，但未识别到积分余额与数字之间的刷新图标；拒绝猜测点击。");
            SaveFailureDiagnostic(window, config, "balance-refresh-not-found", "未识别到积分余额刷新图标");
            return false;
        }

        Log($"识别并点击积分余额刷新图标：({refreshPoint.X},{refreshPoint.Y})。");
        ClickWindowPoint(window, refreshPoint.X, refreshPoint.Y);
        var balanceFrames = new List<BalanceReading?>();
        int reopenAttemptsAfterRefresh = 0;
        var until = DateTime.UtcNow.AddSeconds(config.CardReadyTimeoutSeconds);
        do
        {
            Thread.Sleep(650);
            using var image = CaptureWindow(window);
            if (image is null) continue;
            var ocr = ReadClaimOcr(image);
            evidence = ReadMenuEvidence(image, ocr, config);
            if (!evidence.IsPersonalCenter && reopenAttemptsAfterRefresh < 3)
            {
                int height = GetWindowHeight(window);
                if (height > config.ProfileBottomOffset)
                {
                    var entryPoint = TryFindProfileEntryPoint(image, out var dynamicPoint)
                        ? dynamicPoint
                        : new Point(config.ProfileX, height - config.ProfileBottomOffset);
                    reopenAttemptsAfterRefresh++;
                    Log($"刷新后个人中心面板意外消失；重新点击个人中心第 {reopenAttemptsAfterRefresh}/3 次：({entryPoint.X},{entryPoint.Y})。");
                    ClickWindowPoint(window, entryPoint.X, entryPoint.Y);
                    balanceFrames.Clear();
                    until = DateTime.UtcNow.AddSeconds(config.CardReadyTimeoutSeconds);
                    Thread.Sleep(1_000);
                    continue;
                }
            }
            if (evidence.IsPersonalCenter &&
                TryConfirmBalanceAcrossFrames(balanceFrames, evidence.Balance, out var confirmedBalance))
            {
                evidence = evidence with { Balance = confirmedBalance };
                Log($"刷新后积分余额已连续确认：{confirmedBalance!.RawText}。");
                return true;
            }
        }
        while (DateTime.UtcNow < until);

        evidence = MenuEvidence.Empty;
        Log("点击刷新图标后未能连续两帧确认明确数字余额。");
        SaveFailureDiagnostic(window, config, "balance-refresh-unconfirmed", "刷新后余额未连续两帧一致");
        return false;
    }

    private static bool TryFindProfileEntryPoint(Bitmap bitmap, out Point point)
    {
        int left = 0;
        int right = Math.Min(bitmap.Width, 220);
        int top = Math.Max(0, bitmap.Height - 70);
        var avatar = FindBuddyGreenComponents(bitmap, left, right, top, bitmap.Height)
            .Where(component => component.Left <= 60 && component.Right <= 90 &&
                                component.Width is >= 18 and <= 70 && component.Height is >= 18 and <= 70 &&
                                component.Width * 10 >= component.Height * 6 &&
                                component.Height * 10 >= component.Width * 6)
            .OrderByDescending(component => component.PixelCount)
            .FirstOrDefault();
        if (avatar.PixelCount == 0)
        {
            point = default;
            return false;
        }

        point = new Point(avatar.Left + avatar.Width / 2, avatar.Top + avatar.Height / 2);
        Log($"动态定位个人中心入口：({point.X},{point.Y})。");
        return true;
    }

    private readonly record struct GreenComponent(int Left, int Top, int Right, int Bottom, int PixelCount)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
    }

    private static IEnumerable<GreenComponent> FindBuddyGreenComponents(
        Bitmap bitmap, int left, int right, int top, int bottom)
    {
        int boundedLeft = Math.Clamp(left, 0, bitmap.Width);
        int boundedRight = Math.Clamp(right, boundedLeft, bitmap.Width);
        int boundedTop = Math.Clamp(top, 0, bitmap.Height);
        int boundedBottom = Math.Clamp(bottom, boundedTop, bitmap.Height);
        var seen = new bool[Math.Max(1, boundedRight - boundedLeft), Math.Max(1, boundedBottom - boundedTop)];
        for (int y = boundedTop; y < boundedBottom; y++)
        for (int x = boundedLeft; x < boundedRight; x++)
        {
            if (seen[x - boundedLeft, y - boundedTop] || !IsBuddyGreen(bitmap.GetPixel(x, y))) continue;
            var pending = new Queue<Point>();
            pending.Enqueue(new Point(x, y));
            seen[x - boundedLeft, y - boundedTop] = true;
            int count = 0, minX = x, maxX = x, minY = y, maxY = y;
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                count++;
                minX = Math.Min(minX, current.X);
                maxX = Math.Max(maxX, current.X);
                minY = Math.Min(minY, current.Y);
                maxY = Math.Max(maxY, current.Y);
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0) continue;
                    int nextX = current.X + offsetX;
                    int nextY = current.Y + offsetY;
                    if (nextX < boundedLeft || nextX >= boundedRight || nextY < boundedTop || nextY >= boundedBottom ||
                        seen[nextX - boundedLeft, nextY - boundedTop] || !IsBuddyGreen(bitmap.GetPixel(nextX, nextY))) continue;
                    seen[nextX - boundedLeft, nextY - boundedTop] = true;
                    pending.Enqueue(new Point(nextX, nextY));
                }
            }
            yield return new GreenComponent(minX, minY, maxX, maxY, count);
        }
    }

    private static bool TryDismissBottomLeftUpdateBanner(
        IntPtr window, Bitmap bitmap, OcrSnapshot ocr, out Point dismissedPoint)
    {
        if (!TryFindBottomLeftUpdateBannerClosePoint(bitmap, ocr, out dismissedPoint)) return false;
        ClickWindowPoint(window, dismissedPoint.X, dismissedPoint.Y);
        return true;
    }

    // WorkBuddy's optional update banner can cover the avatar at the very point used
    // to reopen the personal center. Identify it by its text and its nearby gray ×,
    // rather than treating any green pixels in that strip as an avatar.
    private static bool TryFindBottomLeftUpdateBannerClosePoint(Bitmap bitmap, OcrSnapshot ocr, out Point point)
    {
        var updateLabel = ocr.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line), Normalized: NormalizeOcrText(line.Text)))
            .FirstOrDefault(item => item.Bounds.Left < Math.Min(bitmap.Width, 160) &&
                                    item.Bounds.Top >= Math.Max(0, bitmap.Height - 90) &&
                                    item.Normalized.Contains("新版本", StringComparison.Ordinal));
        if (updateLabel.Line is null)
        {
            point = default;
            return false;
        }

        int left = Math.Clamp(updateLabel.Bounds.Right + 20, 0, bitmap.Width);
        int right = Math.Clamp(updateLabel.Bounds.Right + 400, left, bitmap.Width);
        int top = Math.Clamp(updateLabel.Bounds.Top - 32, 0, bitmap.Height);
        int bottom = Math.Clamp(updateLabel.Bounds.Top, top, bitmap.Height);
        var seen = new bool[Math.Max(1, right - left), Math.Max(1, bottom - top)];
        var glyphs = new List<GreenComponent>();
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            if (seen[x - left, y - top] || !IsCloseGlyphPixel(bitmap.GetPixel(x, y))) continue;
            var pending = new Queue<Point>();
            pending.Enqueue(new Point(x, y));
            seen[x - left, y - top] = true;
            int count = 0, minX = x, maxX = x, minY = y, maxY = y;
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                count++;
                minX = Math.Min(minX, current.X);
                maxX = Math.Max(maxX, current.X);
                minY = Math.Min(minY, current.Y);
                maxY = Math.Max(maxY, current.Y);
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0) continue;
                    int nextX = current.X + offsetX;
                    int nextY = current.Y + offsetY;
                    if (nextX < left || nextX >= right || nextY < top || nextY >= bottom ||
                        seen[nextX - left, nextY - top] || !IsCloseGlyphPixel(bitmap.GetPixel(nextX, nextY))) continue;
                    seen[nextX - left, nextY - top] = true;
                    pending.Enqueue(new Point(nextX, nextY));
                }
            }
            glyphs.Add(new GreenComponent(minX, minY, maxX, maxY, count));
        }

        var closeGlyph = glyphs
            .Where(glyph => glyph.Width is >= 5 and <= 14 && glyph.Height is >= 5 and <= 14 && glyph.PixelCount >= 8)
            .OrderByDescending(glyph => glyph.PixelCount)
            .FirstOrDefault();
        if (closeGlyph.PixelCount == 0)
        {
            point = default;
            return false;
        }

        point = new Point(closeGlyph.Left + closeGlyph.Width / 2, closeGlyph.Top + closeGlyph.Height / 2);
        return true;
    }

    private static bool IsCloseGlyphPixel(Color color) =>
        Math.Max(color.R, Math.Max(color.G, color.B)) <= 240 &&
        Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B)) <= 5;

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

    private readonly record struct UiFrameSignature(byte[] Cells);

    private static UiFrameSignature CreateUiFrameSignature(Bitmap bitmap)
    {
        var cells = new byte[UiFrameSignatureColumns * UiFrameSignatureRows];
        int index = 0;
        for (int cellY = 0; cellY < UiFrameSignatureRows; cellY++)
        for (int cellX = 0; cellX < UiFrameSignatureColumns; cellX++)
        {
            int left = cellX * bitmap.Width / UiFrameSignatureColumns;
            int right = Math.Max(left + 1, (cellX + 1) * bitmap.Width / UiFrameSignatureColumns);
            int top = cellY * bitmap.Height / UiFrameSignatureRows;
            int bottom = Math.Max(top + 1, (cellY + 1) * bitmap.Height / UiFrameSignatureRows);
            long lumaTotal = 0;
            int pixelCount = 0;
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                var color = bitmap.GetPixel(x, y);
                lumaTotal += color.R * 299L + color.G * 587L + color.B * 114L;
                pixelCount++;
            }
            cells[index++] = (byte)Math.Clamp((int)(lumaTotal / Math.Max(1, pixelCount) / 1_000L / UiFrameLumaQuantization), 0, 7);
        }
        return new UiFrameSignature(cells);
    }

    private static bool HasMeaningfulUiChange(UiFrameSignature before, UiFrameSignature after)
    {
        if (before.Cells is null || after.Cells is null || before.Cells.Length != after.Cells.Length) return false;
        int changedCells = 0;
        for (int index = 0; index < before.Cells.Length; index++)
        {
            if (Math.Abs(before.Cells[index] - after.Cells[index]) >= UiTransitionMinimumCellDelta) changedCells++;
        }
        return changedCells >= UiTransitionMinimumChangedCells;
    }

    private static ClaimVerification WaitForClaimResult(
        IntPtr window, Config config, BalanceReading beforeBalance, UiFrameSignature beforeClickSignature,
        bool successTextWasPresentBeforeClick, TimeSpan timeout,
        out BalanceReading? afterBalance)
    {
        afterBalance = null;
        var statusUntil = DateTime.UtcNow.AddSeconds(Math.Min(6, timeout.TotalSeconds));
        bool observedUiTransition = false;
        bool stableClaimedText = false;
        int consecutiveClaimedFrames = 0;
        do
        {
            using var image = CaptureWindow(window);
            if (image is not null)
            {
                if (!observedUiTransition && HasMeaningfulUiChange(beforeClickSignature, CreateUiFrameSignature(image)))
                {
                    observedUiTransition = true;
                    Log("已观察到立即领取点击后的界面变化。");
                }
                bool hasClaimedText = HasClaimSuccessText(ReadClaimOcr(image));
                bool newlyVisibleClaimedText = hasClaimedText &&
                                               (observedUiTransition || !successTextWasPresentBeforeClick);
                consecutiveClaimedFrames = newlyVisibleClaimedText ? consecutiveClaimedFrames + 1 : 0;
                if (consecutiveClaimedFrames >= 2)
                {
                    stableClaimedText = true;
                    Log("点击立即领取后连续两帧识别到“今日已领/已领取”。");
                    break;
                }
            }
            Thread.Sleep(650);
        }
        while (DateTime.UtcNow < statusUntil);

        Log("点击立即领取后重新打开个人中心，点击刷新并连续读取明确数字余额。");
        if (!TryOpenPersonalCenterAndReadEvidence(window, config, out var afterEvidence) ||
            afterEvidence.Balance is null)
            return ClaimVerification.NotConfirmed;

        afterBalance = afterEvidence.Balance;
        if (IsBalanceIncreased(beforeBalance, afterEvidence.Balance, config))
        {
            Log($"OCR 积分余额变化：{beforeBalance.RawText} -> {afterEvidence.Balance.RawText}。");
            return ClaimVerification.BalanceChanged;
        }

        if (!stableClaimedText && afterEvidence.HasSuccessText)
            stableClaimedText = TryConfirmStableClaimSuccessText(window);
        return stableClaimedText && AreSameBalance(beforeBalance, afterEvidence.Balance, config)
            ? ClaimVerification.ClaimedText
            : ClaimVerification.NotConfirmed;
    }

    private static bool TryGetStableBalanceBeforeAction(
        IntPtr window, Config config, BalanceReading observed, out BalanceReading stable)
    {
        stable = observed;
        return HasConfirmedNumericBalance(observed);
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

    private static bool HasConfirmedNumericBalance(BalanceReading? balance) =>
        balance is { IsVisualFingerprint: false } &&
        !string.IsNullOrWhiteSpace(balance.Fingerprint) &&
        decimal.TryParse(balance.Fingerprint, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out _);

    private enum ClaimActionKind { CheckIn, Immediate, BuddyFuelStation }
    private enum ClaimVerification { NotConfirmed, BalanceChanged, ClaimedText }
    private enum ClaimOutcomeKind { Claimed, AlreadyClaimed, Failed }
    private enum CheckInFollowup { NotFound, Immediate, AlreadyClaimed }
    private enum ClaimRouteKind { Immediate, AlreadyClaimed, CheckIn, None }

    private static ClaimRouteKind SelectClaimRoute(
        bool hasImmediate, bool hasStableClaimedText, IReadOnlyList<ClaimAction> checkInActions)
    {
        if (hasImmediate) return ClaimRouteKind.Immediate;
        if (hasStableClaimedText) return ClaimRouteKind.AlreadyClaimed;
        return checkInActions.Count > 0 ? ClaimRouteKind.CheckIn : ClaimRouteKind.None;
    }

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

    private static int VerifyClaimOcr(string? imagePath, Config config, string? expectedBalance = null, string? expectedCheckIn = null)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("请提供可读取的截图路径。", imagePath);
        using var bitmap = new Bitmap(imagePath);
        var ocr = ReadOcr(bitmap);
        var evidence = ReadMenuEvidence(bitmap, ocr, config);
        var immediate = FindImmediateClaimAction(bitmap, ocr, config);
        var checkInActions = FindCheckInActions(bitmap, ocr, config);
        if (!HasConfirmedNumericBalance(evidence.Balance))
            throw new InvalidOperationException("未读取到明确数字的积分余额，OCR 领取验证不通过。\n");
        var numericBalance = FormatNotificationBalance(evidence.Balance);
        if (!string.IsNullOrWhiteSpace(expectedBalance) &&
            !StringComparer.Ordinal.Equals(numericBalance, expectedBalance))
            throw new InvalidOperationException($"积分余额 OCR 校验失败：期望 {expectedBalance}，实际 {numericBalance ?? "未读取到"}。\n");
        if (!string.IsNullOrWhiteSpace(expectedCheckIn) && !checkInActions.Any(action =>
                NormalizeOcrText(action.Text).Contains(NormalizeOcrText(expectedCheckIn), StringComparison.Ordinal)))
            throw new InvalidOperationException($"签到入口 OCR 校验失败：未识别到 {expectedCheckIn}。\n");
        if (!HasOfflineClaimRouteOrState(evidence, immediate, checkInActions))
            throw new InvalidOperationException("OCR 未识别到成功状态或可领取文字。\n");
        Console.WriteLine($"余额={numericBalance ?? "无"}; 成功文字={evidence.HasSuccessText}; 立即领取={immediate?.Text ?? "无"}; 签到入口={string.Join(", ", checkInActions.Select(action => action.Text))}");
        Log($"OCR 领取截图验证通过：余额={evidence.Balance?.RawText ?? "无"}，成功文字={evidence.HasSuccessText}，立即领取={immediate?.Text ?? "无"}，签到入口数量={checkInActions.Count}。\n");
        return 0;
    }

    private static bool HasOfflineClaimRouteOrState(
        MenuEvidence evidence, ClaimAction? immediate, IReadOnlyList<ClaimAction> checkInActions) =>
        evidence.HasSuccessText || immediate is not null || checkInActions.Count > 0;

    // Offline regression seam for the exact post-claim failure frame. It does not
    // touch WorkBuddy: the profile avatar is deliberately hidden, so the verifier
    // requires a safe update-banner close target instead of a guessed green click.
    private static int VerifyProfileRecovery(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("请提供可读取的领取后截图路径。", imagePath);
        using var bitmap = new Bitmap(imagePath);
        var ocr = ReadOcr(bitmap);
        if (TryFindProfileEntryPoint(bitmap, out var falseProfilePoint))
            throw new InvalidOperationException($"领取后遮挡截图不应把绿色横幅识别成头像：({falseProfilePoint.X},{falseProfilePoint.Y})。\n");
        if (!TryFindBottomLeftUpdateBannerClosePoint(bitmap, ocr, out var closePoint))
            throw new InvalidOperationException("领取后遮挡截图未找到可安全关闭的新版本提示。\n");
        Log($"领取后个人中心恢复截图验证通过：未误识别头像，更新提示关闭点=({closePoint.X},{closePoint.Y})。\n");
        return 0;
    }

    private static int VerifyProfileEntry(string? imagePath, string? expectedXText, string? expectedYText)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("请提供可读取的个人中心入口截图路径。", imagePath);
        if (!int.TryParse(expectedXText, out var expectedX) || !int.TryParse(expectedYText, out var expectedY))
            throw new ArgumentException("请提供头像入口的预期 X 与 Y 坐标。");
        using var bitmap = new Bitmap(imagePath);
        if (!TryFindProfileEntryPoint(bitmap, out var profilePoint))
            throw new InvalidOperationException("截图中未识别到左下头像入口。\n");
        const int tolerance = 12;
        if (Math.Abs(profilePoint.X - expectedX) > tolerance || Math.Abs(profilePoint.Y - expectedY) > tolerance)
            throw new InvalidOperationException($"头像入口坐标错误：预期约 ({expectedX},{expectedY})，实际 ({profilePoint.X},{profilePoint.Y})。\n");
        Log($"个人中心入口截图验证通过：头像入口=({profilePoint.X},{profilePoint.Y})。\n");
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
            int processId = process.Id;
            var waitResult = WaitForOcrProcess(process, OcrProcessTimeout);
            if (waitResult != OcrProcessWaitResult.Completed)
            {
                if (waitResult == OcrProcessWaitResult.TerminatedAfterTimeout)
                {
                    Log($"Windows OCR timed out after {OcrProcessTimeout.TotalSeconds:0} seconds; stopped PID={processId}.");
                    throw new TimeoutException($"Windows OCR 超过 {OcrProcessTimeout.TotalSeconds:0} 秒并已安全终止。");
                }
                Log($"Windows OCR timed out after {OcrProcessTimeout.TotalSeconds:0} seconds; PID={processId} did not exit after termination request.");
                throw new InvalidOperationException($"Windows OCR 超过 {OcrProcessTimeout.TotalSeconds:0} 秒且无法确认终止，流程已安全停止。");
            }
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

    private static OcrSnapshot ReadClaimOcr(Bitmap bitmap)
    {
        var snapshot = ReadOcr(bitmap);
        ThrowIfLoginRequired(snapshot);
        return snapshot;
    }

    private enum OcrProcessWaitResult { Completed, TerminatedAfterTimeout, StillRunningAfterTimeout }

    private static OcrProcessWaitResult WaitForOcrProcess(Process process, TimeSpan timeout)
    {
        if (process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue))) return OcrProcessWaitResult.Completed;
        try { process.Kill(entireProcessTree: true); } catch { }
        process.WaitForExit(2_000);
        return process.HasExited ? OcrProcessWaitResult.TerminatedAfterTimeout : OcrProcessWaitResult.StillRunningAfterTimeout;
    }

    private static MenuEvidence ReadMenuEvidence(Bitmap bitmap, Config config)
    {
        return ReadMenuEvidence(bitmap, ReadClaimOcr(bitmap), config);
    }

    private static MenuEvidence ReadMenuEvidence(Bitmap bitmap, OcrSnapshot ocr, Config config)
    {
        var balance = TryReadBalance(bitmap, ocr, config);
        var actions = FindCheckInActions(ocr, config);
        bool hasSuccessText = HasClaimSuccessText(ocr);
        bool isPersonalCenter = IsConfirmedPersonalCenterMenu(ocr, balance, config);
        return new MenuEvidence(balance, actions, hasSuccessText, isPersonalCenter);
    }

    private static readonly string[] PersonalCenterMenuAnchors =
        ["Buddy加油站", "设置", "外观", "退出登录"];

    private static bool LooksLikePersonalCenterShell(OcrSnapshot snapshot)
    {
        var lines = snapshot.Lines
            .Where(line => line.Words.Count > 0)
            .Select(line => NormalizeOcrText(line.Text))
            .ToArray();
        return PersonalCenterMenuAnchors.Count(anchor =>
            lines.Any(line => line.Contains(anchor, StringComparison.Ordinal))) >= 2;
    }

    private static bool IsConfirmedPersonalCenterMenu(
        OcrSnapshot snapshot, BalanceReading? balance, Config config)
    {
        if (!HasConfirmedNumericBalance(balance)) return false;
        var label = FindBalanceLabel(snapshot);
        if (label is null || !HasNumericBalanceOnSameRow(snapshot, label, config)) return false;

        return snapshot.Lines
            .Where(line => line.Words.Count > 0)
            .Select(line => (Bounds: GetOcrBounds(line), Text: NormalizeOcrText(line.Text)))
            .Any(item => item.Bounds.Left >= Math.Max(0, label.Bounds.Left - 48) &&
                         item.Bounds.Left <= label.Bounds.Right + 96 &&
                         PersonalCenterMenuAnchors.Any(anchor =>
                             item.Text.Contains(anchor, StringComparison.Ordinal)));
    }

    private static bool HasNumericBalanceOnSameRow(
        OcrSnapshot snapshot, BalanceLabel label, Config config)
    {
        var afterLabel = TryGetTextAfterNormalizedAnchor(label.Line.Text, "积分余额");
        if (!string.IsNullOrWhiteSpace(afterLabel) && NormalizeBalanceToken(afterLabel) is not null)
            return true;

        return snapshot.Lines
            .Where(line => !ReferenceEquals(line, label.Line) && line.Words.Count > 0 &&
                           line.Text.Any(char.IsDigit))
            .Select(line => GetOcrBounds(line))
            .Any(bounds => bounds.Left >= label.Bounds.Right - 12 &&
                           bounds.Left <= label.Bounds.Right + config.BalanceValueDirectRightPixels &&
                           Math.Abs(bounds.CenterY - label.Bounds.CenterY) <= config.BalanceValueSameRowTolerance);
    }

    private static bool TryFindBalanceRefreshPoint(
        OcrSnapshot snapshot, Config config, out Point point)
    {
        var label = FindBalanceLabel(snapshot);
        if (label is null)
        {
            point = default;
            return false;
        }

        foreach (var line in snapshot.Lines)
        {
            if (line.Words.Count < 2) continue;
            var lineBounds = GetOcrBounds(line);
            if (lineBounds.Left < label.Bounds.Right - 12 ||
                lineBounds.Left > label.Bounds.Right + config.BalanceValueDirectRightPixels ||
                Math.Abs(lineBounds.CenterY - label.Bounds.CenterY) > config.BalanceValueSameRowTolerance)
                continue;

            var balanceWord = line.Words
                .Select(word => (Word: word, Value: NormalizeBalanceToken(word.Text)))
                .Where(item => item.Word.X > label.Bounds.Right &&
                               item.Value is not null && item.Value.Count(char.IsDigit) >= 2)
                .OrderByDescending(item => item.Value!.Count(char.IsDigit))
                .ThenBy(item => item.Word.X)
                .Select(item => item.Word)
                .FirstOrDefault();
            if (balanceWord is null) continue;

            var refreshWord = line.Words
                .Where(word => word.X >= label.Bounds.Right && word.X + word.Width < balanceWord.X &&
                               word.Width is >= 6 and <= 24 && word.Height is >= 6 and <= 24)
                .Where(word => word.Text.Trim() is "0" or "O" or "o" or "Q" or "q" or "C" or "c")
                .OrderByDescending(word => word.X)
                .FirstOrDefault();
            if (refreshWord is null) continue;

            point = new Point(refreshWord.X + refreshWord.Width / 2,
                refreshWord.Y + refreshWord.Height / 2);
            return true;
        }

        point = default;
        return false;
    }

    private static void SavePersonalCenterFailureEvidence(
        IntPtr window, Config config, Bitmap? image, OcrSnapshot? ocr,
        bool profileEntryLocated, bool personalCenterAlreadyOpen)
    {
        if (image is not null && ocr is not null)
        {
            Directory.CreateDirectory(DataDirectory);
            var screenshotPath = Path.Combine(DataDirectory, "workbuddy-personal-center-ocr-failure.png");
            var ocrPath = Path.Combine(DataDirectory, "workbuddy-personal-center-ocr-failure.txt");
            image.Save(screenshotPath, ImageFormat.Png);
            var text = string.Join(Environment.NewLine, ocr.Lines.Select(line =>
                $"{string.Join(' ', line.Words.Select(word => $"{word.X},{word.Y},{word.Width},{word.Height}"))}: {line.Text}"));
            File.WriteAllText(ocrPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Log($"个人中心 OCR 失败证据已保存: 截图={screenshotPath}，原文={ocrPath}");
        }
        SaveFailureDiagnostic(window, config, "personal-center-unreadable", "未能确认积分余额",
            image, ocr, $"profileEntryLocated={profileEntryLocated}; personalCenterAlreadyOpen={personalCenterAlreadyOpen}");
    }

    private static void SaveFailureDiagnostic(
        IntPtr window, Config config, string phase, string reason,
        Bitmap? image = null, OcrSnapshot? ocr = null, string? additionalContext = null)
    {
        Bitmap? capturedHere = null;
        try
        {
            var capture = image ?? (capturedHere = CaptureWindow(window));
            if (capture is null) return;
            var diagnosticsPath = Path.Combine(DataDirectory, "diagnostics");
            Directory.CreateDirectory(diagnosticsPath);
            var baseName = $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{phase}";
            var screenshotPath = Path.Combine(diagnosticsPath, baseName + ".png");
            var metadataPath = Path.Combine(diagnosticsPath, baseName + ".json");
            capture.Save(screenshotPath, ImageFormat.Png);

            string? ocrFailure = null;
            var snapshot = ocr;
            if (snapshot is null)
            {
                try { snapshot = ReadOcr(capture); }
                catch (Exception ex) { ocrFailure = ex.Message; }
            }
            Native.GetWindowRect(window, out var rect);
            uint dpi = 0;
            try { dpi = Native.GetDpiForWindow(window); } catch { }
            string version;
            try { version = FileVersionInfo.GetVersionInfo(config.WorkBuddyPath).FileVersion ?? "unknown"; }
            catch { version = "unknown"; }
            var metadata = new
            {
                capturedAt = DateTimeOffset.Now,
                phase,
                reason,
                workBuddyVersion = version,
                window = new { width = rect.Right - rect.Left, height = rect.Bottom - rect.Top, dpi },
                additionalContext,
                ocrFailure,
                ocrLines = snapshot?.Lines.Select(line => new
                {
                    text = line.Text,
                    words = line.Words.Select(word => new { word.Text, word.X, word.Y, word.Width, word.Height })
                })
            };
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            TrimFailureDiagnostics(diagnosticsPath, DateTime.Now.AddDays(-LogRetentionDays));
            Log($"Failure diagnostic saved: screenshot={screenshotPath}, metadata={metadataPath}.");
        }
        catch (Exception ex)
        {
            Log("Failure diagnostic capture failed: " + ex.Message);
        }
        finally { capturedHere?.Dispose(); }
    }

    private sealed record BalanceLabel(OcrLine Line, OcrBounds Bounds);

    private static BalanceReading? TryReadBalance(Bitmap bitmap, OcrSnapshot snapshot, Config config)
    {
        var direct = TryReadBalance(snapshot, config);
        var label = FindBalanceLabel(snapshot);
        if (label is null) return null;

        var readings = new List<BalanceReading>();
        if (direct is not null) readings.Add(direct);
        var broadValue = TryReadBalanceFromCrop(bitmap, label, config, leftOffset: -4,
            width: config.BalanceValueCropWidthPixels, scale: config.BalanceValueCropScale,
            above: config.BalanceValueCropAbovePixels, below: config.BalanceValueCropBelowPixels,
            treatment: BalanceCropTreatment.Raw);
        if (broadValue is not null) readings.Add(broadValue);

        // The value can be a single small digit at the far right of the row. A focused,
        // higher-resolution retry prevents a visible 0 balance from being treated as missing.
        var focusedRawValue = TryReadBalanceFromCrop(bitmap, label, config,
            config.BalanceValueFocusedCropLeftOffsetPixels,
            config.BalanceValueFocusedCropWidthPixels,
            config.BalanceValueFocusedCropScale,
            config.BalanceValueFocusedCropAbovePixels,
            config.BalanceValueFocusedCropBelowPixels,
            treatment: BalanceCropTreatment.Raw);
        if (focusedRawValue is not null) readings.Add(focusedRawValue);

        var focusedValue = TryReadBalanceFromCrop(bitmap, label, config,
            config.BalanceValueFocusedCropLeftOffsetPixels,
            config.BalanceValueFocusedCropWidthPixels,
            config.BalanceValueFocusedCropScale,
            config.BalanceValueFocusedCropAbovePixels,
            config.BalanceValueFocusedCropBelowPixels,
            treatment: BalanceCropTreatment.LightContrast);
        if (focusedValue is not null) readings.Add(focusedValue);

        var darkThemeFocusedValue = TryReadBalanceFromCrop(bitmap, label, config,
            config.BalanceValueFocusedCropLeftOffsetPixels,
            config.BalanceValueFocusedCropWidthPixels,
            config.BalanceValueFocusedCropScale,
            config.BalanceValueFocusedCropAbovePixels,
            config.BalanceValueFocusedCropBelowPixels,
            treatment: BalanceCropTreatment.DarkContrast);
        if (darkThemeFocusedValue is not null) readings.Add(darkThemeFocusedValue);

        var confirmed = SelectConfirmedNumericBalance(readings);
        if (confirmed is not null)
            return confirmed with { VisualSignature = CreateVisualBalanceFingerprint(bitmap, label.Bounds, config) };

        Log("积分余额多路 OCR 未形成一致数字；本轮不将视觉指纹视为可用余额。");
        return null;
    }

    private static BalanceReading? SelectConfirmedNumericBalance(IEnumerable<BalanceReading> readings) =>
        readings
            .Where(reading => !reading.IsVisualFingerprint && !string.IsNullOrWhiteSpace(reading.Fingerprint))
            .GroupBy(reading => reading.Fingerprint, StringComparer.Ordinal)
            .Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.First().RawText.Length)
            .Select(group => group.First())
            .FirstOrDefault();

    // A single frame must already contain a multi-route OCR confirmation. This
    // second layer requires the same explicit numeric value in two consecutive frames.
    private static BalanceReading? SelectConfirmedBalanceAcrossFrames(IEnumerable<BalanceReading?> readings) =>
        readings
            .TakeLast(2)
            .ToArray() is [var first, var second] &&
            HasConfirmedNumericBalance(first) &&
            HasConfirmedNumericBalance(second) &&
            StringComparer.Ordinal.Equals(first!.Fingerprint, second!.Fingerprint)
                ? second
                : null;

    private static bool TryConfirmBalanceAcrossFrames(
        List<BalanceReading?> frames, BalanceReading? candidate, out BalanceReading? confirmedBalance)
    {
        frames.Add(candidate);
        if (frames.Count > 2) frames.RemoveAt(0);
        confirmedBalance = SelectConfirmedBalanceAcrossFrames(frames);
        return confirmedBalance is not null;
    }

    private static BalanceReading? TryReadBalance(OcrSnapshot snapshot, Config config)
    {
        var label = FindBalanceLabel(snapshot);
        if (label is null) return null;

        // The left-side icon is sometimes OCR'd as 0, so only accept digits that
        // occur after the “积分余额” text on the same line.
        const string labelText = "积分余额";
        var textAfterLabel = TryGetTextAfterNormalizedAnchor(label.Line.Text, labelText);
        var sameLineValue = textAfterLabel is null ? null : NormalizeBalanceToken(textAfterLabel);
        if (!string.IsNullOrWhiteSpace(sameLineValue))
            return CreateNumericBalanceReading(sameLineValue, label.Bounds);

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

        var value = TryParseBalanceWords(candidate.Line.Words);
        if (value is null) return null;
        return CreateNumericBalanceReading(value, label.Bounds);
    }

    private static string? TryGetTextAfterNormalizedAnchor(string text, string anchor)
    {
        var normalized = new StringBuilder();
        for (int index = 0; index < text.Length; index++)
        {
            normalized.Append(NormalizeOcrText(text[index].ToString()));
            if (normalized.ToString().EndsWith(anchor, StringComparison.Ordinal))
                return text[(index + 1)..];
        }
        return null;
    }

    private static BalanceLabel? FindBalanceLabel(OcrSnapshot snapshot)
    {
        const string expected = "积分余额";
        foreach (var line in snapshot.Lines)
        {
            if (TryFindExactOcrPhraseBounds(line, expected, out var bounds))
                return new BalanceLabel(line, bounds);
        }
        return null;
    }

    private static bool TryFindExactOcrPhraseBounds(OcrLine line, string expected, out OcrBounds bounds)
    {
        for (int start = 0; start < line.Words.Count; start++)
        {
            var normalized = new StringBuilder();
            var matchedWords = new List<OcrWord>();
            for (int index = start; index < line.Words.Count; index++)
            {
                var wordText = NormalizeExactActionText(line.Words[index].Text);
                if (wordText.Length == 0) continue;
                normalized.Append(wordText);
                matchedWords.Add(line.Words[index]);
                var candidate = normalized.ToString();
                if (StringComparer.Ordinal.Equals(candidate, expected))
                {
                    if (HasAdjacentCjkOcrWord(line.Words, start - 1, step: -1) ||
                        HasAdjacentCjkOcrWord(line.Words, index + 1, step: 1))
                        break;
                    bounds = new OcrBounds(
                        matchedWords.Min(word => word.X),
                        matchedWords.Min(word => word.Y),
                        matchedWords.Max(word => word.X + word.Width),
                        matchedWords.Max(word => word.Y + word.Height));
                    return true;
                }
                if (!expected.StartsWith(candidate, StringComparison.Ordinal)) break;
            }
        }

        bounds = default;
        return false;
    }

    private static bool HasAdjacentCjkOcrWord(IReadOnlyList<OcrWord> words, int index, int step)
    {
        for (; index >= 0 && index < words.Count; index += step)
        {
            var text = NormalizeExactActionText(words[index].Text);
            if (text.Length == 0) continue;
            return text.Any(character => character is >= '\u3400' and <= '\u9fff');
        }
        return false;
    }

    private enum BalanceCropTreatment { Raw, LightContrast, DarkContrast }

    private static BalanceReading? TryReadBalanceFromCrop(
        Bitmap bitmap, BalanceLabel label, Config config, int leftOffset, int width, int scale, int above, int below,
        BalanceCropTreatment treatment)
    {
        using var enlargedValueArea = CreateBalanceValueCrop(bitmap, label.Bounds, leftOffset, width, scale, above, below);
        if (treatment == BalanceCropTreatment.LightContrast) IncreaseOcrContrast(enlargedValueArea);
        else if (treatment == BalanceCropTreatment.DarkContrast) NormalizeDarkThemeOcr(enlargedValueArea);
        var enlargedOcr = ReadOcr(enlargedValueArea, "en-US");
        var value = enlargedOcr.Lines
            .Where(line => line.Words.Count > 0 && line.Text.Count(char.IsDigit) >= 1)
            .OrderBy(line => GetOcrBounds(line).Top)
            .FirstOrDefault();
        if (value is null) return null;
        var parsed = TryParseBalanceWords(value.Words);
        if (parsed is null) return null;
        return CreateNumericBalanceReading(parsed, label.Bounds);
    }

    private static BalanceReading? CreateNumericBalanceReading(string rawText, OcrBounds labelBounds)
    {
        var normalized = NormalizeBalanceToken(rawText);
        if (normalized is null ||
            !decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var numericValue))
            return null;
        return new BalanceReading(normalized, numericValue.ToString(CultureInfo.InvariantCulture), labelBounds);
    }

    private static string? TryParseBalanceWords(IEnumerable<OcrWord> words)
    {
        return words
            .Select(word => (Word: word, Value: NormalizeBalanceToken(word.Text)))
            .Where(item => item.Value is not null)
            .OrderByDescending(item => item.Value!.Count(char.IsDigit))
            .ThenByDescending(item => item.Word.Width)
            .Select(item => item.Value)
            .FirstOrDefault();
    }

    private static string? NormalizeBalanceToken(string text)
    {
        var token = new StringBuilder();
        bool hasDigit = false;
        foreach (char character in text)
        {
            if (char.IsDigit(character))
            {
                token.Append(character);
                hasDigit = true;
            }
            else if ((character == '.' || character == ',' || character == '．' || character == '，') && hasDigit)
            {
                token.Append(character is ',' or '，' ? ',' : '.');
            }
            // Windows OCR read the final 9 in the verified balance screenshot as g.
            else if ((character == 'g' || character == 'G') && hasDigit)
            {
                token.Append('9');
            }
        }
        if (!hasDigit) return null;

        var raw = token.ToString().TrimEnd('.', ',');
        if (raw.Length == 0) return null;
        int lastDot = raw.LastIndexOf('.');
        int lastComma = raw.LastIndexOf(',');
        int decimalSeparatorIndex = -1;
        int inferredDecimalDigits = 0;
        if (lastDot >= 0 && lastComma >= 0)
        {
            // When both separators are present, the final one is the decimal mark and
            // all earlier separators are thousands grouping (1,722.8 / 1.722,8).
            decimalSeparatorIndex = Math.Max(lastDot, lastComma);
        }
        else if (lastDot >= 0)
        {
            int trailingDigits = raw[(lastDot + 1)..].Count(char.IsDigit);
            // WorkBuddy displays no more than two decimal digits. On small text OCR
            // can read "2,155.4" as "2.1554", collapsing the thousands and decimal
            // marks into one. Three trailing digits are grouping; four/five mean a
            // grouped value with one/two decimal digits.
            if (trailingDigits is 1 or 2) decimalSeparatorIndex = lastDot;
            else if (trailingDigits is 4 or 5) inferredDecimalDigits = trailingDigits - 3;
        }
        else if (lastComma >= 0)
        {
            int trailingDigits = raw[(lastComma + 1)..].Count(char.IsDigit);
            // A lone comma followed by three digits is a thousands separator. One or
            // two trailing digits are accepted as a locale-style decimal mark.
            if (trailingDigits is 1 or 2) decimalSeparatorIndex = lastComma;
            else if (trailingDigits is 4 or 5) inferredDecimalDigits = trailingDigits - 3;
        }

        if (inferredDecimalDigits > 0)
        {
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            return digits.Insert(digits.Length - inferredDecimalDigits, ".");
        }

        var value = new StringBuilder();
        for (int index = 0; index < raw.Length; index++)
        {
            if (char.IsDigit(raw[index])) value.Append(raw[index]);
            else if (index == decimalSeparatorIndex) value.Append('.');
        }
        return value.ToString();
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

    private static void NormalizeDarkThemeOcr(Bitmap bitmap)
    {
        IncreaseOcrContrast(bitmap);
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
            bitmap.SetPixel(x, y, bitmap.GetPixel(x, y).ToArgb() == Color.Black.ToArgb() ? Color.White : Color.Black);
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

    private const int FullWindowActionOcrScale = 3;

    private static ClaimAction? FindImmediateClaimAction(Bitmap bitmap, OcrSnapshot directOcr, Config config)
    {
        var direct = FindImmediateClaimAction(directOcr, config);
        if (direct is not null) return direct;

        foreach (var treatment in new[]
                 {
                     FullWindowOcrTreatment.Raw,
                     FullWindowOcrTreatment.Grayscale,
                     FullWindowOcrTreatment.Inverted
                 })
        {
            using var enlarged = CreateScaledCrop(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                FullWindowActionOcrScale);
            if (treatment == FullWindowOcrTreatment.Grayscale) ConvertToGrayscale(enlarged);
            else if (treatment == FullWindowOcrTreatment.Inverted) NormalizeDarkThemeOcr(enlarged);

            var passOcr = ReadOcr(enlarged, "zh-Hans");
            var enlargedAction = FindImmediateClaimAction(passOcr, config);
            if (enlargedAction is null) continue;
            var mappedAction = MapScaledActionToWindow(enlargedAction, bitmap, config, FullWindowActionOcrScale);
            int x = mappedAction.CenterX;
            int y = mappedAction.CenterY;
            Log($"整图 {treatment} OCR 识别立即领取：文本={enlargedAction.Text}，位置=({x},{y})。");
            return mappedAction;
        }

        return null;
    }

    private enum FullWindowOcrTreatment { Raw, Grayscale, Inverted }

    private static void ConvertToGrayscale(Bitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            int luma = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
            bitmap.SetPixel(x, y, Color.FromArgb(luma, luma, luma));
        }
    }

    private static ClaimAction? FindImmediateClaimAction(OcrSnapshot snapshot, Config config)
    {
        return snapshot.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line), Normalized: NormalizeExactActionText(line.Text)))
            .Where(item => item.Line.Words.Count > 0)
            .Select(item => (item.Line, item.Bounds, item.Normalized,
                Keyword: FindExactImmediateKeyword(item.Normalized)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Keyword))
            .Select(item => new ClaimAction(item.Keyword!, item.Line.Text, item.Bounds.CenterX, item.Bounds.CenterY,
                GetCandidateId(item.Bounds, config.ClaimCandidatePositionTolerancePixels), ClaimActionKind.Immediate))
            .FirstOrDefault();
    }

    private static string? FindExactImmediateKeyword(string text)
    {
        const string expected = "立即领取";
        return StringComparer.Ordinal.Equals(text, expected) ? expected : null;
    }

    private static ClaimAction? FindBuddyFuelStationAction(OcrSnapshot snapshot, Config config)
    {
        const string expected = "Buddy加油站";
        return snapshot.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line), Normalized: NormalizeExactActionText(line.Text)))
            .Where(item => item.Line.Words.Count > 0 &&
                           StringComparer.Ordinal.Equals(item.Normalized, expected))
            .Select(item => new ClaimAction(expected, item.Line.Text, item.Bounds.CenterX, item.Bounds.CenterY,
                GetCandidateId(item.Bounds, config.ClaimCandidatePositionTolerancePixels),
                ClaimActionKind.BuddyFuelStation))
            .FirstOrDefault();
    }

    private static IReadOnlyList<ClaimAction> FindCheckInActions(OcrSnapshot snapshot, Config config)
    {
        var keywords = GetExactActionKeywords(config.CheckInKeywords, Config.DefaultCheckInKeywords);
        return snapshot.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line), Normalized: NormalizeExactActionText(line.Text)))
            .Where(item => item.Line.Words.Count > 0 &&
                           !item.Normalized.Contains("已签到", StringComparison.Ordinal) &&
                           !item.Normalized.Contains("签到成功", StringComparison.Ordinal) &&
                           !item.Normalized.Contains("已领取", StringComparison.Ordinal) &&
                           !item.Normalized.Contains("今日已领", StringComparison.Ordinal))
            .Select(item => (item.Line, item.Bounds,
                Keyword: keywords.FirstOrDefault(keyword => item.Normalized.Contains(keyword, StringComparison.Ordinal))))
            .Where(item => !string.IsNullOrWhiteSpace(item.Keyword))
            .Select(item => new ClaimAction(item.Keyword!, item.Line.Text, item.Bounds.CenterX, item.Bounds.CenterY,
                GetCandidateId(item.Bounds, config.ClaimCandidatePositionTolerancePixels), ClaimActionKind.CheckIn))
            .GroupBy(action => action.CandidateId)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<ClaimAction> FindCheckInActions(Bitmap bitmap, OcrSnapshot directOcr, Config config)
    {
        var direct = FindCheckInActions(directOcr, config);
        if (direct.Count > 0) return direct;

        // As with “立即领取”, retry only after enlarging the entire current window.
        // No card, crop, or fixed-position condition is introduced here.
        using var enlarged = CreateScaledCrop(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            FullWindowActionOcrScale);
        var enlargedActions = FindCheckInActions(ReadOcr(enlarged), config);
        if (enlargedActions.Count == 0) return direct;

        Log("整图放大 OCR 识别到签到入口。");
        return enlargedActions
            .Select(action => MapScaledActionToWindow(action, bitmap, config, FullWindowActionOcrScale))
            .ToArray();
    }

    private static ClaimAction MapScaledActionToWindow(
        ClaimAction action, Bitmap originalWindow, Config config, int scale)
    {
        int x = Math.Clamp((int)Math.Round(action.CenterX / (double)scale),
            0, Math.Max(0, originalWindow.Width - 1));
        int y = Math.Clamp((int)Math.Round(action.CenterY / (double)scale),
            0, Math.Max(0, originalWindow.Height - 1));
        var mappedBounds = new OcrBounds(x, y, x, y);
        return action with
        {
            CenterX = x,
            CenterY = y,
            CandidateId = GetCandidateId(mappedBounds, config.ClaimCandidatePositionTolerancePixels)
        };
    }

    private static string[] GetExactActionKeywords(IEnumerable<string>? configured, IEnumerable<string> fallback) =>
        (configured ?? fallback)
            .Select(NormalizeExactActionText)
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .OrderByDescending(keyword => keyword.Length)
            .ToArray();

    private static bool IsSameBalanceAnchor(OcrBounds expected, OcrBounds actual, Config config) =>
        Math.Abs(expected.CenterX - actual.CenterX) <= config.BalanceAnchorDriftPixels &&
        Math.Abs(expected.CenterY - actual.CenterY) <= config.BalanceAnchorDriftPixels;

    private static bool AreSameBalance(BalanceReading expected, BalanceReading actual, Config config)
    {
        return HasConfirmedNumericBalance(expected) && HasConfirmedNumericBalance(actual) &&
               StringComparer.Ordinal.Equals(expected.Fingerprint, actual.Fingerprint);
    }

    private static bool IsBalanceIncreased(BalanceReading before, BalanceReading after, Config config) =>
        TryGetNumericBalance(before, out var beforeValue) &&
        TryGetNumericBalance(after, out var afterValue) &&
        afterValue > beforeValue;

    private static bool TryGetNumericBalance(BalanceReading balance, out decimal value)
    {
        value = 0;
        if (!HasConfirmedNumericBalance(balance)) return false;
        var normalized = NormalizeBalanceToken(balance.RawText);
        return normalized is not null &&
               decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    private static ClaimOutcomeKind? ClassifyClaimOutcome(
        BalanceReading before, BalanceReading after, bool hasAlreadyClaimedText, Config config)
    {
        if (IsBalanceIncreased(before, after, config)) return ClaimOutcomeKind.Claimed;
        if (hasAlreadyClaimedText && AreSameBalance(before, after, config)) return ClaimOutcomeKind.AlreadyClaimed;
        return null;
    }

    private static bool IsConfirmedAlreadyClaimed(
        IReadOnlyList<MenuEvidence> samples, BalanceReading beforeBalance, Config config,
        out BalanceReading? confirmedBalance)
    {
        confirmedBalance = null;
        int stableSamples = 0;
        foreach (var sample in samples)
        {
            if (!sample.IsPersonalCenter || !sample.HasSuccessText || sample.Balance is null ||
                !HasConfirmedNumericBalance(sample.Balance) ||
                !AreSameBalance(beforeBalance, sample.Balance, config))
            {
                stableSamples = 0;
                continue;
            }

            stableSamples++;
            if (stableSamples < 2) continue;
            confirmedBalance = sample.Balance;
            return true;
        }
        return false;
    }

    private static bool TryConfirmAlreadyClaimed(
        IntPtr window, Config config, BalanceReading beforeBalance, MenuEvidence firstEvidence,
        out BalanceReading? confirmedBalance)
    {
        var samples = new List<MenuEvidence> { firstEvidence };
        for (int sampleIndex = 1; sampleIndex < 2; sampleIndex++)
        {
            Thread.Sleep(650);
            using var image = CaptureWindow(window);
            if (image is null) continue;
            samples.Add(ReadMenuEvidence(image, config));
        }
        return IsConfirmedAlreadyClaimed(samples, beforeBalance, config, out confirmedBalance);
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

    private static bool HasClaimSuccessText(OcrSnapshot snapshot)
    {
        return snapshot.Lines
            .Select(line => (Line: line, Bounds: GetOcrBounds(line), Normalized: NormalizeOcrText(line.Text)))
            .Where(item => item.Line.Words.Count > 0)
            .Any(item => item.Normalized.Contains("今日已领", StringComparison.Ordinal) ||
                          item.Normalized.Contains("已领取", StringComparison.Ordinal));
    }

    private static bool HasLoginRequiredText(OcrSnapshot snapshot)
    {
        string[] loginRequiredPhrases = ["登录失效", "请登录", "重新登录"];
        return snapshot.Lines
            .Select(line => NormalizeOcrText(line.Text))
            .Any(text => loginRequiredPhrases.Any(phrase => text.Contains(phrase, StringComparison.Ordinal)));
    }

    private sealed class LoginRequiredException : Exception
    {
        public LoginRequiredException() : base("OCR 检测到 WorkBuddy 登录失效，领取流程已安全停止。") { }
    }

    private static void ThrowIfLoginRequired(OcrSnapshot snapshot)
    {
        if (HasLoginRequiredText(snapshot)) throw new LoginRequiredException();
    }

    private static bool IsLoginRequiredResult(string result) =>
        result.StartsWith(LoginRequiredResultPrefix, StringComparison.Ordinal);

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
            .Replace('簽', '签')
            .Replace('卽', '即')
            .Replace('娶', '取');

    private static string NormalizeExactActionText(string text) =>
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

    internal enum StartupTaskState { Missing, Disabled, Enabled, Unavailable }

    internal static StartupTaskState GetStartupTaskState()
    {
        object? schedulerObject = null;
        object? folderObject = null;
        object? taskObject = null;
        try
        {
            var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("当前系统没有 Task Scheduler COM 服务。");
            schedulerObject = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("无法创建 Task Scheduler COM 服务。");
            dynamic scheduler = schedulerObject;
            scheduler.Connect();
            folderObject = scheduler.GetFolder("\\");
            dynamic folder = folderObject;
            taskObject = folder.GetTask(TaskName);
            dynamic task = taskObject;
            return task.Enabled ? StartupTaskState.Enabled : StartupTaskState.Disabled;
        }
        catch (COMException ex) when (IsTaskNotFoundHResult(ex.HResult))
        {
            return StartupTaskState.Missing;
        }
        catch (Exception ex)
        {
            Log("查询开机启动状态失败: " + ex.Message);
            return StartupTaskState.Unavailable;
        }
        finally
        {
            ReleaseComObject(taskObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(schedulerObject);
        }
    }

    internal static bool IsTaskNotFoundHResult(int hResult) =>
        hResult == unchecked((int)0x80070002);

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    internal static ProcessStartInfo CreateStartupTaskChangeStartInfo(bool enabled)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/Change");
        start.ArgumentList.Add("/TN");
        start.ArgumentList.Add(TaskName);
        start.ArgumentList.Add(enabled ? "/ENABLE" : "/DISABLE");
        return start;
    }

    internal static void SetStartupEnabled(bool enabled)
    {
        var current = GetStartupTaskState();
        if (enabled && current == StartupTaskState.Missing)
        {
            CreateOrUpdateStartupTask();
        }
        else if (current != StartupTaskState.Missing || enabled)
        {
            using var process = Process.Start(CreateStartupTaskChangeStartInfo(enabled))
                ?? throw new InvalidOperationException("无法启动任务计划设置。");
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("修改开机启动状态超时。");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"修改开机启动状态失败，退出码 {process.ExitCode}。");
        }

        var expected = enabled ? StartupTaskState.Enabled : StartupTaskState.Disabled;
        var actual = GetStartupTaskState();
        if (!enabled && actual == StartupTaskState.Missing) return;
        if (actual != expected)
            throw new InvalidOperationException($"开机启动状态核验失败，当前状态为 {actual}。");
        Log(enabled ? "已从托盘启用开机启动。" : "已从托盘禁用开机启动；当前守护继续运行。");
    }

    private static int RunStartupStatusProbe() => GetStartupTaskState() switch
    {
        StartupTaskState.Enabled => 0,
        StartupTaskState.Disabled or StartupTaskState.Missing => 1,
        _ => 2
    };

    private static int RunSetStartup(string? value)
    {
        try
        {
            if (string.Equals(value, "enabled", StringComparison.OrdinalIgnoreCase))
            {
                SetStartupEnabled(true);
                return 0;
            }
            if (string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                SetStartupEnabled(false);
                return 0;
            }
            return 2;
        }
        catch (Exception ex)
        {
            Log("命令行修改开机启动失败: " + ex);
            return 1;
        }
    }

    private static int Install(Config config)
    {
        CreateOrUpdateStartupTask();
        // 安装发生在当天领取时间之后时，从下一天开始，避免安装动作立刻打断正在使用的 WorkBuddy。
        // Installation must not manufacture a successful daily claim or overwrite an
        // observed terminal failure. Only the numeric-balance verification path writes
        // SuccessDate; the daemon will use the persisted state on its next wake-up.
        Log("已安装开机自启任务。\n");
        Notify("WorkBuddy 自动领取", "已启用：每天 00:00 后自动领取。", ToolTipIcon.Info);
        return 0;
    }

    private static void CreateOrUpdateStartupTask()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法解析程序路径。");
        var xmlPath = Path.Combine(Path.GetTempPath(), "workbuddy-auto-claim-task.xml");
        var escapedExe = SecurityElement.Escape(exe) ?? throw new InvalidOperationException("无法转义程序路径。");
        var escapedDir = SecurityElement.Escape(BaseDir) ?? throw new InvalidOperationException("无法转义工作目录。");
        var xml = BuildScheduledTaskXml(escapedExe, escapedDir);
        File.WriteAllText(xmlPath, xml, new System.Text.UnicodeEncoding());
        try { RunProcess("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F"); }
        finally { try { File.Delete(xmlPath); } catch { } }
    }

    private static string BuildScheduledTaskXml(string escapedExe, string escapedDir) => $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><RunLevel>LeastPrivilege</RunLevel><LogonType>InteractiveToken</LogonType></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>
  <Actions Context=""Author""><Exec><Command>{escapedExe}</Command><Arguments>--daemon</Arguments><WorkingDirectory>{escapedDir}</WorkingDirectory></Exec></Actions>
</Task>";

    private static int Uninstall()
    {
        try
        {
            RunProcess("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
            Log("已删除开机自启任务。\n");
        }
        finally
        {
            try
            {
                ToastNotificationManagerCompat.Uninstall();
                Log("已清理通知中心 Toast 注册与历史。");
            }
            catch (Exception ex)
            {
                Log($"通知中心 Toast 清理失败：{ex.Message}");
            }
        }
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
        var originalWindow = FindWorkBuddyWindow();
        bool wasRunning = originalWindow != IntPtr.Zero || HasExistingWorkBuddyProcess();
        bool wasForeground = wasRunning && Native.GetForegroundWindow() == originalWindow;
        bool launchedByTool = false;
        IntPtr window = IntPtr.Zero;
        try
        {
            window = EnsureWorkBuddyWindow(config, out launchedByTool);
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
        finally
        {
            if (window != IntPtr.Zero)
            {
                if (launchedByTool) CloseWorkBuddy();
                else if (!wasForeground) Native.ShowWindow(window, Native.SW_MINIMIZE);
            }
        }
    }

    // Sends one real Windows notification with the currently OCR-read balance. It never
    // clicks a claim or check-in control; it falls back to a tray balloon only when Toast is blocked.
    private static int TestNotification(Config config)
    {
        bool launchedByTool = false;
        var window = EnsureWorkBuddyWindow(config, out launchedByTool);
        if (window == IntPtr.Zero) throw new InvalidOperationException("未找到 WorkBuddy 主窗口。");
        bool wasForeground = Native.GetForegroundWindow() == window;
        try
        {
            Native.ShowWindow(window, Native.SW_SHOWNOACTIVATE);
            if (!TryOpenPersonalCenterAndReadEvidence(window, config, out var evidence) || evidence.Balance is null)
                throw new InvalidOperationException("通知测试未能读取个人中心的积分余额。");
            var balance = FormatNotificationBalance(evidence.Balance);
            Notify("WorkBuddy 通知测试",
                BuildClaimNotificationText(ClaimOutcomeKind.AlreadyClaimed, balance, balance), ToolTipIcon.Info);
            Log($"通知测试已发送：当前余额={balance}；未执行领取点击。");
            return 0;
        }
        finally
        {
            if (launchedByTool) CloseWorkBuddy();
            else if (!wasForeground) Native.ShowWindow(window, Native.SW_MINIMIZE);
        }
    }

    // Controlled compatibility probe: it clicks one OCR-confirmed entry once, captures
    // the resulting layer, and never clicks any “立即领取” control.
    private static int ProbeCheckInEntry(Config config)
    {
        bool launchedByTool = false;
        var window = EnsureWorkBuddyWindow(config, out launchedByTool);
        if (window == IntPtr.Zero) throw new InvalidOperationException("未找到 WorkBuddy 主窗口。");
        bool wasForeground = Native.GetForegroundWindow() == window;
        try
        {
            Native.ShowWindow(window, Native.SW_SHOWNOACTIVATE);
            if (!TryOpenPersonalCenterAndReadEvidence(window, config, out var before))
                throw new InvalidOperationException("探测前未能打开个人中心。");
            using var beforeImage = CaptureWindow(window) ?? throw new InvalidOperationException("探测前无法捕获 WorkBuddy。");
            var entry = FindCheckInActions(beforeImage, ReadOcr(beforeImage), config).FirstOrDefault()
                        ?? throw new InvalidOperationException("探测前未识别到签到入口。");
            SaveBuddyDiagnosticCapture(window, "probe-before-checkin");
            Log($"签到入口探测：仅点击一次 {entry.Text}，位置=({entry.CenterX},{entry.CenterY})，不会点击最终领取。");
            ClickWindowPoint(window, entry.CenterX, entry.CenterY);
            var until = DateTime.UtcNow.AddSeconds(10);
            Bitmap? afterImage = null;
            MenuEvidence afterEvidence = MenuEvidence.Empty;
            bool immediateFound = false;
            do
            {
                Thread.Sleep(500);
                afterImage?.Dispose();
                afterImage = CaptureWindow(window) ?? throw new InvalidOperationException("入口点击后无法捕获 WorkBuddy。");
                afterEvidence = ReadMenuEvidence(afterImage, config);
                var afterOcr = ReadOcr(afterImage);
                immediateFound = FindImmediateClaimAction(afterImage, afterOcr, config) is not null;
                if (immediateFound) break;
            }
            while (DateTime.UtcNow < until);
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
            Directory.CreateDirectory(folder);
            var output = Path.Combine(folder, "workbuddy-probe-after-checkin.png");
            using var savedAfterImage = afterImage ?? throw new InvalidOperationException("入口点击后未获得可保存的 WorkBuddy 截图。");
            savedAfterImage.Save(output, ImageFormat.Png);
            Log($"签到入口探测完成：余额锚点={(afterEvidence.Balance is null ? "无" : afterEvidence.Balance.RawText)}，" +
                $"个人中心={afterEvidence.IsPersonalCenter}，完整当前界面立即领取={immediateFound}，截图={output}；未执行最终领取点击。");
            return 0;
        }
        finally
        {
            if (launchedByTool) CloseWorkBuddy();
            else if (!wasForeground) Native.ShowWindow(window, Native.SW_MINIMIZE);
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
        if (OcrProcessTimeout != TimeSpan.FromSeconds(ExpectedOcrProcessTimeoutSeconds))
            throw new InvalidOperationException("Windows OCR 必须在 8 秒后超时并安全停止。");
        if (ClassifyFailureStage("Windows OCR exceeded 8 seconds and was safely stopped.") != "OCR 超时")
            throw new InvalidOperationException("OCR 超时必须在失败通知中归类为 OCR 超时。");
        using (var timeoutProcess = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 2")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("无法启动 OCR 超时回归进程。"))
        {
            var timeoutOutput = timeoutProcess.StandardOutput.ReadToEndAsync();
            var timeoutError = timeoutProcess.StandardError.ReadToEndAsync();
            if (WaitForOcrProcess(timeoutProcess, TimeSpan.FromMilliseconds(50)) != OcrProcessWaitResult.TerminatedAfterTimeout ||
                !timeoutProcess.HasExited)
                throw new InvalidOperationException("OCR 超时回归必须终止卡住的子进程。");
        }
        var stateTestDirectory = Path.Combine(Path.GetTempPath(), "WorkBuddyAutoClaim-StateSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateTestDirectory);
        var stateTestPath = Path.Combine(stateTestDirectory, "state.json");
        var stateTestBackupPath = stateTestPath + ".bak";
        try
        {
            SaveState(new State { SuccessDate = new DateOnly(2026, 8, 10) }, stateTestPath, stateTestBackupPath);
            SaveState(new State { TerminalFailureDate = new DateOnly(2026, 8, 11) }, stateTestPath, stateTestBackupPath);
            File.WriteAllText(stateTestPath, "{invalid json");
            var recoveredState = LoadState(stateTestPath, stateTestBackupPath);
            if (recoveredState.Source != StateLoadSource.Backup ||
                recoveredState.State?.SuccessDate != new DateOnly(2026, 8, 10))
                throw new InvalidOperationException("状态主文件损坏时必须从有效备份恢复，而不能重新执行领取。");
            File.WriteAllText(stateTestBackupPath, "{invalid backup");
            if (LoadState(stateTestPath, stateTestBackupPath).Source != StateLoadSource.Invalid)
                throw new InvalidOperationException("主状态与备份都损坏时必须进入安全失败状态。");
        }
        finally
        {
            if (File.Exists(stateTestPath)) File.Delete(stateTestPath);
            if (File.Exists(stateTestBackupPath)) File.Delete(stateTestBackupPath);
            if (Directory.Exists(stateTestDirectory)) Directory.Delete(stateTestDirectory);
        }
        if (!HasDailyTerminalState(new State { SuccessDate = new DateOnly(2026, 8, 11) }, new DateOnly(2026, 8, 11)) ||
            HasDailyTerminalState(new State { SuccessDate = new DateOnly(2026, 8, 10) }, new DateOnly(2026, 8, 11)))
            throw new InvalidOperationException("从备份恢复时必须能区分已确认的今日终态与未知的今日状态。");
        var retainedDiagnostics = SelectRetainedFailureDiagnosticBases(
            Enumerable.Range(1, FailureDiagnosticRetentionCount + 1).Select(index => $"20260811-0000{index:D2}-failure"));
        if (retainedDiagnostics.Count != FailureDiagnosticRetentionCount ||
            retainedDiagnostics.Contains("20260811-000001-failure") ||
            !retainedDiagnostics.Contains($"20260811-0000{FailureDiagnosticRetentionCount + 1:D2}-failure"))
            throw new InvalidOperationException("失败诊断必须仅保留最新 20 份。");
        var loginExpiredOcr = new OcrSnapshot
        {
            Lines = [new OcrLine { Text = "请重新登录", Words = [new OcrWord { Text = "请重新登录", X = 20, Y = 20, Width = 80, Height = 20 }] }]
        };
        var loginRewardOcr = new OcrSnapshot
        {
            Lines = [new OcrLine { Text = "登录奖励", Words = [new OcrWord { Text = "登录奖励", X = 20, Y = 20, Width = 80, Height = 20 }] }]
        };
        if (!HasLoginRequiredText(loginExpiredOcr) || HasLoginRequiredText(loginRewardOcr))
            throw new InvalidOperationException("登录失效必须单独识别，不能把普通登录相关文字误判为失效。");
        try
        {
            ThrowIfLoginRequired(loginExpiredOcr);
            throw new InvalidOperationException("领取流程中途识别到登录失效时必须立即中止。");
        }
        catch (LoginRequiredException) { }
        ThrowIfLoginRequired(loginRewardOcr);
        var taskXml = BuildScheduledTaskXml("C:\\WorkBuddyAutoClaim.exe", "C:\\WorkBuddy");
        if (!taskXml.Contains("<RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>", StringComparison.Ordinal))
            throw new InvalidOperationException("任务计划必须在守护进程异常退出后自动重启。");
        var daemonRestart = CreateScheduledTaskDaemonStartInfo();
        if (!string.Equals(daemonRestart.FileName, "schtasks.exe", StringComparison.OrdinalIgnoreCase) ||
            daemonRestart.UseShellExecute ||
            daemonRestart.ArgumentList.Count != 3 ||
            daemonRestart.ArgumentList[0] != "/Run" ||
            daemonRestart.ArgumentList[1] != "/TN" ||
            daemonRestart.ArgumentList[2] != TaskName)
            throw new InvalidOperationException("已安装任务时必须通过任务计划恢复守护，不能让测试命令派生 --daemon 子进程。");
        var daemonReadyProbeName = $"Local\\WorkBuddyAutoClaim.DaemonReadySelfTest.{Guid.NewGuid():N}";
        using (var daemonReadyProbe = new EventWaitHandle(false, EventResetMode.AutoReset, daemonReadyProbeName))
        {
            daemonReadyProbe.Set();
            DrainReadySignal(daemonReadyProbe);
            if (daemonReadyProbe.WaitOne(0))
                throw new InvalidOperationException("守护恢复前必须清除旧的就绪信号。");
            using var daemonReadyChild = Process.Start(CreateDaemonReadyProbeStartInfo(daemonReadyProbeName))
                ?? throw new InvalidOperationException("无法启动守护就绪安全测试子进程。");
            if (!daemonReadyProbe.WaitOne(TimeSpan.FromSeconds(5)) ||
                !daemonReadyChild.WaitForExit(5_000) || daemonReadyChild.ExitCode != 0)
                throw new InvalidOperationException("守护恢复必须由新子进程发出的就绪信号确认。");
        }
        using (var unchangedFrame = new Bitmap(60, 40))
        using (var changedFrame = new Bitmap(60, 40))
        using (var graphics = Graphics.FromImage(changedFrame))
        {
            graphics.FillRectangle(Brushes.White, 0, 0, 20, 20);
            graphics.FillRectangle(Brushes.White, 40, 20, 20, 20);
            if (HasMeaningfulUiChange(CreateUiFrameSignature(unchangedFrame), CreateUiFrameSignature(unchangedFrame)) ||
                !HasMeaningfulUiChange(CreateUiFrameSignature(unchangedFrame), CreateUiFrameSignature(changedFrame)))
                throw new InvalidOperationException("点击后必须观察到实际界面变化，不能接受旧帧 OCR。");
        }
        var detailedFailureNotification = BuildRunNotificationText(ClaimOutcomeKind.Failed, "100", null, 5, 5,
            "保留原后台并最小化", "未能在个人中心读取积分余额");
        if (!detailedFailureNotification.Contains("失败阶段：个人中心/余额", StringComparison.Ordinal) ||
            !detailedFailureNotification.Contains("尝试：5/5", StringComparison.Ordinal) ||
            !detailedFailureNotification.Contains("WorkBuddy：保留原后台并最小化", StringComparison.Ordinal))
            throw new InvalidOperationException("失败通知必须包含阶段、尝试次数和 WorkBuddy 进程处理方式。");
        if (!BuildClaimActionNotFoundResult(clickedCheckIn: false).Contains("未识别到签到入口", StringComparison.Ordinal) ||
            !BuildClaimActionNotFoundResult(clickedCheckIn: true).Contains("已点击签到入口", StringComparison.Ordinal))
            throw new InvalidOperationException("签到入口失败结果必须如实区分未识别与已点击后未出现立即领取。");
        ValidateUserConfig(config);
        var defaultConfig = new Config();
        if (defaultConfig.MaxAttempts != 5 || defaultConfig.ManualMaxAttempts != 1 ||
            defaultConfig.RetryIntervalSeconds != 60)
            throw new InvalidOperationException("新配置的自动/手动尝试次数和重试间隔默认值不正确。");
        if (ShouldTreatWorkBuddyAsToolLaunched(hadVisibleWindow: false, hadExistingProcess: true))
            throw new InvalidOperationException("已有后台 WorkBuddy 进程时不得被视为工具启动并关闭。");
        if (!ShouldTreatWorkBuddyAsToolLaunched(hadVisibleWindow: false, hadExistingProcess: false) ||
            ShouldTreatWorkBuddyAsToolLaunched(hadVisibleWindow: true, hadExistingProcess: true))
            throw new InvalidOperationException("WorkBuddy 启动归属判定回归失败。");
        var customAttemptConfig = new Config { ManualMaxAttempts = 2, MaxAttempts = 4 };
        if (GetAttemptLimit(customAttemptConfig, ClaimRunMode.ManualTest) != 2 ||
            GetAttemptLimit(customAttemptConfig, ClaimRunMode.Automatic) != 4)
            throw new InvalidOperationException("手动测试与自动领取必须分别使用各自配置的次数。");
        if (GetAttemptLimit(new Config { MaxAttempts = 10 }, ClaimRunMode.Automatic) != 5)
            throw new InvalidOperationException("自动领取每批尝试次数必须硬性限制为最多 5 次。");
        using (var externalRequest = new AutoResetEvent(false))
        using (var changedRequest = new AutoResetEvent(false))
        {
            changedRequest.Set();
            if (SleepUntilOrManualTestRequest(DateTime.Now.AddMinutes(1), "配置唤醒自测", externalRequest, changedRequest))
                throw new InvalidOperationException("配置更新必须只重算计划，不能被误判为外部手动测试交接。");
            externalRequest.Set();
            if (!SleepUntilOrManualTestRequest(DateTime.Now.AddMinutes(1), "交接唤醒自测", externalRequest, changedRequest))
                throw new InvalidOperationException("外部手动测试请求必须让守护进程交出执行权。");
        }
        if (!IsTaskNotFoundHResult(unchecked((int)0x80070002)) || IsTaskNotFoundHResult(unchecked((int)0x80070005)))
            throw new InvalidOperationException("只有任务不存在可以显示为未启用，权限错误不得误判为任务不存在。");
        var disableStartup = CreateStartupTaskChangeStartInfo(enabled: false);
        var enableStartup = CreateStartupTaskChangeStartInfo(enabled: true);
        if (disableStartup.ArgumentList[^1] != "/DISABLE" || enableStartup.ArgumentList[^1] != "/ENABLE" ||
            disableStartup.UseShellExecute || !disableStartup.CreateNoWindow)
            throw new InvalidOperationException("开机启动切换必须使用无窗口的任务计划启用/禁用命令。");
        var configTestDirectory = Path.Combine(Path.GetTempPath(), "WorkBuddyAutoClaim-ConfigSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configTestDirectory);
        var configTestPath = Path.Combine(configTestDirectory, "config.json");
        try
        {
            var editableConfig = new Config
            {
                WorkBuddyPath = @"D:\Program Files\WorkBuddy\WorkBuddy.exe",
                ClaimTime = "01:25",
                RetryIntervalSeconds = 75,
                MaxAttempts = 4,
                ManualMaxAttempts = 2,
                BalanceValueCropScale = 7
            };
            ValidateUserConfig(editableConfig);
            SaveConfig(editableConfig, configTestPath);
            var reloadedConfig = LoadConfig(configTestPath, createFromExample: false);
            if (reloadedConfig.ClaimTime != "01:25" || reloadedConfig.MaxAttempts != 4 ||
                reloadedConfig.ManualMaxAttempts != 2 || reloadedConfig.BalanceValueCropScale != 7)
                throw new InvalidOperationException("GUI 保存配置时必须保留高级 OCR 参数并立即可重新读取。");
        }
        finally
        {
            if (Directory.Exists(configTestDirectory)) Directory.Delete(configTestDirectory, recursive: true);
        }
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
        using (var profileEntryBitmap = new Bitmap(220, 100))
        {
            for (int y = 40; y <= 72; y++)
            for (int x = 24; x <= 60; x++)
                profileEntryBitmap.SetPixel(x, y, Color.FromArgb(20, 170, 130));
            // The update banner can contain a much larger green control in the same
            // bottom strip. It must never pull the account-entry click away from the avatar.
            for (int y = 74; y <= 98; y++)
            for (int x = 176; x <= 219; x++)
                profileEntryBitmap.SetPixel(x, y, Color.FromArgb(20, 170, 130));
            if (!TryFindProfileEntryPoint(profileEntryBitmap, out var profileEntryPoint) ||
                profileEntryPoint.X < 24 || profileEntryPoint.X > 60)
                throw new InvalidOperationException("动态个人中心入口必须落在绿色头像本体内。");
        }

        var selfTestConfig = new Config();
        if (NormalizeExactActionText("立 即，领 取！") != "立即领取" ||
            NormalizeExactActionText("立 卽 领 取") == "立即领取")
            throw new InvalidOperationException("动作文字规范化只能移除空格和标点。");
        var exactImmediateOcr = new OcrSnapshot
        {
            Lines = [new OcrLine { Text = "立 即，领 取！", Words = [new OcrWord { Text = "立 即，领 取！", X = 60, Y = 290, Width = 74, Height = 22 }] }]
        };
        if (FindImmediateClaimAction(exactImmediateOcr, selfTestConfig) is null)
            throw new InvalidOperationException("完整四字“立即领取”必须在清理空格和标点后精确命中。");
        foreach (var approximateImmediateText in new[] { "立即領取", "立卽领取", "立即领职", "立即领娶", "立刻领取", "立刻领娶" })
        {
            var approximateOcr = new OcrSnapshot
            {
                Lines = [new OcrLine { Text = approximateImmediateText, Words = [new OcrWord { Text = approximateImmediateText, X = 60, Y = 290, Width = 74, Height = 22 }] }]
            };
            if (FindImmediateClaimAction(approximateOcr, selfTestConfig) is not null)
                throw new InvalidOperationException($"非完整精确四字不得被当成立即领取：{approximateImmediateText}。");
        }
        foreach (var unrelatedText in new[]
                 {
                     "领取说明", "今日可领100积分", "升级套餐", "立即取", "X立即领取",
                     // 四个字中即使包含目标字符，只要不在“立即领取”的对应位置，也不能命中。
                     "领立取即", "马上领娶"
                 })
        {
            var unrelatedOcr = new OcrSnapshot
            {
                Lines = [new OcrLine { Text = unrelatedText, Words = [new OcrWord { Text = unrelatedText, X = 60, Y = 290, Width = 100, Height = 22 }] }]
            };
            if (FindImmediateClaimAction(unrelatedOcr, selfTestConfig) is not null)
                throw new InvalidOperationException($"非按钮文字不得被当成立即领取：{unrelatedText}。");
        }
        var unapprovedClaimStateOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "本期已领", Words = [new OcrWord { Text = "本期已领", X = 45, Y = 278, Width = 68, Height = 18 }] }
            ]
        };
        if (HasClaimSuccessText(unapprovedClaimStateOcr))
            throw new InvalidOperationException("本期已领不是已授权的今日已领取判定文字。");
        var ocr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "积分余额", Words = [new OcrWord { Text = "积分余额", X = 30, Y = 350, Width = 80, Height = 18 }] },
                new OcrLine { Text = "1324.67", Words = [new OcrWord { Text = "1324.67", X = 198, Y = 350, Width = 58, Height = 18 }] },
                new OcrLine { Text = "签到领积分", Words = [new OcrWord { Text = "签到领积分", X = 52, Y = 292, Width = 98, Height = 22 }] },
                new OcrLine { Text = "体验版", Words = [new OcrWord { Text = "体验版", X = 52, Y = 145, Width = 42, Height = 18 }] },
                new OcrLine { Text = "领取说明", Words = [new OcrWord { Text = "领取说明", X = 52, Y = 100, Width = 90, Height = 18 }] },
                new OcrLine { Text = "Buddy加油站", Words = [new OcrWord { Text = "Buddy加油站", X = 52, Y = 185, Width = 98, Height = 18 }] },
                new OcrLine { Text = "设置", Words = [new OcrWord { Text = "设置", X = 52, Y = 420, Width = 42, Height = 18 }] }
            ]
        };
        var balance = TryReadBalance(ocr, selfTestConfig) ?? throw new InvalidOperationException("积分余额 OCR 锚点校验失败。");
        if (balance.Fingerprint != "1324.67") throw new InvalidOperationException("积分余额 OCR 指纹校验失败。");
        if (!IsConfirmedPersonalCenterMenu(ocr, balance, selfTestConfig))
            throw new InvalidOperationException("个人中心必须由积分余额、同行数字和菜单辅助文字组合确认。");
        var incompletePersonalCenterOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "积分余额", Words = [new OcrWord { Text = "积分余额", X = 30, Y = 350, Width = 80, Height = 18 }] },
                new OcrLine { Text = "1324.67", Words = [new OcrWord { Text = "1324.67", X = 198, Y = 350, Width = 58, Height = 18 }] }
            ]
        };
        if (IsConfirmedPersonalCenterMenu(incompletePersonalCenterOcr, balance, selfTestConfig))
            throw new InvalidOperationException("只有积分余额和数字时不得确认个人中心菜单。");
        var misleadingBalanceLabelOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "积分余额说明", Words = [new OcrWord { Text = "积分余额说明", X = 30, Y = 350, Width = 110, Height = 18 }] }
            ]
        };
        if (FindBalanceLabel(misleadingBalanceLabelOcr) is not null)
            throw new InvalidOperationException("积分余额标签必须是精确词组，不得接受积分余额说明等包含文字。");
        var splitMisleadingBalanceLabelOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "积分余额 说明", Words =
                [
                    new OcrWord { Text = "积分余额", X = 30, Y = 350, Width = 80, Height = 18 },
                    new OcrWord { Text = "说明", X = 112, Y = 350, Width = 36, Height = 18 }
                ] }
            ]
        };
        if (FindBalanceLabel(splitMisleadingBalanceLabelOcr) is not null)
            throw new InvalidOperationException("拆词后的积分余额说明也不得被当成精确余额标签。");
        var buddyFuelAction = FindBuddyFuelStationAction(ocr, selfTestConfig);
        if (buddyFuelAction is null || buddyFuelAction.Keyword != "Buddy加油站" ||
            buddyFuelAction.Kind != ClaimActionKind.BuddyFuelStation)
            throw new InvalidOperationException("已确认个人中心后必须能精确定位 Buddy加油站入口。");
        var ambiguousBuddyOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "Buddy加油站活动", Words = [new OcrWord { Text = "Buddy加油站活动", X = 52, Y = 185, Width = 118, Height = 18 }] }
            ]
        };
        if (FindBuddyFuelStationAction(ambiguousBuddyOcr, selfTestConfig) is not null)
            throw new InvalidOperationException("Buddy加油站入口必须是完整精确文字，不得点击附加文案。");
        var refreshOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "积分余额", Words = [new OcrWord { Text = "积分余额", X = 30, Y = 350, Width = 80, Height = 18 }] },
                new OcrLine { Text = "0 1324.67", Words =
                [
                    new OcrWord { Text = "0", X = 168, Y = 351, Width = 14, Height = 14 },
                    new OcrWord { Text = "1324.67", X = 198, Y = 350, Width = 58, Height = 18 }
                ] }
            ]
        };
        if (!TryFindBalanceRefreshPoint(refreshOcr, selfTestConfig, out var refreshPoint) ||
            refreshPoint.X != 175 || refreshPoint.Y != 358)
            throw new InvalidOperationException("必须根据积分余额与数字之间的刷新图标确定点击点。");
        var noRefreshOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "积分余额", Words = [new OcrWord { Text = "积分余额", X = 30, Y = 350, Width = 80, Height = 18 }] },
                new OcrLine { Text = "1324.67", Words = [new OcrWord { Text = "1324.67", X = 198, Y = 350, Width = 58, Height = 18 }] }
            ]
        };
        if (TryFindBalanceRefreshPoint(noRefreshOcr, selfTestConfig, out _))
            throw new InvalidOperationException("没有明确刷新图标时不得猜测点击坐标。");
        var actions = FindCheckInActions(ocr, selfTestConfig);
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
        var immediateAction = FindImmediateClaimAction(immediateOcr, selfTestConfig);
        if (immediateAction is null)
            throw new InvalidOperationException("立即领取 OCR 优先路由校验失败。");
        var immediateOnlyOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "立即领取", Words = [new OcrWord { Text = "立即领取", X = 460, Y = 620, Width = 74, Height = 22 }] }
            ]
        };
        if (FindImmediateClaimAction(immediateOnlyOcr, selfTestConfig) is null)
            throw new InvalidOperationException("立即领取不得依赖余额、本期、Buddy 加油站或固定区域。");
        var mixedActionStateOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "已签到", Words = [new OcrWord { Text = "已签到", X = 50, Y = 200, Width = 60, Height = 18 }] },
                new OcrLine { Text = "签到成功", Words = [new OcrWord { Text = "签到成功", X = 50, Y = 240, Width = 74, Height = 18 }] },
                new OcrLine { Text = "立即领取", Words = [new OcrWord { Text = "立即领取", X = 50, Y = 280, Width = 74, Height = 18 }] }
            ]
        };
        if (FindCheckInActions(mixedActionStateOcr, selfTestConfig).Count != 0 ||
            FindImmediateClaimAction(mixedActionStateOcr, selfTestConfig) is null)
            throw new InvalidOperationException("签到状态文字必须排除，但不得排除同画面的立即领取。");
        var routeCheckIn = new ClaimAction("签到", "签到", 50, 200, "2:8", ClaimActionKind.CheckIn);
        if (SelectClaimRoute(hasImmediate: true, hasStableClaimedText: true, [routeCheckIn]) != ClaimRouteKind.Immediate ||
            SelectClaimRoute(hasImmediate: false, hasStableClaimedText: true, [routeCheckIn]) != ClaimRouteKind.AlreadyClaimed ||
            SelectClaimRoute(hasImmediate: false, hasStableClaimedText: false, [routeCheckIn]) != ClaimRouteKind.CheckIn ||
            SelectClaimRoute(hasImmediate: false, hasStableClaimedText: false, []) != ClaimRouteKind.None)
            throw new InvalidOperationException("整窗领取路由必须按立即领取、已领状态、签到入口、无动作排序。");
        var labelOnlyOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "积分余额", Words = [new OcrWord { Text = "积分余额", X = 30, Y = 350, Width = 80, Height = 18 }] }
            ]
        };
        using (var labelOnlyImage = new Bitmap(500, 500))
        using (var labelOnlyGraphics = Graphics.FromImage(labelOnlyImage))
        {
            labelOnlyGraphics.Clear(Color.White);
            if (TryReadBalance(labelOnlyImage, labelOnlyOcr, selfTestConfig) is not null)
                throw new InvalidOperationException("未读到明确数字的积分余额不得以视觉指纹继续领取流程。");
        }
        var visualBefore = new BalanceReading("视觉余额指纹", "V:000000000000", immediateBalance.Bounds, true);
        var visualSame = new BalanceReading("视觉余额指纹", "V:000100000000", immediateBalance.Bounds, true);
        var visualChanged = new BalanceReading("视觉余额指纹", "V:010101010101", immediateBalance.Bounds, true);
        if (HasConfirmedNumericBalance(visualBefore) ||
            AreSameBalance(visualBefore, visualSame, selfTestConfig) ||
            IsBalanceIncreased(visualBefore, visualChanged, selfTestConfig))
            throw new InvalidOperationException("视觉余额指纹不得作为领取或今日已领的余额证据。");
        var claimedOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "今日已领", Words = [new OcrWord { Text = "今日已领", X = 45, Y = 278, Width = 68, Height = 18 }] },
                new OcrLine { Text = "+100", Words = [new OcrWord { Text = "+100", X = 70, Y = 314, Width = 45, Height = 18 }] }
            ]
        };
        if (!HasClaimSuccessText(claimedOcr))
            throw new InvalidOperationException("今日已领 OCR 文本校验失败。");
        var staleAlreadyClaimedSamples = new[]
        {
            new MenuEvidence(immediateBalance, [], true, true),
            new MenuEvidence(immediateBalance, [], false, true)
        };
        if (IsConfirmedAlreadyClaimed(staleAlreadyClaimedSamples, immediateBalance, selfTestConfig, out _))
            throw new InvalidOperationException("单帧“今日已领”文字不得直接判定为今日已领取。");
        var stableAlreadyClaimedSamples = new[]
        {
            new MenuEvidence(immediateBalance, [], true, true),
            new MenuEvidence(immediateBalance, [], true, true)
        };
        if (!IsConfirmedAlreadyClaimed(stableAlreadyClaimedSamples, immediateBalance, selfTestConfig, out _))
            throw new InvalidOperationException("个人中心连续两帧“今日已领”且余额不变必须判定为今日已领取。");
        if (BuildClaimNotificationText(ClaimOutcomeKind.Claimed, "100", "200") != "领取成功 · 积分余额：100 → 200")
            throw new InvalidOperationException("领取成功通知必须在同一正文中显示领取前后余额。");
        if (BuildClaimNotificationText(ClaimOutcomeKind.AlreadyClaimed, "200", "200") != "今日已领取 · 当前余额：200")
            throw new InvalidOperationException("今日已领取通知必须在同一正文中显示当前余额。");
        if (BuildClaimNotificationText(ClaimOutcomeKind.Failed, "100", null) != "领取失败 · 最后读取余额：100")
            throw new InvalidOperationException("领取失败通知必须在同一正文中显示最后读取余额。");
        var claimNotificationRequest = BuildPersistentNotificationRequest(
            "WorkBuddy 自动领取", BuildClaimNotificationText(ClaimOutcomeKind.Claimed, "100", "200"),
            new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.FromHours(8)));
        var claimNotificationXml = BuildToastXml(claimNotificationRequest).GetXml();
        if (Regex.Matches(claimNotificationXml, "<text").Count != 2 ||
            !claimNotificationXml.Contains(">WorkBuddy 自动领取<", StringComparison.Ordinal) ||
            !claimNotificationXml.Contains(">领取成功 · 积分余额：100 → 200<", StringComparison.Ordinal))
            throw new InvalidOperationException("领取通知必须在首个 Toast 正文中同时显示状态和余额。");
        var misreadBalanceOcr = new OcrSnapshot
        {
            Lines =
            [
                new OcrLine { Text = "0积分余额", Words = [
                    new OcrWord { Text = "0", X = 30, Y = 494, Width = 14, Height = 14 },
                    new OcrWord { Text = "积", X = 53, Y = 495, Width = 15, Height = 13 },
                    new OcrWord { Text = "分", X = 69, Y = 495, Width = 14, Height = 13 },
                    new OcrWord { Text = "余", X = 83, Y = 494, Width = 15, Height = 14 },
                    new OcrWord { Text = "额", X = 99, Y = 494, Width = 14, Height = 14 }
                ] },
                new OcrLine { Text = "《3398.4g>", Words = [
                    new OcrWord { Text = "《", X = 243, Y = 497, Width = 2, Height = 8 },
                    new OcrWord { Text = "3", X = 245, Y = 495, Width = 10, Height = 12 },
                    new OcrWord { Text = "398.4g", X = 264, Y = 497, Width = 37, Height = 9 },
                    new OcrWord { Text = ">", X = 306, Y = 497, Width = 4, Height = 8 }
                ] }
            ]
        };
        var correctedBalance = TryReadBalance(misreadBalanceOcr, selfTestConfig)
                               ?? throw new InvalidOperationException("通知余额 OCR 回归样本未读到余额。");
        if (FormatNotificationBalance(correctedBalance) != "398.49")
            throw new InvalidOperationException("通知余额必须忽略加载图标误识别，并将 398.4g 还原为 398.49。");
        if (NormalizeBalanceToken("1,722.8") != "1722.8" ||
            NormalizeBalanceToken("1.722,8") != "1722.8" ||
            NormalizeBalanceToken("2.1554") != "2155.4" ||
            NormalizeBalanceToken("2,15549") != "2155.49")
            throw new InvalidOperationException("余额解析必须区分千位分隔符与小数点。");
        var sameLineGroupedBalanceOcr = new OcrSnapshot
        {
            Lines = [new OcrLine { Text = "积分余额1,722.8", Words = [
                new OcrWord { Text = "积分余额", X = 30, Y = 350, Width = 80, Height = 18 },
                new OcrWord { Text = "1,722.8", X = 130, Y = 350, Width = 70, Height = 18 }
            ] }]
        };
        var sameLineGroupedBalance = TryReadBalance(sameLineGroupedBalanceOcr, selfTestConfig)
                                     ?? throw new InvalidOperationException("同行千位分隔余额未读取到。");
        if (FormatNotificationBalance(sameLineGroupedBalance) != "1722.8")
            throw new InvalidOperationException("同行积分余额必须保留原始千位符与小数点后再解析。");
        var verifiedBalance = new BalanceReading("398.49", "398.49", correctedBalance.Bounds);
        var outlierBalance = new BalanceReading("3398.49", "3398.49", correctedBalance.Bounds);
        var balanceOnlyEvidence = new MenuEvidence(verifiedBalance, [], HasSuccessText: false, IsPersonalCenter: true);
        if (HasOfflineClaimRouteOrState(balanceOnlyEvidence, immediate: null, checkInActions: []))
            throw new InvalidOperationException("离线 OCR 验证不得让只有余额、没有领取状态或入口的截图通过。");
        var lowerBalance = new BalanceReading("1622.8", "1622.8", correctedBalance.Bounds);
        var higherBalance = new BalanceReading("1822.8", "1822.8", correctedBalance.Bounds);
        var startingBalance = new BalanceReading("1722.8", "1722.8", correctedBalance.Bounds);
        if (IsBalanceIncreased(startingBalance, lowerBalance, selfTestConfig) ||
            !IsBalanceIncreased(startingBalance, higherBalance, selfTestConfig))
            throw new InvalidOperationException("领取成功只能由明确数字余额增加确认，余额下降不得判定成功。");
        if (SelectConfirmedNumericBalance([verifiedBalance, verifiedBalance, outlierBalance])?.RawText != "398.49")
            throw new InvalidOperationException("余额多路读取必须接受两个一致值，并忽略一个异常值。");
        if (SelectConfirmedNumericBalance([verifiedBalance, outlierBalance]) is not null)
            throw new InvalidOperationException("余额多路读取没有一致结果时不得显示猜测数值。");
        if (SelectConfirmedBalanceAcrossFrames([verifiedBalance, outlierBalance, verifiedBalance]) is not null)
            throw new InvalidOperationException("A-B-A 三帧不得冒充连续两帧一致余额。");
        if (SelectConfirmedBalanceAcrossFrames([outlierBalance, verifiedBalance, verifiedBalance])?.RawText != "398.49")
            throw new InvalidOperationException("只有末尾连续两帧相同明确数字才能形成跨帧共识。");
        if (SelectConfirmedBalanceAcrossFrames([verifiedBalance, outlierBalance,
                new BalanceReading("498.49", "498.49", correctedBalance.Bounds)]) is not null)
            throw new InvalidOperationException("三帧余额读取互不一致时不得形成跨帧共识。");
        using (var darkThemeBalance = new Bitmap(2, 1))
        {
            darkThemeBalance.SetPixel(0, 0, Color.FromArgb(20, 20, 20));
            darkThemeBalance.SetPixel(1, 0, Color.White);
            NormalizeDarkThemeOcr(darkThemeBalance);
            if (darkThemeBalance.GetPixel(0, 0).ToArgb() != Color.White.ToArgb() ||
                darkThemeBalance.GetPixel(1, 0).ToArgb() != Color.Black.ToArgb())
                throw new InvalidOperationException("深色主题余额预处理必须输出黑字白底供 OCR 读取。");
        }
        var changedBalance = new BalanceReading("498.49", "498.49", correctedBalance.Bounds);
        if (ClassifyClaimOutcome(verifiedBalance, changedBalance, hasAlreadyClaimedText: false, config: selfTestConfig) !=
            ClaimOutcomeKind.Claimed)
            throw new InvalidOperationException("余额变化必须报告领取成功。");
        if (ClassifyClaimOutcome(verifiedBalance, verifiedBalance, hasAlreadyClaimedText: true, config: selfTestConfig) !=
            ClaimOutcomeKind.AlreadyClaimed)
            throw new InvalidOperationException("余额不变且出现已领取文字必须报告今日已领取。");
        var toastRequest = BuildPersistentNotificationRequest("WorkBuddy 自动领取", "领取成功",
            new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.FromHours(8)));
        if (toastRequest.Title != "WorkBuddy 自动领取" || toastRequest.Text != "领取成功" ||
            toastRequest.ExpiresAt != new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.FromHours(8)))
            throw new InvalidOperationException("通知中心消息必须保留标题、正文，并在三天后过期。");
        if (PersistentNotificationRetentionDays != 3 ||
            !CanUseToastNotificationSetting(NotificationSetting.Enabled))
            throw new InvalidOperationException("通知中心只应在 Windows 允许时投递，并保留三天。");
        var advancedConfigTestPath = Path.Combine(Path.GetTempPath(), "WorkBuddyAutoClaim advanced config test.json");
        File.WriteAllText(advancedConfigTestPath, "{}");
        var advancedConfigStarts = new List<ProcessStartInfo>();
        try
        {
            DashboardForm.OpenAdvancedConfig(advancedConfigTestPath, startInfo =>
            {
                advancedConfigStarts.Add(startInfo);
                if (advancedConfigStarts.Count == 1)
                    throw new System.ComponentModel.Win32Exception(1155, "No application is associated with JSON files.");
            });
            if (advancedConfigStarts.Count != 2 ||
                !advancedConfigStarts[0].UseShellExecute ||
                !string.Equals(advancedConfigStarts[0].FileName, advancedConfigTestPath, StringComparison.Ordinal) ||
                advancedConfigStarts[1].UseShellExecute ||
                !string.Equals(advancedConfigStarts[1].FileName, "notepad.exe", StringComparison.OrdinalIgnoreCase) ||
                advancedConfigStarts[1].ArgumentList.Count != 1 ||
                !string.Equals(advancedConfigStarts[1].ArgumentList[0], advancedConfigTestPath, StringComparison.Ordinal))
                throw new InvalidOperationException("高级配置必须先使用系统默认程序打开；无 JSON 关联时应回退到记事本。");
        }
        finally
        {
            File.Delete(advancedConfigTestPath);
        }
        var statusTestDirectory = Path.Combine(Path.GetTempPath(), "WorkBuddyAutoClaim-RunStatusSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(statusTestDirectory);
        var statusTestPath = Path.Combine(statusTestDirectory, "run-status.json");
        try
        {
            var completedStatus = new RunStatus
            {
                UpdatedAt = new DateTimeOffset(2026, 9, 4, 0, 0, 8, TimeSpan.FromHours(8)),
                Mode = "Automatic",
                Outcome = "Claimed",
                Message = "余额已增加",
                BeforeBalance = "1722.8",
                AfterBalance = "1822.8",
                AttemptsPerformed = 1,
                MaxAttempts = 5
            };
            SaveRunStatus(completedStatus, statusTestPath);
            var reloadedStatus = LoadRunStatus(statusTestPath);
            var completedView = DashboardStatusView.From(reloadedStatus);
            if (completedView.Title != "领取成功" || completedView.Balance != "1822.8" ||
                !completedView.Detail.Contains("1722.8 → 1822.8", StringComparison.Ordinal))
                throw new InvalidOperationException("GUI 必须把余额变化显示为领取成功，并展示领取前后余额。");

            var alreadyView = DashboardStatusView.From(completedStatus with
            {
                Outcome = "AlreadyClaimed",
                BeforeBalance = "1822.8",
                AfterBalance = "1822.8"
            });
            if (alreadyView.Title != "今日已领取" || alreadyView.Balance != "1822.8")
                throw new InvalidOperationException("GUI 必须把余额不变且已领状态显示为今日已领取。");
        }
        finally
        {
            if (Directory.Exists(statusTestDirectory)) Directory.Delete(statusTestDirectory, recursive: true);
        }
        Log("Self test OK.");
        return 0;
    }

    internal static Config LoadConfig() => LoadConfig(ConfigPath, createFromExample: true);

    internal static Config LoadConfig(string path, bool createFromExample)
    {
        if (!File.Exists(path))
        {
            if (!createFromExample) throw new FileNotFoundException("找不到配置文件。", path);
            var example = Path.Combine(BaseDir, "config.example.json");
            File.Copy(example, path);
        }
        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("配置文件无效。");
        ValidateUserConfig(config);
        return config;
    }

    internal static void ValidateUserConfig(Config config)
    {
        if (string.IsNullOrWhiteSpace(config.WorkBuddyPath))
            throw new InvalidOperationException("WorkBuddy 路径不能为空。");
        if (!TimeSpan.TryParseExact(config.ClaimTime, @"hh\:mm", CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("领取时间必须是 HH:mm，例如 00:00。");
        if (config.MaxAttempts is < 1 or > 5)
            throw new InvalidOperationException("每日自动重试次数必须在 1 到 5 之间。");
        if (config.ManualMaxAttempts is < 1 or > 10)
            throw new InvalidOperationException("手动重试次数必须在 1 到 10 之间。");
        if (config.RetryIntervalSeconds is < 10 or > 3600)
            throw new InvalidOperationException("失败重试间隔必须在 10 到 3600 秒之间。");
        if (config.LaunchWaitSeconds is < 5 or > 120 || config.CardReadyTimeoutSeconds is < 5 or > 120)
            throw new InvalidOperationException("启动等待和界面等待必须在 5 到 120 秒之间。");
    }

    internal static void SaveConfig(Config config) => SaveConfig(config, ConfigPath);

    internal static void SaveConfig(Config config, string path)
    {
        ValidateUserConfig(config);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("配置目录不可用。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    internal static RunStatus? LoadRunStatus()
    {
        var status = LoadRunStatus(RunStatusPath);
        if (status is not null) return status;
        var stateLoad = LoadState();
        if (!stateLoad.IsUsable) return null;
        var state = stateLoad.State!;
        var latestDate = new[] { state.SuccessDate, state.TerminalFailureDate }
            .Where(date => date.HasValue)
            .Select(date => date!.Value)
            .DefaultIfEmpty()
            .Max();
        if (latestDate == default) return null;
        var failed = state.TerminalFailureDate == latestDate;
        var localTime = latestDate.ToDateTime(TimeOnly.MinValue);
        return new RunStatus
        {
            UpdatedAt = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime)),
            Mode = "Automatic",
            Outcome = failed ? "Failed" : "AlreadyClaimed",
            Message = failed ? "由原有每日状态恢复：当天领取失败。" : "由原有每日状态恢复：当天已完成领取。",
            MaxAttempts = 0
        };
    }

    internal static RunStatus? LoadRunStatus(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<RunStatus>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log("读取最近领取状态失败: " + ex.Message);
            return null;
        }
    }

    internal static void SaveRunStatus(RunStatus status) => SaveRunStatus(status, RunStatusPath);

    private static void RecordRunStatus(RunStatus status)
    {
        try { SaveRunStatus(status); }
        catch (Exception ex) { Log("保存最近领取状态失败: " + ex.Message); }
    }

    internal static void SaveRunStatus(RunStatus status, string path)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("状态目录不可用。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }
    private enum StateLoadSource { Primary, Backup, Missing, Invalid }

    private readonly record struct StateLoadResult(State? State, StateLoadSource Source)
    {
        public bool IsUsable => State is not null && Source != StateLoadSource.Invalid;
    }

    private static StateLoadResult LoadState() => LoadState(StatePath, StateBackupPath);

    private static StateLoadResult LoadState(string statePath, string backupPath)
    {
        if (TryReadStateFile(statePath, out var primary))
            return new StateLoadResult(primary, StateLoadSource.Primary);
        if (TryReadStateFile(backupPath, out var backup))
            return new StateLoadResult(backup, StateLoadSource.Backup);

        if (!File.Exists(statePath) && !File.Exists(backupPath))
            return new StateLoadResult(new State(), StateLoadSource.Missing);
        return new StateLoadResult(null, StateLoadSource.Invalid);
    }

    private static bool TryReadStateFile(string path, out State? state)
    {
        state = null;
        try
        {
            if (!File.Exists(path)) return false;
            state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
            return state is not null;
        }
        catch { return false; }
    }

    private static void SaveState(State state) => SaveState(state, StatePath, StateBackupPath);

    private static void SaveState(State state, string statePath, string backupPath)
    {
        var directory = Path.GetDirectoryName(statePath) ?? throw new InvalidOperationException("State directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, Path.GetFileName(statePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            if (TryReadStateFile(statePath, out _)) File.Copy(statePath, backupPath, overwrite: true);
            File.Move(temporaryPath, statePath, overwrite: true);
            if (!File.Exists(backupPath)) File.Copy(statePath, backupPath, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static void MaintainDiagnosticRetention()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_lastRetentionMaintenanceDate == today) return;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var cutoff = DateTime.Now.AddDays(-LogRetentionDays);
            TrimLogToRetentionWindow(Path.Combine(DataDirectory, "workbuddy-auto-claim.log"), cutoff);
            TrimFailureDiagnostics(Path.Combine(DataDirectory, "diagnostics"), cutoff);
            _lastRetentionMaintenanceDate = today;
        }
        catch (Exception ex)
        {
            Log("Diagnostic retention maintenance failed: " + ex.Message);
        }
    }

    private static void TrimLogToRetentionWindow(string logPath, DateTime cutoff)
    {
        if (!File.Exists(logPath)) return;
        var kept = File.ReadLines(logPath)
            .Where(line => !TryGetLogTimestamp(line, out var timestamp) || timestamp >= cutoff)
            .ToArray();
        File.WriteAllLines(logPath, kept, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static bool TryGetLogTimestamp(string line, out DateTime timestamp)
    {
        timestamp = default;
        return line.Length >= 21 && line[0] == '[' && line[20] == ']' &&
               DateTime.TryParseExact(line.Substring(1, 19), "yyyy-MM-dd HH:mm:ss",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None, out timestamp);
    }

    private static void TrimFailureDiagnostics(string diagnosticsPath, DateTime cutoff)
    {
        if (!Directory.Exists(diagnosticsPath)) return;
        foreach (var file in Directory.EnumerateFiles(diagnosticsPath).Select(path => new FileInfo(path)).ToArray())
        {
            if (file.LastWriteTime >= cutoff) continue;
            try { file.Delete(); } catch { }
        }

        var retainedBases = SelectRetainedFailureDiagnosticBases(
            Directory.EnumerateFiles(diagnosticsPath).Select(Path.GetFileNameWithoutExtension));
        foreach (var file in Directory.EnumerateFiles(diagnosticsPath).ToArray())
        {
            if (retainedBases.Contains(Path.GetFileNameWithoutExtension(file))) continue;
            try { File.Delete(file); } catch { }
        }
    }

    private static HashSet<string> SelectRetainedFailureDiagnosticBases(IEnumerable<string?> baseNames) =>
        baseNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .Take(FailureDiagnosticRetentionCount)
            .ToHashSet(StringComparer.Ordinal);

    private static void Log(string text)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "workbuddy-auto-claim.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
    private static void Notify(string title, string text, ToolTipIcon icon)
    {
        var request = BuildPersistentNotificationRequest(title, text, DateTimeOffset.Now);
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            if (!CanUseToastNotificationSetting(notifier.Setting))
            {
                Log($"通知中心 Toast 被 Windows 阻止：状态={notifier.Setting}；将回退为临时托盘气泡。");
            }
            else
            {
                var toast = new ToastNotification(BuildToastXml(request))
                {
                    ExpirationTime = request.ExpiresAt
                };
                notifier.Show(toast);
                Log($"通知中心 Toast 已投递：标题={title}，保留至 {request.ExpiresAt:yyyy-MM-dd HH:mm:ss}。");
                return;
            }
        }
        catch (Exception ex)
        {
            Log($"通知中心 Toast 投递失败，将回退为临时托盘气泡：{ex.Message}");
        }

        using var tray = new NotifyIcon { Icon = SystemIcons.Information, Visible = true, BalloonTipTitle = title, BalloonTipText = text, BalloonTipIcon = icon };
        tray.ShowBalloonTip(8000);
        Application.DoEvents();
        Thread.Sleep(8500);
    }

    private readonly record struct PersistentNotificationRequest(string Title, string Text, DateTimeOffset ExpiresAt);

    private static PersistentNotificationRequest BuildPersistentNotificationRequest(
        string title, string text, DateTimeOffset now) =>
        new(title, text, now.AddDays(PersistentNotificationRetentionDays));

    private static Windows.Data.Xml.Dom.XmlDocument BuildToastXml(PersistentNotificationRequest request) =>
        new ToastContentBuilder()
            .AddText(request.Title)
            .AddText(request.Text)
            .GetToastContent()
            .GetXml();

    private static bool CanUseToastNotificationSetting(NotificationSetting setting) =>
        setting == NotificationSetting.Enabled;

    private static string BuildClaimNotificationText(ClaimOutcomeKind outcome, string? beforeBalance, string? afterBalance) =>
        outcome switch
        {
            ClaimOutcomeKind.Claimed => $"领取成功 · 积分余额：{beforeBalance ?? "未读取到"} → {afterBalance ?? "未读取到"}",
            ClaimOutcomeKind.AlreadyClaimed => $"今日已领取 · 当前余额：{afterBalance ?? beforeBalance ?? "未读取到"}",
            _ => $"领取失败 · 最后读取余额：{beforeBalance ?? afterBalance ?? "未读取到"}"
        };

    private static string BuildRunNotificationText(
        ClaimOutcomeKind outcome, string? beforeBalance, string? afterBalance,
        int attemptsPerformed, int maxAttempts, string lifecycle, string result)
    {
        var baseText = BuildClaimNotificationText(outcome, beforeBalance, afterBalance);
        var attempts = $"尝试：{Math.Max(0, attemptsPerformed)}/{Math.Max(1, maxAttempts)}";
        return outcome == ClaimOutcomeKind.Failed
            ? $"{baseText}\n失败阶段：{ClassifyFailureStage(result)} · {attempts} · WorkBuddy：{lifecycle}"
            : $"{baseText}\n{attempts} · WorkBuddy：{lifecycle}";
    }

    private static string ClassifyFailureStage(string result)
    {
        if (result.Contains("登录", StringComparison.Ordinal)) return "登录失效";
        if (result.Contains("OCR", StringComparison.OrdinalIgnoreCase) &&
            (result.Contains("超时", StringComparison.Ordinal) ||
             result.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
             result.Contains("exceeded", StringComparison.OrdinalIgnoreCase))) return "OCR 超时";
        if (result.Contains("积分余额", StringComparison.Ordinal) || result.Contains("个人中心", StringComparison.Ordinal)) return "个人中心/余额";
        if (result.Contains("立即领取", StringComparison.Ordinal) || result.Contains("领取", StringComparison.Ordinal)) return "领取文字/结果核验";
        return "未确认";
    }

    private static string DescribeWorkBuddyLifecycle(bool launchedByTool, bool wasForeground) =>
        launchedByTool ? "工具启动后关闭" : wasForeground ? "保留原前台" : "保留原后台并最小化";

    private static string? FormatNotificationBalance(BalanceReading? balance)
    {
        if (!HasConfirmedNumericBalance(balance)) return null;
        var match = Regex.Match(balance!.RawText, @"\d+(?:[.,]\d+)?");
        return match.Success ? match.Value.Replace(',', '.') : balance.RawText.Trim();
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
    internal static readonly string[] DefaultImmediateClaimKeywords = ["立即领取"];
    internal static readonly string[] DefaultCheckInKeywords = ["签到领积分", "去签到", "签到"];
    public string WorkBuddyPath { get; set; } = @"D:\Program Files\WorkBuddy\WorkBuddy.exe";
    public string ClaimTime { get; set; } = "00:00";
    public int RetryIntervalSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
    public int ManualMaxAttempts { get; set; } = 1;
    public int LaunchWaitSeconds { get; set; } = 20;
    public int CardReadyTimeoutSeconds { get; set; } = 30;
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
    public int ClaimCandidatePositionTolerancePixels { get; set; } = 24;
    public int BalanceAnchorDriftPixels { get; set; } = 48;
    public int VisualBalanceSameFrameMaxChangedCells { get; set; } = 4;
    public int VisualBalanceChangeMinimumChangedCells { get; set; } = 5;
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
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
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
