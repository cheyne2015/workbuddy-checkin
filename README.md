# WorkBuddy 自动领取

1. 运行 `build.ps1` 生成 `release\WorkBuddyAutoClaim.exe`。
2. 双击 `install.cmd`，工具会创建当前用户的登录自启任务，并立即开始后台守护。
3. 默认每天 00:00 后领取；未到时间和当天领取成功后都会休眠至下一个领取时间。只有到点后的锁屏或领取失败，才会每 60 秒重试；睡眠或锁屏期间不操作，解锁/恢复后自动继续。

所有成功与失败均通过右下角通知提示。由工具启动的 WorkBuddy 会在流程结束后自动退出；原本在前台的窗口保持前台，原本在后台的窗口会恢复最小化。日志和当天成功记录位于 `%LOCALAPPDATA%\WorkBuddyAutoClaim\`。

工具统一通过左下个人菜单内的“Buddy 加油站”领取；菜单会显示“今日已领”和积分余额。点击后若卡片暂时隐藏，工具会重新打开菜单核验“今日已领”；若状态文字未刷新，再核对积分余额显示区域是否变化。两项都未变化才会判为失败。每次实际点击都会在日志目录保存点击前后的窗口截图，便于排查失败。

`release\config.json` 首次运行时会从 `config.example.json` 创建；领取卡片使用动态定位，不依赖固定的屏幕分辨率或窗口尺寸。请先运行 `WorkBuddyAutoClaim.exe --dry-run` 检查是否能识别窗口。

`RetryIntervalSeconds` 默认是 `60`，只控制到点后的锁屏或失败重试间隔；当天领取成功后不会继续按该间隔轮询。

## 上游复用与验证

本项目以 `GitOfUser/workbuddy-checkin` 的最新 `cb9f5e2` 提交为上游参考：复用其窗口定位、账户菜单、领取按钮的操作顺序，并保留该仓库为 Git 远端 `upstream`。它的固定 1920×1080 坐标仅作校准参考，不能直接用于本机不同窗口尺寸。

`WorkBuddyAutoClaim.exe --verify-layout` 不会点击、不领取，只会使用 `PrintWindow` 在窗口被其他应用遮挡时抓取 WorkBuddy 内容，输出到 `%LOCALAPPDATA%\WorkBuddyAutoClaim\workbuddy-background-capture.png`。只有这项验证通过，后台领取的状态识别才会启用。

`WorkBuddyAutoClaim.exe --test-buddy-card` 会读取真实 WorkBuddy 窗口；若需要会打开左下个人菜单，识别 Buddy 加油站卡片并保存 `%LOCALAPPDATA%\WorkBuddyAutoClaim\workbuddy-buddy-card-test.png`；不点击“立即领取”。

`WorkBuddyAutoClaim.exe --verify-card <截图路径>` 只校验保存的截图是否能识别领取卡片和“可领取/今日已领”状态；不启动或控制 WorkBuddy。

`WorkBuddyAutoClaim.exe --test-menu` 只测试后台点击账户菜单：点击一次、保存 `workbuddy-menu-test.png`，再点击一次还原；不会领取或退出 WorkBuddy。

`WorkBuddyAutoClaim.exe --test-personal-center` 只打开个人中心并保存 `workbuddy-personal-center-test.png`，不会点击“立即领取”。
