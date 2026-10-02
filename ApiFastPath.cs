using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkBuddyAutoClaim;

internal enum ApiAttemptKind
{
    Claimed,
    AlreadyClaimed,
    NeedsOcr,
    InactiveNeedsSingleOcr,
    Retry,
    RefreshCredentials,
    UnknownHost
}

internal sealed record ApiAttemptResult(
    ApiAttemptKind Kind,
    string Message,
    string? Credit = null,
    int? StreakDays = null,
    string? TotalCredits = null,
    int? HttpStatus = null,
    TimeSpan? RetryAfter = null,
    string? EndpointHost = null,
    string? FallbackReason = null,
    bool ClaimMayHaveBeenSent = false);

internal sealed class WorkBuddyApiSession
{
    internal required Uri Endpoint { get; init; }
    internal required string AccessToken { get; init; }
    internal required string UserId { get; init; }
    internal string? EnterpriseId { get; init; }
    internal string? Domain { get; init; }
}

internal sealed record ApiSessionLoadResult(
    WorkBuddyApiSession? Session,
    string Message,
    string? PendingHost = null,
    string? FallbackReason = null)
{
    internal bool IsReady => Session is not null;
}

internal sealed record ApiHttpResult(
    int? StatusCode,
    string? Body,
    bool TimedOut = false,
    bool NetworkFailed = false,
    TimeSpan? RetryAfter = null,
    long ElapsedMilliseconds = 0);

internal static class WorkBuddyApiFastPath
{
    internal const string DefaultEndpoint = "https://copilot.tencent.com";
    internal const string StatusPath = "/v2/billing/meter/checkin-activity-status";
    internal const string ClaimPath = "/v2/billing/meter/daily-checkin";
    internal const string UpstreamPinnedCommit = "cb2bf1f02db8900922dc0f06090cdb7334d45ff5";
    private const string UpstreamHeadApi = "https://api.github.com/repos/88lin/workbuddy-auto-signin/commits/HEAD";
    private const int MaximumAuthFileBytes = 1_048_576;
    private const int MaximumTokenLength = 32_768;
    private const int MaximumResponseBytes = 131_072;
    private static readonly Regex TokenPattern = new(@"\A[A-Za-z0-9._~+/\-]+=*\z", RegexOptions.CultureInvariant);

