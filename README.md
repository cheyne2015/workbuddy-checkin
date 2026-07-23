# WorkBuddy 自动领取

运行 `build.ps1` 生成 `release\WorkBuddyAutoClaim.exe`，再双击 `install.cmd`。它会为当前用户创建登录自启动任务并立即启动后台守护进程；默认每天 `00:00` 执行一次。

当天确认成功后，进程会休眠到下一个 `00:00`，不会继续轮询。只有到点后遇到锁屏、睡眠或领取失败，才每 60 秒重新尝试。每轮最多连续尝试 5 次；成功和五次失败都会通过右下角通知提示。

## 领取策略

正常领取不依赖 Buddy 加油站卡片颜色、固定按钮位置或真实鼠标：

1. 点击左下个人中心入口，并在后台截图中同时确认“积分余额”与“设置 / 外观 / 浅色 / 深色”等个人中心文字。
2. 读取并记录积分余额的数字指纹。若当前版本把一个很小的数字（例如 `0`）绘制得 Windows OCR 无法转写，工具会改为记录同一数字区域的本机视觉指纹；点击前必须连续稳定采样，点击后也必须连续两帧确认同一变化，绝不猜测余额数值。
3. 优先寻找并点击“立即领取”。
4. 若没有“立即领取”，才依次点击“签到领积分”“去签到”“签到”等入口文字；入口本身不算领取成功。
5. 入口后，识别 Buddy 加油站弹层中新增的“立即领取”。弹层不承担余额读取：余额始终以个人中心入口前的稳定记录为基线；点击最终按钮后重新打开个人中心、再比较余额。只有余额变化，才报告领取成功并写入当天成功记录。

截图完全在本机由 Windows OCR 处理，不会上传。由工具启动的 WorkBuddy 会在结束后关闭；原本前台的窗口保持前台，原本后台/最小化的窗口会恢复最小化。

## 配置与检查

`release\config.json` 首次运行时由 `config.example.json` 创建。常用字段：

- `ClaimTime`：每日领取时间，默认 `00:00`。
- `RetryIntervalSeconds`：到点后失败或锁屏的重试间隔，默认 `60`。
- `ImmediateClaimKeywords`：最终领取按钮文字，默认 `立即领取`。
- `CheckInKeywords`：进入领取流程的入口文字，默认 `签到领积分`、`去签到`、`签到`。
- `ClaimActionExclusions`：排除相似但不是领取动作的文字，默认排除“体验版”。
- `BalanceAnchorDriftPixels` 与 `VisualBalance*`：个人中心余额锚点和视觉余额采样的防抖容差；正常无需修改。
- `PopupCard*`：Buddy 加油站弹层的 OCR 放大裁剪范围；仅在 WorkBuddy 更新了弹层尺寸后校准。
- `BalanceValue*` 与 `ClaimAction*Pixels`：余额和按钮相对于“积分余额”文字的容差。界面改版后可按诊断截图调整。
- `ProfileX`、`ProfileBottomOffset`：左下个人中心入口位置；领取按钮位置始终由 OCR 动态决定。

日志、当天成功记录与点击前/后的诊断截图都在 `%LOCALAPPDATA%\WorkBuddyAutoClaim\`。

这些命令均不会点击领取：

```powershell
release\WorkBuddyAutoClaim.exe --self-test
release\WorkBuddyAutoClaim.exe --test-personal-center
release\WorkBuddyAutoClaim.exe --verify-claim-ocr "C:\path\to\screenshot.png"
```

`--test-personal-center` 会打开个人中心、验证余额识别并保存截图；`--verify-claim-ocr` 只检查已有截图的余额与领取文字。

## 上游参考

本项目是独立实现，不是 `GitOfUser/workbuddy-checkin` 的 Fork，也不与它共享提交历史。
`GitOfUser/workbuddy-checkin` 仅作为流程参考：账户入口和领取步骤的思路曾被参考；固定屏幕坐标、真实鼠标移动和其余实现没有用于本工具的正常领取路径。
