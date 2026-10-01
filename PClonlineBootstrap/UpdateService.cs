using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace PClonlineBootstrap;

/// <summary>清单里的单个文件（sha256 校验用）。</summary>
internal sealed record ManifestFile(string Path, long Size, string Sha256);

/// <summary>
/// 清单里的运行时包（net.zip）：缺运行时时下它，解压到 runtime\ 当移动运行时用。
/// Files 只取一份抽样（构建侧截断到 200 条），够校验完整性、不必整套下下来。
/// </summary>
internal sealed record ManifestRuntime(string Url, long Size, string Sha256, int Count,
                                        IReadOnlyList<ManifestFile> Files);

/// <summary>
/// 可下载的应用层包。v50.11 起只有一种：全量无框架版（每次更新整体覆盖 app\），
/// 多基线 / 文件级 diff 那套复杂度已经去掉。
/// </summary>
internal sealed record ManifestPackage(string Type, string From, string Url, long Size, string Sha256);

/// <summary>远端更新清单（由 build_update.py 生成并随包发布）。</summary>
internal sealed record UpdateManifest(string Version, IReadOnlyList<ManifestFile> Files,
                                      ManifestRuntime? Runtime, IReadOnlyList<ManifestPackage> Packages,
                                      string SourceUrl);

/// <summary>
/// 更新服务（v50.11，两层分离：runtime\ 不动，app\ 全量覆盖）。
///
/// 流程：拉清单（验签）→ 与本地版本比对 → 下 app.zip（全量无框架版）
///       → 校验包 sha256 → 逐文件 sha256 复核 → 覆盖写 app\ → 写版本标记。
///
/// 三条硬规则：
///   ① 任何异常都不阻断启动（更新是增益，不是前提）；
///   ② 内容以 sha256 为准，来源以 ECDSA 签名为准（将来接 P2P / CDN 镜像时来源不可信但内容可验）；
///   ③ 坏包在落地前就拦掉：包校验 + 逐文件复核都过了才写盘，所以不做备份回滚
///      （覆盖中途失败的话本地版本标记仍是旧的，下次启动会重新更新一遍）。
/// </summary>
internal static class UpdateService
{
    /// <summary>
    /// 清单公钥（ECDSA P-256 的 SubjectPublicKeyInfo，base64）。
    /// 对应私钥不入库，只保存在打包机的 secrets 目录；更换密钥=更换客户端，不要轻易动。
    /// </summary>
    private const string ManifestPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEZfDDySjKzqKaWrzQ4N8nK0VVtv+sAxGeJDLEXSxpKh37knCmJv+X3icAkCs37hY2PNtgly67YjCmsGQoYs1E5A==";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(60) };

