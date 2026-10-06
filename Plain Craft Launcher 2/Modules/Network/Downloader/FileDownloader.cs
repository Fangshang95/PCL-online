using System.IO;
using System.Net.Http;
using Downloader;
using PCL.Core.IO.Net;


namespace PCL.Network;

public static class FileDownloader
{
    public static async Task DownloadAsync(string url, string localPath, bool useBrowserUserAgent = false,
        string customUserAgent = "", CancellationToken cancellationToken = default,
        bool enableParallelChunks = true, DownloadFile? trackedFile = null)
    {
        await DownloadCoreAsync([url], localPath, useBrowserUserAgent, customUserAgent, cancellationToken,
            enableParallelChunks, trackedFile).ConfigureAwait(false);
    }

    public static async Task DownloadAsync(IEnumerable<string> urls, string localPath, bool useBrowserUserAgent = false,
        string customUserAgent = "", CancellationToken cancellationToken = default,
        bool enableParallelChunks = true, DownloadFile? trackedFile = null)
    {
        await DownloadCoreAsync(urls, localPath, useBrowserUserAgent, customUserAgent, cancellationToken,
            enableParallelChunks, trackedFile).ConfigureAwait(false);
    }

    public static void DownloadByLoader(string url, string localPath, bool useBrowserUserAgent = false,
        string customUserAgent = "")
    {
        DownloadAsync(url, localPath, useBrowserUserAgent, customUserAgent).GetAwaiter().GetResult();
    }

    public static void DownloadByLoader(IEnumerable<string> urls, string localPath, bool useBrowserUserAgent = false,
        string customUserAgent = "")
    {
        DownloadAsync(urls, localPath, useBrowserUserAgent, customUserAgent).GetAwaiter().GetResult();
    }

    private static async Task DownloadCoreAsync(IEnumerable<string> urls, string localPath, bool useBrowserUserAgent,
        string customUserAgent, CancellationToken cancellationToken, bool enableParallelChunks, DownloadFile? trackedFile)
    {
        var urlList = urls.Select(url => RequestSigning.SecretCdnSign(url.Trim())).Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct().ToList();
        if (urlList.Count == 0)
            throw new ArgumentException("未提供可用的下载地址", nameof(urls));

        Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? throw new ArgumentException("下载路径无效", nameof(localPath)));

        Exception? lastException = null;
        foreach (var url in urlList)
        {
            try
            {
                await DownloadSingleAsync(url, localPath, useBrowserUserAgent, customUserAgent, cancellationToken,
                    enableParallelChunks, trackedFile).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                CleanupTempFiles(localPath);
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                // 换源时**不要**删掉已下载的部分：开着续传的话，Downloader 会接着
                // 已有分块继续下。原实现在这里调 CleanupTempFiles，等于每次换源
                // 都把进度清零——镜像源一超时，6MB 的 Forge installer 就得从 0 重下，
                // 正是"整合包安装卡在下载 Forge 主文件、速度只有几 KB/s"的直接原因。
                ModBase.Log(ex, $"[Download] 下载失败，尝试下一个源：{url}", ModBase.LogLevel.Debug);
            }
        }

