using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
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
                // 新启动的 WorkBuddy 不抢占用户前台；后续用窗口消息和后台截图完成领取。
                Native.ShowWindow(window, Native.SW_MINIMIZE);
                Log("WorkBuddy 由工具启动，已最小化到后台领取。");
            }
            else if (wasForeground)
            {
                Log("WorkBuddy 原本在前台，保留前台状态领取。");
            }
            else
            {
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

    // Chromium/Electron 会把内容放在子窗口；向该窗口投递鼠标消息无需激活或显示 WorkBuddy。
    private static void ClickWindowPoint(IntPtr topWindow, int windowX, int windowY)
    {
        var target = FindChromeChild(topWindow);
        if (target == IntPtr.Zero) target = topWindow;
        Native.GetWindowRect(topWindow, out var rect);
        var point = new Native.POINT { X = rect.Left + windowX, Y = rect.Top + windowY };
        Native.ScreenToClient(target, ref point);
        var lParam = (IntPtr)((point.Y << 16) | (point.X & 0xffff));
        Native.PostMessage(target, Native.WM_MOUSEMOVE, IntPtr.Zero, lParam);
        Native.PostMessage(target, Native.WM_LBUTTONDOWN, (IntPtr)1, lParam);
        Native.PostMessage(target, Native.WM_LBUTTONUP, IntPtr.Zero, lParam);
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
        // 用户确认的稳定入口：左下个人中心。这里展示今日领取状态、当期累计积分和主领取按钮。
        if (!OpenPersonalCenter(window, config))
        {
            result = "个人中心未完成加载或未能展开";
            return false;
        }
        if (LooksClaimed(window, config))
        {
            // 用户指定个人中心为状态依据：已显示“今日已领”即代表今天无需再次点击，
            // 写入当天成功记录并发送成功通知，避免重复重试和错误告警。
            result = "WorkBuddy 今日已领取";
            return true;
        }
        if (!LooksMenuClaimButtonEnabled(window, config))
        {
            result = "个人中心未显示可用的立即领取按钮";
            return false;
        }
        ClickWindowPoint(window, config.ClaimClickX, GetWindowHeight(window) - config.ClaimBottomOffset);
        Thread.Sleep(1500);
        bool claimed = LooksClaimed(window, config);
        result = claimed ? "领取成功，个人中心已更新为今日已领" : "点击后个人中心未更新为今日已领";
        return claimed;
    }

    private static bool OpenPersonalCenter(IntPtr window, Config config)
    {
        // Electron 窗口可先于侧栏用户信息完成渲染。最多等待 15 秒，并以绿色签到卡片出现为准。
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (IsPersonalCenterReady(window, config)) return true;
            ClickWindowPoint(window, config.ProfileX, GetWindowHeight(window) - config.ProfileBottomOffset);
            Thread.Sleep(1800);
            if (IsPersonalCenterReady(window, config)) return true;
        }
        return false;
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
        return colors.Length == 3 && colors.All(c =>
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            int min = Math.Min(c.R, Math.Min(c.G, c.B));
            // 浅色已领按钮约 RGB(242,242,242)；深色已领按钮约 RGB(47,47,47)。
            return max - min <= 8 && (darkTheme ? max >= 38 && max <= 72 : max >= 230 && max <= 248);
        });
    }

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
        if (!OpenPersonalCenter(window, config)) throw new InvalidOperationException("个人中心未完成加载或未能展开。");
        using var image = CaptureWindow(window) ?? throw new InvalidOperationException("无法捕获个人中心。");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyAutoClaim");
        Directory.CreateDirectory(folder);
        image.Save(Path.Combine(folder, "workbuddy-personal-center-test.png"), ImageFormat.Png);
        bool claimed = LooksClaimed(window, config);
        Log($"个人中心测试完成：已领状态={claimed}，未执行领取点击。");
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
    public string WorkBuddyPath { get; set; } = @"D:\Program Files\WorkBuddy\WorkBuddy.exe";
    public string ClaimTime { get; set; } = "00:00";
    public int RetryIntervalSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
    public int LaunchWaitSeconds { get; set; } = 20;
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
    internal const int SW_MINIMIZE = 6;
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
