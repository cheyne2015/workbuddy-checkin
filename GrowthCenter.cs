using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkBuddyAutoClaim;

internal sealed record GrowthCenterResult(
    bool NeedsAttention,
    bool Idle,
    string Report,
    int CreditsGained = 0,
    int? Energy = null,
    int? StreakDays = null,
    bool AuthenticationRejected = false,
    bool Cancelled = false,
    IReadOnlyDictionary<string, string>? Modules = null);

internal static class WorkBuddyGrowthCenter
{
    internal static GrowthCenterResult Execute(
        WorkBuddyApiSession session,
        Config config,
        HttpMessageHandler? handler = null,
        Func<TimeSpan, bool>? wait = null,
        Func<bool>? cancellationRequested = null)
    {
        using var client = CreateClient(session, handler);
        var context = new GrowthContext(client, session.Endpoint, config,
            wait ?? (duration => { Thread.Sleep(duration); return false; }),
            cancellationRequested ?? (() => false));
        context.RunModule(context.RunTravel, GrowthModuleCatalog.Travel, "旅行");
        context.RunModule(context.RunTasks, GrowthModuleCatalog.Tasks, "任务");
        context.RunModule(context.RunMakeup, GrowthModuleCatalog.Makeup, "补登");
        context.RunModule(context.RunRedeem, GrowthModuleCatalog.Redeem, "连登兑换");
        context.RunModule(context.RunLottery, GrowthModuleCatalog.Lottery, "盲盒");
        context.RunModule(context.RunBuddyBoxes, GrowthModuleCatalog.Buddy, "Buddy 盲盒");
        context.RunModule(context.LoadSummary, GrowthModuleCatalog.Summary, "状态汇总");
        return context.Complete();
    }