    internal static ApiSessionLoadResult LoadSession(Config config)
    {
        string? authPath = FindAuthFile(config);
        if (authPath is null)
            return new ApiSessionLoadResult(null, "未找到 WorkBuddy 登录会话，转入 OCR 保底。", FallbackReason: "未找到登录会话");

        JsonDocument? document = null;
        Exception? lastReadError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var info = new FileInfo(authPath);
                if (info.Length is <= 0 or > MaximumAuthFileBytes)
                    return new ApiSessionLoadResult(null, "WorkBuddy 登录会话格式无效，转入 OCR 保底。", FallbackReason: "会话文件大小异常");
                document = JsonDocument.Parse(File.ReadAllText(authPath, Encoding.UTF8));
                break;
            }
            catch (UnauthorizedAccessException ex) { lastReadError = ex; }
            catch (IOException ex) { lastReadError = ex; }
            catch (JsonException)
            {
                return new ApiSessionLoadResult(null, "WorkBuddy 登录会话格式无效，转入 OCR 保底。", FallbackReason: "会话 JSON 无效");
            }
            if (attempt < 3) Thread.Sleep(TimeSpan.FromSeconds(2));
        }
        if (document is null)
            return new ApiSessionLoadResult(null,
                "读取 WorkBuddy 登录会话失败，转入 OCR 保底。",
                FallbackReason: lastReadError?.GetType().Name ?? "会话读取失败");

        using (document)
        {
            try
            {
                var root = document.RootElement;
                if (!TryGetObject(root, "auth", out var auth) || !TryGetObject(root, "account", out var account))
                    throw new ApiCredentialException("会话缺少 auth/account");
                var userId = RequiredAscii(account, "uid");
                var enterpriseId = OptionalAscii(account, "enterpriseId");
                var domain = OptionalAscii(auth, "domain");
                var endpointText = OptionalString(auth, "endpoint") ?? DefaultEndpoint;
                if (!TryValidateEndpoint(endpointText, out var endpoint, out var host))
                    throw new ApiCredentialException("接口地址格式无效");
                if (!IsHostAllowed(host, config.ApiAllowedHosts))
                {
                    if (IsHostAllowed(host, config.ApiRejectedHosts))
                        return new ApiSessionLoadResult(null,
                            $"服务端域名 {host} 已被拒绝，未发送登录令牌，转入 OCR 保底。",
                            FallbackReason: "已拒绝服务端域名");
                    return new ApiSessionLoadResult(null,
                        $"发现尚未允许的服务端域名 {host}；未发送登录令牌，已转入 OCR 保底。",
                        PendingHost: host, FallbackReason: "未知服务端域名");
                }

                if (!auth.TryGetProperty("accessToken", out var tokenElement))
                    throw new ApiCredentialException("会话缺少 accessToken");
                var token = ResolveToken(tokenElement, config.WorkBuddyPath);
                return new ApiSessionLoadResult(new WorkBuddyApiSession
                {
                    Endpoint = endpoint,
                    AccessToken = token,
                    UserId = userId,
                    EnterpriseId = enterpriseId,
                    Domain = domain
                }, $"已读取 WorkBuddy 登录会话；接口域名 {host}。");
            }
            catch (ApiCredentialException ex)
            {
                return new ApiSessionLoadResult(null,
                    "WorkBuddy 登录会话不可用，转入 OCR 保底。",
                    FallbackReason: ex.SafeReason);
            }
        }
    }

    internal static ApiAttemptResult ExecuteAttempt(
        WorkBuddyApiSession session,
        Config config,
        bool claimMayHaveBeenSent,
        Action beforeClaimSend,
        HttpMessageHandler? handler = null)
    {
        using var client = CreateClient(session, config, handler);
        var status = Send(client, BuildApiUri(session.Endpoint, StatusPath), config.ApiTimeoutSeconds);
        var statusDecision = ClassifyStatusResponse(status, claimMayHaveBeenSent);
        if (statusDecision.Kind != ApiAttemptKind.NeedsOcr || statusDecision.FallbackReason != "STATUS_UNCLAIMED")
            return statusDecision with { EndpointHost = session.Endpoint.Host };

        beforeClaimSend();
        var claim = Send(client, BuildApiUri(session.Endpoint, ClaimPath), config.ApiTimeoutSeconds);
        var claimDecision = ClassifyClaimResponse(claim);
        if (claimDecision.Kind is ApiAttemptKind.Claimed or ApiAttemptKind.AlreadyClaimed or
            ApiAttemptKind.RefreshCredentials or ApiAttemptKind.NeedsOcr or ApiAttemptKind.Retry)
        {
            if (claimDecision.Kind == ApiAttemptKind.Claimed)
            {
                var freshStatus = Send(client, BuildApiUri(session.Endpoint, StatusPath), config.ApiTimeoutSeconds);
                claimDecision = EnrichClaimedFromStatus(claimDecision, freshStatus);
            }
            else if (claimDecision.Kind == ApiAttemptKind.Retry && claimDecision.ClaimMayHaveBeenSent &&
                     claim.StatusCode is >= 200 and < 300)
            {
                var verification = Send(client, BuildApiUri(session.Endpoint, StatusPath), config.ApiTimeoutSeconds);
                var verified = ClassifyStatusResponse(verification, claimMayHaveBeenSent: true);
                if (verified.Kind == ApiAttemptKind.AlreadyClaimed) claimDecision = verified;
                else if (verified.FallbackReason == "STATUS_UNCLAIMED")
                    claimDecision = new ApiAttemptResult(ApiAttemptKind.NeedsOcr,
                        "接口返回结构无法确认领取，复查仍明确未领取，转入 OCR 保底。",
                        HttpStatus: claim.StatusCode, FallbackReason: "接口响应结构变化", ClaimMayHaveBeenSent: true);
            }
            return claimDecision with { EndpointHost = session.Endpoint.Host };
        }
        return claimDecision with { EndpointHost = session.Endpoint.Host };
    }

    internal static ApiAttemptResult QueryStatusOnly(WorkBuddyApiSession session, Config config, HttpMessageHandler? handler = null)
    {
        using var client = CreateClient(session, config, handler);
        return ClassifyStatusResponse(
            Send(client, BuildApiUri(session.Endpoint, StatusPath), config.ApiTimeoutSeconds), claimMayHaveBeenSent: false)
            with { EndpointHost = session.Endpoint.Host };
    }

    private static HttpClient CreateClient(WorkBuddyApiSession session, Config config, HttpMessageHandler? handler)
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

    private static ApiHttpResult Send(HttpClient client, Uri uri, int timeoutSeconds)
    {
        var started = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent([])
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 60)));
        try
        {
            using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            string body;
            using (var stream = response.Content.ReadAsStream(cancellation.Token))
            {
                try { body = ReadBoundedUtf8Async(stream, MaximumResponseBytes, cancellation.Token).GetAwaiter().GetResult(); }
                catch (DecoderFallbackException) { body = string.Empty; }
            }
            var retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
            return new ApiHttpResult((int)response.StatusCode, body, RetryAfter: retryAfter,
                ElapsedMilliseconds: started.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return new ApiHttpResult(null, null, TimedOut: true, ElapsedMilliseconds: started.ElapsedMilliseconds);
        }
        catch (HttpRequestException)
        {
            return new ApiHttpResult(null, null, NetworkFailed: true, ElapsedMilliseconds: started.ElapsedMilliseconds);
        }
        catch (IOException)
        {
            return new ApiHttpResult(null, null, NetworkFailed: true, ElapsedMilliseconds: started.ElapsedMilliseconds);
        }
    }

    internal static ApiAttemptResult ClassifyStatusResponse(ApiHttpResult response, bool claimMayHaveBeenSent)
    {
        if (response.TimedOut || response.NetworkFailed)
            return claimMayHaveBeenSent
                ? new ApiAttemptResult(ApiAttemptKind.Retry, "领取请求结果未知，状态复查失败；下次先查状态。",
                    FallbackReason: response.TimedOut ? "接口超时" : "网络失败", ClaimMayHaveBeenSent: true)
                : new ApiAttemptResult(ApiAttemptKind.NeedsOcr, "接口状态查询失败，转入 OCR 保底。",
                    FallbackReason: response.TimedOut ? "接口超时" : "网络失败");
        if (response.StatusCode == 401)
            return new ApiAttemptResult(ApiAttemptKind.RefreshCredentials, "接口认证失效，准备重新读取一次登录会话。",
                HttpStatus: 401, ClaimMayHaveBeenSent: claimMayHaveBeenSent);
        if (response.StatusCode == 403)
            return new ApiAttemptResult(ApiAttemptKind.NeedsOcr, "接口拒绝状态查询，转入 OCR 保底。", HttpStatus: 403,
                FallbackReason: "HTTP 403");
        if (response.StatusCode == 429)
            return new ApiAttemptResult(ApiAttemptKind.Retry, "接口限流，稍后重新查询状态。", HttpStatus: 429,
                RetryAfter: ClampRetryAfter(response.RetryAfter), ClaimMayHaveBeenSent: claimMayHaveBeenSent);
        if (response.StatusCode is not (>= 200 and < 300))
            return claimMayHaveBeenSent
                ? new ApiAttemptResult(ApiAttemptKind.Retry, $"领取结果待确认；状态接口 HTTP {response.StatusCode?.ToString() ?? "未知"}。",
                    HttpStatus: response.StatusCode, FallbackReason: "状态接口异常", ClaimMayHaveBeenSent: true)
                : new ApiAttemptResult(ApiAttemptKind.NeedsOcr, $"状态接口 HTTP {response.StatusCode?.ToString() ?? "未知"}，转入 OCR 保底。",
                    HttpStatus: response.StatusCode,
                    FallbackReason: $"状态接口 HTTP {response.StatusCode?.ToString() ?? "未知"}");

        if (!TryParseJson(response.Body, out var document))
            return claimMayHaveBeenSent
                ? new ApiAttemptResult(ApiAttemptKind.Retry, "领取结果待确认；状态接口返回未知结构。",
                    HttpStatus: response.StatusCode, FallbackReason: "状态响应结构变化", ClaimMayHaveBeenSent: true)
                : new ApiAttemptResult(ApiAttemptKind.NeedsOcr, "状态接口返回未知结构，转入 OCR 保底。",
                    HttpStatus: response.StatusCode, FallbackReason: "状态响应结构变化");
        using (document)
        {
            var root = document.RootElement;
            if (TryFindBoolean(root, "active", out var active) && !active)
                return new ApiAttemptResult(ApiAttemptKind.InactiveNeedsSingleOcr,
                    "服务端签到活动未开启；按约定仅执行一次完整 OCR 保底。", HttpStatus: response.StatusCode,
                    FallbackReason: "active=false");
            if (TryFindBoolean(root, "today_checked_in", out var checkedIn))
            {
                var streak = FindInt(root, "streak_days");
                var total = FindNumberText(root, "total_credits");
                return checkedIn
                    ? new ApiAttemptResult(ApiAttemptKind.AlreadyClaimed, "接口确认今日已领取。",
                        StreakDays: streak, TotalCredits: total, HttpStatus: response.StatusCode)
                    : new ApiAttemptResult(ApiAttemptKind.NeedsOcr, "接口确认今日尚未领取。",
                        HttpStatus: response.StatusCode, FallbackReason: "STATUS_UNCLAIMED");
            }
        }
        return claimMayHaveBeenSent
            ? new ApiAttemptResult(ApiAttemptKind.Retry, "领取结果待确认；状态接口缺少已领字段。",
                HttpStatus: response.StatusCode, FallbackReason: "状态响应结构变化", ClaimMayHaveBeenSent: true)
            : new ApiAttemptResult(ApiAttemptKind.NeedsOcr, "状态接口缺少已领字段，转入 OCR 保底。",
                HttpStatus: response.StatusCode, FallbackReason: "状态响应结构变化");
    }

    internal static ApiAttemptResult ClassifyClaimResponse(ApiHttpResult response)
    {
        if (response.TimedOut || response.NetworkFailed)
            return new ApiAttemptResult(ApiAttemptKind.Retry,
                "领取请求可能已送达但响应丢失；下次先查询状态。",
                FallbackReason: response.TimedOut ? "领取接口超时" : "领取接口网络失败", ClaimMayHaveBeenSent: true);
        if (response.StatusCode == 401)
            return new ApiAttemptResult(ApiAttemptKind.RefreshCredentials, "领取接口认证失效，准备重新读取一次登录会话。",
                HttpStatus: 401, ClaimMayHaveBeenSent: false);
        if (response.StatusCode == 403)
            return new ApiAttemptResult(ApiAttemptKind.NeedsOcr, "领取接口拒绝操作，转入 OCR 保底。",
                HttpStatus: 403, FallbackReason: "HTTP 403", ClaimMayHaveBeenSent: true);
        if (response.StatusCode == 429)
            return new ApiAttemptResult(ApiAttemptKind.Retry, "领取接口限流；下次先查询状态。", HttpStatus: 429,
                RetryAfter: ClampRetryAfter(response.RetryAfter), ClaimMayHaveBeenSent: true);
        if (response.StatusCode is >= 500 || response.StatusCode is >= 300 and < 400)
            return new ApiAttemptResult(ApiAttemptKind.Retry,
                $"领取接口 HTTP {response.StatusCode}，结果待确认；下次先查询状态。",
                HttpStatus: response.StatusCode, FallbackReason: "领取接口暂时异常", ClaimMayHaveBeenSent: true);
        if (response.StatusCode is not (>= 200 and < 300))
            return new ApiAttemptResult(ApiAttemptKind.NeedsOcr,
                $"领取接口 HTTP {response.StatusCode?.ToString() ?? "未知"}，转入 OCR 保底。",
                HttpStatus: response.StatusCode, FallbackReason: $"领取接口 HTTP {response.StatusCode?.ToString() ?? "未知"}",
                ClaimMayHaveBeenSent: true);
        if (!TryParseJson(response.Body, out var document))
            return new ApiAttemptResult(ApiAttemptKind.Retry, "领取接口返回未知结构；准备复查状态。",
                HttpStatus: response.StatusCode, FallbackReason: "领取响应结构变化", ClaimMayHaveBeenSent: true);
        using (document)
        {
            var root = document.RootElement;
            var credit = FindNumberText(root, "credit");
            if (response.StatusCode is >= 200 and < 300 && credit is not null)
                return new ApiAttemptResult(ApiAttemptKind.Claimed, $"接口领取成功，本次 +{credit} 积分。",
                    Credit: credit, StreakDays: FindInt(root, "streak_days"),
                    TotalCredits: FindNumberText(root, "total_credits"), HttpStatus: response.StatusCode,
                    ClaimMayHaveBeenSent: true);
            if (IsAlreadyClaimed(root))
                return new ApiAttemptResult(ApiAttemptKind.AlreadyClaimed, "服务端确认今日已领取。",
                    StreakDays: FindInt(root, "streak_days"), TotalCredits: FindNumberText(root, "total_credits"),
                    HttpStatus: response.StatusCode, ClaimMayHaveBeenSent: true);
            var safeMessage = FindSafeMessage(root);
            if (IsExplicitBusinessFailure(root, safeMessage))
                return new ApiAttemptResult(ApiAttemptKind.NeedsOcr,
                    $"领取接口返回明确业务失败{(safeMessage is null ? "" : "：" + safeMessage)}，转入 OCR 保底。",
                    HttpStatus: response.StatusCode, FallbackReason: "领取接口业务失败", ClaimMayHaveBeenSent: true);
        }
        return new ApiAttemptResult(ApiAttemptKind.Retry, "领取接口返回未知结构；准备复查状态。",
            HttpStatus: response.StatusCode, FallbackReason: "领取响应结构变化", ClaimMayHaveBeenSent: true);
    }

    private static ApiAttemptResult EnrichClaimedFromStatus(ApiAttemptResult claimed, ApiHttpResult status)
    {
        if (status.StatusCode is not (>= 200 and < 300) || !TryParseJson(status.Body, out var document)) return claimed;
        using (document)
            return claimed with
            {
                StreakDays = FindInt(document.RootElement, "streak_days") ?? claimed.StreakDays,
                TotalCredits = FindNumberText(document.RootElement, "total_credits") ?? claimed.TotalCredits
            };
    }

    internal static bool IsHostAllowed(string host, IEnumerable<string> allowedHosts) =>
        allowedHosts.Any(allowed => string.Equals(host, NormalizeHost(allowed), StringComparison.OrdinalIgnoreCase));

    internal static bool TryValidateEndpoint(string endpointText, out Uri endpoint, out string host)
    {
        endpoint = null!;
        host = string.Empty;
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(parsed.Host) ||
            !parsed.IsDefaultPort || !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment)) return false;
        endpoint = new Uri(endpointText.TrimEnd('/') + "/", UriKind.Absolute);
        host = parsed.IdnHost.ToLowerInvariant();
        return true;
    }

    private static string NormalizeHost(string value)
    {
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) return uri.IdnHost.ToLowerInvariant();
        return value.TrimEnd('.').ToLowerInvariant();
    }

    internal static string AddManualUpstreamUpdateHint(string fallbackReason)
    {
        if (!IsCompatibilityFailure(fallbackReason)) return fallbackReason;
        const string manualHint = "请手动检查 88lin/workbuddy-auto-signin，并安装本项目审核后的新版 EXE";
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WorkBuddyAutoClaim/1.3");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = client.GetAsync(UpstreamHeadApi, HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token).GetAwaiter().GetResult();
            if (response.StatusCode != HttpStatusCode.OK)
                return fallbackReason + "；" + manualHint;
            using var stream = response.Content.ReadAsStream(cancellation.Token);
            var body = ReadBoundedUtf8Async(stream, 32_768, cancellation.Token).GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("sha", out var shaElement) &&
                shaElement.ValueKind == JsonValueKind.String)
            {
                var sha = shaElement.GetString() ?? string.Empty;
                if (Regex.IsMatch(sha, @"\A[0-9a-f]{40}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                    !string.Equals(sha, UpstreamPinnedCommit, StringComparison.OrdinalIgnoreCase))
                    return fallbackReason + $"；检测到上游新提交 {sha[..7]}，{manualHint}";
            }
        }
        catch { }
        return fallbackReason + "；" + manualHint;
    }

    internal static bool IsCompatibilityFailure(string reason) =>
        reason.Contains("结构变化", StringComparison.Ordinal) ||
        reason.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("HTTP 410", StringComparison.OrdinalIgnoreCase);

    private static Uri BuildApiUri(Uri endpoint, string path) => new(endpoint, path.TrimStart('/'));

    private static string? FindAuthFile(Config config)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiAuthFilePath))
            return File.Exists(config.ApiAuthFilePath) ? Path.GetFullPath(config.ApiAuthFilePath) : null;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(local, "CodeBuddyExtension", "Data", "Public", "auth", "workbuddy-desktop.info");
        return File.Exists(path) ? path : null;
    }

    private static string ResolveToken(JsonElement element, string workBuddyPath)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var token = element.GetString() ?? string.Empty;
            if (!IsValidToken(token)) throw new ApiCredentialException("访问令牌格式无效");
            return token;
        }
        ValidateEncryptedToken(element);
        if (!File.Exists(workBuddyPath)) throw new ApiCredentialException("找不到用于解密会话的 WorkBuddy.exe");
        return RunCredentialHelper(workBuddyPath, element);
    }

    internal static bool IsValidToken(string token) =>
        token.Length is > 0 and <= MaximumTokenLength && TokenPattern.IsMatch(token);

    internal static void ValidateEncryptedToken(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != 2 ||
            !element.TryGetProperty("$wbEncrypted", out var marker) || marker.ValueKind != JsonValueKind.Number ||
            !marker.TryGetInt32(out var markerValue) || markerValue != 1 ||
            !element.TryGetProperty("envelope", out var encodedElement) || encodedElement.ValueKind != JsonValueKind.String)
            throw new ApiCredentialException("不支持的加密会话格式");
        try
        {
            var encoded = encodedElement.GetString()!;
            if (encoded.Length is <= 0 or > 64_512) throw new FormatException();
            var raw = Convert.FromBase64String(encoded);
            if (Convert.ToBase64String(raw) != encoded) throw new FormatException();
            using var envelope = JsonDocument.Parse(raw);
            var root = envelope.RootElement;
            var names = root.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();
            if (!names.SequenceEqual(new[] { "authTag", "ciphertext", "keyId", "nonce", "suite" }) ||
                !root.TryGetProperty("suite", out var suite) || suite.ValueKind != JsonValueKind.Number ||
                !suite.TryGetInt32(out var suiteValue) || suiteValue != 1 ||
                !root.TryGetProperty("keyId", out var keyId) || keyId.ValueKind != JsonValueKind.String ||
                !Regex.IsMatch(keyId.GetString() ?? "", @"\A[0-9a-f]{16}\z", RegexOptions.CultureInvariant))
                throw new FormatException();
            ValidateBase64Field(root, "nonce", 12);
            ValidateBase64Field(root, "authTag", 16);
            ValidateBase64Field(root, "ciphertext", null);
        }
        catch (ApiCredentialException) { throw; }
        catch { throw new ApiCredentialException("加密会话格式无效"); }
    }

    private static void ValidateBase64Field(JsonElement root, string name, int? expectedLength)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) throw new FormatException();
        var encoded = value.GetString()!;
        var bytes = Convert.FromBase64String(encoded);
        if (Convert.ToBase64String(bytes) != encoded || (expectedLength.HasValue && bytes.Length != expectedLength.Value))
            throw new FormatException();
    }

    private static string RunCredentialHelper(string executable, JsonElement encryptedToken, string? scriptPrefix = null)
    {
        using var process = new Process { StartInfo = CreateCredentialHelperStartInfo(executable, scriptPrefix) };
        bool started = false;
        try
        {
            if (!process.Start()) throw new ApiCredentialException("无法启动本地凭据助手");
            started = true;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stdout = ReadBoundedTextAsync(process.StandardOutput, 65_536, deadline.Token);
            var stderr = ReadBoundedTextAsync(process.StandardError, 8_192, deadline.Token);
            var input = JsonSerializer.Serialize(new { version = 1, operation = "decrypt", value = encryptedToken });
            var writeInput = WriteCredentialInputAsync(process.StandardInput, input, deadline.Token);
            var waitForExit = process.WaitForExitAsync(deadline.Token);
            try
            {
                Task.WhenAll(writeInput, waitForExit, stdout, stderr).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(2_000); } catch { }
                throw new ApiCredentialException("本地凭据助手超时");
            }
            using var reply = JsonDocument.Parse(stdout.Result);
            var root = reply.RootElement;
            if (process.ExitCode != 0 || !root.TryGetProperty("version", out var version) || version.GetInt32() != 1 ||
                !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("accessToken", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String)
                throw new ApiCredentialException("本地凭据助手未能解密会话");
            var token = tokenElement.GetString() ?? string.Empty;
            if (!IsValidToken(token)) throw new ApiCredentialException("本地凭据助手返回无效令牌");
            return token;
        }
        catch (ApiCredentialException) { throw; }
        catch { throw new ApiCredentialException("本地凭据助手运行失败"); }
        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private static async Task WriteCredentialInputAsync(StreamWriter writer, string input, CancellationToken cancellationToken)
    {
        await writer.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        writer.Close();
    }

    internal static ProcessStartInfo CreateCredentialHelperStartInfo(string executable, string? scriptPrefix = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add((scriptPrefix ?? string.Empty) + CredentialHelperJavaScript);
        foreach (var key in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase) ||
                                   key.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) ||
                                   key.StartsWith("WORKBUDDY_", StringComparison.OrdinalIgnoreCase)).ToArray())
            startInfo.Environment.Remove(key);
        startInfo.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        return startInfo;
    }

    internal static void VerifyEncryptedCredentialFixture(string executable)
    {
        var secretBytes = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var secretText = Convert.ToBase64String(secretBytes);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(secretText));
        var keyId = Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()[..16];
        var nonce = Enumerable.Range(17, 12).Select(value => (byte)value).ToArray();
        var plaintext = Encoding.UTF8.GetBytes("fixture.Token_123");
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        var aad = BuildFixtureAad(keyId);
        using (var cipher = new AesGcm(key, tag.Length))
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        CryptographicOperations.ZeroMemory(key);

        var envelopeJson = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["suite"] = 1,
            ["keyId"] = keyId,
            ["nonce"] = Convert.ToBase64String(nonce),
            ["authTag"] = Convert.ToBase64String(tag),
            ["ciphertext"] = Convert.ToBase64String(ciphertext)
        });
        var wrapperJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["$wbEncrypted"] = 1,
            ["envelope"] = Convert.ToBase64String(envelopeJson)
        });
        using var wrapper = JsonDocument.Parse(wrapperJson);
        var nativePayload = JsonSerializer.Serialize(new { version = 1, atRestSecretKey = secretText });
        var prefix = "const __wbFixture=" + JsonSerializer.Serialize(nativePayload) +
                     ";process._linkedBinding=()=>({loggerGet:()=>__wbFixture});\n";
        var decrypted = RunCredentialHelper(executable, wrapper.RootElement, prefix);
        if (decrypted != "fixture.Token_123")
            throw new InvalidOperationException("sym-v1 固定夹具解密结果不一致。");
        CryptographicOperations.ZeroMemory(plaintext);
    }

    private static byte[] BuildFixtureAad(string keyId)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("WB-AAD\0"));
        stream.WriteByte(1);
        WriteLengthPrefixed(stream, "WBEV1");
        WriteLengthPrefixed(stream, "sym-v1");
        stream.Write([0, 0, 0, 1]);
        WriteLengthPrefixed(stream, keyId);
        stream.Write([2, 0, 0]);
        return stream.ToArray();
    }

    private static void WriteLengthPrefixed(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.WriteByte((byte)(bytes.Length >> 24));
        stream.WriteByte((byte)(bytes.Length >> 16));
        stream.WriteByte((byte)(bytes.Length >> 8));
        stream.WriteByte((byte)bytes.Length);
        stream.Write(bytes);
    }

    private const string CredentialHelperJavaScript = """
'use strict';
const crypto = require('crypto');
const limit = 65536;
const fail = reason => { throw {reason}; };
const obj = x => x !== null && typeof x === 'object' && !Array.isArray(x);
function b64(value, length) {
  if (typeof value !== 'string' || value.length > limit) fail('INVALID_FORMAT');
  const bytes = Buffer.from(value, 'base64');
  if (bytes.toString('base64') !== value || (length !== undefined && bytes.length !== length)) fail('INVALID_FORMAT');
  return bytes;
}
function utf8(bytes) { const text = bytes.toString('utf8'); if (!Buffer.from(text, 'utf8').equals(bytes)) fail('INVALID_FORMAT'); return text; }
function decode(value) {
  if (!obj(value) || Object.keys(value).sort().join(',') !== '$wbEncrypted,envelope' || value.$wbEncrypted !== 1) fail('UNSUPPORTED_ENVELOPE');
  let e; try { e = JSON.parse(utf8(b64(value.envelope))); } catch (x) { fail(x.reason || 'INVALID_FORMAT'); }
  if (!obj(e) || !Number.isInteger(e.suite) || e.suite !== 1 || Object.keys(e).sort().join(',') !== 'authTag,ciphertext,keyId,nonce,suite' ||
      typeof e.keyId !== 'string' || !/^[0-9a-f]{16}$/.test(e.keyId)) fail('INVALID_FORMAT');
  return {keyId:e.keyId, nonce:b64(e.nonce,12), tag:b64(e.authTag,16), ciphertext:b64(e.ciphertext)};
}
function decrypt(e) {
  let payload, key, plain;
  try {
    try { payload = JSON.parse(process._linkedBinding('electron_browser_workbuddy_storage').loggerGet()); } catch (_) { fail('RUNTIME_UNAVAILABLE'); }
    if (!obj(payload) || payload.version !== 1) fail('RUNTIME_UNAVAILABLE');
    let secret; try { secret = b64(payload.atRestSecretKey,32); } catch (_) { fail('RUNTIME_UNAVAILABLE'); }
    const empty = secret.every(b => b === 0); secret.fill(0); if (empty) fail('RUNTIME_UNAVAILABLE');
    key = crypto.createHash('sha256').update(payload.atRestSecretKey,'utf8').digest(); payload = null;
    if (crypto.createHash('sha256').update(key).digest('hex').slice(0,16) !== e.keyId) fail('KEY_MISMATCH');
    const lp = s => { const b=Buffer.from(s,'utf8'), n=Buffer.alloc(4); n.writeUInt32BE(b.length); return Buffer.concat([n,b]); };
    const aad=Buffer.concat([Buffer.from('WB-AAD\0','ascii'),Buffer.from([1]),lp('WBEV1'),lp('sym-v1'),Buffer.from([0,0,0,1]),lp(e.keyId),Buffer.from([2,0,0])]);
    try { const c=crypto.createDecipheriv('aes-256-gcm',key,e.nonce,{authTagLength:16}); c.setAAD(aad); c.setAuthTag(e.tag); plain=Buffer.concat([c.update(e.ciphertext),c.final()]); }
    catch (_) { fail('DECRYPT_FAILED'); }
    const token=utf8(plain); if (!token.length || token.length>32768 || !/^[A-Za-z0-9._~+\/-]+=*$/.test(token)) fail('INVALID_FORMAT');
    return token;
  } finally { if (key) key.fill(0); if (plain) plain.fill(0); }
}
let chunks=[], size=0;
function reply(value) { process.stdout.write(JSON.stringify({version:1,...value}),()=>process.exit(value.ok?0:1)); }
process.stdin.on('data', chunk => { size += chunk.length; if (size>limit) reply({ok:false,reason:'INVALID_FORMAT'}); else chunks.push(chunk); });
process.stdin.on('error',()=>reply({ok:false,reason:'HELPER_PROTOCOL'}));
process.stdin.on('end',()=>{ try { const request=JSON.parse(utf8(Buffer.concat(chunks))); chunks=[];
  if (!obj(request)||request.version!==1||request.operation!=='decrypt') fail('HELPER_PROTOCOL');
  reply({ok:true,accessToken:decrypt(decode(request.value))});
} catch(e) { reply({ok:false,reason:e.reason||'HELPER_PROTOCOL'}); } });
""";

    private static string RequiredAscii(JsonElement parent, string name) =>
        OptionalAscii(parent, name) ?? throw new ApiCredentialException($"会话缺少 {name}");

    private static string? OptionalAscii(JsonElement parent, string name)
    {
        var value = OptionalString(parent, name);
        if (value is null) return null;
        if (value.Length is < 1 or > 2048 || value.Any(character => character < 0x21 || character > 0x7e))
            throw new ApiCredentialException($"会话字段 {name} 格式无效");
        return value;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ApiCredentialException($"会话字段 {name} 格式无效");
        var text = value.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
            return true;
        value = default;
        return false;
    }

    private static bool TryParseJson(string? body, out JsonDocument document)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body)) throw new JsonException();
            document = JsonDocument.Parse(body);
            return true;
        }
        catch
        {
            document = null!;
            return false;
        }
    }

    private static bool TryFindBoolean(JsonElement element, string name, out bool value)
    {
        if (TryFind(element, name, out var found))
        {
            if (found.ValueKind == JsonValueKind.True) { value = true; return true; }
            if (found.ValueKind == JsonValueKind.False) { value = false; return true; }
            if (found.ValueKind == JsonValueKind.Number && found.TryGetInt32(out var number) && number is 0 or 1)
            { value = number == 1; return true; }
        }
        value = false;
        return false;
    }

    private static int? FindInt(JsonElement element, string name)
    {
        if (!TryFind(element, name, out var found)) return null;
        if (found.ValueKind == JsonValueKind.Number && found.TryGetInt32(out var value)) return value;
        if (found.ValueKind == JsonValueKind.String && int.TryParse(found.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)) return value;
        return null;
    }

    private static string? FindNumberText(JsonElement element, string name)
    {
        if (!TryFind(element, name, out var found)) return null;
        if (found.ValueKind == JsonValueKind.Number && found.TryGetDecimal(out var value))
            return value.ToString("0.################", CultureInfo.InvariantCulture);
        if (found.ValueKind == JsonValueKind.String &&
            decimal.TryParse(found.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            return value.ToString("0.################", CultureInfo.InvariantCulture);
        return null;
    }

    private static bool TryFind(JsonElement element, string name, out JsonElement value, int depth = 0)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value)) return true;
            if (depth < 4)
                foreach (var property in element.EnumerateObject())
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array &&
                        TryFind(property.Value, name, out value, depth + 1)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array && depth < 4)
            foreach (var item in element.EnumerateArray())
                if (TryFind(item, name, out value, depth + 1)) return true;
        value = default;
        return false;
    }

    private static bool IsAlreadyClaimed(JsonElement root)
    {
        if (TryFind(root, "code", out var code) &&
            ((code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) && number == 10001) ||
             (code.ValueKind == JsonValueKind.String && code.GetString() == "10001"))) return true;
        var message = FindSafeMessage(root);
        return message is not null && message.Contains("今日", StringComparison.Ordinal) &&
               (message.Contains("已领", StringComparison.Ordinal) || message.Contains("已签", StringComparison.Ordinal));
    }

    private static bool IsExplicitBusinessFailure(JsonElement root, string? message)
    {
        if (TryFind(root, "code", out var code))
        {
            if (code.ValueKind == JsonValueKind.Number && code.TryGetInt64(out var number) && number != 0) return true;
            if (code.ValueKind == JsonValueKind.String)
            {
                var text = code.GetString()?.Trim();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number != 0;
                if (!string.IsNullOrWhiteSpace(text) &&
                    !text.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
                    !text.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        if (string.IsNullOrWhiteSpace(message)) return false;
        string[] failureWords = ["失败", "不可用", "未开启", "拒绝", "过期", "无效", "异常",
            "fail", "error", "denied", "unavailable", "invalid", "expired"];
        return failureWords.Any(word => message.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindSafeMessage(JsonElement root)
    {
        foreach (var key in new[] { "msg", "message" })
            if (TryFind(root, key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = new string((value.GetString() ?? string.Empty).Where(character => !char.IsControl(character)).Take(120).ToArray());
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
        return null;
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? value)
    {
        if (value?.Delta is { } delta) return delta;
        if (value?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    private static TimeSpan? ClampRetryAfter(TimeSpan? value)
    {
        if (!value.HasValue || value <= TimeSpan.Zero) return null;
        return value > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : value;
    }

    private static async Task<string> ReadBoundedUtf8Async(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, maximumBytes + 1 - (int)buffer.Length)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximumBytes) throw new HttpRequestException("Response too large.");
        }
        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
    }

    private static async Task<string> ReadBoundedTextAsync(
        StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var remaining = maximumCharacters + 1 - builder.Length;
            if (remaining <= 0) throw new IOException("Credential helper output exceeded its limit.");
            var read = await reader.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0) return builder.ToString();
            builder.Append(buffer, 0, read);
            if (builder.Length > maximumCharacters) throw new IOException("Credential helper output exceeded its limit.");
        }
    }

    private sealed class ApiCredentialException(string safeReason) : Exception
    {
        internal string SafeReason { get; } = safeReason;
    }
}

internal static class WorkBuddyApiSelfTests
{
    internal static void Run(Config config)
    {
        if (!new Config().UseApiFastPath || new Config().ApiTimeoutSeconds != 15 ||
            !WorkBuddyApiFastPath.IsHostAllowed("copilot.tencent.com", new Config().ApiAllowedHosts))
            throw new InvalidOperationException("接口快速通道默认配置不正确。");
        if (WorkBuddyApiFastPath.IsHostAllowed("evil.example", new[] { "copilot.tencent.com" }))
            throw new InvalidOperationException("接口域名白名单不得接受未知域名。");
        if (!WorkBuddyApiFastPath.TryValidateEndpoint("https://copilot.tencent.com", out _, out var host) ||
            host != "copilot.tencent.com" ||
            WorkBuddyApiFastPath.TryValidateEndpoint("http://copilot.tencent.com", out _, out _) ||
            WorkBuddyApiFastPath.TryValidateEndpoint("https://copilot.tencent.com:444", out _, out _))
            throw new InvalidOperationException("接口地址必须使用无凭据的 HTTPS 地址。");

        var unclaimed = WorkBuddyApiFastPath.ClassifyStatusResponse(
            new ApiHttpResult(200, "{\"data\":{\"active\":true,\"today_checked_in\":false}}"), false);
        if (unclaimed.FallbackReason != "STATUS_UNCLAIMED")
            throw new InvalidOperationException("状态接口明确未领取时必须允许进入领取请求。");
        var already = WorkBuddyApiFastPath.ClassifyStatusResponse(
            new ApiHttpResult(200, "{\"active\":true,\"today_checked_in\":true,\"streak_days\":7}"), false);
        if (already.Kind != ApiAttemptKind.AlreadyClaimed || already.StreakDays != 7)
            throw new InvalidOperationException("状态接口必须识别今日已领取及连签天数。");
        var inactive = WorkBuddyApiFastPath.ClassifyStatusResponse(
            new ApiHttpResult(200, "{\"active\":false,\"today_checked_in\":false}"), false);
        if (inactive.Kind != ApiAttemptKind.InactiveNeedsSingleOcr)
            throw new InvalidOperationException("active=false 必须只触发一次 OCR 保底。");
        var claimed = WorkBuddyApiFastPath.ClassifyClaimResponse(
            new ApiHttpResult(200, "{\"data\":{\"credit\":100,\"total_credits\":2372.85}}"));
        if (claimed.Kind != ApiAttemptKind.Claimed || claimed.Credit != "100" || claimed.TotalCredits != "2372.85")
            throw new InvalidOperationException("领取接口必须以 credit 字段确认成功。");
        var unknownClaim = WorkBuddyApiFastPath.ClassifyClaimResponse(new ApiHttpResult(200, "{\"data\":{}}"));
        if (unknownClaim.Kind != ApiAttemptKind.Retry || !unknownClaim.ClaimMayHaveBeenSent)
            throw new InvalidOperationException("已发送领取但响应未知时不得盲目进入 OCR。");
        var businessFailure = WorkBuddyApiFastPath.ClassifyClaimResponse(
            new ApiHttpResult(200, "{\"code\":40001,\"msg\":\"activity unavailable\"}"));
        if (businessFailure.Kind != ApiAttemptKind.NeedsOcr || businessFailure.FallbackReason != "领取接口业务失败")
            throw new InvalidOperationException("明确的接口业务失败应进入 OCR，而不是反复发送领取请求。");
        var ambiguousSuccess = WorkBuddyApiFastPath.ClassifyClaimResponse(
            new ApiHttpResult(200, "{\"code\":0,\"message\":\"success\"}"));
        if (ambiguousSuccess.Kind != ApiAttemptKind.Retry || !ambiguousSuccess.ClaimMayHaveBeenSent)
            throw new InvalidOperationException("HTTP 200 但缺少 credit 的成功样式未知响应必须先复查状态，不得盲目进入 OCR。");
        var timeoutAfterClaim = WorkBuddyApiFastPath.ClassifyStatusResponse(
            new ApiHttpResult(null, null, TimedOut: true), claimMayHaveBeenSent: true);
        if (timeoutAfterClaim.Kind != ApiAttemptKind.Retry || !timeoutAfterClaim.ClaimMayHaveBeenSent)
            throw new InvalidOperationException("领取后状态查询超时必须保持待确认状态。");
        var unauthorizedAfterClaim = WorkBuddyApiFastPath.ClassifyStatusResponse(
            new ApiHttpResult(401, "{}"), claimMayHaveBeenSent: true);
        if (unauthorizedAfterClaim.Kind != ApiAttemptKind.RefreshCredentials || !unauthorizedAfterClaim.ClaimMayHaveBeenSent)
            throw new InvalidOperationException("领取待确认状态必须穿过 HTTP 401 会话刷新。");
        var rejectedClaim = WorkBuddyApiFastPath.ClassifyClaimResponse(new ApiHttpResult(401, "{}"));
        if (rejectedClaim.Kind != ApiAttemptKind.RefreshCredentials || rejectedClaim.ClaimMayHaveBeenSent)
            throw new InvalidOperationException("领取接口明确返回 401 时应允许刷新后安全回退，不得误记为响应丢失。");
        var serverFailure = WorkBuddyApiFastPath.ClassifyClaimResponse(
            new ApiHttpResult(503, "{\"msg\":\"temporarily unavailable\"}"));
        if (serverFailure.Kind != ApiAttemptKind.Retry || !serverFailure.ClaimMayHaveBeenSent)
            throw new InvalidOperationException("领取 POST 的 5xx 必须先重查状态，不得直接进入 OCR。");
        var missingEndpoint = WorkBuddyApiFastPath.ClassifyClaimResponse(new ApiHttpResult(404, ""));
        if (missingEndpoint.Kind != ApiAttemptKind.NeedsOcr || missingEndpoint.FallbackReason != "领取接口 HTTP 404")
            throw new InvalidOperationException("领取接口 404 必须进入 OCR 并触发人工兼容更新提示。");
        var limited = WorkBuddyApiFastPath.ClassifyStatusResponse(
            new ApiHttpResult(429, "{}", RetryAfter: TimeSpan.FromMinutes(5)), false);
        if (limited.Kind != ApiAttemptKind.Retry || limited.RetryAfter != TimeSpan.FromSeconds(60))
            throw new InvalidOperationException("HTTP 429 的 Retry-After 必须限制为最多 60 秒且不得转 OCR。");
        if (!WorkBuddyApiFastPath.IsValidToken("abc.DEF_123-xy") || WorkBuddyApiFastPath.IsValidToken("secret token"))
            throw new InvalidOperationException("令牌格式验证回归失败。");
        var helper = WorkBuddyApiFastPath.CreateCredentialHelperStartInfo(config.WorkBuddyPath);
        if (helper.UseShellExecute || !helper.CreateNoWindow || helper.Environment.Keys.Any(key =>
                key.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("WORKBUDDY_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) && key != "ELECTRON_RUN_AS_NODE") ||
            helper.Environment["ELECTRON_RUN_AS_NODE"] != "1")
            throw new InvalidOperationException("凭据助手必须隐藏运行并清理继承的 Node/Electron/WorkBuddy 环境变量。");
        if (File.Exists(config.WorkBuddyPath))
            WorkBuddyApiFastPath.VerifyEncryptedCredentialFixture(config.WorkBuddyPath);
        if (!WorkBuddyApiFastPath.IsCompatibilityFailure("领取响应结构变化") ||
            WorkBuddyApiFastPath.IsCompatibilityFailure("网络失败"))
            throw new InvalidOperationException("只有接口兼容性失败才应触发上游人工更新检查。");

        var session = new WorkBuddyApiSession
        {
            Endpoint = new Uri(WorkBuddyApiFastPath.DefaultEndpoint),
            AccessToken = "fixture.token",
            UserId = "fixture-user"
        };
        var alreadyHandler = new SequenceHandler([
            _ => JsonResponse(HttpStatusCode.OK, "{\"active\":true,\"today_checked_in\":true}")
        ]);
        var alreadyAttempt = WorkBuddyApiFastPath.ExecuteAttempt(session, new Config(), false,
            () => throw new InvalidOperationException("已领取状态不得发送领取请求。"), alreadyHandler);
        if (alreadyAttempt.Kind != ApiAttemptKind.AlreadyClaimed || alreadyHandler.Paths.Count != 1 ||
            alreadyHandler.Paths[0] != WorkBuddyApiFastPath.StatusPath)
            throw new InvalidOperationException("接口快速通道必须先查状态，已领取时不得调用领取接口。");

        var claimHandler = new SequenceHandler([
            _ => JsonResponse(HttpStatusCode.OK, "{\"active\":true,\"today_checked_in\":false}"),
            _ => JsonResponse(HttpStatusCode.OK, "{\"credit\":100}"),
            _ => JsonResponse(HttpStatusCode.OK, "{\"today_checked_in\":true,\"streak_days\":3,\"total_credits\":900}")
        ]);
        bool markedPending = false;
        var claimAttempt = WorkBuddyApiFastPath.ExecuteAttempt(session, new Config(), false,
            () => markedPending = true, claimHandler);
        if (claimAttempt.Kind != ApiAttemptKind.Claimed || !markedPending || claimAttempt.StreakDays != 3 ||
            !claimHandler.Paths.SequenceEqual(new[]
                { WorkBuddyApiFastPath.StatusPath, WorkBuddyApiFastPath.ClaimPath, WorkBuddyApiFastPath.StatusPath }))
            throw new InvalidOperationException("接口领取必须按 状态→领取→状态核验 顺序执行并先持久化 Pending。");

        var unknownHostDirectory = Path.Combine(Path.GetTempPath(), "WorkBuddyApiHostSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unknownHostDirectory);
        try
        {
            var authFile = Path.Combine(unknownHostDirectory, "workbuddy-desktop.info");
            File.WriteAllText(authFile,
                "{\"auth\":{\"endpoint\":\"https://unknown.example\",\"accessToken\":{\"bad\":true}},\"account\":{\"uid\":\"u\"}}",
                Encoding.UTF8);
            var unknown = WorkBuddyApiFastPath.LoadSession(new Config
            {
                ApiAuthFilePath = authFile,
                WorkBuddyPath = Path.Combine(unknownHostDirectory, "missing.exe")
            });
            if (unknown.IsReady || unknown.PendingHost != "unknown.example" || unknown.FallbackReason != "未知服务端域名")
                throw new InvalidOperationException(
                    $"未知域名必须在读取或解密令牌之前停止并转入 OCR（ready={unknown.IsReady}, host={unknown.PendingHost ?? "null"}, reason={unknown.FallbackReason ?? "null"}）。");
            var rejected = WorkBuddyApiFastPath.LoadSession(new Config
            {
                ApiAuthFilePath = authFile,
                WorkBuddyPath = Path.Combine(unknownHostDirectory, "missing.exe"),
                ApiRejectedHosts = ["unknown.example"]
            });
            if (rejected.IsReady || rejected.PendingHost is not null || rejected.FallbackReason != "已拒绝服务端域名")
                throw new InvalidOperationException("已拒绝域名必须保持拦截且不再反复请求用户确认。");
        }
        finally
        {
            Directory.Delete(unknownHostDirectory, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class SequenceHandler(IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);
        internal List<string> Paths { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? "");
            if (request.Headers.Authorization?.Scheme != "Bearer" || string.IsNullOrWhiteSpace(request.Headers.Authorization.Parameter))
                throw new InvalidOperationException("接口请求缺少 Bearer 登录会话。");
            if (_responses.Count == 0) throw new InvalidOperationException("接口请求次数超出离线夹具预期。");
            return _responses.Dequeue()(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
