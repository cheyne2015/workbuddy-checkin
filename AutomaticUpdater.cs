using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkBuddyAutoClaim;

internal sealed record GitHubReleaseAsset(string Name, Uri DownloadUri, long Size, string Sha256);
internal sealed record GitHubReleaseInfo(Version Version, string Tag, GitHubReleaseAsset Asset);
internal sealed record AutomaticUpdateResult(bool UpdateAvailable, bool Staged, string Message, string? Version = null);

internal sealed class UpdateApplyPlan
{
    public int SchemaVersion { get; set; } = 1;
    public int ParentProcessId { get; set; }
    public string StagedRoot { get; set; } = "";
    public string TargetRoot { get; set; } = "";
    public string ExpectedExeSha256 { get; set; } = "";
}

internal static class AutomaticUpdater
{
    private const int MaximumReleaseMetadataBytes = 1_048_576;
    private const int MaximumArchiveBytes = 100 * 1024 * 1024;
    private const long MaximumExtractedBytes = 200L * 1024 * 1024;
    private const int MaximumArchiveEntries = 200;
    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com"
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string ResultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WorkBuddyAutoClaim", "update-result.json");

    internal static AutomaticUpdateResult CheckDownloadAndStage(Config config, string currentExecutable,
        HttpMessageHandler? handler = null)
    {
        var repository = NormalizeRepository(config.UpdateGitHubRepository);
        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        using var client = CreateClient(handler);
        var metadataUri = new Uri($"https://api.github.com/repos/{repository}/releases/latest");
        var metadata = DownloadBytes(client, metadataUri, MaximumReleaseMetadataBytes, allowGitHubRedirects: false);
        var release = ParseRelease(Encoding.UTF8.GetString(metadata), currentVersion);
        if (release is null) return new AutomaticUpdateResult(false, false, "当前已是最新版本。");

        var updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorkBuddyAutoClaim", "updates", "v" + release.Version);
        Directory.CreateDirectory(updateRoot);
        var archivePath = Path.Combine(updateRoot, release.Asset.Name);
        var bytes = DownloadBytes(client, release.Asset.DownloadUri, MaximumArchiveBytes, allowGitHubRedirects: true);
        if (bytes.LongLength != release.Asset.Size)
            throw new InvalidDataException("GitHub Release 资源大小与元数据不一致。");
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(release.Asset.Sha256)))
            throw new InvalidDataException("GitHub Release SHA-256 校验失败，已拒绝安装。");
        File.WriteAllBytes(archivePath, bytes);

        var extracted = Path.Combine(updateRoot, "extracted");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        Directory.CreateDirectory(extracted);
        var stagedRoot = ExtractVerifiedPackage(archivePath, extracted);
        var stagedExe = Path.Combine(stagedRoot, "release", "WorkBuddyAutoClaim.exe");
        ValidateStagedExecutable(stagedExe, release.Version);

        var targetRoot = ResolveInstallRoot(currentExecutable);
        var plan = new UpdateApplyPlan
        {
            ParentProcessId = Environment.ProcessId,
            StagedRoot = stagedRoot,
            TargetRoot = targetRoot,
            ExpectedExeSha256 = Sha256File(stagedExe)
        };
        var planPath = Path.Combine(updateRoot, "apply-plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan, JsonOptions), new UTF8Encoding(false));
        var startInfo = new ProcessStartInfo(stagedExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(stagedExe)!
        };
        startInfo.ArgumentList.Add("--apply-update");
        startInfo.ArgumentList.Add(planPath);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动更新交接进程。");
        return new AutomaticUpdateResult(true, true, $"已下载并验证 v{release.Version}，准备自动更新。", release.Version.ToString());
    }

    internal static int ApplyUpdatePlan(string? planPath)
    {
        string? targetExe = null;
        try
        {
            if (string.IsNullOrWhiteSpace(planPath) || !File.Exists(planPath)) return 2;
            var updateRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkBuddyAutoClaim", "updates")) + Path.DirectorySeparatorChar;
            var fullPlanPath = Path.GetFullPath(planPath);
            if (!fullPlanPath.StartsWith(updateRoot, StringComparison.OrdinalIgnoreCase)) return 2;
            var plan = JsonSerializer.Deserialize<UpdateApplyPlan>(File.ReadAllText(planPath, Encoding.UTF8));
            if (plan is null || plan.SchemaVersion != 1 || plan.ParentProcessId <= 0) return 2;
            var stagedRoot = Path.GetFullPath(plan.StagedRoot);
            if (!stagedRoot.StartsWith(updateRoot, StringComparison.OrdinalIgnoreCase)) return 2;
            targetExe = Path.Combine(Path.GetFullPath(plan.TargetRoot), "release", "WorkBuddyAutoClaim.exe");
            if (!File.Exists(targetExe)) return 2;
            return ExecuteApplyPlan(plan, WaitForParent, StartDaemon, WriteResult);
        }
        catch (Exception ex) { WriteResult("自动更新交接计划无效：" + ex.Message, success: false); return 1; }
    }

    internal static int ExecuteApplyPlan(UpdateApplyPlan plan, Func<int, bool> waitForParent,
        Action<string> startDaemon, Action<string, bool> writeResult, string? backupRoot = null)
    {
        if (!waitForParent(plan.ParentProcessId)) return 3;
        var targetExe = Path.Combine(Path.GetFullPath(plan.TargetRoot), "release", "WorkBuddyAutoClaim.exe");
        try
        {
            ApplyFiles(plan.StagedRoot, plan.TargetRoot, plan.ExpectedExeSha256, backupRoot);
            try { startDaemon(targetExe); }
            catch (Exception ex)
            {
                writeResult("自动更新已安装，但守护重启失败，请手动启动程序：" + ex.Message, false);
                return 4;
            }
            writeResult("自动更新安装成功，已重新启动守护。", true);
            return 0;
        }
        catch (Exception ex)
        {
            var recovery = ex.Message.Contains("回滚未完全成功", StringComparison.Ordinal)
                ? "自动更新安装失败，且原文件未能完全恢复："
                : "自动更新安装失败，原文件已恢复：";
            writeResult(recovery + ex.Message, false);
            if (File.Exists(targetExe)) { try { startDaemon(targetExe); } catch { } }
            return 1;
        }
    }

    private static bool WaitForParent(int parentProcessId)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            return parent.WaitForExit(120_000);
        }
        catch (ArgumentException) { return true; }
    }

    internal static string? ConsumeResult()
    {
        try
        {
            if (!File.Exists(ResultPath)) return null;
            var result = File.ReadAllText(ResultPath, Encoding.UTF8);
            File.Delete(ResultPath);
            return result;
        }
        catch { return null; }
    }

    private static void WriteResult(string message, bool success)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath)!);
            File.WriteAllText(ResultPath, (success ? "成功：" : "失败：") + message, new UTF8Encoding(false));
        }
        catch { }
    }

    private static void StartDaemon(string targetExe)
    {
        Process.Start(new ProcessStartInfo(targetExe, "--daemon")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(targetExe)!
        });
    }

    internal static GitHubReleaseInfo? ParseRelease(string json, Version currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("Release 缺少 tag_name。");
        var versionText = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) || version <= currentVersion) return null;
        var expectedName = $"WorkBuddyAutoClaim-v{version}.zip";
        foreach (var element in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(element.GetProperty("name").GetString(), expectedName, StringComparison.Ordinal)) continue;
            var uri = new Uri(element.GetProperty("browser_download_url").GetString() ?? "");
            if (uri.Scheme != Uri.UriSchemeHttps || !DownloadHosts.Contains(uri.Host))
                throw new InvalidDataException("Release 下载地址不是受信任的 GitHub HTTPS 域名。");
            var size = element.GetProperty("size").GetInt64();
            if (size is <= 0 or > MaximumArchiveBytes) throw new InvalidDataException("Release 资源大小异常。");
            var digest = element.GetProperty("digest").GetString() ?? "";
            if (!Regex.IsMatch(digest, @"\Asha256:[0-9a-f]{64}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Release 缺少 GitHub 提供的 SHA-256 digest。");
            return new GitHubReleaseInfo(version, tag,
                new GitHubReleaseAsset(expectedName, uri, size, digest[7..].ToLowerInvariant()));
        }
        throw new InvalidDataException($"Release 中缺少预期安装包 {expectedName}。");
    }

    internal static string ExtractVerifiedPackage(string archivePath, string destination)
    {
        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long total = 0;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > MaximumArchiveEntries) throw new InvalidDataException("Release 文件数量异常。");
        foreach (var entry in archive.Entries)
        {
            total += entry.Length;
            if (total > MaximumExtractedBytes) throw new InvalidDataException("Release 解压总量超出限制。");
            var output = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!output.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release ZIP 包含路径穿越条目。");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(output); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var source = entry.Open();
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
        var roots = Directory.GetDirectories(destination);
        if (roots.Length != 1 || !File.Exists(Path.Combine(roots[0], "release", "WorkBuddyAutoClaim.exe")))
            throw new InvalidDataException("Release 包目录结构无效。");
        return roots[0];
    }

    internal static void ApplyFiles(string stagedRoot, string targetRoot, string expectedExeSha256, string? backupRoot = null)
    {
        stagedRoot = Path.GetFullPath(stagedRoot);
        targetRoot = Path.GetFullPath(targetRoot);
        if (Path.GetPathRoot(targetRoot)?.TrimEnd(Path.DirectorySeparatorChar) == targetRoot.TrimEnd(Path.DirectorySeparatorChar) ||
            !File.Exists(Path.Combine(stagedRoot, "release", "WorkBuddyAutoClaim.exe")))
            throw new InvalidDataException("更新目标或暂存目录无效。");
        var files = Directory.GetFiles(stagedRoot, "*", SearchOption.AllDirectories);
        if (files.Length > MaximumArchiveEntries) throw new InvalidDataException("更新文件数量异常。");
        backupRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorkBuddyAutoClaim", "update-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        backupRoot = Path.GetFullPath(backupRoot);
        var changes = new List<(string Target, bool Existed, string? Backup)>();
        try
        {
            foreach (var source in files)
            {
                var relative = Path.GetRelativePath(stagedRoot, source);
                if (string.Equals(relative, Path.Combine("release", "config.json"), StringComparison.OrdinalIgnoreCase) ||
                    relative.EndsWith("state.json", StringComparison.OrdinalIgnoreCase) ||
                    relative.EndsWith("run-status.json", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.GetFullPath(Path.Combine(targetRoot, relative));
                if (!target.StartsWith(targetRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新文件目标越界。");
                var existed = File.Exists(target);
                string? backup = null;
                if (existed)
                {
                    backup = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, overwrite: false);
                }
                changes.Add((target, existed, backup));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + ".update-new";
                try
                {
                    File.Copy(source, temporary, overwrite: true);
                    File.Move(temporary, target, overwrite: true);
                }
                finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
            }
            var installedExe = Path.Combine(targetRoot, "release", "WorkBuddyAutoClaim.exe");
            if (!string.Equals(Sha256File(installedExe), expectedExeSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新后的 EXE 哈希校验失败。");
        }
        catch (Exception original)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                try
                {
                    if (change.Existed && change.Backup is not null) File.Copy(change.Backup, change.Target, overwrite: true);
                    else if (File.Exists(change.Target)) File.Delete(change.Target);
                }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
                throw new InvalidOperationException("更新失败，且回滚未完全成功，请从 update-backups 手动恢复。",
                    new AggregateException([original, .. rollbackErrors]));
            throw;
        }
    }

    private static void ValidateStagedExecutable(string path, Version expected)
    {
        if (!File.Exists(path)) throw new InvalidDataException("Release 缺少 WorkBuddyAutoClaim.exe。");
        var text = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        if (!Version.TryParse(text, out var actual) || actual.Major != expected.Major || actual.Minor != expected.Minor ||
            actual.Build != expected.Build) throw new InvalidDataException("Release EXE 版本与标签不一致。");
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var client = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WorkBuddyAutoClaim-Updater/1.3.1");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static byte[] DownloadBytes(HttpClient client, Uri initial, int maximumBytes, bool allowGitHubRedirects)
    {
        var uri = initial;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !DownloadHosts.Contains(uri.Host))
                throw new InvalidDataException("更新器拒绝访问非 GitHub HTTPS 域名。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
            {
                if (!allowGitHubRedirects) throw new InvalidDataException("Release 元数据发生意外重定向。");
                uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximumBytes)
                throw new InvalidDataException("下载内容超出限制。");
            using var stream = response.Content.ReadAsStream(cancellation.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var remaining = maximumBytes + 1 - (int)buffer.Length;
                if (remaining <= 0) throw new InvalidDataException("下载内容超出限制。");
                var read = stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellation.Token)
                    .AsTask().GetAwaiter().GetResult();
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length > maximumBytes) throw new InvalidDataException("下载内容超出限制。");
            return buffer.ToArray();
        }
        throw new InvalidDataException("GitHub 下载重定向次数过多。");
    }

    private static string NormalizeRepository(string repository)
    {
        repository = repository.Trim().Trim('/');
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("GitHub 更新仓库格式无效。");
        return repository;
    }

    private static string ResolveInstallRoot(string currentExecutable)
    {
        var directory = new FileInfo(currentExecutable).Directory ?? throw new InvalidOperationException("无法确定安装目录。");
        return string.Equals(directory.Name, "release", StringComparison.OrdinalIgnoreCase) && directory.Parent is not null
            ? directory.Parent.FullName
            : directory.FullName;
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

internal static class AutomaticUpdateSelfTests
{
    internal static int RunPublic()
    {
        Run();
        Console.WriteLine("自动更新离线测试通过：版本选择、GitHub digest、ZIP 防穿越、配置保留与文件替换正常。未连接 GitHub，未替换当前程序。");
        return 0;
    }

    internal static void Run()
    {
        var payload = Encoding.UTF8.GetBytes("fixture-package");
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var metadata = $$"""{"tag_name":"v9.8.7","draft":false,"prerelease":false,"assets":[{"name":"WorkBuddyAutoClaim-v9.8.7.zip","browser_download_url":"https://github.com/cheyne2015/workbuddy-checkin/releases/download/v9.8.7/WorkBuddyAutoClaim-v9.8.7.zip","size":{{payload.Length}},"digest":"sha256:{{digest}}"}]}""";
        var release = AutomaticUpdater.ParseRelease(metadata, new Version(1, 3, 0));
        if (release?.Version != new Version(9, 8, 7) || release.Asset.Sha256 != digest)
            throw new InvalidOperationException("更新器必须选择较新稳定版并读取 GitHub digest。");
        if (AutomaticUpdater.ParseRelease(metadata, new Version(9, 8, 7)) is not null)
            throw new InvalidOperationException("相同版本不得重复更新。");

        var temp = Path.Combine(Path.GetTempPath(), "WorkBuddyUpdateSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var package = Path.Combine(temp, "package.zip");
            var sourceRoot = Path.Combine(temp, "source", "WorkBuddyAutoClaim-v9.8.7");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "release"));
            var fixtureExe = Path.Combine(sourceRoot, "release", "WorkBuddyAutoClaim.exe");
            File.Copy(Environment.ProcessPath!, fixtureExe);
            File.WriteAllText(Path.Combine(sourceRoot, "README.md"), "new", Encoding.UTF8);
            ZipFile.CreateFromDirectory(Path.Combine(temp, "source"), package);
            var extracted = Path.Combine(temp, "extracted");
            Directory.CreateDirectory(extracted);
            var stagedRoot = AutomaticUpdater.ExtractVerifiedPackage(package, extracted);
            var target = Path.Combine(temp, "target");
            Directory.CreateDirectory(Path.Combine(target, "release"));
            File.WriteAllText(Path.Combine(target, "release", "config.json"), "keep", Encoding.UTF8);
            File.WriteAllText(Path.Combine(target, "README.md"), "old", Encoding.UTF8);
            File.WriteAllText(Path.Combine(target, "release", "WorkBuddyAutoClaim.exe"), "old-exe", Encoding.UTF8);
            var exeHash = Sha256(fixtureExe);
            var rollbackBlocked = false;
            try { AutomaticUpdater.ApplyFiles(stagedRoot, target, new string('0', 64), Path.Combine(temp, "rollback-backup")); }
            catch (InvalidDataException) { rollbackBlocked = true; }
            if (!rollbackBlocked || File.ReadAllText(Path.Combine(target, "README.md"), Encoding.UTF8) != "old" ||
                File.ReadAllText(Path.Combine(target, "release", "WorkBuddyAutoClaim.exe"), Encoding.UTF8) != "old-exe")
                throw new InvalidOperationException("更新校验失败时必须恢复全部已覆盖文件。");
            AutomaticUpdater.ApplyFiles(stagedRoot, target, exeHash, Path.Combine(temp, "success-backup"));
            if (File.ReadAllText(Path.Combine(target, "release", "config.json"), Encoding.UTF8) != "keep" ||
                File.ReadAllText(Path.Combine(target, "README.md"), Encoding.UTF8) != "new" ||
                Sha256(Path.Combine(target, "release", "WorkBuddyAutoClaim.exe")) != exeHash)
                throw new InvalidOperationException("自动替换必须更新程序文件并保留用户 config.json。");

            var handoffTarget = Path.Combine(temp, "handoff-target");
            Directory.CreateDirectory(Path.Combine(handoffTarget, "release"));
            File.WriteAllText(Path.Combine(handoffTarget, "release", "WorkBuddyAutoClaim.exe"), "old", Encoding.UTF8);
            var plan = new UpdateApplyPlan
            {
                ParentProcessId = 123,
                StagedRoot = stagedRoot,
                TargetRoot = handoffTarget,
                ExpectedExeSha256 = exeHash
            };
            var waited = false;
            var started = false;
            string? applyMessage = null;
            bool? applySuccess = null;
            var applyCode = AutomaticUpdater.ExecuteApplyPlan(plan,
                _ => { waited = true; return true; },
                _ => started = true,
                (message, success) => { applyMessage = message; applySuccess = success; },
                Path.Combine(temp, "handoff-backup"));
            if (applyCode != 0 || !waited || !started || applySuccess != true ||
                applyMessage?.Contains("重新启动守护", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("更新交接必须等待父进程、替换文件、恢复守护并记录成功结果。");

            File.WriteAllText(Path.Combine(handoffTarget, "release", "WorkBuddyAutoClaim.exe"), "old-again", Encoding.UTF8);
            started = false;
            applySuccess = null;
            plan.ExpectedExeSha256 = new string('0', 64);
            var failedCode = AutomaticUpdater.ExecuteApplyPlan(plan, _ => true, _ => started = true,
                (_, success) => applySuccess = success, Path.Combine(temp, "handoff-failed-backup"));
            if (failedCode != 1 || !started || applySuccess != false ||
                File.ReadAllText(Path.Combine(handoffTarget, "release", "WorkBuddyAutoClaim.exe"), Encoding.UTF8) != "old-again")
                throw new InvalidOperationException("更新交接失败时必须回滚旧文件、恢复旧守护并记录失败结果。");

            plan.ExpectedExeSha256 = exeHash;
            var restartFailure = AutomaticUpdater.ExecuteApplyPlan(plan, _ => true,
                _ => throw new InvalidOperationException("fixture restart failure"), (_, success) => applySuccess = success,
                Path.Combine(temp, "handoff-restart-backup"));
            if (restartFailure != 4 || applySuccess != false)
                throw new InvalidOperationException("安装完成但守护重启失败时必须单独报告，不能声称已回滚。");

            var malicious = Path.Combine(temp, "malicious.zip");
            using (var archive = ZipFile.Open(malicious, ZipArchiveMode.Create))
                archive.CreateEntry("../escape.txt");
            var blocked = false;
            try { AutomaticUpdater.ExtractVerifiedPackage(malicious, Path.Combine(temp, "malicious-out")); }
            catch (InvalidDataException) { blocked = true; }
            if (!blocked) throw new InvalidOperationException("更新器必须拒绝 ZIP 路径穿越。");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
