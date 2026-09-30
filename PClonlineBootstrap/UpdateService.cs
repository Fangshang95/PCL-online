using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace PClonlineBootstrap;

/// <summary>清单里的单个文件（增量更新的依据：path + sha256）。</summary>
internal sealed record ManifestFile(string Path, long Size, string Sha256);

/// <summary>
/// 可下载的包。
/// type=patch：From 表示"从哪个版本增量升级"，包内只含变化的文件，Remove 列出新版本已删除的文件；
/// type=full ：全量兜底（任何版本都能升），From 为空、Remove 为空。
/// </summary>
internal sealed record ManifestPackage(string Type, string From, string Url, long Size, string Sha256,
                                      List<string> Remove);

/// <summary>远端更新清单（由 build_update.py 生成并随包发布）。
/// SourceUrl = 清单实际取自哪个地址，用于把包里的相对文件名解析成绝对地址。</summary>
internal sealed record UpdateManifest(string Version, List<ManifestFile> Files, List<ManifestPackage> Packages,
                                      string SourceUrl);

/// <summary>
/// 增量更新服务（v50.10）。
///
/// 流程：拉清单（含签名）→ 验签 → 与本地版本比对 → 选包（优先匹配本地版本的增量包，否则全量兜底）
///       → 下载到 staging → 校验包 sha256 → 解压并逐文件校验 sha256
///       → 备份旧文件 → 原子替换 + 删除废弃文件 → 记录本地版本；任一步失败即从备份回滚。
///
/// 两条硬规则：
///   ① 任何异常都不阻断启动（更新是增益，不是前提）；
///   ② 内容以 sha256 为准，来源以 ECDSA 签名为准——后续接入 P2P 玩家互传 / CDN 镜像时
///      来源不可信，但内容可验证，防止镜像源投毒。
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

    public static async Task RunAsync(string appDir, IReadOnlyList<string> manifestUrls, Action<string> log)
    {
        try
        {
            var manifest = await FetchManifestAsync(manifestUrls, log);
            if (manifest is null) { log("未能获取更新清单，跳过更新"); return; }

            var localVer = LocalVersion(appDir);
            if (!string.IsNullOrEmpty(localVer) && localVer == manifest.Version)
            {
                log("已是最新版本：" + manifest.Version);
                return;
            }
            log($"发现更新：{localVer ?? "（未知）"} → {manifest.Version}");

            var patch = manifest.Packages?.FirstOrDefault(p =>
                string.Equals(p.Type, "patch", StringComparison.OrdinalIgnoreCase) && p.From == localVer);
            var full = manifest.Packages?.FirstOrDefault(p =>
                string.Equals(p.Type, "full", StringComparison.OrdinalIgnoreCase));
            var pkg = patch ?? full;
            if (pkg is null) { log("清单中没有可用的更新包，跳过"); return; }
            // 清单里的包地址是相对文件名（这样同一份清单放到 GitHub / 自建服务器都成立，
            // 且服务器可以原样返回文件、不破坏签名），这里按清单来源解析成绝对地址
            var pkgUrl = ResolveUrl(manifest.SourceUrl, pkg.Url);
            log($"使用{(patch is not null ? "增量" : "全量")}包：{pkgUrl}（{pkg.Size / 1048576} MB）");

            var stagingDir = Path.Combine(appDir, ".staging");
            var backupDir = Path.Combine(appDir, ".backup");
            var pkgPath = Path.Combine(stagingDir, "update.zip");

            try
            {
                Directory.CreateDirectory(stagingDir);
                await DownloadAsync(pkgUrl, pkgPath, log);
                var actual = Sha256Of(pkgPath);
                if (!string.Equals(actual, pkg.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    log("更新包校验失败（sha256 不符），放弃本次更新");
                    return;
                }

                var extractDir = Path.Combine(stagingDir, "files");
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(pkgPath, extractDir, overwriteFiles: true);

                // 校验待替换文件：以清单 sha256 为准，不符则整包放弃（不落地半个更新）
                var pending = new List<string>();
                foreach (var file in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(extractDir, file).Replace("\\", "/");
                    var expect = manifest.Files?.FirstOrDefault(f => f.Path == rel);
                    if (expect is not null && !string.Equals(Sha256Of(file), expect.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        log("文件内容校验失败：" + rel + "，放弃本次更新");
                        return;
                    }
                    pending.Add(rel);
                }

                // 新版本里已不存在的文件（增量包用 remove 标注，全量包为空）
                var removals = (pkg.Remove ?? new List<string>())
                    .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();

                Apply(appDir, extractDir, pending, removals, backupDir, log);
                File.WriteAllText(Path.Combine(appDir, "version.json"),
                    "{\"version\":\"" + manifest.Version.Replace("\\", "").Replace("\"", "") + "\"}");
                log($"更新完成：{manifest.Version}（替换 {pending.Count} 个，删除 {removals.Count} 个）");
            }
            finally
            {
                TryDeleteDir(stagingDir);
                TryDeleteDir(backupDir);
            }
        }
        catch (Exception ex)
        {
            log("更新失败（不影响启动）：" + ex.Message);
        }
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
                    f.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : ""));
            }
        }

        var pkgs = new List<ManifestPackage>();
        if (root.TryGetProperty("packages", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ps.EnumerateArray())
            {
                var remove = new List<string>();
                if (p.TryGetProperty("remove", out var rm) && rm.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in rm.EnumerateArray())
                    {
                        var s = r.GetString();
                        if (!string.IsNullOrEmpty(s)) remove.Add(s);
                    }
                }
                pkgs.Add(new ManifestPackage(
                    p.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                    p.TryGetProperty("from", out var fr) ? fr.GetString() ?? "" : "",
                    p.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    p.TryGetProperty("size", out var s2) && s2.TryGetInt64(out var n) ? n : 0,
                    p.TryGetProperty("sha256", out var sh) ? sh.GetString() ?? "" : "",
                    remove));
            }
        }
        return new UpdateManifest(version, files, pkgs, "");
    }

    /// <summary>把清单里的相对文件名解析成绝对下载地址（已是绝对地址则原样返回）。</summary>
    private static string ResolveUrl(string manifestUrl, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        var i = manifestUrl.LastIndexOf('/');
        return i < 0 ? url : manifestUrl[..(i + 1)] + url.TrimStart('/');
    }

    private static async Task DownloadAsync(string url, string dest, Action<string> log)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var src = await resp.Content.ReadAsStreamAsync();
        var buffer = new byte[1 << 16];
        long read = 0;
        var lastReport = 0L;
        int n;
        while ((n = await src.ReadAsync(buffer)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, n));
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

    /// <summary>备份旧文件 → 逐文件替换 / 删除；中途失败从备份整体回滚。</summary>
    private static void Apply(string appDir, string extractDir, List<string> rels, List<string> removals,
                              string backupDir, Action<string> log)
    {
        Directory.CreateDirectory(backupDir);
        var moved = new List<string>();
        var deleted = new List<string>();
        try
        {
            foreach (var rel in rels)
            {
                var target = Path.Combine(appDir, rel.Replace("/", Path.DirectorySeparatorChar.ToString()));
                var src = Path.Combine(extractDir, rel.Replace("/", Path.DirectorySeparatorChar.ToString()));
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(target))
                {
                    File.Move(target, Path.Combine(backupDir, rel.Replace("/", "_")), overwrite: true);
                    moved.Add(rel);
                }
                File.Move(src, target, overwrite: true);
            }
            foreach (var rel in removals)
            {
                var target = Path.Combine(appDir, rel.Replace("/", Path.DirectorySeparatorChar.ToString()));
                if (!File.Exists(target)) continue;
                File.Move(target, Path.Combine(backupDir, rel.Replace("/", "_")), overwrite: true);
                deleted.Add(rel);
            }
            log($"已替换 {moved.Count} 个（新增 {rels.Count - moved.Count} 个），删除 {deleted.Count} 个");
        }
        catch
        {
            log("替换过程出错，正在回滚…");
            foreach (var rel in moved.Concat(deleted))
            {
                try
                {
                    var target = Path.Combine(appDir, rel.Replace("/", Path.DirectorySeparatorChar.ToString()));
                    var bak = Path.Combine(backupDir, rel.Replace("/", "_"));
                    if (File.Exists(bak)) File.Move(bak, target, overwrite: true);
                }
                catch { /* 尽力回滚 */ }
            }
            throw;
        }
    }

    private static string? LocalVersion(string appDir)
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

    private static string Sha256Of(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
