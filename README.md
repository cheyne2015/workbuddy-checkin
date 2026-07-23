# WorkBuddy 自动领取

运行 `build.ps1` 生成 `release\WorkBuddyAutoClaim.exe`，再双击 `install.cmd`。它会为当前用户创建登录自启动任务并立即启动后台守护进程；默认每天 `00:00` 执行一次。

当天确认成功后，进程会休眠到下一个 `00:00`，不会继续轮询。只有到点后遇到锁屏、睡眠或领取失败，才每 60 秒重新尝试。每轮领取最多连续执行 5 次；成功和五次失败都通过右下角通知提示。

## 领取策略

正常领取不再依赖 Buddy 加油站的绿色卡片、按钮颜色或固定按钮坐标：

1. 以配置中的左下个人中心入口打开菜单；不会抢占前台焦点或移动真实鼠标。
2. 用 `PrintWindow` 取得 WorkBuddy 后台截图，并由本机 Windows OCR 识别文字。截图不会上传。
3. 先识别“积分余额”并记录其数字指纹；未找到这个锚点时拒绝点击，避免误操作。
4. 在余额附近的同一张个人中心卡片内，动态寻找 `签到领积分`、`立即领取`、`去签到`、`签到`、`领积分`、`领取`、`体验` 等文字。每个识别到的候选只点击一次。
5. 每点一次立即重新 OCR，比较本次点击前后的积分余额。余额数字变化即为领取成功；也会识别“今日已领”“本期已领”“领取成功”“签到成功”或 `+100` 作为已领取证据。

实际 WorkBuddy 有时会把奖励计入 Credits 而不立即改写“积分余额”。因此余额变化是最直接的成功证据，但不会是唯一条件；工具必须再读到明确的已领取文字或 `+100`，才会在余额未变的情况下报告成功，绝不会仅凭一次点击就报告成功。

由工具启动的 WorkBuddy 会在结束后关闭；原本前台的窗口保持前台，原本后台/最小化的窗口会恢复最小化。

## 配置与检查

`release\config.json` 首次运行时由 `config.example.json` 创建。可编辑：

- `ClaimTime`：每日领取时间，默认 `00:00`。
- `RetryIntervalSeconds`：仅用于到点后失败或锁屏的重试间隔，默认 `60`。
- `ClaimActionKeywords`：可领取按钮的 OCR 关键词；遇到版本更新时在此增加新文案即可，不要删除现有词。
- `ProfileX`、`ProfileBottomOffset`：左下个人中心入口的相对点击位置。只有这一个稳定入口使用配置坐标；领取按钮位置由 OCR 动态决定。

日志、当天成功记录及“点击前/后”诊断截图保存在 `%LOCALAPPDATA%\WorkBuddyAutoClaim\`。

这些命令均不会点击领取：

```powershell
release\WorkBuddyAutoClaim.exe --self-test
release\WorkBuddyAutoClaim.exe --test-personal-center
release\WorkBuddyAutoClaim.exe --verify-claim-ocr "C:\path\to\screenshot.png"
```

`--test-personal-center` 会打开个人中心、确认 OCR 能读到积分余额并保存截图，但不领取。`--verify-claim-ocr` 只验证已有截图中能否识别余额和领取/已领取文本。

## 上游参考

项目保留 `GitOfUser/workbuddy-checkin` 为 `upstream` 参考。它的账户入口和领取流程思路已被复用；其固定屏幕坐标没有用于本工具的正常领取路径。