    /// <summary>拉清单并执行更新。返回解析后的清单（供运行时安装复用），没拿到就返回 null。</summary>
    public static async Task<UpdateManifest?> RunAsync(string appDir, IReadOnlyList<string> manifestUrls,
                                                       Action<string> log)
    {
        // 声明在 try 外：catch 之后还要把它交给运行时安装器
        UpdateManifest? manifest = null;
        try
        {
            manifest = await FetchManifestAsync(manifestUrls, log);
            if (manifest is null) { log("未能获取更新清单，跳过更新"); return null; }

            var localVer = LocalVersion(appDir);
            if (!string.IsNullOrEmpty(localVer) && localVer == manifest.Version)
            {
                log("已是最新版本：" + manifest.Version);
                return manifest;
            }
            log($"发现更新：{localVer ?? "（未知）"} → {manifest.Version}");

            // v50.11 起只有全量无框架包：不管本地是哪个版本，一律拉 app.zip 覆盖
            var pkg = manifest.Packages?.FirstOrDefault(p =>
                string.Equals(p.Type, "full", StringComparison.OrdinalIgnoreCase));
            if (pkg is null) { log("清单中没有可用的更新包，跳过"); return manifest; }
            var pkgUrl = ResolveUrl(manifest.SourceUrl, pkg.Url);
            log($"使用全量包：{pkgUrl}（{pkg.Size / 1048576} MB）");

            var stagingDir = Path.Combine(appDir, ".staging");
            var pkgPath = Path.Combine(stagingDir, "app.zip");

            try
            {
                Directory.CreateDirectory(stagingDir);
                await DownloadAsync(pkgUrl, pkgPath, log);
                if (!string.Equals(Sha256Of(pkgPath), pkg.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    log("更新包校验失败（sha256 不符），放弃本次更新");
                    return manifest;
                }

                var extractDir = Path.Combine(stagingDir, "files");
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(pkgPath, extractDir, overwriteFiles: true);

                // 落地前逐文件复核：以清单 sha256 为准，一个不符就整包放弃（不落地半个更新）
                var pending = new List<string>();
                foreach (var file in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(extractDir, file).Replace("\\", "/");
                    var expect = manifest.Files?.FirstOrDefault(f => f.Path == rel);
                    if (expect is not null && !string.Equals(Sha256Of(file), expect.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        log("文件内容校验失败：" + rel + "，放弃本次更新");
                        return manifest;
                    }
                    pending.Add(rel);
                }

                Overwrite(appDir, extractDir, pending.Count, log);
                File.WriteAllText(Path.Combine(appDir, "version.json"),
                    "{\"version\":\"" + manifest.Version.Replace("\\", "").Replace("\"", "") + "\"}");
                log($"更新完成：{manifest.Version}（覆盖 {pending.Count} 个文件）");
            }
            finally
            {
                TryDeleteDir(stagingDir);
            }
        }
        catch (Exception ex)
        {
            log("更新失败（不影响启动）：" + ex.Message);
        }
        return manifest;
    }

    /// <summary>覆盖写 app\：新文件直接盖住旧的，不删旧文件（残留的旧 dll 不会被 deps.json 加载）。</summary>
    private static void Overwrite(string appDir, string extractDir, int count, Action<string> log)
    {
        foreach (var file in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(extractDir, file).Replace("\\", "/");
            var target = Path.Combine(appDir, rel.Replace("/", Path.DirectorySeparatorChar.ToString()));
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(file, target, overwrite: true);
        }
        log($"已覆盖 {count} 个文件到 app\\");
    }

    private static async Task<UpdateManifest?> FetchManifestAsync(IReadOnlyList<string> urls, Action<string> log)
    {
        foreach (var url in urls)
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                // 签名：与清单同名的 .sig。取到就必须验过，取不到（旧源/404）只警告——
                // 兼顾"逐步上线签名"的过渡期，但一旦源提供签名就不再接受未过验的内容。
                var sig = await TryFetchSignatureAsync(url + ".sig", log);
                if (sig is not null && !VerifyManifest(bytes, sig, log))
                {
                    log("清单签名校验失败，忽略该源：" + url);
                    continue;
                }
                var m = ParseManifest(bytes);   // 不用 JsonSerializer：full trim 下会 IL2026 失效
                if (m is not null) { log("已获取清单：" + url); return m with { SourceUrl = url }; }
            }
            catch (Exception ex) { log("清单源不可用（" + url + "）：" + ex.Message); }
        }
        return null;
    }

    private static async Task<byte[]?> TryFetchSignatureAsync(string url, Action<string> log)
    {
        try
        {
            // 文件里放的是 base64 文本，验签要的是签名原始字节
            var text = System.Text.Encoding.UTF8.GetString(await Http.GetByteArrayAsync(url)).Trim();
            return Convert.FromBase64String(text);
        }
        catch (Exception ex)
        {
            log("未取到清单签名（" + url + "）：" + ex.Message + " —— 按未签名处理");
            return null;
        }
    }

