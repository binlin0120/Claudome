using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ClaudeSetup;

public sealed class Downloader(HttpClient? client = null) : IDisposable
{
    private readonly HttpClient http = client ?? new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly bool owned = client is null;
    public static async Task<string> Hash(string path, CancellationToken token = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant();
    }
    public Task<string> Get(Resource resource, string directory, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(resource.Sha256, "^[a-fA-F0-9]{64}$"))
            throw new SetupException("资源缺少可信的 SHA-256，已停止下载。");
        return DownloadCoreAsync(resource, directory, requireSha256: true, progress, token);
    }

    public Task<string> GetOfficialOptionalHashAsync(Resource resource, string directory, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(resource.Sha256) &&
            !System.Text.RegularExpressions.Regex.IsMatch(resource.Sha256, "^[a-fA-F0-9]{64}$"))
            throw new SetupException("SHA-256 应为 64 位十六进制字符。");
        return DownloadCoreAsync(resource, directory, requireSha256: !string.IsNullOrWhiteSpace(resource.Sha256), progress, token);
    }

    private async Task<string> DownloadCoreAsync(Resource resource, string directory, bool requireSha256, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (!Uri.TryCreate(resource.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new SetupException("下载来源必须是 HTTPS。");
        var expectedHash = requireSha256 ? resource.Sha256.ToLowerInvariant() : "";
        Directory.CreateDirectory(directory);
        if (Path.GetFileName(resource.FileName) != resource.FileName) throw new SetupException("资源文件名不合法。");
        var target = Paths.Under(directory, resource.FileName);
        if (File.Exists(target))
        {
            if (requireSha256)
            {
                if (await Hash(target, token) == expectedHash)
                {
                    var size = new FileInfo(target).Length;
                    progress?.Report(new(resource.Name, size, size, 0, FromCache: true));
                    return target;
                }
                throw new SetupException($"已有 {resource.Name} 校验不匹配，请在下载目录中移开该文件后重试。");
            }
            var existingSize = new FileInfo(target).Length;
            progress?.Report(new(resource.Name, existingSize, existingSize, 0, FromCache: true));
            return target;
        }
        var part = target + ".part"; var metaPath = part + ".json";
        Paths.RejectLinks(part); Paths.RejectLinks(metaPath);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var offset = File.Exists(part) ? new FileInfo(part).Length : 0L;
                DownloadMeta? metadata = null;
                if (File.Exists(metaPath)) metadata = JsonFile.Read<DownloadMeta>(metaPath);
                if (offset > 0 && ((requireSha256 && metadata?.Sha256 != resource.Sha256) || (!requireSha256 && metadata?.Url != resource.Url) || (resource.Size > 0 && offset > resource.Size)))
                    throw new SetupException("已有下载分片与本次版本不一致，请移开对应分片后重试。");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Claudome/0.1");
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    if (metadata?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag) && !tag.IsWeak) request.Headers.IfRange = new RangeConditionHeaderValue(tag);
                }
                using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
                watchdog.CancelAfter(TimeSpan.FromSeconds(60));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
                if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new SetupException("下载发生了不安全的协议跳转。");
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
                {
                    if (!requireSha256 || await Hash(part, token) == expectedHash) { File.Move(part, target); File.Delete(metaPath); return target; }
                    throw new SetupException("下载分片不完整且服务器无法继续，请移开该分片后重试。");
                }
                response.EnsureSuccessStatusCode();
                var resume = response.StatusCode == HttpStatusCode.PartialContent && offset > 0;
                if (resume && response.Content.Headers.ContentRange?.From != offset) throw new SetupException("服务器返回的下载范围不正确，已停止写入。");
                if (response.StatusCode == HttpStatusCode.PartialContent && !resume) throw new SetupException("服务器返回了非预期的分段响应。");
                if (!resume) offset = 0;
                var total = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength is long length ? length + offset : null);
                if (resource.Size > 0 && total.HasValue && total != resource.Size) throw new SetupException("资源大小与版本清单不一致。");
                JsonFile.Write(metaPath, new DownloadMeta(resource.Sha256, response.Headers.ETag?.ToString(), resource.Url));
                await using var output = new FileStream(part, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                await using var input = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[256 * 1024]; long added = 0; var watch = Stopwatch.StartNew(); var last = TimeSpan.Zero;
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); readTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                    var count = await input.ReadAsync(buffer, readTimeout.Token);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), token); added += count;
                    if (resource.Size > 0 && offset + added > resource.Size) throw new SetupException("资源内容超出预期大小。");
                    if (watch.Elapsed - last > TimeSpan.FromMilliseconds(300)) { progress?.Report(new(resource.Name, offset + added, total, added / Math.Max(watch.Elapsed.TotalSeconds, .01))); last = watch.Elapsed; }
                }
                await output.FlushAsync(token);
                await output.DisposeAsync();
                if (resource.Size > 0 && new FileInfo(part).Length != resource.Size) throw new IOException("Incomplete download");
                if (total.HasValue && new FileInfo(part).Length != total.Value) throw new IOException("Incomplete download");
                if (requireSha256 && await Hash(part, token) != expectedHash) throw new SetupException($"{resource.Name} 的 SHA-256 不匹配，未安装该文件。");
                File.Move(part, target); File.Delete(metaPath);
                progress?.Report(new(resource.Name, offset + added, offset + added, added / Math.Max(watch.Elapsed.TotalSeconds, .01)));
                return target;
            }
            catch (Exception ex) when ((ex is HttpRequestException || ex is IOException || ex is OperationCanceledException && !token.IsCancellationRequested) && attempt < 3)
            { await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token); }
        }
        throw new SetupException($"{resource.Name} 下载未完成，已保留进度。请检查网络后继续。");
    }
    public void Dispose() { if (owned) http.Dispose(); }
    private sealed record DownloadMeta(string Sha256, string? ETag, string? Url = null);
}

public interface ICommandRunner
{
    Task<string> Run(string exe, IEnumerable<string> args, CancellationToken token, int seconds = 120);
}

public sealed class CommandRunner : ICommandRunner
{
    public async Task<string> Run(string exe, IEnumerable<string> args, CancellationToken token, int seconds = 120)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var arguments = args.ToArray();
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new SetupException("无法启动所需组件。");
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { try { process.Kill(true); } catch { } throw; }
        var text = await output; var failure = await error;
        if (process.ExitCode != 0) throw new SetupException(CommandFailure.Describe(exe, arguments, process.ExitCode, failure));
        return text;
    }
}
