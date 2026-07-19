using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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
            return command switch
            {
                "--install" => Install(config),
                "--uninstall" => Uninstall(),
                "--run-now" => RunOnce(config, notify: true),
                "--dry-run" => DryRun(config),
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
        while (true)
        {
            try
            {
                var state = LoadState();
                if (DateTime.TryParse(config.ClaimTime, out var scheduled) &&
                    DateTime.Now.TimeOfDay >= scheduled.TimeOfDay && state.SuccessDate != DateOnly.FromDateTime(DateTime.Today))
                {
                    if (IsInteractiveDesktop())
                    {
                        RunOnce(config, notify: true);
                    }
                    else
                    {
                        Log("桌面已锁定；等待解锁后继续领取。");
                    }
                }
            }
            catch (Exception ex) { Log("轮询错误: " + ex.Message); }
            Thread.Sleep(Math.Max(10, config.CheckIntervalSeconds) * 1000);
        }
    }

    private static int RunOnce(Config config, bool notify)
    {
        if (!IsInteractiveDesktop())
        {
            Log("桌面已锁定，跳过本次尝试。");
            return 3;
        }

        bool succeeded = false;
        string result = "未能确认领取成功";
        for (int attempt = 1; attempt <= config.MaxAttempts && !succeeded; attempt++)
        {
            try
            {
                Log($"开始领取，第 {attempt}/{config.MaxAttempts} 次。");
                var window = EnsureWorkBuddyWindow(config);
                if (window == IntPtr.Zero) throw new InvalidOperationException("未找到 WorkBuddy 主窗口。");

                // 已领取按钮为灰色；识别到它即表示今天的任务已经完成。
                if (LooksClaimed(window, config))
                {
                    succeeded = true;
                    result = "今日已领";
                    break;
                }

                ClickWindowPoint(window, config.ProfileX, GetWindowHeight(window) - config.ProfileBottomOffset);
                Thread.Sleep(800);

                if (LooksClaimed(window, config))
                {
                    succeeded = true;
                    result = "今日已领";
                    break;
                }

                ClickWindowPoint(window, config.ClaimClickX, GetWindowHeight(window) - config.ClaimBottomOffset);
                Thread.Sleep(1200);
                succeeded = LooksClaimed(window, config);
                result = succeeded ? "领取成功" : "未识别到“今日已领”状态";
            }
            catch (Exception ex)
            {
                result = ex.Message;
                Log($"第 {attempt} 次失败: {ex.Message}");
            }
            finally { CloseWorkBuddy(); }
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
        var existing = FindWorkBuddyWindow();
        if (existing != IntPtr.Zero) return existing;
        if (!File.Exists(config.WorkBuddyPath)) throw new FileNotFoundException("找不到 WorkBuddy.exe", config.WorkBuddyPath);
        Process.Start(new ProcessStartInfo(config.WorkBuddyPath) { UseShellExecute = true });
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
        Native.GetWindowRect(window, out var rect);
        var p = new Point(rect.Left + config.ClaimStatusX, rect.Top + height - config.ClaimBottomOffset);
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(p, Point.Empty, new Size(1, 1));
        var c = bitmap.GetPixel(0, 0);
        // “今日已领”禁用按钮为低饱和浅灰；未领取按钮是高饱和薄荷绿。
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        bool gray = max - min < 18 && max > 170;
        Log($"状态像素 RGB({c.R},{c.G},{c.B})，已领取判定: {gray}");
        return gray;
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
        var desktop = Native.OpenInputDesktop(0, false, Native.DESKTOP_SWITCHDESKTOP | Native.GENERIC_READ);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = Native.GetDesktopName(desktop);
            return name.Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { Native.CloseDesktop(desktop); }
    }

    private static int Install(Config config)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法解析程序路径。");
        var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --daemon\" /SC ONLOGON /RL LIMITED /F";
        RunProcess("schtasks.exe", args);
        // 安装发生在当天领取时间之后时，从下一天开始，避免安装动作立刻打断正在使用的 WorkBuddy。
        if (DateTime.TryParse(config.ClaimTime, out var scheduled) && DateTime.Now.TimeOfDay >= scheduled.TimeOfDay)
            SaveState(new State { SuccessDate = DateOnly.FromDateTime(DateTime.Today) });
        Log("已安装开机自启任务。\n");
        Notify("WorkBuddy 自动领取", "已启用：每天 00:00 后自动领取。", ToolTipIcon.Info);
        // 安装器自身持有互斥锁；延迟启动，确保守护进程不会被误判为重复实例。
        var delayed = $"/c timeout /t 2 /nobreak >nul & start \"\" /b \"{exe}\" --daemon";
        Process.Start(new ProcessStartInfo("cmd.exe", delayed) { UseShellExecute = false, CreateNoWindow = true });
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

    private static int SelfTest(Config config)
    {
        if (!DateTime.TryParse(config.ClaimTime, out _)) throw new InvalidOperationException("ClaimTime 必须是 HH:mm。");
        if (config.MaxAttempts != 5) throw new InvalidOperationException("MaxAttempts 必须保持为 5。");
        if (config.CheckIntervalSeconds < 10) throw new InvalidOperationException("CheckIntervalSeconds 不能小于 10。");
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
    public int CheckIntervalSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 5;
    public int LaunchWaitSeconds { get; set; } = 20;
    public int ProfileX { get; set; } = 44;
    public int ProfileBottomOffset { get; set; } = 35;
    public int ClaimClickX { get; set; } = 92;
    public int ClaimStatusX { get; set; } = 50;
    public int ClaimBottomOffset { get; set; } = 432;
}
internal sealed class State { public DateOnly? SuccessDate { get; set; } }

internal static class Native
{
    internal const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    internal const uint DESKTOP_SWITCHDESKTOP = 0x0100, GENERIC_READ = 0x80000000;
    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetUserObjectInformation(IntPtr hObj, int index, IntPtr info, uint length, out uint needed);
    internal static string GetWindowText(IntPtr hwnd) { var b = new System.Text.StringBuilder(512); GetWindowText(hwnd, b, b.Capacity); return b.ToString(); }
    internal static string GetClassName(IntPtr hwnd) { var b = new System.Text.StringBuilder(512); GetClassName(hwnd, b, b.Capacity); return b.ToString(); }
    internal static string GetDesktopName(IntPtr desktop)
    {
        GetUserObjectInformation(desktop, 2, IntPtr.Zero, 0, out uint needed);
        var ptr = Marshal.AllocHGlobal((int)needed);
        try { return GetUserObjectInformation(desktop, 2, ptr, needed, out _) ? Marshal.PtrToStringUni(ptr) ?? "" : ""; }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
