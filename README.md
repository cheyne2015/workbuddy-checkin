# WorkBuddy 自动领取

1. 运行 `build.ps1` 生成 `release\WorkBuddyAutoClaim.exe`。
2. 双击 `install.cmd`，工具会创建当前用户的登录自启任务，并立即开始后台守护。
3. 默认每天 00:00 后领取；睡眠或锁屏期间不操作，解锁/恢复后自动继续。

所有成功与失败均通过右下角通知提示。领取后 WorkBuddy 会自动退出。日志和当天成功记录位于 `%LOCALAPPDATA%\WorkBuddyAutoClaim\`。

`release\config.json` 首次运行时会从 `config.example.json` 创建；其中四个坐标参数用于适配 WorkBuddy 的账户菜单布局。请先运行 `WorkBuddyAutoClaim.exe --dry-run` 检查是否能识别窗口；不要在当天已领的界面上直接运行 `--run-now`。