    private static HttpClient CreateClient(WorkBuddyApiSession session, HttpMessageHandler? handler)
    {
        var client = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WorkBuddy");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-User-Id", session.UserId);
        if (!string.IsNullOrWhiteSpace(session.EnterpriseId))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Enterprise-Id", session.EnterpriseId);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Tenant-Id", session.EnterpriseId);
        }
        if (!string.IsNullOrWhiteSpace(session.Domain))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Domain", session.Domain);
        return client;
    }

    private sealed class GrowthContext(
        HttpClient client,
        Uri endpoint,
        Config config,
        Func<TimeSpan, bool> wait,
        Func<bool> cancellationRequested)
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly List<string> _parts = [];
        private readonly Dictionary<string, string> _modules = new(StringComparer.Ordinal);
        private int _credits;
        private int _failures;
        private int _hardFailures;
        private int _successes;
        private bool _authenticationRejected;
        private bool _stopAll;
        private bool _cancelled;
        private JsonNode? _streakBody;
        private bool _streakStale;
        private int? _energy;
        private int? _streakDays;

        internal void RunModule(Action action, string key, string label)
        {
            if (CheckCancellation())
            {
                _modules[key] = "已取消";
                return;
            }
            if (_stopAll)
            {
                _modules[key] = "因前序错误跳过";
                return;
            }
            var start = _parts.Count;
            try { action(); }
            catch (Exception ex) { ModuleException(label, ex); }
            var details = _parts.Skip(start).ToArray();
            _modules[key] = details.Length > 0
                ? string.Join("；", details)
                : _cancelled ? "已取消" : _stopAll ? "因前序错误停止" : "无可处理项";
        }

        internal void RunTravel()
        {
            var status = Get(Route(ApiRouteKeys.TravelStatus));
            if (StopForAuth(status)) return;
            if (status.NetworkFailed || status.BudgetOut)
            {
                _parts.Add(status.BudgetOut
                    ? "时间预算耗尽，成长中心跳过，下次自动重试"
                    : $"网络不可达，成长中心跳过（{Text(status.Body, "error") ?? ""}）");
                _failures++;
                _hardFailures++;
                _stopAll = true;
                return;
            }
            if (!status.IsSuccess)
            {
                NoteHttp(status, "查旅行状态");
                return;
            }

            var travel = Text(status.Body, "state");
            var dailyLimit = Bool(status.Body, "daily_limit_reached");
            var claimed = false;
            if (travel == "arrived")
            {
                var result = Post(Route(ApiRouteKeys.TravelClaim), new { record_id = Value(status.Body, "record_id") });
                if (StopForAuth(result)) return;
                var reward = Integer(result.Body, "reward_credit");
                if (result.IsSuccess && reward.HasValue)
                {
                    _credits += reward.Value;
                    _parts.Add($"领旅行礼物 +{reward.Value} 积分");
                    _successes++;
                    claimed = true;
                }
                else
                {
                    _parts.Add($"领旅行礼物失败：{MessageOrHttp(result)}");
                    _failures++;
                    if (result.IsHardFailure) _hardFailures++;
                }
                if (claimed) travel = "idle";
            }

            if (travel == "idle" && dailyLimit)
            {
                _parts.Add("今日旅行名额已用完");
            }
            else if (travel == "idle")
            {
                var locations = Get(Route(ApiRouteKeys.TravelConfig));
                if (StopForAuth(locations)) return;
                var first = Find(locations.Body, "locations") is JsonArray array && array.Count > 0
                    ? array[0] as JsonObject
                    : null;
                if (locations.IsSuccess && first is not null)
                {
                    var departed = Post(Route(ApiRouteKeys.TravelDepart), new { location_id = first["id"]?.DeepClone() });
                    if (StopForAuth(departed)) return;
                    if (departed.IsSuccess)
                    {
                        var location = Find(departed.Body, "location") as JsonObject;
                        var name = location?["name"]?.ToString() ?? "?";
                        var hours = Text(departed.Body, "duration_hours") ?? location?["duration_hours"]?.ToString() ?? "?";
                        _parts.Add($"派 Buddy 去{name}（{hours} 小时后回）");
                        _successes++;
                    }
                    else
                    {
                        _parts.Add($"派 Buddy 失败：{MessageOrHttp(departed)}");
                        _failures++;
                        if (departed.IsHardFailure) _hardFailures++;
                    }
                }
                else if (!locations.IsSuccess)
                {
                    NoteHttp(locations, "查旅行地点");
                }
            }
            else if (travel == "traveling")
            {
                var location = Find(status.Body, "location") as JsonObject;
                _parts.Add($"Buddy 旅行中（{location?["name"]?.ToString() ?? "?"}{FormatEta(Find(status.Body, "arrive_at"), Find(status.Body, "server_now"))}）");
            }
        }

        internal void RunTasks()
        {
            if (CannotContinue("任务领奖")) return;
            try
            {
                var response = Get(Route(ApiRouteKeys.Tasks));
                if (StopForAuth(response)) return;
                if (!response.IsSuccess)
                {
                    NoteHttp(response, "查任务列表");
                    return;
                }
                var tasks = Find(response.Body, "tasks") as JsonArray ?? [];
                var titles = tasks.OfType<JsonObject>()
                    .Where(task => !string.IsNullOrWhiteSpace(task["task_code"]?.ToString()))
                    .ToDictionary(task => task["task_code"]!.ToString(),
                        task => task["title"]?.ToString() ?? task["task_code"]!.ToString(), StringComparer.Ordinal);
                var pending = tasks.OfType<JsonObject>()
                    .Where(task => !Bool(task, "locked") && Text(task, "accept_status") == "not_accepted")
                    .Select(task => Text(task, "task_code"))
                    .Where(code => !string.IsNullOrWhiteSpace(code)).Cast<string>().ToArray();
                foreach (var batch in pending.Chunk(20))
                {
                    if (CannotContinue("剩余任务接单")) break;
                    var accepted = Post(Route(ApiRouteKeys.TasksAccept), new { task_codes = batch });
                    if (StopForAuth(accepted)) return;
                    var results = Find(accepted.Body, "results") as JsonArray;
                    if (results is null)
                    {
                        results = [];
                        foreach (var code in batch)
                            results.Add(new JsonObject
                            {
                                ["task_code"] = code,
                                ["status"] = accepted.IsSuccess ? "ok" : "error",
                                ["message"] = Text(accepted.Body, "msg")
                            });
                    }
                    foreach (var result in results.OfType<JsonObject>())
                    {
                        var code = Text(result, "task_code") ?? "?";
                        var title = titles.GetValueOrDefault(code, code);
                        if (Text(result, "status") == "error")
                        {
                            _parts.Add($"领取任务「{title}」失败：{Text(result, "message") ?? $"HTTP {accepted.StatusCode}"}");
                            _failures++;
                            if (accepted.IsHardFailure) _hardFailures++;
                        }
                        else
                        {
                            _parts.Add($"领取任务「{title}」（进度开始计）");
                            _successes++;
                        }
                    }
                }

                foreach (var task in tasks.OfType<JsonObject>())
                {
                    if (CannotContinue("剩余任务奖励")) break;
                    if (Bool(task, "locked") || Text(task, "accept_status") != "completed") continue;
                    var code = Text(task, "task_code");
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    var title = titles.GetValueOrDefault(code, code);
                    var claimed = Post(Route(ApiRouteKeys.TaskClaim, code), new { });
                    if (StopForAuth(claimed)) return;
                    if (claimed.IsSuccess && !Bool(claimed.Body, "already_claimed"))
                    {
                        var credit = Integer(claimed.Body, "credit") ?? Integer(task, "reward_credit") ?? 0;
                        var energy = Integer(claimed.Body, "energy") ?? Integer(task, "reward_energy") ?? 0;
                        _credits += credit;
                        _parts.Add($"领任务奖「{title}」+credit{credit}+energy{energy}");
                        _successes++;
                    }
                    else if (claimed.IsSuccess)
                    {
                        _parts.Add($"任务奖「{title}」已领过");
                    }
                    else
                    {
                        _parts.Add($"领任务奖「{title}」失败：{MessageOrHttp(claimed)}");
                        _failures++;
                        if (claimed.IsHardFailure) _hardFailures++;
                    }
                }
            }
            catch (Exception ex)
            {
                ModuleException("任务", ex);
            }
        }

        internal void RunMakeup()
        {
            if (CannotContinue("补登")) return;
            try
            {
                var streak = Get(Route(ApiRouteKeys.Streak));
                if (StopForAuth(streak)) return;
                if (!IsApiSuccess(streak))
                {
                    NoteHttp(streak, "查连登状态", required: true);
                    return;
                }
                _streakBody = streak.Body;
                var cardsNode = Find(streak.Body, "makeup_cards");
                var cards = cardsNode is JsonObject cardObject
                    ? Integer(cardObject, "balance") ?? 0
                    : ToInt(cardsNode);
                if (cards <= 0) return;

                var heatmap = Get(Route(ApiRouteKeys.Heatmap));
                if (StopForAuth(heatmap)) return;
                if (!IsApiSuccess(heatmap))
                {
                    NoteHttp(heatmap, "查补登日历", required: true);
                    return;
                }
                var dates = MakeupCandidates(streak.Body, heatmap.Body);
                var madeUp = 0;
                foreach (var date in dates.Take(Math.Min(cards, 1)))
                {
                    if (CannotContinue("剩余补登")) break;
                    var used = Post(Route(ApiRouteKeys.MakeupUse), new { target_date = date });
                    if (StopForAuth(used)) return;
                    if (IsApiSuccess(used))
                    {
                        cards--;
                        madeUp++;
                        _streakStale = true;
                        var remainingNode = Find(used.Body, "makeup_cards");
                        var remaining = remainingNode is JsonObject remainingObject
                            ? Integer(remainingObject, "balance") ?? cards
                            : ToInt(remainingNode, cards);
                        cards = Math.Max(0, remaining);
                        _parts.Add($"补登 {date}（剩 {cards} 张卡）");
                        _successes++;
                    }
                    else
                    {
                        var message = Text(used.Body, "msg") ?? string.Empty;
                        if (used.StatusCode == 400 && message.Trim().Equals("date is not broken, no makeup needed", StringComparison.OrdinalIgnoreCase))
                        {
                            _parts.Add($"{date} 已活跃或已补登，无需再次补登");
                            break;
                        }
                        _parts.Add($"补登 {date} 失败：{MessageOrHttp(used)}");
                        _failures++;
                        _hardFailures++;
                        break;
                    }
                }
                if (madeUp > 0 && dates.Count > madeUp && cards > 0)
                    _parts.Add($"另有 {dates.Count - madeUp} 天可补、剩 {cards} 张卡，下轮继续");
            }
            catch (Exception ex)
            {
                ModuleException("补登", ex);
            }
        }

        internal void RunRedeem()
        {
            if (CannotContinue("连登兑换")) return;
            try
            {
                var summary = Get(Route(ApiRouteKeys.RedeemSummary));
                if (StopForAuth(summary)) return;
                if (!summary.IsSuccess)
                {
                    NoteHttp(summary, "查连登兑换");
                    return;
                }
                (string Tier, string StatusKey, string Label, int Days)[] tiers =
                [
                    ("7d", "starter_status", "入门", 7),
                    ("14d", "advanced_status", "进阶", 14),
                    ("28d", "legendary_status", "巅峰", 28)
                ];
                foreach (var tier in tiers)
                {
                    if (CannotContinue("剩余连登兑换")) break;
                    var status = Text(summary.Body, tier.StatusKey);
                    if (string.IsNullOrWhiteSpace(status) || status is "claimed" or "locked") continue;
                    var redeemed = Post(Route(ApiRouteKeys.Redeem), new { tier = tier.Tier, client_token = ClientToken() });
                    if (IsUnknownTier(redeemed))
                        redeemed = Post(Route(ApiRouteKeys.Redeem), new { tier = tier.Days, client_token = ClientToken() });
                    if (IsTierLocked(redeemed))
                    {
                        _parts.Add($"连登兑换「{tier.Label}」未解锁（连登天数不足）");
                        continue;
                    }
                    if (StopForAuth(redeemed)) return;
                    if (redeemed.IsSuccess)
                    {
                        var credit = Integer(redeemed.Body, "credit_granted") ?? 0;
                        _credits += credit;
                        _parts.Add($"连登兑换「{tier.Label}」{RedeemReward(redeemed.Body, tier.Tier)}");
                        _successes++;
                    }
                    else
                    {
                        _parts.Add($"连登兑换「{tier.Label}」失败：{MessageOrHttp(redeemed)}");
                        _failures++;
                        if (redeemed.IsHardFailure) _hardFailures++;
                    }
                }
            }
            catch (Exception ex)
            {
                ModuleException("连登兑换", ex);
            }
        }

        internal void RunLottery()
        {
            if (CannotContinue("盲盒")) return;
            try
            {
                var chances = Get(Route(ApiRouteKeys.LotteryChances));
                if (StopForAuth(chances)) return;
                if (!chances.IsSuccess)
                {
                    NoteHttp(chances, "查抽奖机会");
                    return;
                }
                var balance = Integer(chances.Body, "balance") ?? 0;
                if (balance <= 0) return;
                var drawn = Post(Route(ApiRouteKeys.LotteryDraw), new { client_token = ClientToken() });
                if (StopForAuth(drawn)) return;
                if (drawn.IsSuccess)
                {
                    var prizeNode = Find(drawn.Body, "prize_name") ?? Find(drawn.Body, "prize");
                    var prize = prizeNode?.ToString() ?? "未知";
                    if (Bool(drawn.Body, "need_address") || Bool(drawn.Body, "require_address"))
                        prize += "（实物奖，需到成长中心填写收件信息）";
                    _parts.Add("开盲盒获得：" + prize);
                    _successes++;
                    if (balance > 1) _parts.Add($"还剩 {balance - 1} 次抽奖机会，下轮继续");
                }
                else if (IsNoChance(Text(drawn.Body, "msg")))
                {
                    _parts.Add("开盲盒：" + (Text(drawn.Body, "msg") ?? "无抽奖机会"));
                }
                else
                {
                    _parts.Add("开盲盒失败：" + MessageOrHttp(drawn));
                    _failures++;
                    if (drawn.IsHardFailure) _hardFailures++;
                }
            }
            catch (Exception ex)
            {
                ModuleException("盲盒", ex);
            }
        }

        internal void RunBuddyBoxes()
        {
            if (CannotContinue("Buddy 盲盒")) return;
            try
            {
                var quota = Get(Route(ApiRouteKeys.BuddyQuota));
                if (StopForAuth(quota)) return;
                if (!quota.IsSuccess)
                {
                    NoteHttp(quota, "查 Buddy 能量");
                    return;
                }
                var affordable = Integer(quota.Body, "affordable") ?? 0;
                var maxOpen = Integer(quota.Body, "max_open_count") ?? 1;
                if (maxOpen <= 0) maxOpen = 1;
                if (affordable <= 0) return;
                var count = Math.Min(affordable, maxOpen);
                var opened = Post(Route(ApiRouteKeys.BuddyOpen), new { count, client_token = ClientToken() });
                if (StopForAuth(opened)) return;
                if (opened.IsSuccess)
                {
                    var nameNode = Find(opened.Body, "buddy") ?? Find(opened.Body, "name") ?? Find(opened.Body, "buddies");
                    var name = nameNode is JsonValue ? nameNode.ToString() : "新 Buddy";
                    _parts.Add($"开 Buddy 盲盒 ×{count}（{name}）");
                    _successes++;
                }
                else
                {
                    _parts.Add("开 Buddy 盲盒失败：" + MessageOrHttp(opened));
                    _failures++;
                    if (opened.IsHardFailure) _hardFailures++;
                }
            }
            catch (Exception ex)
            {
                ModuleException("Buddy 盲盒", ex);
            }
        }

        internal void LoadSummary()
        {
            if (_authenticationRejected || BudgetLeft <= 0) return;
            try
            {
                var energy = Get(Route(ApiRouteKeys.Energy));
                if (energy.IsSuccess) _energy = Integer(energy.Body, "balance");
            }
            catch { }
            try
            {
                var streak = _streakBody;
                if (streak is null || _streakStale)
                {
                    var refreshed = Get(Route(ApiRouteKeys.Streak));
                    streak = refreshed.IsSuccess ? refreshed.Body : null;
                }
                _streakDays = Integer(Find(streak, "streak"), "days");
            }
            catch { }
        }

        internal GrowthCenterResult Complete()
        {
            var report = _parts.Count > 0
                ? string.Join("；", _parts)
                : _failures > 0 ? "成长中心各步骤均失败" : "成长中心无可领取项";
            var tail = new List<string>();
            if (_energy.HasValue) tail.Add($"能量 {_energy.Value}");
            if (_streakDays.HasValue) tail.Add($"连签 {_streakDays.Value} 天");
            if (_credits != 0) tail.Add($"本次 +共 {_credits} 积分");
            if (tail.Count > 0) report += "（" + string.Join("，", tail) + "）";
            return new GrowthCenterResult(_hardFailures > 0 || _authenticationRejected,
                _successes == 0 && _failures == 0, report, _credits, _energy, _streakDays,
                _authenticationRejected, _cancelled,
                new Dictionary<string, string>(_modules, StringComparer.Ordinal));
        }

        private bool CannotContinue(string label)
        {
            if (CheckCancellation()) return true;
            if (_authenticationRejected || _stopAll) return true;
            if (BudgetLeft > 0) return false;
            _parts.Add($"时间预算耗尽，{label}跳过");
            return true;
        }

        private void ModuleException(string label, Exception ex)
        {
            _parts.Add($"{label}模块异常（{ex.GetType().Name}: {ex.Message}）");
            _failures++;
            _hardFailures++;
        }

        private GrowthResponse Get(string path) => Send(HttpMethod.Get, path, null, retryRead: true);
        private GrowthResponse Post(string path, object? payload) => Send(HttpMethod.Post, path, payload, retryRead: false);
        private string Route(string key, string? value = null) => ApiRouteCatalog.Get(key, config, value);

        private GrowthResponse Send(HttpMethod method, string path, object? payload, bool retryRead)
        {
            var networkDelays = new[] { 5, 15, 30, 60, 90 };
            var serverDelays = new[] { 3, 10 };
            var networkUsed = 0;
            var serverUsed = 0;
            while (true)
            {
                if (CheckCancellation()) return GrowthResponse.CancelledRequest();
                var response = SendOnce(method, path, payload);
                if (!retryRead) return response;
                int waitSeconds = response.NetworkFailed && networkUsed < networkDelays.Length
                    ? networkDelays[networkUsed++]
                    : response.StatusCode >= 500 && serverUsed < serverDelays.Length
                        ? serverDelays[serverUsed++]
                        : 0;
                if (waitSeconds == 0 || BudgetLeft <= waitSeconds + config.GrowthRequestTimeoutSeconds) return response;
                if (wait(TimeSpan.FromSeconds(waitSeconds)))
                {
                    Cancel();
                    return GrowthResponse.CancelledRequest();
                }
            }
        }

        private GrowthResponse SendOnce(HttpMethod method, string path, object? payload)
        {
            if (CheckCancellation()) return GrowthResponse.CancelledRequest();
            if (BudgetLeft <= 1) return GrowthResponse.BudgetExpired();
            using var request = new HttpRequestMessage(method, new Uri(endpoint, path.TrimStart('/')));
            if (payload is not null)
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1,
                Math.Min(config.GrowthRequestTimeoutSeconds, BudgetLeft))));
            try
            {
                using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
                const int maximumBytes = 131_072;
                if (response.Content.Headers.ContentLength is > maximumBytes)
                    return new GrowthResponse(-4, new JsonObject { ["error"] = "响应过大" }, TooLarge: true);
                using var stream = response.Content.ReadAsStream(cancellation.Token);
                byte[] bytes;
                try { bytes = ReadBounded(stream, maximumBytes, cancellation.Token); }
                catch (ResponseTooLargeException)
                {
                    return new GrowthResponse(-4, new JsonObject { ["error"] = "响应过大" }, TooLarge: true);
                }
                JsonNode body;
                try { body = JsonNode.Parse(bytes) ?? new JsonObject(); }
                catch { body = new JsonObject { ["raw"] = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(500, bytes.Length))) }; }
                return new GrowthResponse((int)response.StatusCode, body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                return new GrowthResponse(0, new JsonObject { ["error"] = ex.GetType().Name }, true);
            }
        }

        private double BudgetLeft => Math.Max(0, config.GrowthRunBudgetSeconds - _elapsed.Elapsed.TotalSeconds);

        private bool CheckCancellation()
        {
            if (_cancelled) return true;
            if (!cancellationRequested()) return false;
            Cancel();
            return true;
        }

        private void Cancel()
        {
            _cancelled = true;
            _stopAll = true;
            if (!_parts.Contains("成长中心已让出执行权", StringComparer.Ordinal))
                _parts.Add("成长中心已让出执行权");
        }

        private static byte[] ReadBounded(Stream stream, int maximumBytes, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var remaining = maximumBytes + 1 - (int)buffer.Length;
                if (remaining <= 0) throw new ResponseTooLargeException();
                var read = stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken)
                    .GetAwaiter().GetResult();
                if (read == 0) return buffer.ToArray();
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maximumBytes) throw new ResponseTooLargeException();
            }
        }

        private sealed class ResponseTooLargeException : Exception;

        private bool StopForAuth(GrowthResponse response)
        {
            if (response.Cancelled) return true;
            if (response.StatusCode is not (401 or 403)) return false;
            _authenticationRejected = true;
            _stopAll = true;
            _parts.Add(response.StatusCode == 401 ? "成长中心认证失效" : "成长中心权限被拒绝");
            _failures++;
            _hardFailures++;
            return true;
        }

        private void NoteHttp(GrowthResponse response, string label, bool required = false)
        {
            if (response.IsSuccess && (!required || IsApiSuccess(response))) return;
            _parts.Add($"{label}失败：{MessageOrHttp(response)}");
            if (required || response.IsHardFailure)
            {
                _failures++;
                _hardFailures++;
            }
        }

        private static string MessageOrHttp(GrowthResponse response) =>
            Text(response.Body, "error") ?? Text(response.Body, "msg") ??
            (response.BudgetOut ? "时间预算耗尽" : response.NetworkFailed ? "网络不可达" : $"HTTP {response.StatusCode}");

        private static bool IsApiSuccess(GrowthResponse response)
        {
            if (!response.IsSuccess) return false;
            var code = Find(response.Body, "code");
            return code is null || ToInt(code, int.MinValue) == 0;
        }

        private static bool IsUnknownTier(GrowthResponse response)
        {
            var message = Text(response.Body, "msg")?.ToLowerInvariant() ?? string.Empty;
            return response.StatusCode == 400 && message.Contains("tier", StringComparison.Ordinal) &&
                   (message.Contains("unknown", StringComparison.Ordinal) ||
                    message.Contains("unsupported", StringComparison.Ordinal) ||
                    message.Contains("invalid", StringComparison.Ordinal));
        }

        private static bool IsTierLocked(GrowthResponse response)
        {
            var message = Text(response.Body, "msg") ?? string.Empty;
            return response.StatusCode == 403 && message.Contains("不足", StringComparison.Ordinal);
        }

        private static bool IsNoChance(string? message)
        {
            var text = message?.ToLowerInvariant() ?? string.Empty;
            return (text.Contains("insufficient", StringComparison.Ordinal) || text.Contains("not enough", StringComparison.Ordinal)) &&
                   (text.Contains("chance", StringComparison.Ordinal) || text.Contains("balance", StringComparison.Ordinal)) ||
                   text.Contains("no chance", StringComparison.Ordinal);
        }

        private static string ClientToken() => "u-" + Guid.NewGuid();

        private static string RedeemReward(JsonNode body, string tier)
        {
            var values = new List<string>();
            var credit = Integer(body, "credit_granted") ?? 0;
            var energy = Integer(body, "energy_granted") ?? 0;
            var cards = Integer(body, "cards_granted") ?? 0;
            var chances = Integer(body, "chances_granted") ?? 0;
            if (credit != 0) values.Add($"+{credit} 积分");
            if (energy != 0) values.Add($"+{energy} 能量");
            if (cards != 0) values.Add($"+{cards} 补登卡");
            if (chances != 0) values.Add($"+{chances} 次抽奖");
            if (values.Count == 0)
                values.Add(tier switch
                {
                    "7d" => "+2 能量 +1 补登卡 +1 次抽奖",
                    "14d" => "+50 积分 +3 能量 +1 补登卡 +1 次抽奖",
                    "28d" => "+150 积分 +5 能量 +1 补登卡 +1 次抽奖",
                    _ => "奖励已到账"
                });
            return "（" + string.Join(" ", values) + "）";
        }

        private static string FormatEta(JsonNode? arrival, JsonNode? serverNow)
        {
            if (!double.TryParse(arrival?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var arrivalValue) ||
                !double.TryParse(serverNow?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var serverValue) ||
                !double.IsFinite(arrivalValue) || !double.IsFinite(serverValue)) return string.Empty;
            var seconds = arrivalValue - serverValue;
            if (seconds <= 0) return "，已到达待领取";
            var minutes = (int)Math.Round(seconds / 60d);
            return minutes < 60 ? $"，约 {Math.Max(1, minutes)} 分钟后回" : $"，约 {seconds / 3600d:0.0} 小时后回";
        }

        private static List<string> MakeupCandidates(JsonNode streakBody, JsonNode heatmapBody)
        {
            var todayObject = Find(heatmapBody, "today") as JsonObject ?? throw new InvalidDataException("活跃日历缺少服务端日期");
            if (!DateOnly.TryParseExact(Text(todayObject, "date"), "yyyy-MM-dd", out var today))
                throw new InvalidDataException("补登日期格式无效");
            if (!DateOnly.TryParseExact(Text(streakBody, "launch_date"), "yyyy-MM-dd", out var launch))
                throw new InvalidDataException("活动上线日期无效");
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var fixedStart = new DateOnly(2026, 6, 17);
            var start = new[] { monthStart, launch, fixedStart }.Max();
            var streak = Find(streakBody, "streak") as JsonObject ?? throw new InvalidDataException("连登状态格式无效");
            var madeUp = new HashSet<DateOnly>();
            if (streak["makeup_dates"] is JsonArray dates)
                foreach (var value in dates)
                    if (DateOnly.TryParseExact(value?.ToString(), "yyyy-MM-dd", out var parsed)) madeUp.Add(parsed);
                    else throw new InvalidDataException("已补登记录格式无效");
            else if (streak["makeup_dates"] is not null)
                throw new InvalidDataException("已补登记录格式无效");
            var cells = Find(heatmapBody, "cells") as JsonArray ?? throw new InvalidDataException("活跃日历缺少日期列表");
            var scores = new Dictionary<DateOnly, double>();
            foreach (var node in cells)
            {
                if (node is not JsonObject cell) throw new InvalidDataException("活跃日历记录格式无效");
                if (!DateOnly.TryParseExact(Text(cell, "date"), "yyyy-MM-dd", out var day))
                    throw new InvalidDataException("活跃日历日期无效");
                if (!double.TryParse(Text(cell, "score"), NumberStyles.Float, CultureInfo.InvariantCulture, out var score) ||
                    !double.IsFinite(score) || score < 0)
                    throw new InvalidDataException("活跃日历分数无效");
                if (scores.TryGetValue(day, out var existing) && existing != score)
                    throw new InvalidDataException("活跃日历同一日期记录冲突");
                scores[day] = score;
            }
            return scores.Where(pair => pair.Key >= start && pair.Key < today && pair.Value == 0 && !madeUp.Contains(pair.Key))
                .OrderByDescending(pair => pair.Key).Select(pair => pair.Key.ToString("yyyy-MM-dd")).ToList();
        }
    }

    private sealed record GrowthResponse(int StatusCode, JsonNode Body, bool NetworkFailed = false, bool BudgetOut = false,
        bool Cancelled = false, bool TooLarge = false)
    {
        internal bool IsSuccess => StatusCode is >= 200 and < 300;
        internal bool IsHardFailure => NetworkFailed || BudgetOut || TooLarge || StatusCode >= 500;
        internal static GrowthResponse BudgetExpired() =>
            new(-2, new JsonObject { ["error"] = "时间预算耗尽" }, BudgetOut: true);
        internal static GrowthResponse CancelledRequest() =>
            new(-3, new JsonObject { ["error"] = "已让出执行权" }, Cancelled: true);
    }

    private static JsonNode? Find(JsonNode? node, string key, int depth = 0)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue(key, out var direct) && direct is not null) return direct;
            if (depth < 5)
                foreach (var name in new[] { "data", "result", "resp", "response" })
                    if (obj.TryGetPropertyValue(name, out var nested) && nested is not null && Find(nested, key, depth + 1) is { } found)
                        return found;
        }
        return null;
    }

    private static object? Value(JsonNode? node, string key) => Find(node, key)?.DeepClone();
    private static string? Text(JsonNode? node, string key) => Find(node, key)?.ToString();
    private static bool Bool(JsonNode? node, string key) =>
        Text(node, key) is { } text && (text == "1" || bool.TryParse(text, out var value) && value);
    private static int? Integer(JsonNode? node, string key)
    {
        var value = Find(node, key);
        return value is null ? null : ToInt(value);
    }
    private static int ToInt(JsonNode? node, int fallback = 0)
    {
        if (node is null) return fallback;
        if (int.TryParse(node.ToString(), out var value)) return value;
        return double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
               double.IsFinite(number) && number >= int.MinValue && number <= int.MaxValue
            ? (int)number
            : fallback;
    }
}

