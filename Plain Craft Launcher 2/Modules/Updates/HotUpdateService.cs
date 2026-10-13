using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCL.Core.Utils;
using PCL.Network;

namespace PCL;

/// <summary>
/// v50.10 热更新链路的应用层视图。
///
/// 与引导器共用同一份 version.json 清单，但这里只做「检查与提示」——app\ 里放的是
/// 正在运行的程序本体，Windows 锁着这些文件，运行期无法自我替换。真正的下载、
/// 验签、替换由引导器在应用退出后完成：应用写 .update-restart 标记再正常退出，
/// 等待中的引导器看到标记就重新执行「更新检查 → 拉起应用」。
///
/// 清单源与引导器保持一致（引导器支持 PCL_UPDATE_MANIFEST 覆盖，环境变量会被
/// 子进程继承，所以这里读同一个变量即可保持一致）。
///
/// v50.11.19 检查链路重做：结果落盘缓存 + 启动后台预热（进页面先出画面），单次检查
/// 有全局时间预算（旧写法走 RequestParam.WithRetry = 30 秒 × 3 次串行重试，GitHub 被
/// SYN 黑洞时实测卡 64 秒才报错），清单源由打包机随包下发、GitHub 恒为兜底源。
/// </summary>
public static class HotUpdateService
{
    /// <summary>
    /// 清单公钥（ECDSA P-256 的 SubjectPublicKeyInfo，base64）。必须与引导器
    /// UpdateService.ManifestPublicKey 一致；私钥只在打包机上，更换密钥=更换客户端。
    /// </summary>
    private const string ManifestPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEZfDDySjKzqKaWrzQ4N8nK0VVtv+sAxGeJDLEXSxpKh37knCmJv+X3icAkCs37hY2PNtgly67YjCmsGQoYs1E5A==";

    /// <summary>
    /// 兜底源 GitHub Releases：清单源配置无论出什么问题（写错、缺失、自建源宕机），
    /// 这条总会被追加到列表末尾，玩家不至于彻底收不到更新。
    /// </summary>
    private const string GithubManifest =
        "https://github.com/Fangshang95/PCL-online/releases/latest/download/version.json";

    /// <summary>单次检查的总时间预算，所有源共用；用完就取缓存或报失败，不再往下试。</summary>
    private const int OverallBudgetMs = 10000;

    private const int PerRequestTimeoutMs = 8000;
    private const int SignatureTimeoutMs = 4000;

    /// <summary>这段时间内打开设置页直接用缓存结果，不联网。</summary>
    private const int DisplayCacheMinutes = 30;

    /// <summary>缓存超过这个时长就在后台静默刷新（页面仍先显示旧结果）。</summary>
    private const int FreshMinutes = 5;

    /// <summary>随包下发的清单源配置文件名（一行一个 URL，# 开头为注释）。</summary>
    private const string SourcesFileName = "update-sources.txt";

    /// <summary>应用层的版本标记：引导器自展开与每次更新后都会写 app\version.json。</summary>
    public static string AppVersionFile =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.json");

