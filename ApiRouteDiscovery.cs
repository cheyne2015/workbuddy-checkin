using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WorkBuddyAutoClaim;

internal static class ApiRouteKeys
{
    internal const string DailyStatus = "daily.status";
    internal const string DailyClaim = "daily.claim";
    internal const string TravelStatus = "growth.travel.status";
    internal const string TravelClaim = "growth.travel.claim";
    internal const string TravelConfig = "growth.travel.config";
    internal const string TravelDepart = "growth.travel.depart";
    internal const string Tasks = "growth.tasks";
    internal const string TasksAccept = "growth.tasks.accept";
    internal const string TaskClaim = "growth.tasks.claim";
    internal const string Streak = "growth.streak";
    internal const string Heatmap = "growth.heatmap";
    internal const string MakeupUse = "growth.makeup.use";
    internal const string RedeemSummary = "growth.redeem.summary";
    internal const string Redeem = "growth.redeem";
    internal const string LotteryChances = "growth.lottery.chances";
    internal const string LotteryDraw = "growth.lottery.draw";
    internal const string BuddyQuota = "growth.buddy.quota";
    internal const string BuddyOpen = "growth.buddy.open";
    internal const string Energy = "growth.energy";
}

internal sealed record ApiRouteDefinition(string Method, string Path, string Source, int Confidence);

internal sealed class ApiRouteManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string WorkBuddyVersion { get; set; } = "unknown";
    public string AsarSha256 { get; set; } = "";
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.Now;
    public bool ReadOnlyVerified { get; set; }
    public Dictionary<string, ApiRouteDefinition> Routes { get; set; } = new(StringComparer.Ordinal);
}

internal static class ApiRouteCatalog
{
    private static readonly IReadOnlyDictionary<string, ApiRouteDefinition> Defaults =
        new Dictionary<string, ApiRouteDefinition>(StringComparer.Ordinal)
        {
            [ApiRouteKeys.DailyStatus] = new("POST", "/v2/billing/meter/checkin-activity-status", "built-in", 100),
            [ApiRouteKeys.DailyClaim] = new("POST", "/v2/billing/meter/daily-checkin", "built-in", 100),
            [ApiRouteKeys.TravelStatus] = new("GET", "/v2/activity/growth/buddy/travel/status", "built-in", 100),
            [ApiRouteKeys.TravelClaim] = new("POST", "/v2/activity/growth/buddy/travel/claim", "built-in", 100),
            [ApiRouteKeys.TravelConfig] = new("GET", "/v2/activity/growth/buddy/travel/config", "built-in", 100),
            [ApiRouteKeys.TravelDepart] = new("POST", "/v2/activity/growth/buddy/travel/depart", "built-in", 100),
            [ApiRouteKeys.Tasks] = new("GET", "/v2/activity/growth/tasks", "built-in", 100),
            [ApiRouteKeys.TasksAccept] = new("POST", "/v2/activity/growth/tasks/accept", "built-in", 100),
            [ApiRouteKeys.TaskClaim] = new("POST", "/v2/activity/growth/tasks/{code}/claim", "built-in", 100),
            [ApiRouteKeys.Streak] = new("GET", "/v2/activity/growth/streak", "built-in", 100),
            [ApiRouteKeys.Heatmap] = new("GET", "/v2/activity/growth/heatmap", "built-in", 100),
            [ApiRouteKeys.MakeupUse] = new("POST", "/v2/activity/growth/makeup-cards/use", "built-in", 100),
            [ApiRouteKeys.RedeemSummary] = new("GET", "/v2/activity/growth/redeem/summary", "built-in", 100),
            [ApiRouteKeys.Redeem] = new("POST", "/v2/activity/growth/redeem", "built-in", 100),
            [ApiRouteKeys.LotteryChances] = new("GET", "/v2/activity/growth/lottery/chances", "built-in", 100),
            [ApiRouteKeys.LotteryDraw] = new("POST", "/v2/activity/growth/lottery/draw", "built-in", 100),
            [ApiRouteKeys.BuddyQuota] = new("GET", "/v2/activity/growth/buddy/quota", "built-in", 100),
            [ApiRouteKeys.BuddyOpen] = new("POST", "/v2/activity/growth/buddy/open", "built-in", 100),
            [ApiRouteKeys.Energy] = new("GET", "/v2/activity/growth/energy", "built-in", 100)
        };