        throw new IOException($"下载失败：{localPath}", lastException);
    }

    private static async Task DownloadSingleAsync(string url, string localPath, bool useBrowserUserAgent,
        string customUserAgent, CancellationToken cancellationToken, bool enableParallelChunks, DownloadFile? trackedFile)
    {
        ModBase.Log($"[Download] 开始下载：{url} -> {localPath}");
        // 只在"没有可续传的半成品"时才清理。开着 EnableAutoResumeDownload 后，
        // Downloader 依赖 <localPath>.pcldownloading 里的已完成分块做续传，
        // 无条件删掉就等于每次都从头开始。
        if (!File.Exists(localPath + ModNet.netDownloadEnd))
            CleanupTempFiles(localPath);

        var perFileThreadLimit = enableParallelChunks ? Math.Max(1, ModNet.NetTaskThreadLimit) : 1;
        // 限制最大分块数，防止大文件下载时内存爆炸
        var chunkCount = Math.Min(perFileThreadLimit, 4);
        var configuration = new DownloadConfiguration
        {
            ChunkCount = chunkCount,
            ParallelCount = chunkCount,
            ParallelDownload = chunkCount > 1,
            MaximumBytesPerSecond = ModNet.NetTaskSpeedLimitHigh > 0 ? ModNet.NetTaskSpeedLimitHigh : 0,
            MaxTryAgainOnFailure = 2,
            BlockTimeout = 60000,
            DownloadFileExtension = ModNet.netDownloadEnd,
            // 开启断点续传：换源/重试时接着已有分块下，而不是清零重来
            EnableAutoResumeDownload = true,
            CustomHttpClientFactory = () => GetHttpClient(url),
            MinimumSizeOfChunking = 1024 * 1024L,
            MaximumMemoryBufferBytes = 256L * 1024 * 1024,
        };

        using var downloader = new DownloadService(configuration);
        using var cancelReg = cancellationToken.Register(() =>
        {
            try { downloader.CancelAsync(); } catch {  } // 忽略
        });
        var tcs = new TaskCompletionSource<bool>();
        void UpdateDownloadStat(DownloadProgressChangedEventArgs args)
        {
            if (trackedFile is null)
                return;

            trackedFile.State = PCL.Network.NetState.Downloading;
            trackedFile.TotalSize = Math.Max(trackedFile.TotalSize, args.TotalBytesToReceive);
            trackedFile.IsUnknownSize = trackedFile.TotalSize <= 0;
            trackedFile.DownloadedBytes = Math.Max(trackedFile.DownloadedBytes, args.ReceivedBytesSize);
            trackedFile.Speed = Math.Max(0L, (long)Math.Round(args.BytesPerSecondSpeed));
            trackedFile.ActiveThreads = Math.Max(0, args.ActiveChunks);
        }

        downloader.DownloadStarted += (_, args) =>
        {
            if (trackedFile is null)
                return;

            trackedFile.State = PCL.Network.NetState.Reading;
            trackedFile.TotalSize = Math.Max(trackedFile.TotalSize, args.TotalBytesToReceive);
            trackedFile.IsUnknownSize = args.TotalBytesToReceive <= 0;
            trackedFile.DownloadedBytes = 0;
            trackedFile.Speed = 0;
            trackedFile.ActiveThreads = 0;
        };
        downloader.DownloadProgressChanged += (_, args) => UpdateDownloadStat(args);
        downloader.ChunkDownloadProgressChanged += (_, args) => UpdateDownloadStat(args);
        downloader.DownloadFileCompleted += (_, args) =>
        {
            if (trackedFile is not null)
            {
                trackedFile.Speed = 0;
                trackedFile.ActiveThreads = 0;
                trackedFile.DownloadedBytes = Math.Max(trackedFile.DownloadedBytes, trackedFile.TotalSize);
            }

            if (args.Cancelled)
                tcs.TrySetCanceled();
            else if (args.Error != null)
                tcs.TrySetException(args.Error);
            else
                tcs.TrySetResult(true);
        };
        try
        {
            await downloader.DownloadFileTaskAsync(url, localPath, cancellationToken).ConfigureAwait(false);
            await tcs.Task.ConfigureAwait(false);
            var tempPath = localPath + ModNet.netDownloadEnd;
            if (!File.Exists(localPath) && File.Exists(tempPath))
            {
                for (var retry = 0; retry < 5; retry++)
                {
                    try
                    {
                        File.Move(tempPath, localPath, true);
                        break;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            if (!File.Exists(localPath))
                throw new IOException($"下载未产生任何文件：{localPath}");
            ModBase.Log($"[Download] 下载成功：{localPath}");
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TaskCanceledException ex)
        {
            throw new TimeoutException($"下载超时（{url}）", ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException($"下载失败：{url}", ex);
        }
    }

    private static void CleanupTempFiles(string localPath)
    {
        var tempPath = localPath + ModNet.netDownloadEnd;
        TryDeleteFile(localPath);
        TryDeleteFile(tempPath);
    }

    private static void TryDeleteFile(string path)
    {
        for (var retry = 0; retry < 5; retry++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static HttpClient GetHttpClient(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri)
            && parsedUri.Host is "edge.forgecdn.net" or "mediafilez.forgecdn.net" or "forgecdn.net" or "api.curseforge.com")
        {
            return NetworkService.GetClient(NetworkService.CurseForgeApi);
        }
        
        return NetworkService.GetClient();
    }
}