internal static class GrowthCenterSelfTests
{
    internal static int RunPublic()
    {
        Run();
        Console.WriteLine("成长中心离线测试通过：签到外的旅行、任务、补登、兑换、抽奖和 Buddy 盲盒策略正常。未访问真实接口。");
        return 0;
    }

    internal static void Run()
    {
        var handler = new SequenceHandler([
            Json(HttpStatusCode.OK, "{\"data\":{\"state\":\"arrived\",\"record_id\":\"r1\"}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"reward_credit\":20}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"locations\":[{\"id\":\"l1\",\"name\":\"海边\",\"duration_hours\":2}]}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"location\":{\"name\":\"海边\"},\"duration_hours\":2}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"tasks\":[{\"task_code\":\"new1\",\"title\":\"新任务\",\"accept_status\":\"not_accepted\",\"locked\":false},{\"task_code\":\"done1\",\"title\":\"完成任务\",\"accept_status\":\"completed\",\"reward_credit\":5,\"reward_energy\":1}]}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"results\":[{\"task_code\":\"new1\",\"status\":\"ok\"}]}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"credit\":8,\"energy\":2}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"launch_date\":\"2026-06-17\",\"makeup_cards\":{\"balance\":2},\"streak\":{\"days\":6,\"makeup_dates\":[]}}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"today\":{\"date\":\"2026-10-02\"},\"cells\":[{\"date\":\"2026-10-01\",\"score\":0}]}}"),
            Json(HttpStatusCode.OK, "{\"code\":0,\"data\":{\"makeup_cards\":{\"balance\":1}}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"starter_status\":\"available\",\"advanced_status\":\"locked\",\"legendary_status\":\"claimed\"}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"credit_granted\":50,\"energy_granted\":2,\"cards_granted\":1,\"chances_granted\":1}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"balance\":2}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"prize_name\":\"积分券\"}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"affordable\":2,\"max_open_count\":1}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"name\":\"绿色 Buddy\"}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"balance\":3}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"streak\":{\"days\":7}}}")
        ]);
        var result = WorkBuddyGrowthCenter.Execute(Session(), new Config(), handler, _ => false);
        string[] expected =
        [
            "/v2/activity/growth/buddy/travel/status",
            "/v2/activity/growth/buddy/travel/claim",
            "/v2/activity/growth/buddy/travel/config",
            "/v2/activity/growth/buddy/travel/depart",
            "/v2/activity/growth/tasks",
            "/v2/activity/growth/tasks/accept",
            "/v2/activity/growth/tasks/done1/claim",
            "/v2/activity/growth/streak",
            "/v2/activity/growth/heatmap",
            "/v2/activity/growth/makeup-cards/use",
            "/v2/activity/growth/redeem/summary",
            "/v2/activity/growth/redeem",
            "/v2/activity/growth/lottery/chances",
            "/v2/activity/growth/lottery/draw",
            "/v2/activity/growth/buddy/quota",
            "/v2/activity/growth/buddy/open",
            "/v2/activity/growth/energy",
            "/v2/activity/growth/streak"
        ];
        if (result.NeedsAttention || result.CreditsGained != 78 || result.Energy != 3 || result.StreakDays != 7 ||
            !handler.Paths.SequenceEqual(expected) ||
            !result.Report.Contains("领旅行礼物 +20", StringComparison.Ordinal) ||
            !result.Report.Contains("派 Buddy", StringComparison.Ordinal) ||
            !result.Report.Contains("领取任务", StringComparison.Ordinal) ||
            !result.Report.Contains("补登 2026-10-01", StringComparison.Ordinal) ||
            !result.Report.Contains("连登兑换", StringComparison.Ordinal) ||
            !result.Report.Contains("开盲盒获得", StringComparison.Ordinal) ||
            !result.Report.Contains("开 Buddy 盲盒", StringComparison.Ordinal) ||
            result.Modules is null || result.Modules.Count != 7 ||
            !result.Modules["travel"].Contains("领旅行礼物 +20", StringComparison.Ordinal) ||
            !result.Modules["tasks"].Contains("领取任务", StringComparison.Ordinal) ||
            !result.Modules["buddy"].Contains("开 Buddy 盲盒", StringComparison.Ordinal))
            throw new InvalidOperationException("成长中心完整策略或请求顺序与上游契约不一致。\n" + result.Report +
                                                "\n" + string.Join("\n", handler.Paths));
        var acceptIndex = Array.IndexOf(expected, "/v2/activity/growth/tasks/accept");
        var redeemIndex = Array.IndexOf(expected, "/v2/activity/growth/redeem");
        var drawIndex = Array.IndexOf(expected, "/v2/activity/growth/lottery/draw");
        if (handler.Methods[acceptIndex] != HttpMethod.Post ||
            !handler.Bodies[acceptIndex].Contains("\"task_codes\":[\"new1\"]", StringComparison.Ordinal) ||
            !handler.Bodies[redeemIndex].Contains("\"tier\":\"7d\"", StringComparison.Ordinal) ||
            !handler.Bodies[redeemIndex].Contains("\"client_token\":\"u-", StringComparison.Ordinal) ||
            !handler.Bodies[drawIndex].Contains("\"client_token\":\"u-", StringComparison.Ordinal))
            throw new InvalidOperationException("成长中心写请求必须沿用上游的批量接单、tier 和防重放 token 契约。");

        var claimFailure = new SequenceHandler([
            Json(HttpStatusCode.OK, "{\"data\":{\"state\":\"arrived\",\"record_id\":\"r2\"}}"),
            Json(HttpStatusCode.ServiceUnavailable, "{\"msg\":\"temporary\"}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"tasks\":[]}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"launch_date\":\"2026-06-17\",\"makeup_cards\":0,\"streak\":{\"days\":2,\"makeup_dates\":[]}}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"starter_status\":\"claimed\",\"advanced_status\":\"locked\",\"legendary_status\":\"locked\"}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"balance\":0}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"affordable\":0}}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"balance\":1}}")
        ]);
        var failedClaim = WorkBuddyGrowthCenter.Execute(Session(), new Config(), claimFailure, _ => false);
        if (!failedClaim.NeedsAttention ||
            claimFailure.Paths.Count(path => path.EndsWith("/buddy/travel/claim", StringComparison.Ordinal)) != 1 ||
            claimFailure.Paths.Any(path => path.EndsWith("/buddy/travel/config", StringComparison.Ordinal) ||
                                           path.EndsWith("/buddy/travel/depart", StringComparison.Ordinal)))
            throw new InvalidOperationException("旅行领奖写请求不得自动重试，失败后也不得覆盖奖励并派出 Buddy。");

        var disconnected = new ThrowingHandler();
        var networkResult = WorkBuddyGrowthCenter.Execute(Session(), new Config(), disconnected, _ => false);
        if (!networkResult.NeedsAttention || disconnected.Attempts != 6 ||
            !networkResult.Report.Contains("网络不可达，成长中心跳过", StringComparison.Ordinal))
            throw new InvalidOperationException("首个旅行状态查询网络失败时，应按上游节奏有限重试并停止后续模块。");

        var cancellable = new ThrowingHandler();
        var cancelled = WorkBuddyGrowthCenter.Execute(Session(), new Config(), cancellable, _ => true);
        if (!cancelled.Cancelled || cancelled.NeedsAttention || cancellable.Attempts != 1)
            throw new InvalidOperationException("成长中心网络退避必须能立即让出执行权，不能阻塞托盘手动重试。");

        var oversized = new OversizedHandler();
        var oversizedResult = WorkBuddyGrowthCenter.Execute(Session(), new Config(), oversized, _ => false);
        if (!oversizedResult.NeedsAttention ||
            oversized.Paths.Count(path => path.EndsWith("/buddy/travel/status", StringComparison.Ordinal)) != 1 ||
            !oversizedResult.Report.Contains("响应过大", StringComparison.Ordinal))
            throw new InvalidOperationException("超限响应必须明确报告且不得按网络故障重试。");
    }

    private static WorkBuddyApiSession Session() => new()
    {
        Endpoint = new Uri(WorkBuddyApiFastPath.DefaultEndpoint),
        AccessToken = "fixture.token",
        UserId = "fixture-user"
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class SequenceHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        internal List<string> Paths { get; } = [];
        internal List<HttpMethod> Methods { get; } = [];
        internal List<string> Bodies { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            Methods.Add(request.Method);
            Bodies.Add(request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty);
            if (_responses.Count == 0) throw new InvalidOperationException("成长中心夹具请求次数超出预期。");
            return _responses.Dequeue();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        internal int Attempts { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new HttpRequestException("offline fixture");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class OversizedHandler : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(new byte[131_073], writable: false))
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