    /// <summary>用内嵌公钥验签（ECDSA P-256 / SHA-256 / IEEE P1363 签名）。</summary>
    private static bool VerifyManifest(byte[] manifestBytes, byte[] sig, Action<string> log)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ManifestPublicKey), out _);
            return ecdsa.VerifyData(manifestBytes, sig, HashAlgorithmName.SHA256);
        }
        catch (Exception ex)
        {
            log("验签异常：" + ex.Message);
            return false;
        }
    }

    /// <summary>手工解析清单（JsonDocument 不受裁剪影响；JsonSerializer 在 TrimMode=full 下会 IL2026）。</summary>
    private static UpdateManifest? ParseManifest(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
        if (string.IsNullOrEmpty(version)) return null;

        var files = new List<ManifestFile>();
        if (root.TryGetProperty("files", out var fs) && fs.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fs.EnumerateArray())
            {
                files.Add(new ManifestFile(
                    f.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    f.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0,
                    f.TryGetProperty("sha256", out var h) ? (h.GetString() ?? "") : ""));
            }
        }

        ManifestRuntime? runtime = null;
        if (root.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Object)
        {
            var probes = new List<ManifestFile>();
            if (rt.TryGetProperty("files", out var rfs) && rfs.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in rfs.EnumerateArray())
                {
                    probes.Add(new ManifestFile(
                        f.TryGetProperty("path", out var p2) ? p2.GetString() ?? "" : "",
                        f.TryGetProperty("size", out var s2) && s2.TryGetInt64(out var sz2) ? sz2 : 0,
                        f.TryGetProperty("sha256", out var h2) ? (h2.GetString() ?? "") : ""));
                }
            }
            runtime = new ManifestRuntime(
                rt.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                rt.TryGetProperty("size", out var s3) && s3.TryGetInt64(out var n3) ? n3 : 0,
                rt.TryGetProperty("sha256", out var sh) ? sh.GetString() ?? "" : "",
                rt.TryGetProperty("count", out var c) && c.TryGetInt32(out var cn) ? cn : 0,
                probes);
        }

        var pkgs = new List<ManifestPackage>();
        if (root.TryGetProperty("packages", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ps.EnumerateArray())
            {
                pkgs.Add(new ManifestPackage(
                    p.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                    p.TryGetProperty("from", out var fr) ? fr.GetString() ?? "" : "",
                    p.TryGetProperty("url", out var u2) ? u2.GetString() ?? "" : "",
                    p.TryGetProperty("size", out var s4) && s4.TryGetInt64(out var n4) ? n4 : 0,
                    p.TryGetProperty("sha256", out var sh2) ? (sh2.GetString() ?? "") : ""));
            }
        }
        return new UpdateManifest(version, files, runtime, pkgs, "");
    }

    /// <summary>把清单里的相对文件名解析成绝对下载地址（已是绝对地址则原样返回）。</summary>
    public static string ResolveUrl(string manifestUrl, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        var i = (manifestUrl ?? "").LastIndexOf('/');
        return i < 0 ? url : manifestUrl![..(i + 1)] + url.TrimStart('/');
    }

    /// <summary>带进度地下载到目标文件（更新包和 net.zip 共用）。</summary>
    public static async Task DownloadAsync(string url, string dest, Action<string> log,
                                           CancellationToken token = default)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var src = await resp.Content.ReadAsStreamAsync(token);
        var buffer = new byte[1 << 16];
        long read = 0;
        var lastReport = 0L;
        int n;
        while ((n = await src.ReadAsync(buffer, token)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, n), token);
            read += n;
            if (read - lastReport >= 8 << 20)
            {
                lastReport = read;
                log(total > 0
                    ? $"下载中 {read / 1048576}/{total / 1048576} MB"
                    : $"下载中 {read / 1048576} MB");
            }
        }
        log($"下载完成 {read / 1048576} MB");
    }

    public static string Sha256Of(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>读 app\version.json 里的版本标记（引导器自展开/更新后都会写）。</summary>
    public static string? LocalVersion(string appDir)
    {
        try
        {
            var p = Path.Combine(appDir, "version.json");
            if (!File.Exists(p)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }
}