    /// <summary>当前应用层版本（读不到清单时退回程序内置版本号，即未走热更新的老安装）。</summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                if (File.Exists(AppVersionFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(AppVersionFile));
                    if (doc.RootElement.TryGetProperty("version", out var v))
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
            catch
            {
                // 清单损坏按"未版本化"处理，退回内置版本号
            }
            return ModBase.versionBaseName;
        }
    }

    /// <summary>
    /// 实际使用的清单源，按优先级：环境变量 PCL_UPDATE_MANIFEST（多个用分号分隔）→
    /// 随包下发的 update-sources.txt → GitHub Releases 兜底。真实自建地址只存在于
    /// 发布包里，源码树与公开仓库不含任何服务器地址（与引导器 <c>ManifestUrls()</c> 同规则）。
    /// </summary>
    public static string[] ManifestUrls()
    {
        var urls = new List<string>();
        Collect(urls, Environment.GetEnvironmentVariable("PCL_UPDATE_MANIFEST"), new[] { ';', ',' });
        Collect(urls, ReadSourcesDisk(), new[] { '\n', '\r', ',', ';' });
        // GitHub 永远排最后：前面的源全挂了才轮到它，配好了也不会被它拖慢
        urls.RemoveAll(u => string.Equals(u, GithubManifest, StringComparison.OrdinalIgnoreCase));
        urls.Add(GithubManifest);
        return urls.ToArray();
    }

    private static void Collect(List<string> into, string? text, char[] separators)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var raw in text.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var url = raw.Trim();
            if (url.Length == 0 || url.StartsWith("#")) continue;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            if (!into.Contains(url, StringComparer.OrdinalIgnoreCase)) into.Add(url);
        }
    }

    /// <summary>数据目录与 app\ 目录都找一遍：玩家只覆盖 exe 时配置照样跟着包走。</summary>
    private static string? ReadSourcesDisk()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (var dir in new[]
                 { baseDir, Path.GetDirectoryName(baseDir.TrimEnd(Path.DirectorySeparatorChar)) })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try
            {
                var path = Path.Combine(dir, SourcesFileName);
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch
            {
                // 读不到就继续往下兜底
            }
        }
        return null;
    }

    private static string CacheDir => Path.Combine(ModBase.pathTemp, "Cache", "Update");
    private static string ManifestCacheFile => Path.Combine(CacheDir, "pcloonline-version.json");
    private static string CacheStampFile => Path.Combine(CacheDir, "pcloonline-version.time");

    /// <summary>写缓存用的编码：File.WriteAllText(Encoding.UTF8) 会带 BOM，把签名原文改得不认识。</summary>
    private static readonly UTF8Encoding NoBomUtf8 = new(false);

    public sealed record CheckResult(
        bool Ok, string LocalVersion, string RemoteVersion,
        bool HasUpdate, long DownloadSize, bool Incremental, string Message);

    /// <summary>缓存是否已经旧到需要后台刷新。</summary>
    public static bool CacheIsStale() => !CacheWithin(FreshMinutes);

    private static bool CacheWithin(int minutes)
    {
        try
        {
            if (!File.Exists(ManifestCacheFile)) return false;
            var written = File.GetLastWriteTimeUtc(ManifestCacheFile);
            // 时间戳文件存 Unix 秒：文件时间会被复制/同步工具改写，本地时钟也会被人调，
            // 这两个都不可信时以我们自己记的为准（绝不能按本地时区解释，否则东八区会把
            // 缓存算成 8 小时后的时间，于是永远"还新鲜"，玩家再也等不到新版本）
            if (File.Exists(CacheStampFile) &&
                long.TryParse(File.ReadAllText(CacheStampFile).Trim(), out var unixSeconds))
                written = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            return (DateTime.UtcNow - written).TotalMinutes < minutes;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读缓存里的上次结果（<see cref="DisplayCacheMinutes"/> 分钟内有效），没有则返回 null。</summary>
    public static CheckResult? PeekCached()
    {
        if (!CacheWithin(DisplayCacheMinutes)) return null;
        try
        {
            return Parse(File.ReadAllText(ManifestCacheFile));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 启动时的后台预热：拉一次清单写进缓存，玩家打开设置页时直接出结果。
    /// 缓存还新鲜就完全不占网络——重启启动器不该重复检查。
    /// </summary>
    public static void Prefetch()
    {
        if (CacheWithin(FreshMinutes)) return;
        try
        {
            CheckAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[HotUpdate] 启动预热失败（不影响使用）");
        }
    }

    /// <summary>
    /// 拉清单并判断有没有更新。只读网络与本地版本，不改任何文件（结果缓存除外）。
    /// 源按 <see cref="ManifestUrls"/> 顺序串行尝试，总耗时受 <see cref="OverallBudgetMs"/> 约束。
    /// </summary>
    public static async Task<CheckResult> CheckAsync()
    {
        var sw = Stopwatch.StartNew();
        foreach (var url in ManifestUrls())
        {
            var left = OverallBudgetMs - (int)sw.ElapsedMilliseconds;
            if (left < 1000)
            {
                ModBase.Log("[HotUpdate] 检查超时预算用完，剩余的源不再尝试");
                break;
            }
            try
            {
                var text = await Requester.FetchStringAsync(url,
                    new RequestParam { Timeout = Math.Min(PerRequestTimeoutMs, left), Retries = 1 })
                    .ConfigureAwait(false);
                if (!await SignatureAcceptableAsync(url, text, left).ConfigureAwait(false)) continue;
                var result = Parse(text);
                if (result is null)
                {
                    ModBase.Log("[HotUpdate] 清单缺少 version 字段，弃用：" + url);
                    continue;
                }
                // 严格禁降级，对所有源一视同仁（GitHub 也不豁免）：残留/伪造的陈旧清单签名一样有效、
                // 包 sha256 也自洽，完整性检查拦不住"合法但更旧"。坏版本的处理方式是往前发修复版。
                if (IsOlder(result.RemoteVersion, result.LocalVersion))
                {
                    ModBase.Log($"[HotUpdate] 清单版本不高于本地（{result.RemoteVersion} ≤ " +
                                $"{result.LocalVersion}），忽略：{url}");
                    continue;
                }
                SaveCache(text);
                return result;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[HotUpdate] 清单源不可用：" + url);
            }
        }

        var cached = PeekCached();
        if (cached is not null) return cached;
        return new CheckResult(false, CurrentVersion, "", false, 0, false,
            "暂时获取不到更新清单（不影响使用，稍后再试）");
    }

    /// <summary>
    /// 明文 HTTP 源在链路上可能被改包，验不过签名就整个弃用；HTTPS 源沿用引导器策略：
    /// 取到签名就必须验过，取不到只按未签名处理（兼容逐步上线签名的过渡期）。
    /// </summary>
    private static async Task<bool> SignatureAcceptableAsync(string manifestUrl, string manifestText, int budgetLeftMs)
    {
        var plainHttp = manifestUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var timeout = Math.Max(1000, Math.Min(SignatureTimeoutMs, budgetLeftMs));
        string? sigText;
        try
        {
            sigText = await Requester.FetchStringAsync(manifestUrl + ".sig",
                new RequestParam { Timeout = timeout, Retries = 1 }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!plainHttp)
            {
                ModBase.Log(ex, "[HotUpdate] 未取到清单签名，按未签名处理：" + manifestUrl);
                return true;
            }
            ModBase.Log(ex, "[HotUpdate] 明文源取不到签名，弃用：" + manifestUrl);
            return false;
        }
        if (VerifyManifest(manifestText, sigText)) return true;
        ModBase.Log("[HotUpdate] 清单签名校验失败，弃用该源：" + manifestUrl);
        return false;
    }

    /// <summary>
    /// 用内嵌公钥验签（ECDSA P-256 / SHA-256 / IEEE P1363）。签的是清单原始字节，这里拿的是
    /// 文本再按 UTF-8 编回去——发布脚本写的是无 BOM 的 UTF-8，所以字节一致。
    /// </summary>
    private static bool VerifyManifest(string manifestText, string sigBase64)
    {
        try
        {
            var sig = Convert.FromBase64String(sigBase64.Trim());
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ManifestPublicKey), out _);
            return ecdsa.VerifyData(Encoding.UTF8.GetBytes(manifestText), sig, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析清单文本。缺少 version 字段时返回 null（该源弃用）。</summary>
    private static CheckResult? Parse(string manifestText)
    {
        using var doc = JsonDocument.Parse(manifestText);
        var root = doc.RootElement;
        if (!root.TryGetProperty("version", out var v)) return null;
        var remote = v.GetString();
        if (string.IsNullOrWhiteSpace(remote)) return null;

        var local = CurrentVersion;
        var hasUpdate = IsNewer(remote, local);
        var (size, incremental) = PickPackage(root, local);
        return new CheckResult(true, local, remote, hasUpdate, size, incremental,
            hasUpdate ? "" : "已是最新版本");
    }

    private static void SaveCache(string manifestText)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            // 不带 BOM：缓存要尽量保持签名原文的字节，将来直接对缓存验签也才不会莫名失败
            File.WriteAllText(ManifestCacheFile, manifestText, NoBomUtf8);
            File.WriteAllText(CacheStampFile,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), NoBomUtf8);
        }
        catch (Exception ex)
        {
            // 缓存只影响下次打开页面的首屏速度，写失败不算错
            ModBase.Log(ex, "[HotUpdate] 清单缓存写入失败");
        }
    }

    private static bool IsNewer(string remote, string local)
    {
        try
        {
            return SemVer.Parse(remote) > SemVer.Parse(local);
        }
        catch
        {
            // 版本号不是合法语义版本时退化为"不相同即更新"
            return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>远端是否比本地更旧。版本号比不出来时返回 false（宁可放行，不把玩家锁死在旧缓存上）。</summary>
    private static bool IsOlder(string remote, string local)
    {
        try
        {
            return SemVer.Parse(remote) < SemVer.Parse(local);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>选包展示用：v50.11 起只有一种包——全量无框架版（app.zip），整体覆盖 app\。</summary>
    private static (long Size, bool Incremental) PickPackage(JsonElement root, string local)
    {
        if (root.TryGetProperty("packages", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ps.EnumerateArray())
            {
                var type = p.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!string.Equals(type, "full", StringComparison.OrdinalIgnoreCase)) continue;
                var size = p.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                return (size, false);
            }
        }
        return (0, false);
    }

    /// <summary>
    /// 请求"退出并更新"：写重启标记后由调用方正常退出程序。
    /// 等待中的引导器会据此重新执行更新检查（此时文件锁已全部释放），完成后再拉起应用。
    /// 返回 false 表示标记没写成（例如目录只读），调用方应提示用户手动重启。
    /// </summary>
    public static bool RequestRestartToUpdate()
    {
        try
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".update-restart"),
                DateTimeOffset.Now.ToString("s"));
            ModBase.Log("[HotUpdate] 已写重启标记，应用退出后引导器将自动完成更新");
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[HotUpdate] 写入重启标记失败");
            return false;
        }
    }
}
