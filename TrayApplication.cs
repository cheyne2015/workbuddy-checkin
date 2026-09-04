using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WorkBuddyAutoClaim;

internal static class TrayApplication
{
    internal static int RunDaemon(Config config)
    {
        ApplicationConfiguration.Initialize();
        using var context = new TrayDaemonContext(config, smokeTest: false, screenshotPath: null);
        Application.Run(context);
        return context.ExitCode;
    }

    internal static int RunSmokeTest(string? screenshotPath)
    {
        ApplicationConfiguration.Initialize();
        using var context = new TrayDaemonContext(new Config(), smokeTest: true, screenshotPath);
        Application.Run(context);
        return context.ExitCode;
    }
}

internal sealed class TrayDaemonContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly DashboardForm _dashboard;
    private readonly ToolStripMenuItem _startupItem;
    private readonly AutoResetEvent _configChanged = new(false);
    private readonly System.Windows.Forms.Timer _lifecycleTimer = new() { Interval = 500 };
    private readonly Task<int>? _schedulerTask;
    private int _retryInProgress;
    private int _startupOperationInProgress;
    private Program.StartupTaskState _startupState = Program.StartupTaskState.Unavailable;

    internal int ExitCode { get; private set; }

    internal TrayDaemonContext(Config initialConfig, bool smokeTest, string? screenshotPath)
    {
        _icon = CreateTrayIcon();
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开守护面板", null, (_, _) => ShowDashboard());
        menu.Items.Add("重试领取", null, (_, _) => StartRetry());
        menu.Items.Add(new ToolStripSeparator());
        _startupItem = new ToolStripMenuItem("开机启动") { CheckOnClick = false };
        _startupItem.Click += (_, _) => ToggleStartup();
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出守护", null, (_, _) => ExitDaemon());
        menu.Opening += (_, _) => BeginStartupStateRefresh();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "WorkBuddy 自动领取守护",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowDashboard();
        };

        _dashboard = new DashboardForm(StartRetry, () => Volatile.Read(ref _retryInProgress) != 0,
            () => _configChanged.Set());
        _ = _dashboard.Handle;
        BeginStartupStateRefresh();

        if (smokeTest)
        {
            var smokeTimer = new System.Windows.Forms.Timer { Interval = 600 };
            smokeTimer.Tick += (_, _) =>
            {
                smokeTimer.Stop();
                try
                {
                    ShowDashboard();
                    if (!_notifyIcon.Visible || !_dashboard.IsHandleCreated || _dashboard.Width < 500 || _dashboard.Height < 500)
                        throw new InvalidOperationException("托盘图标或守护面板未正确创建。");
                    var menuLabels = _notifyIcon.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>()
                        .Select(item => item.Text ?? string.Empty).ToArray();
                    if (!menuLabels.Contains("打开守护面板") || !menuLabels.Contains("重试领取") ||
                        !menuLabels.Any(label => label.StartsWith("开机启动", StringComparison.Ordinal)) || !menuLabels.Contains("退出守护"))
                        throw new InvalidOperationException("托盘右键菜单缺少打开、重试、开机启动或退出命令。");
                    _dashboard.ValidateSmoke();
                    if (!string.IsNullOrWhiteSpace(screenshotPath))
                    {
                        var fullPath = Path.GetFullPath(screenshotPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                        _dashboard.SaveSmokeScreenshots(fullPath);
                    }
                    ExitCode = 0;
                }
                catch
                {
                    ExitCode = 1;
                }
                finally
                {
                    _notifyIcon.Visible = false;
                    _dashboard.AllowClose();
                    _dashboard.Close();
                    ExitThread();
                    smokeTimer.Dispose();
                }
            };
            smokeTimer.Start();
            return;
        }

        _schedulerTask = Task.Run(() => Program.RunDaemon(initialConfig, _configChanged));
        _lifecycleTimer.Tick += (_, _) =>
        {
            if (!_schedulerTask.IsCompleted) return;
            try { ExitCode = _schedulerTask.GetAwaiter().GetResult(); }
            catch { ExitCode = 1; }
            _notifyIcon.Visible = false;
            _dashboard.AllowClose();
            _dashboard.Close();
            ExitThread();
        };
        _lifecycleTimer.Start();
    }

    private void ShowDashboard()
    {
        _dashboard.RefreshFromDisk();
        if (!_dashboard.Visible) _dashboard.Show();
        if (_dashboard.WindowState == FormWindowState.Minimized) _dashboard.WindowState = FormWindowState.Normal;
        _dashboard.BringToFront();
        _dashboard.Activate();
    }

    private void StartRetry()
    {
        if (Interlocked.CompareExchange(ref _retryInProgress, 1, 0) != 0)
        {
            ShowDashboard();
            return;
        }
        ShowDashboard();
        _dashboard.SetRetryRunning(true);
        _ = Task.Run(Program.RunManualRetryFromTray).ContinueWith(task =>
        {
            Interlocked.Exchange(ref _retryInProgress, 0);
            if (_dashboard.IsDisposed) return;
            try
            {
                _dashboard.BeginInvoke(() =>
                {
                    _dashboard.SetRetryRunning(false);
                    _dashboard.RefreshFromDisk();
                });
            }
            catch { }
        }, TaskScheduler.Default);
    }

    private void ExitDaemon()
    {
        var status = Program.LoadRunStatus();
        if (Volatile.Read(ref _retryInProgress) != 0 ||
            (status?.Outcome == "Running" && DateTimeOffset.Now - status.UpdatedAt < TimeSpan.FromMinutes(15)))
        {
            _notifyIcon.ShowBalloonTip(5000, "WorkBuddy 自动领取守护",
                "领取正在执行，为避免中途打断，请完成后再退出。", ToolTipIcon.Info);
            ShowDashboard();
            return;
        }
        _notifyIcon.Visible = false;
        _dashboard.AllowClose();
        _dashboard.Close();
        ExitThread();
    }

    private void BeginStartupStateRefresh()
    {
        if (Interlocked.CompareExchange(ref _startupOperationInProgress, 1, 0) != 0) return;
        _startupItem.Enabled = false;
        _startupItem.Text = "开机启动（检查中…）";
        _ = Task.Run(Program.GetStartupTaskState).ContinueWith(task =>
        {
            try
            {
                _dashboard.BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref _startupOperationInProgress, 0);
                    ApplyStartupMenuState(task.Status == TaskStatus.RanToCompletion
                        ? task.Result
                        : Program.StartupTaskState.Unavailable);
                });
            }
            catch { Interlocked.Exchange(ref _startupOperationInProgress, 0); }
        }, TaskScheduler.Default);
    }

    private void ApplyStartupMenuState(Program.StartupTaskState state)
    {
        _startupState = state;
        _startupItem.Checked = state == Program.StartupTaskState.Enabled;
        _startupItem.Enabled = state != Program.StartupTaskState.Unavailable;
        _startupItem.Text = state == Program.StartupTaskState.Unavailable ? "开机启动（状态不可用）" : "开机启动";
    }

    private void ToggleStartup()
    {
        if (Interlocked.CompareExchange(ref _startupOperationInProgress, 1, 0) != 0) return;
        var enable = _startupState != Program.StartupTaskState.Enabled;
        _startupItem.Enabled = false;
        _startupItem.Text = enable ? "开机启动（正在启用…）" : "开机启动（正在禁用…）";
        _ = Task.Run(() => Program.SetStartupEnabled(enable)).ContinueWith(task =>
        {
            try
            {
                _dashboard.BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref _startupOperationInProgress, 0);
                    if (task.IsFaulted)
                    {
                        ApplyStartupMenuState(Program.StartupTaskState.Unavailable);
                        _notifyIcon.ShowBalloonTip(7000, "开机启动设置失败",
                            task.Exception?.GetBaseException().Message ?? "未知错误", ToolTipIcon.Error);
                        return;
                    }
                    ApplyStartupMenuState(enable ? Program.StartupTaskState.Enabled : Program.StartupTaskState.Disabled);
                    _notifyIcon.ShowBalloonTip(5000, "WorkBuddy 自动领取守护",
                        enable ? "已开启开机启动。" : "已关闭开机启动，当前守护仍会继续运行。", ToolTipIcon.Info);
                });
            }
            catch { Interlocked.Exchange(ref _startupOperationInProgress, 0); }
        }, TaskScheduler.Default);
    }

    protected override void ExitThreadCore()
    {
        _lifecycleTimer.Stop();
        _notifyIcon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifecycleTimer.Dispose();
            _notifyIcon.Dispose();
            _dashboard.Dispose();
            _configChanged.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(20, 168, 118));
            graphics.FillEllipse(background, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var foreground = new SolidBrush(Color.White);
            var text = "W";
            var size = graphics.MeasureString(text, font);
            graphics.DrawString(text, font, foreground, (32 - size.Width) / 2, (32 - size.Height) / 2 - 1);
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

internal sealed class DashboardForm : Form
{
    private readonly Action _retry;
    private readonly Func<bool> _retryRunning;
    private readonly Action _configSaved;
    private readonly Label _statusTitle = new();
    private readonly Label _balanceValue = new();
    private readonly Label _updatedValue = new();
    private readonly Label _nextRunValue = new();
    private readonly Label _detailValue = new();
    private readonly Button _retryButton = new();
    private readonly TextBox _workBuddyPath = new();
    private readonly DateTimePicker _claimTime = new();
    private readonly NumericUpDown _automaticAttempts = new();
    private readonly NumericUpDown _manualAttempts = new();
    private readonly NumericUpDown _retryInterval = new();
    private readonly NumericUpDown _launchWait = new();
    private readonly NumericUpDown _cardWait = new();
    private readonly Label _saveMessage = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(16, 7) };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1500 };
    private bool _allowClose;
    private bool _darkMode;

    internal DashboardForm(Action retry, Func<bool> retryRunning, Action configSaved)
    {
        _retry = retry;
        _retryRunning = retryRunning;
        _configSaved = configSaved;
        Text = "WorkBuddy 自动领取守护";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 610);
        Size = new Size(620, 680);
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        _tabs.TabPages.Add(BuildOverviewTab());
        _tabs.TabPages.Add(BuildSettingsTab());
        Controls.Add(_tabs);

        FormClosing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        };
        Shown += (_, _) => RefreshFromDisk();
        _refreshTimer.Tick += (_, _) => RefreshFromDisk();
        _refreshTimer.Start();
        ApplyTheme();
        LoadSettings();
    }

    private TabPage BuildOverviewTab()
    {
        var page = new TabPage("概览") { Padding = new Padding(22) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            AutoScroll = true
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        _statusTitle.Font = new Font(Font.FontFamily, 15, FontStyle.Bold);
        _statusTitle.Text = "暂无领取记录";
        _statusTitle.Dock = DockStyle.Fill;
        _balanceValue.Font = new Font("Segoe UI Variable Display", 30, FontStyle.Bold);
        _balanceValue.Text = "--";
        _balanceValue.Dock = DockStyle.Fill;
        _updatedValue.Dock = DockStyle.Fill;
        _nextRunValue.Dock = DockStyle.Fill;
        _detailValue.Dock = DockStyle.Fill;
        _detailValue.AutoEllipsis = true;
        _detailValue.Padding = new Padding(0, 10, 0, 0);

        _retryButton.Text = "↻  重试领取";
        _retryButton.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
        _retryButton.Height = 44;
        _retryButton.Width = 190;
        _retryButton.Anchor = AnchorStyles.Left;
        _retryButton.FlatStyle = FlatStyle.Flat;
        _retryButton.Click += (_, _) => _retry();

        var hint = new Label
        {
            Text = "关闭此窗口只会隐藏到右下角，守护仍会继续运行。",
            Dock = DockStyle.Fill,
            AutoSize = false
        };
        layout.Controls.Add(_statusTitle);
        layout.Controls.Add(_balanceValue);
        layout.Controls.Add(_updatedValue);
        layout.Controls.Add(_nextRunValue);
        layout.Controls.Add(new Label { Text = "最近详情", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) });
        layout.Controls.Add(_detailValue);
        layout.Controls.Add(_retryButton);
        layout.Controls.Add(hint);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("设置") { Padding = new Padding(18) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 9,
            AutoScroll = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        for (var i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _workBuddyPath.Dock = DockStyle.Fill;
        var browse = new Button { Text = "浏览…", Dock = DockStyle.Fill };
        browse.Click += (_, _) => BrowseForWorkBuddy();
        ConfigureTimePicker();
        ConfigureNumeric(_automaticAttempts, 1, 10, 5);
        ConfigureNumeric(_manualAttempts, 1, 10, 1);
        ConfigureNumeric(_retryInterval, 10, 3600, 60);
        ConfigureNumeric(_launchWait, 5, 120, 20);
        ConfigureNumeric(_cardWait, 5, 120, 30);

        AddSettingRow(layout, 0, "WorkBuddy 程序", _workBuddyPath, browse);
        AddSettingRow(layout, 1, "每天领取时间", _claimTime, new Label { Text = "HH:mm", TextAlign = ContentAlignment.MiddleCenter });
        AddSettingRow(layout, 2, "自动尝试次数", _automaticAttempts, new Label { Text = "次", TextAlign = ContentAlignment.MiddleCenter });
        AddSettingRow(layout, 3, "手动尝试次数", _manualAttempts, new Label { Text = "次", TextAlign = ContentAlignment.MiddleCenter });
        AddSettingRow(layout, 4, "失败重试间隔", _retryInterval, new Label { Text = "秒", TextAlign = ContentAlignment.MiddleCenter });
        AddSettingRow(layout, 5, "程序启动等待", _launchWait, new Label { Text = "秒", TextAlign = ContentAlignment.MiddleCenter });
        AddSettingRow(layout, 6, "领取界面等待", _cardWait, new Label { Text = "秒", TextAlign = ContentAlignment.MiddleCenter });

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        var save = new Button { Text = "保存并立即应用", AutoSize = true, Height = 36 };
        save.Click += (_, _) => SaveSettings();
        var advanced = new Button { Text = "打开高级配置", AutoSize = true, Height = 36 };
        advanced.Click += (_, _) => OpenAdvancedConfig();
        actions.Controls.Add(save);
        actions.Controls.Add(advanced);
        layout.Controls.Add(actions, 0, 7);
        layout.SetColumnSpan(actions, 3);
        _saveMessage.Dock = DockStyle.Fill;
        _saveMessage.AutoSize = false;
        _saveMessage.Padding = new Padding(0, 10, 0, 0);
        layout.Controls.Add(_saveMessage, 0, 8);
        layout.SetColumnSpan(_saveMessage, 3);
        page.Controls.Add(layout);
        return page;
    }

    private static void AddSettingRow(TableLayoutPanel layout, int row, string label, Control editor, Control suffix)
    {
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        editor.Margin = new Padding(3, 8, 3, 8);
        suffix.Margin = new Padding(3, 8, 3, 8);
        layout.Controls.Add(editor, 1, row);
        layout.Controls.Add(suffix, 2, row);
    }

    private void ConfigureTimePicker()
    {
        _claimTime.Format = DateTimePickerFormat.Custom;
        _claimTime.CustomFormat = "HH:mm";
        _claimTime.ShowUpDown = true;
        _claimTime.Dock = DockStyle.Left;
        _claimTime.Width = 120;
    }

    private static void ConfigureNumeric(NumericUpDown control, decimal minimum, decimal maximum, decimal value)
    {
        control.Minimum = minimum;
        control.Maximum = maximum;
        control.Value = value;
        control.Width = 120;
        control.Dock = DockStyle.Left;
    }

    internal void RefreshFromDisk()
    {
        var darkMode = IsDarkMode();
        if (darkMode != _darkMode) ApplyTheme();
        var status = Program.LoadRunStatus();
        var view = DashboardStatusView.From(status);
        _statusTitle.Text = view.Title;
        _statusTitle.ForeColor = status?.Outcome switch
        {
            "Failed" => Color.FromArgb(232, 93, 88),
            "Running" or "Queued" => Color.FromArgb(226, 168, 51),
            _ => Color.FromArgb(20, 168, 118)
        };
        _balanceValue.Text = view.Balance;
        _updatedValue.Text = "更新时间：" + view.UpdatedAt;
        _detailValue.Text = view.Detail;
        try
        {
            var config = Program.LoadConfig();
            _nextRunValue.Text = "下次自动领取：" + CalculateNextRun(config, status).ToString("yyyy-MM-dd HH:mm");
        }
        catch (Exception ex)
        {
            _nextRunValue.Text = "配置错误：" + ex.Message;
        }
        var busy = _retryRunning() || status?.Outcome is "Running" or "Queued";
        _retryButton.Enabled = !busy;
        _retryButton.Text = busy ? "…  正在领取" : "↻  重试领取";
    }

    internal void SetRetryRunning(bool running)
    {
        _retryButton.Enabled = !running;
        _retryButton.Text = running ? "…  正在领取" : "↻  重试领取";
    }

    internal void SaveSmokeScreenshots(string overviewPath)
    {
        _tabs.SelectedIndex = 0;
        Application.DoEvents();
        SaveWindowBitmap(overviewPath);
        _tabs.SelectedIndex = 1;
        Application.DoEvents();
        var settingsPath = Path.Combine(Path.GetDirectoryName(overviewPath)!,
            Path.GetFileNameWithoutExtension(overviewPath) + "-settings.png");
        SaveWindowBitmap(settingsPath);
        _tabs.SelectedIndex = 0;
    }

    internal void ValidateSmoke()
    {
        if (_tabs.TabPages.Count != 2 || _tabs.TabPages[0].Text != "概览" || _tabs.TabPages[1].Text != "设置")
            throw new InvalidOperationException("守护面板必须包含概览和设置页。");
        if (!_retryButton.Text.Contains("重试领取", StringComparison.Ordinal) ||
            _automaticAttempts.Minimum != 1 || _manualAttempts.Minimum != 1)
            throw new InvalidOperationException("守护面板缺少可用的重试领取或次数配置控件。");
    }

    private void SaveWindowBitmap(string path)
    {
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void LoadSettings()
    {
        try
        {
            var config = Program.LoadConfig();
            _workBuddyPath.Text = config.WorkBuddyPath;
            if (TimeSpan.TryParseExact(config.ClaimTime, @"hh\:mm", null, out var time))
                _claimTime.Value = DateTime.Today.Add(time);
            _automaticAttempts.Value = Math.Clamp(config.MaxAttempts, (int)_automaticAttempts.Minimum, (int)_automaticAttempts.Maximum);
            _manualAttempts.Value = Math.Clamp(config.ManualMaxAttempts, (int)_manualAttempts.Minimum, (int)_manualAttempts.Maximum);
            _retryInterval.Value = Math.Clamp(config.RetryIntervalSeconds, (int)_retryInterval.Minimum, (int)_retryInterval.Maximum);
            _launchWait.Value = Math.Clamp(config.LaunchWaitSeconds, (int)_launchWait.Minimum, (int)_launchWait.Maximum);
            _cardWait.Value = Math.Clamp(config.CardReadyTimeoutSeconds, (int)_cardWait.Minimum, (int)_cardWait.Maximum);
        }
        catch (Exception ex)
        {
            _saveMessage.Text = "读取配置失败：" + ex.Message;
        }
    }

    private void SaveSettings()
    {
        try
        {
            var config = Program.LoadConfig();
            config.WorkBuddyPath = _workBuddyPath.Text.Trim();
            config.ClaimTime = _claimTime.Value.ToString("HH:mm");
            config.MaxAttempts = Decimal.ToInt32(_automaticAttempts.Value);
            config.ManualMaxAttempts = Decimal.ToInt32(_manualAttempts.Value);
            config.RetryIntervalSeconds = Decimal.ToInt32(_retryInterval.Value);
            config.LaunchWaitSeconds = Decimal.ToInt32(_launchWait.Value);
            config.CardReadyTimeoutSeconds = Decimal.ToInt32(_cardWait.Value);
            Program.SaveConfig(config);
            _configSaved();
            _saveMessage.Text = $"已保存并应用 · {DateTime.Now:HH:mm:ss}";
            RefreshFromDisk();
        }
        catch (Exception ex)
        {
            _saveMessage.Text = "保存失败：" + ex.Message;
        }
    }

    private void BrowseForWorkBuddy()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "WorkBuddy (WorkBuddy.exe)|WorkBuddy.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
            InitialDirectory = Path.GetDirectoryName(_workBuddyPath.Text)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _workBuddyPath.Text = dialog.FileName;
    }

    internal static void OpenAdvancedConfig(string? pathOverride = null, Action<ProcessStartInfo>? startProcess = null)
    {
        var path = pathOverride ?? Program.ConfigFilePath;
        if (!File.Exists(path))
        {
            if (pathOverride is null) Program.LoadConfig();
            else throw new FileNotFoundException("找不到高级配置文件。", path);
        }

        startProcess ??= startInfo =>
        {
            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动配置文件编辑器。");
        };

        try
        {
            startProcess(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            var fallback = new ProcessStartInfo("notepad.exe")
            {
                UseShellExecute = false
            };
            fallback.ArgumentList.Add(path);
            startProcess(fallback);
        }
    }

    private static DateTime CalculateNextRun(Config config, RunStatus? status)
    {
        var time = TimeSpan.ParseExact(config.ClaimTime, @"hh\:mm", null);
        var now = DateTime.Now;
        var today = now.Date.Add(time);
        var hasTodayTerminalResult = status is not null && status.UpdatedAt.LocalDateTime.Date == now.Date &&
                                     status.Outcome is "Claimed" or "AlreadyClaimed";
        if (hasTodayTerminalResult || now >= today) return today.AddDays(1);
        return today;
    }

    private void ApplyTheme()
    {
        _darkMode = IsDarkMode();
        var background = _darkMode ? Color.FromArgb(30, 32, 36) : Color.FromArgb(247, 248, 250);
        var surface = _darkMode ? Color.FromArgb(39, 42, 47) : Color.White;
        var foreground = _darkMode ? Color.FromArgb(238, 240, 243) : Color.FromArgb(31, 35, 40);
        var muted = _darkMode ? Color.FromArgb(180, 185, 193) : Color.FromArgb(87, 96, 106);
        var accent = Color.FromArgb(20, 168, 118);
        BackColor = background;
        ForeColor = foreground;
        ApplyThemeToChildren(this, background, surface, foreground, muted, accent);
        _statusTitle.ForeColor = accent;
        _retryButton.BackColor = accent;
        _retryButton.ForeColor = Color.White;
        _retryButton.FlatAppearance.BorderColor = accent;
    }

    private static void ApplyThemeToChildren(Control parent, Color background, Color surface, Color foreground, Color muted, Color accent)
    {
        foreach (Control control in parent.Controls)
        {
            control.ForeColor = foreground;
            control.BackColor = control is TabPage or TextBox or NumericUpDown or DateTimePicker ? surface : background;
            if (control is Label label && !label.Font.Bold) label.ForeColor = muted;
            if (control is Button button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = _BorderColor(background, foreground);
                button.BackColor = surface;
            }
            ApplyThemeToChildren(control, background, surface, foreground, muted, accent);
        }
    }

    private static Color _BorderColor(Color background, Color foreground) =>
        Color.FromArgb(90, foreground.R, foreground.G, foreground.B);

    private static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    internal void AllowClose() => _allowClose = true;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refreshTimer.Dispose();
        base.Dispose(disposing);
    }
}
