# WorkBuddy 自动领取

在 Windows 后台按计划领取 WorkBuddy 积分的本地工具。它复用已经登录的 WorkBuddy 会话，通过个人中心读取“积分余额”并核验结果；不会保存账号密码，也不会上传截图或 OCR 文字。

## 它会做什么

- 默认每天 `00:00` 尝试领取一次；时间可在 `config.json` 的 `ClaimTime` 中修改。
- 电脑锁屏、睡眠或桌面暂不可操作时，等恢复后每 `60` 秒重试；当天最多尝试 `5` 次。
- 成功、确认“今日已领取”或达到当天失败上限后，守护进程休眠到下一天，不会全天轮询或反复点击。
- WorkBuddy 未运行时会后台启动；由工具启动的 WorkBuddy 会在流程结束后关闭。原本在前台运行的窗口保持前台，原本最小化的窗口会恢复为最小化。
- 成功、今日已领取、失败都会发送 Windows 通知中心通知，并保留三天。

## 领取与核验逻辑

每次尝试严格按以下顺序执行：

1. 打开左下角个人中心。
2. 找到并记录“积分余额”。
3. 优先识别并点击“立即领取”。
4. 若已出现“今日已领”或“已领取”等状态，判定为当天已领取。
5. 没有“立即领取”时，识别并点击“签到领积分”“去签到”或“签到”；若随后出现“立即领取”，再点击它。
6. 点击“立即领取”后重新回到个人中心：余额变化才判定为领取成功；余额未变化且看到已领取状态才判定为“今日已领取”。其余情况报告失败，不猜测成功。

按钮与余额均由 OCR 动态识别，不依赖固定领取按钮坐标、Buddy 加油站期数或界面颜色。因此 WorkBuddy 的浅色/深色模式和普通布局变化都有适配空间。

## 通知内容

通知正文将状态与余额放在同一行，避免 Windows 通知中心折叠后遗漏余额：

- `领取成功 · 积分余额：领取前 → 领取后`
- `今日已领取 · 当前余额：余额`
- `领取失败 · 最后读取余额：余额`

如果 Windows 禁止应用通知，工具会写入日志并退回为临时托盘气泡；请在 Windows 设置中允许 WorkBuddy Auto Claim 通知。

## 安装与启动

### 前提

- Windows 10 版本 19041 或更高。
- 已安装并登录 WorkBuddy。
- 已安装 .NET 8 Desktop Runtime（如果双击程序没有任何反应，先安装它）。

### 首次安装

1. 打开 PowerShell，进入项目目录并构建：

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
   ```

2. 双击 `install.cmd`，或运行：

   ```powershell
   .\install.cmd
   ```

这会为当前用户创建开机自启任务，并启动后台守护。首次运行会在 `release\` 内从 `config.example.json` 创建 `config.json`。

要删除开机自启任务，双击 `uninstall.cmd`。它不会强制结束已经运行的守护进程；当前进程会在退出登录或下次重启后停止。

## 配置

编辑 `release\config.json`，修改后重新启动守护进程才会生效。

常用项如下：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `WorkBuddyPath` | `D:\Program Files\WorkBuddy\WorkBuddy.exe` | WorkBuddy 程序路径 |
| `ClaimTime` | `00:00` | 每日领取时间，格式 `HH:mm` |
| `RetryIntervalSeconds` | `60` | 锁屏、睡眠恢复或失败后的重试等待秒数 |
| `MaxAttempts` | `5` | 每日自动领取的最多尝试次数 |
| `LaunchWaitSeconds` | `20` | 启动 WorkBuddy 后的等待秒数 |
| `CardReadyTimeoutSeconds` | `30` | 等待个人中心余额区域加载的最长秒数 |
| `ImmediateClaimKeywords` | `立即领取` | 最终领取按钮的识别文字 |
| `CheckInKeywords` | `签到领积分`、`去签到`、`签到` | 进入领取流程的入口文字 |

其余 `Balance*`、`VisualBalance*`、`Profile*` 配置用于 OCR 兼容和截图校准。正常使用无需修改；只有 WorkBuddy 大版本改版、日志和诊断截图显示定位失败时才调整。

## 测试与常用命令

所有命令均从 `release\` 目录运行：

```powershell
cd .\release

# 运行内置回归检查；不会领取
.\WorkBuddyAutoClaim.exe --self-test

# 读取当前余额并发送一条真实 Windows 通知；不会点击签到或领取
.\WorkBuddyAutoClaim.exe --test-notification

# 打开个人中心、验证“积分余额”并保存截图；不会领取
.\WorkBuddyAutoClaim.exe --test-personal-center

# 对已有截图检查余额和领取文字；不会控制 WorkBuddy
.\WorkBuddyAutoClaim.exe --verify-claim-ocr "C:\path\to\screenshot.png"
```

以下命令会执行一次真实领取流程，适合在需要人工验证时使用：

```powershell
.\WorkBuddyAutoClaim.exe --manual-test
```

手动测试只尝试一次，不会写入当天的自动领取成功状态；失败后会停止并等待处理，后台守护随后恢复。

## 日志与排错

工具的运行数据都在：

```text
%LOCALAPPDATA%\WorkBuddyAutoClaim\
```

重点文件：

- `workbuddy-auto-claim.log`：运行、OCR、通知与失败原因。
- `state.json`：当天自动领取的成功或终止失败状态。
- `workbuddy-*.png`：领取前后、个人中心识别失败等诊断截图。
- `workbuddy-personal-center-ocr-failure.txt`：识别失败时保存的 OCR 原文。

常见检查顺序：

1. 运行 `--test-notification`，确认通知中心能看到“当前余额”。
2. 运行 `--test-personal-center`，确认个人中心截图中可见“积分余额”。
3. 若每日未运行，检查 `workbuddy-auto-claim.log` 中是否有“桌面已锁定”“未发现积分余额”或“通知中心 Toast 被 Windows 阻止”。
4. 发生界面改版时，保留对应诊断截图和日志，再调整 OCR 配置或更新工具。

## 项目关系

本项目是独立仓库实现，不是 `GitOfUser/workbuddy-checkin` 的 Fork，也不共享提交历史。早期仅参考了其流程思路；本工具的后台行为、OCR 校验、通知和维护均独立实现。