    internal static string Get(string key, Config config, string? value = null)
    {
        var route = Defaults[key].Path;
        if (config.EnableAutomaticRouteDiscovery)
        {
            var path = ResolveManifestPath(config);
            var manifest = RouteDiscoveryEngine.LoadVerifiedManifest(path) ??
                           RouteDiscoveryEngine.LoadVerifiedManifest(path + ".bak");
            if (manifest?.ReadOnlyVerified == true && manifest.Routes.TryGetValue(key, out var discovered) &&
                IsSafeRoute(key, discovered)) route = discovered.Path;
        }
        return value is null ? route : route.Replace("{code}", Uri.EscapeDataString(value), StringComparison.Ordinal);
    }

    internal static IReadOnlyDictionary<string, ApiRouteDefinition> BuiltIn => Defaults;

    internal static string ResolveManifestPath(Config config) => string.IsNullOrWhiteSpace(config.ApiRouteManifestPath)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorkBuddyAutoClaim", "api-routes.json")
        : Path.GetFullPath(config.ApiRouteManifestPath);

    internal static bool IsSafeRoute(string key, ApiRouteDefinition route)
    {
        if (!Defaults.TryGetValue(key, out var expected) || route.Confidence < 80 ||
            !string.Equals(route.Method, expected.Method, StringComparison.OrdinalIgnoreCase) ||
            !route.Path.StartsWith('/')) return false;
        if (key.StartsWith("daily.", StringComparison.Ordinal))
            return route.Path.StartsWith("/v2/billing/meter/", StringComparison.Ordinal);
        return route.Path.StartsWith("/v2/activity/growth/", StringComparison.Ordinal);
    }
}

