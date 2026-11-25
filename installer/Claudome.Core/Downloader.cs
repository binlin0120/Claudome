using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ClaudeSetup;

public sealed class Downloader(HttpClient? client = null) : IDisposable
{
    public const int DefaultMaxParallelStreams = 16;
    public const long DefaultParallelThresholdBytes = 2L * 1024 * 1024;
    private readonly HttpClient http = client ?? new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = DefaultMaxParallelStreams }) { Timeout = Timeout.InfiniteTimeSpan };
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
        return DownloadCoreAsync(resource, directory, requireSha256: true, DefaultParallelThresholdBytes, progress, token);
    }

    internal Task<string> GetWithThresholdAsync(Resource resource, string directory, long parallelThresholdBytes, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(resource.Sha256, "^[a-fA-F0-9]{64}$"))
            throw new SetupException("资源缺少可信的 SHA-256，已停止下载。");
        return DownloadCoreAsync(resource, directory, requireSha256: true, parallelThresholdBytes, progress, token);
    }

    public Task<string> GetOfficialOptionalHashAsync(Resource resource, string directory, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(resource.Sha256) &&
            !System.Text.RegularExpressions.Regex.IsMatch(resource.Sha256, "^[a-fA-F0-9]{64}$"))
            throw new SetupException("SHA-256 应为 64 位十六进制字符。");
        return DownloadCoreAsync(resource, directory, requireSha256: !string.IsNullOrWhiteSpace(resource.Sha256), DefaultParallelThresholdBytes, progress, token);
    }

    private static void QuarantineCorruptPart(string part, params string[] auxiliaryPaths)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        if (File.Exists(part))
        {
            var dest = $"{part}.corrupt-{stamp}.quarantine";
            try { File.Move(part, dest, overwrite: true); } catch { try { File.Delete(part); } catch { } }
        }
        foreach (var aux in auxiliaryPaths)
        {
            if (File.Exists(aux))
            {
                try { File.Delete(aux); } catch { }
            }
        }
    }

    private async Task<string> DownloadCoreAsync(Resource resource, string directory, bool requireSha256, long parallelThresholdBytes, IProgress<DownloadProgress>? progress, CancellationToken token)
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
        var part = target + ".part"; var metaPath = part + ".json"; var chunksPath = part + ".chunks.json";
        Paths.RejectLinks(part); Paths.RejectLinks(metaPath); Paths.RejectLinks(chunksPath);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(chunksPath))
                {
                    var resumedParallel = await TryResumeParallelChunksAsync(uri, resource, requireSha256, expectedHash, part, metaPath, chunksPath, target, progress, token);
                    if (resumedParallel is not null) return resumedParallel;
                }
                var offset = File.Exists(part) ? new FileInfo(part).Length : 0L;
                DownloadMeta? metadata = null;
                if (File.Exists(metaPath)) metadata = JsonFile.Read<DownloadMeta>(metaPath);
                if (offset > 0 && ((requireSha256 && metadata?.Sha256 != resource.Sha256) || (!requireSha256 && metadata?.Url != resource.Url) || (resource.Size > 0 && offset > resource.Size)))
                {
                    QuarantineCorruptPart(part, metaPath, chunksPath);
                    throw new SetupException("已有下载分片与本次版本不一致，已隔离损坏分片，可重试重新下载。");
                }
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
                    QuarantineCorruptPart(part, metaPath, chunksPath);
                    throw new SetupException("下载分片不完整且服务器无法继续，已隔离损坏分片，可重试下载。");
                }
                response.EnsureSuccessStatusCode();
                var resume = response.StatusCode == HttpStatusCode.PartialContent && offset > 0;
                if (resume && response.Content.Headers.ContentRange?.From != offset)
                {
                    QuarantineCorruptPart(part, metaPath, chunksPath);
                    throw new SetupException("服务器返回的下载范围不正确，已停止写入。");
                }
                if (response.StatusCode == HttpStatusCode.PartialContent && !resume) throw new SetupException("服务器返回了非预期的分段响应。");
                if (!resume) offset = 0;
                var total = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength is long length ? length + offset : null);
                if (resource.Size > 0 && total.HasValue && total != resource.Size) throw new SetupException("资源大小与版本清单不一致。");

                var effectiveUri = response.RequestMessage?.RequestUri ?? uri;
                var supportsRanges = response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase);
                if (!resume && total is { } knownTotal && knownTotal >= parallelThresholdBytes && supportsRanges)
                {
                    var etagHeader = response.Headers.ETag?.ToString();
                    response.Dispose();
                    return await DownloadParallelRangesAsync(effectiveUri, resource, requireSha256, expectedHash, knownTotal, etagHeader, part, metaPath, chunksPath, target, progress, token);
                }

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
                if (requireSha256 && await Hash(part, token) != expectedHash)
                {
                    QuarantineCorruptPart(part, metaPath, chunksPath);
                    throw new SetupException($"{resource.Name} 的 SHA-256 不匹配，未安装该文件。");
                }
                File.Move(part, target); File.Delete(metaPath);
                progress?.Report(new(resource.Name, offset + added, offset + added, added / Math.Max(watch.Elapsed.TotalSeconds, .01)));
                return target;
            }
            catch (Exception ex) when ((ex is HttpRequestException || ex is IOException || ex is OperationCanceledException && !token.IsCancellationRequested) && attempt < 3)
            { await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token); }
        }
        throw new SetupException($"{resource.Name} 下载未完成，已保留进度。请检查网络后继续。");
    }

    private async Task<string?> TryResumeParallelChunksAsync(
        Uri uri, Resource resource, bool requireSha256, string expectedHash,
        string part, string metaPath, string chunksPath, string target,
        IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        ParallelChunkState? state = null;
        try { state = JsonFile.Read<ParallelChunkState>(chunksPath); } catch { }
        if (state is null || !File.Exists(part) || state.Chunks.Length == 0 ||
            (requireSha256 && state.Sha256 != resource.Sha256) ||
            (!requireSha256 && state.Url != resource.Url) ||
            new FileInfo(part).Length != state.TotalBytes)
        {
            QuarantineCorruptPart(part, metaPath, chunksPath);
            return null;
        }
        return await ExecuteParallelChunksAsync(uri, resource, requireSha256, expectedHash, state, part, metaPath, chunksPath, target, progress, token);
    }

    private async Task<string> DownloadParallelRangesAsync(
        Uri effectiveUri, Resource resource, bool requireSha256, string expectedHash,
        long totalBytes, string? etag, string part, string metaPath, string chunksPath, string target,
        IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var chunkCount = Math.Min(DefaultMaxParallelStreams, Math.Max(2, (int)Math.Ceiling((double)totalBytes / Math.Max(1, totalBytes / DefaultMaxParallelStreams))));
        if (chunkCount > DefaultMaxParallelStreams) chunkCount = DefaultMaxParallelStreams;
        var baseSize = totalBytes / chunkCount;
        var chunks = new ChunkRange[chunkCount];
        long pos = 0;
        for (var i = 0; i < chunkCount; i++)
        {
            var end = (i == chunkCount - 1) ? totalBytes - 1 : pos + baseSize - 1;
            chunks[i] = new ChunkRange(pos, end, false);
            pos = end + 1;
        }
        using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            fs.SetLength(totalBytes);
        var state = new ParallelChunkState(resource.Sha256, resource.Url, totalBytes, etag, chunks);
        JsonFile.Write(chunksPath, state);
        return await ExecuteParallelChunksAsync(effectiveUri, resource, requireSha256, expectedHash, state, part, metaPath, chunksPath, target, progress, token);
    }

    private async Task<string> ExecuteParallelChunksAsync(
        Uri uri, Resource resource, bool requireSha256, string expectedHash,
        ParallelChunkState state, string part, string metaPath, string chunksPath, string target,
        IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var stateLock = new object();
        long completedBefore = state.Chunks.Where(c => c.Completed).Sum(c => c.End - c.Start + 1);
        long received = completedBefore;
        var watch = Stopwatch.StartNew();
        long lastReportTicks = 0;

        using var fileHandle = File.OpenHandle(part, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);
        var pendingIndices = Enumerable.Range(0, state.Chunks.Length).Where(i => !state.Chunks[i].Completed).ToArray();
        await Parallel.ForEachAsync(pendingIndices, new ParallelOptions { MaxDegreeOfParallelism = DefaultMaxParallelStreams, CancellationToken = token }, async (idx, ct) =>
        {
            var chunk = state.Chunks[idx];
            for (var chunkAttempt = 1; chunkAttempt <= 3; chunkAttempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                    req.Headers.UserAgent.ParseAdd("Claudome/0.1");
                    req.Headers.Range = new RangeHeaderValue(chunk.Start, chunk.End);
                    if (chunkAttempt == 1 && state.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag) && !tag.IsWeak)
                        req.Headers.IfRange = new RangeConditionHeaderValue(tag);
                    using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    watchdog.CancelAfter(TimeSpan.FromSeconds(60));
                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
                    if (resp.RequestMessage?.RequestUri?.Scheme != "https")
                        throw new SetupException("下载发生了不安全的协议跳转。");
                    if (resp.StatusCode != HttpStatusCode.PartialContent ||
                        resp.Content.Headers.ContentRange?.From != chunk.Start ||
                        resp.Content.Headers.ContentRange?.To != chunk.End)
                        throw new SetupException("服务器返回的并行分段范围不一致。");

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    var buffer = new byte[128 * 1024];
                    long writePos = chunk.Start;
                    long expectedLen = chunk.End - chunk.Start + 1;
                    long chunkRead = 0;
                    while (chunkRead < expectedLen)
                    {
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        readTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                        var toRead = (int)Math.Min(buffer.Length, expectedLen - chunkRead);
                        var count = await stream.ReadAsync(buffer.AsMemory(0, toRead), readTimeout.Token);
                        if (count == 0) break;
                        await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, count), writePos, ct);
                        writePos += count;
                        chunkRead += count;
                        var totalSoFar = Interlocked.Add(ref received, count);
                        var nowTicks = watch.ElapsedMilliseconds;
                        if (nowTicks - Interlocked.Read(ref lastReportTicks) > 250)
                        {
                            Interlocked.Exchange(ref lastReportTicks, nowTicks);
                            progress?.Report(new(resource.Name, totalSoFar, state.TotalBytes, (totalSoFar - completedBefore) / Math.Max(watch.Elapsed.TotalSeconds, .01)));
                        }
                    }
                    if (chunkRead != expectedLen) throw new IOException("Incomplete parallel chunk");
                    lock (stateLock)
                    {
                        state.Chunks[idx] = chunk with { Completed = true };
                        JsonFile.Write(chunksPath, state);
                    }
                    return;
                }
                catch (Exception ex) when ((ex is HttpRequestException || ex is IOException || ex is OperationCanceledException && !ct.IsCancellationRequested) && chunkAttempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(chunkAttempt), ct);
                }
            }
            throw new IOException($"Chunk {idx} failed after retries");
        });
        fileHandle.Dispose();
        if (new FileInfo(part).Length != state.TotalBytes)
        {
            QuarantineCorruptPart(part, metaPath, chunksPath);
            throw new SetupException($"{resource.Name} 文件长度校验不一致，已隔离分片。");
        }
        if (requireSha256 && await Hash(part, token) != expectedHash)
        {
            QuarantineCorruptPart(part, metaPath, chunksPath);
            throw new SetupException($"{resource.Name} 的 SHA-256 不匹配，已隔离损坏分片。");
        }
        File.Move(part, target);
        if (File.Exists(chunksPath)) try { File.Delete(chunksPath); } catch { }
        if (File.Exists(metaPath)) try { File.Delete(metaPath); } catch { }
        progress?.Report(new(resource.Name, state.TotalBytes, state.TotalBytes, (state.TotalBytes - completedBefore) / Math.Max(watch.Elapsed.TotalSeconds, .01)));
        return target;
    }

    public void Dispose() { if (owned) http.Dispose(); }
    private sealed record DownloadMeta(string Sha256, string? ETag, string? Url = null);
    private sealed record ChunkRange(long Start, long End, bool Completed);
    private sealed record ParallelChunkState(string Sha256, string Url, long TotalBytes, string? ETag, ChunkRange[] Chunks);
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