internal static class RouteDiscoveryEngine
{
    private const int MaximumAsarHeaderBytes = 16 * 1024 * 1024;
    private const int MaximumSourceBytes = 30 * 1024 * 1024;
    private const long MaximumTotalSourceBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex RoutePattern = new(
        @"/(?:v2/)?(?:billing/meter|activity/growth)/[A-Za-z0-9_{}$./:-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> PublicSourceHosts = new(StringComparer.OrdinalIgnoreCase)
        { "www.workbuddy.cn", "download.codebuddy.cn", "static.workbuddy.cn" };

    internal static ApiRouteManifest DiscoverAndSave(Config config, HttpMessageHandler? handler = null)
    {
        var manifest = Discover(config, handler);
        SaveWithBackup(manifest, ApiRouteCatalog.ResolveManifestPath(config));
        return manifest;
    }

    internal static ApiRouteManifest Discover(Config config, HttpMessageHandler? handler = null)
    {
        var resources = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config.WorkBuddyPath)) ?? "", "resources");
        var asarPath = Path.Combine(resources, "app.asar");
        if (!File.Exists(asarPath)) throw new FileNotFoundException("找不到 WorkBuddy app.asar。", asarPath);
        var sources = ReadAsarSources(asarPath);
        try { sources.AddRange(ReadOfficialWebSources(handler)); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidDataException)
        {
            // The shipped ASAR remains a trusted source for daily routes. Public H5 discovery is best effort.
        }
        var version = File.Exists(config.WorkBuddyPath)
            ? System.Diagnostics.FileVersionInfo.GetVersionInfo(config.WorkBuddyPath).FileVersion ?? "unknown"
            : "unknown";
        var manifest = DiscoverFromSources(sources, version, Sha256File(asarPath));
        return manifest;
    }

    internal static ApiRouteManifest DiscoverFromSources(IEnumerable<(string Name, string Text)> sources,
        string version, string asarSha256)
    {
        var manifest = new ApiRouteManifest { WorkBuddyVersion = version, AsarSha256 = asarSha256 };
        var candidates = new Dictionary<string, List<ApiRouteDefinition>>(StringComparer.Ordinal);
        foreach (var (name, text) in sources)
        {
            foreach (Match match in RoutePattern.Matches(text))
            {
                var path = NormalizePath(match.Value);
                var start = Math.Max(0, match.Index - 900);
                var length = Math.Min(text.Length - start, match.Length + 1800);
                var context = text.Substring(start, length);
                var key = IdentifyOperation(path, context);
                if (key is null) continue;
                var method = InferMethod(text, match.Index, ApiRouteCatalog.BuiltIn[key].Method);
                var confidence = ScoreCandidate(key, path, context, method);
                if (!candidates.TryGetValue(key, out var list)) candidates[key] = list = [];
                list.Add(new ApiRouteDefinition(method, CanonicalizeTemplate(key, path), name, confidence));
            }
        }

        foreach (var (key, list) in candidates)
        {
            var best = list.OrderByDescending(candidate => candidate.Confidence)
                .ThenBy(candidate => candidate.Path, StringComparer.Ordinal).First();
            if (ApiRouteCatalog.IsSafeRoute(key, best)) manifest.Routes[key] = best;
        }
        if (!manifest.Routes.ContainsKey(ApiRouteKeys.DailyStatus) ||
            !manifest.Routes.ContainsKey(ApiRouteKeys.DailyClaim))
            throw new InvalidDataException("未能从可信客户端资源中同时确认签到状态与领取接口。");
        return manifest;
    }

    internal static List<(string Name, string Text)> ReadAsarSources(string asarPath)
    {
        using var stream = new FileStream(asarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> prefix = stackalloc byte[16];
        stream.ReadExactly(prefix);
        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix[12..16]);
        if (headerLength is 0 or > MaximumAsarHeaderBytes) throw new InvalidDataException("ASAR 头大小异常。");
        var headerBytes = new byte[headerLength];
        stream.ReadExactly(headerBytes);
        using var header = JsonDocument.Parse(headerBytes);
        var files = new List<AsarEntry>();
        WalkAsar(header.RootElement, "", files);
        long total = 0;
        var output = new List<(string, string)>();
        var dataOffset = 16L + headerLength;
        foreach (var file in files.Where(entry => !entry.Unpacked &&
                     Regex.IsMatch(entry.Path, @"\.(?:js|cjs|mjs|json|html)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
        {
            if (file.Size <= 0 || file.Size > MaximumSourceBytes) continue;
            total += file.Size;
            if (total > MaximumTotalSourceBytes) throw new InvalidDataException("ASAR 可扫描源码总量超出限制。");
            var bytes = new byte[file.Size];
            stream.Position = checked(dataOffset + file.Offset);
            stream.ReadExactly(bytes);
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("daily-checkin", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("checkin-activity-status", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("activity/growth", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("growth-center", StringComparison.OrdinalIgnoreCase))
                output.Add(("asar:" + file.Path, text));
        }
        return output;
    }

    internal static ApiRouteManifest? LoadVerifiedManifest(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var manifest = JsonSerializer.Deserialize<ApiRouteManifest>(File.ReadAllText(path, Encoding.UTF8));
            if (manifest is null || manifest.SchemaVersion != 1 || manifest.Routes.Count == 0) return null;
            if (manifest.Routes.Any(pair => !ApiRouteCatalog.IsSafeRoute(pair.Key, pair.Value))) return null;
            return manifest;
        }
        catch { return null; }
    }

    internal static void MarkReadOnlyVerified(ApiRouteManifest manifest, string path)
    {
        manifest.ReadOnlyVerified = true;
        SaveWithBackup(manifest, path);
    }

    internal static List<(string Name, string Text)> ReadOfficialWebSources(HttpMessageHandler? handler = null)
    {
        using var client = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WorkBuddyAutoClaim-RouteDiscovery/1.3.1");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var root = new Uri("https://www.workbuddy.cn/profile/growth-center");
        var html = DownloadPublicText(client, root, 1_048_576);
        var output = new List<(string, string)> { ("h5:" + root, html) };
        var queue = new Queue<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnqueueModuleUris(html, root, queue, seen);
        long total = html.Length;
        while (queue.Count > 0 && seen.Count <= 40 && total <= 64L * 1024 * 1024)
        {
            var uri = queue.Dequeue();
            var text = DownloadPublicText(client, uri, 5 * 1024 * 1024);
            total += Encoding.UTF8.GetByteCount(text);
            if (total > 64L * 1024 * 1024) throw new InvalidDataException("官方前端资源总量超出限制。");
            output.Add(("h5:" + uri, text));
            EnqueueModuleUris(text, uri, queue, seen);
        }
        return output;
    }

    private static string DownloadPublicText(HttpClient client, Uri initial, int maximumBytes)
    {
        var uri = initial;
        for (var redirect = 0; redirect <= 4; redirect++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !PublicSourceHosts.Contains(uri.Host))
                throw new InvalidDataException("路由发现拒绝访问非官方前端域名。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
            {
                uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximumBytes)
                throw new InvalidDataException("官方前端资源超出单文件限制。");
            using var stream = response.Content.ReadAsStream(cancellation.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var remaining = maximumBytes + 1 - (int)buffer.Length;
                if (remaining <= 0) throw new InvalidDataException("官方前端资源超出单文件限制。");
                var read = stream.Read(chunk, 0, Math.Min(chunk.Length, remaining));
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length > maximumBytes) throw new InvalidDataException("官方前端资源超出单文件限制。");
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        throw new InvalidDataException("官方前端重定向次数过多。");
    }

    private static void EnqueueModuleUris(string text, Uri baseUri, Queue<Uri> queue, HashSet<string> seen)
    {
        foreach (Match match in Regex.Matches(text,
                     """(?:(?:src|href)\s*=\s*|import\s*\(|from\s*)['"]([^'"]+\.js(?:\?[^'"]*)?)['"]|(?:https?:)?//[A-Za-z0-9._/-]+\.js(?:\?[A-Za-z0-9._=&-]+)?|assets/[A-Za-z0-9._-]+\.js""",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var raw = match.Groups[1].Success ? match.Groups[1].Value : match.Value;
            if (raw.StartsWith("//", StringComparison.Ordinal)) raw = "https:" + raw;
            if (!Uri.TryCreate(baseUri, raw, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !PublicSourceHosts.Contains(uri.Host) || !uri.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Count >= 40) break;
            if (seen.Add(uri.AbsoluteUri)) queue.Enqueue(uri);
        }
    }

    internal static void SaveWithBackup(ApiRouteManifest manifest, string path)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("路由清单目录无效。");
        Directory.CreateDirectory(directory);
        var temp = path + ".new";
        var backup = path + ".bak";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
        if (LoadVerifiedManifest(temp) is null) throw new InvalidDataException("新路由清单校验失败。");
        if (File.Exists(path)) File.Copy(path, backup, overwrite: true);
        File.Move(temp, path, overwrite: true);
    }

    internal static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string NormalizePath(string path)
    {
        path = path.TrimEnd('.', ':');
        if (path.StartsWith("/billing/", StringComparison.Ordinal) || path.StartsWith("/activity/", StringComparison.Ordinal))
            path = "/v2" + path;
        return path;
    }

    private static string CanonicalizeTemplate(string key, string path)
    {
        if (key != ApiRouteKeys.TaskClaim) return path;
        return Regex.Replace(path, @"/tasks/[^/{}$]+/claim$", "/tasks/{code}/claim", RegexOptions.CultureInvariant);
    }

    private static string InferMethod(string text, int routeIndex, string fallback)
    {
        var start = Math.Max(0, routeIndex - 180);
        var context = text.Substring(start, Math.Min(text.Length - start, 360)).ToLowerInvariant();
        if (Regex.IsMatch(context, "(?:\\.post|post\\s*\\(|method\\s*:\\s*['\\\"]post)", RegexOptions.CultureInvariant)) return "POST";
        if (Regex.IsMatch(context, "(?:\\.get|get\\s*\\(|method\\s*:\\s*['\\\"]get)", RegexOptions.CultureInvariant)) return "GET";
        return fallback;
    }

    private static string? IdentifyOperation(string path, string context)
    {
        var lower = path.ToLowerInvariant();
        if (lower.Contains("billing/meter/", StringComparison.Ordinal))
        {
            if (lower.Contains("status", StringComparison.Ordinal)) return ApiRouteKeys.DailyStatus;
            if (lower.Contains("daily-checkin", StringComparison.Ordinal)) return ApiRouteKeys.DailyClaim;
            if (context.Contains("today_checked_in", StringComparison.OrdinalIgnoreCase)) return ApiRouteKeys.DailyStatus;
            return null;
        }
        if (lower.EndsWith("/buddy/travel/status", StringComparison.Ordinal)) return ApiRouteKeys.TravelStatus;
        if (lower.EndsWith("/buddy/travel/claim", StringComparison.Ordinal)) return ApiRouteKeys.TravelClaim;
        if (lower.EndsWith("/buddy/travel/config", StringComparison.Ordinal)) return ApiRouteKeys.TravelConfig;
        if (lower.EndsWith("/buddy/travel/depart", StringComparison.Ordinal)) return ApiRouteKeys.TravelDepart;
        if (lower.EndsWith("/tasks/accept", StringComparison.Ordinal)) return ApiRouteKeys.TasksAccept;
        if (lower.EndsWith("/claim", StringComparison.Ordinal) && lower.Contains("/tasks/", StringComparison.Ordinal)) return ApiRouteKeys.TaskClaim;
        if (lower.EndsWith("/tasks", StringComparison.Ordinal)) return ApiRouteKeys.Tasks;
        if (lower.EndsWith("/streak", StringComparison.Ordinal)) return ApiRouteKeys.Streak;
        if (lower.EndsWith("/heatmap", StringComparison.Ordinal)) return ApiRouteKeys.Heatmap;
        if (lower.EndsWith("/makeup-cards/use", StringComparison.Ordinal)) return ApiRouteKeys.MakeupUse;
        if (lower.EndsWith("/redeem/summary", StringComparison.Ordinal)) return ApiRouteKeys.RedeemSummary;
        if (lower.EndsWith("/redeem", StringComparison.Ordinal)) return ApiRouteKeys.Redeem;
        if (lower.EndsWith("/lottery/chances", StringComparison.Ordinal)) return ApiRouteKeys.LotteryChances;
        if (lower.EndsWith("/lottery/draw", StringComparison.Ordinal)) return ApiRouteKeys.LotteryDraw;
        if (lower.EndsWith("/buddy/quota", StringComparison.Ordinal)) return ApiRouteKeys.BuddyQuota;
        if (lower.EndsWith("/buddy/open", StringComparison.Ordinal)) return ApiRouteKeys.BuddyOpen;
        if (lower.EndsWith("/energy", StringComparison.Ordinal)) return ApiRouteKeys.Energy;
        return null;
    }

    private static int ScoreCandidate(string key, string path, string context, string method)
    {
        var expected = ApiRouteCatalog.BuiltIn[key];
        var score = 60;
        if (path == expected.Path) score += 25;
        if (string.Equals(method, expected.Method, StringComparison.OrdinalIgnoreCase)) score += 10;
        string[] anchors = key switch
        {
            ApiRouteKeys.DailyStatus => ["today_checked_in", "active"],
            ApiRouteKeys.DailyClaim => ["credit", "daily-checkin"],
            ApiRouteKeys.MakeupUse => ["target_date"],
            ApiRouteKeys.TasksAccept => ["task_codes"],
            ApiRouteKeys.Redeem => ["tier", "client_token"],
            ApiRouteKeys.LotteryDraw or ApiRouteKeys.BuddyOpen => ["client_token"],
            ApiRouteKeys.TravelClaim => ["record_id"],
            ApiRouteKeys.TravelDepart => ["location_id"],
            _ => []
        };
        score += Math.Min(10, anchors.Count(anchor => context.Contains(anchor, StringComparison.OrdinalIgnoreCase)) * 5);
        return Math.Min(100, score);
    }

    private static void WalkAsar(JsonElement node, string prefix, List<AsarEntry> output)
    {
        if (!node.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object) return;
        foreach (var property in files.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(prefix) ? property.Name : prefix + "/" + property.Name;
            var value = property.Value;
            if (value.TryGetProperty("files", out _)) { WalkAsar(value, path, output); continue; }
            if (!value.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt32(out var size)) continue;
            var unpacked = value.TryGetProperty("unpacked", out var unpackedElement) && unpackedElement.ValueKind == JsonValueKind.True;
            long offset = 0;
            if (!unpacked && (!value.TryGetProperty("offset", out var offsetElement) ||
                              !long.TryParse(offsetElement.GetString(), out offset))) continue;
            output.Add(new AsarEntry(path, size, offset, unpacked));
        }
    }

    private sealed record AsarEntry(string Path, int Size, long Offset, bool Unpacked);
}

internal static class RouteDiscoverySelfTests
{
    internal static int RunPublic()
    {
        Run();
        Console.WriteLine("接口路由发现离线测试通过：ASAR 解析、语义识别、清单回滚与恶意路径拦截正常。未访问真实接口。");
        return 0;
    }

    internal static void Run()
    {
        var sources = new (string, string)[]
        {
            ("asar:main/service.js", "http.post('/v2/billing/meter/checkin-activity-status',{}); today_checked_in active; http.post('/v2/billing/meter/daily-checkin',{}); credit"),
            ("h5:growthSpace.js", "get('/activity/growth/heatmap'); post('/activity/growth/makeup-cards/use',{target_date:d}); get('/activity/growth/energy')")
        };
        var manifest = RouteDiscoveryEngine.DiscoverFromSources(sources, "fixture", new string('a', 64));
        if (manifest.Routes[ApiRouteKeys.DailyStatus].Path != "/v2/billing/meter/checkin-activity-status" ||
            manifest.Routes[ApiRouteKeys.MakeupUse].Path != "/v2/activity/growth/makeup-cards/use")
            throw new InvalidOperationException("路由发现未按语义生成规范路径。");

        var asarFixture = Path.Combine(Path.GetTempPath(), "WorkBuddyRouteFixture-" + Guid.NewGuid().ToString("N") + ".asar");
        try
        {
            var sourceText = "post('/v2/billing/meter/checkin-activity-status'); today_checked_in active; post('/v2/billing/meter/daily-checkin'); credit";
            var sourceBytes = Encoding.UTF8.GetBytes(sourceText);
            var headerBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                files = new Dictionary<string, object>
                {
                    ["main.js"] = new { size = sourceBytes.Length, offset = "0" }
                }
            });
            var bytes = new byte[16 + headerBytes.Length + sourceBytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), (uint)headerBytes.Length);
            headerBytes.CopyTo(bytes, 16);
            sourceBytes.CopyTo(bytes, 16 + headerBytes.Length);
            File.WriteAllBytes(asarFixture, bytes);
            var parsed = RouteDiscoveryEngine.ReadAsarSources(asarFixture);
            if (parsed.Count != 1 || !parsed[0].Text.Contains("daily-checkin", StringComparison.Ordinal))
                throw new InvalidOperationException("ASAR 二进制读取未提取到接口源码。");
        }
        finally { if (File.Exists(asarFixture)) File.Delete(asarFixture); }

        var temp = Path.Combine(Path.GetTempPath(), "WorkBuddyRouteDiscovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var manifestPath = Path.Combine(temp, "api-routes.json");
            var config = new Config { ApiRouteManifestPath = manifestPath };
            RouteDiscoveryEngine.SaveWithBackup(manifest, manifestPath);
            if (ApiRouteCatalog.Get(ApiRouteKeys.DailyClaim, config) != ApiRouteCatalog.BuiltIn[ApiRouteKeys.DailyClaim].Path)
                throw new InvalidOperationException("未经只读状态接口验证的动态路由不得启用。");
            RouteDiscoveryEngine.MarkReadOnlyVerified(manifest, manifestPath);
            if (ApiRouteCatalog.Get(ApiRouteKeys.DailyClaim, config) != manifest.Routes[ApiRouteKeys.DailyClaim].Path)
                throw new InvalidOperationException("只读验证后的动态路由应被启用。");
            var second = manifest.WithRoute(ApiRouteKeys.DailyClaim,
                new ApiRouteDefinition("POST", "/v2/billing/meter/daily-checkin-v2", "fixture", 90));
            RouteDiscoveryEngine.SaveWithBackup(second, manifestPath);
            File.WriteAllText(manifestPath, "{broken", Encoding.UTF8);
            if (RouteDiscoveryEngine.LoadVerifiedManifest(manifestPath) is not null || !File.Exists(manifestPath + ".bak"))
                throw new InvalidOperationException("损坏主清单必须停止使用并保留上一份备份。");
            if (ApiRouteCatalog.Get(ApiRouteKeys.DailyClaim, config) != manifest.Routes[ApiRouteKeys.DailyClaim].Path)
                throw new InvalidOperationException("主清单损坏时必须回退到上一份已验证路由清单。");

            var unsafeManifest = manifest.WithRoute(ApiRouteKeys.DailyClaim,
                new ApiRouteDefinition("POST", "https://evil.example/claim", "fixture", 100));
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(unsafeManifest), Encoding.UTF8);
            if (RouteDiscoveryEngine.LoadVerifiedManifest(manifestPath) is not null)
                throw new InvalidOperationException("绝对 URL 或未知主机不得进入动态路由清单。");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static ApiRouteManifest WithRoute(this ApiRouteManifest manifest, string key, ApiRouteDefinition route)
    {
        var clone = JsonSerializer.Deserialize<ApiRouteManifest>(JsonSerializer.Serialize(manifest))!;
        clone.Routes[key] = route;
        return clone;
    }
}
