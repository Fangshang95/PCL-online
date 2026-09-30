using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.MCStudio;

/// <summary>
/// 官方/镜像源统一访问：Mojang piston-meta、Fabric meta、Modrinth、BMCLAPI 镜像。
/// 红线：客户端/服务端文件与模组一律从官方源或其镜像下载，不经自有服务器分发。
/// </summary>
public static class McSources
{
    public const string Bmclapi = "https://bmclapi2.bangbang93.com";
    public const string PistonManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string FabricMeta = "https://meta.fabricmc.net/v2";
    public const string ModrinthApi = "https://api.modrinth.com/v2";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // Infinite：默认 100s/自设 60s 都会截断 DownloadFileAsync 里"单源 10 分钟慢速宽限"（曾致大文件慢网必败）。
        // 超时预算下沉到每个方法内部：元数据 30s、单源下载 10min。
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MCStudio/1.0 (PCL2-CE)");
        return http;
    }

    // ---------------------------------------------------------------- HTTP 基础

    /// <summary>GET 并解析 JSON（失败重试 2 次，退避递增；单次请求 30s 预算，防挂起）。</summary>
    public static async Task<JsonNode> GetJsonAsync(string url, CancellationToken ct = default)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return JsonNode.Parse(text) ?? throw new JsonException($"空响应: {url}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 30s 预算耗尽（非用户取消）：按可重试超时处理
                last = new TimeoutException($"请求超时（30 秒）: {url}");
                if (attempt < 2) await Task.Delay(1000 * (attempt + 1), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                last = e;
                if (attempt < 2) await Task.Delay(1000 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException($"请求失败: {url}", last);
    }

    /// <summary>POST JSON 并解析 JSON 响应（失败重试 2 次，退避递增；单次请求 30s 预算）。
    /// v50.6.9：Modrinth /version_files 批量 sha1 反查使用。</summary>
    public static async Task<JsonNode?> PostJsonAsync(string url, JsonObject body, CancellationToken ct = default)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                using var resp = await Http.PostAsync(url,
                    new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                    cts.Token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException($"请求超时（30 秒）: {url}");
                if (attempt < 2) await Task.Delay(1000 * (attempt + 1), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                last = e;
                if (attempt < 2) await Task.Delay(1000 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException($"请求失败: {url}", last);
    }

    /// <summary>GET 并返回原始文本（失败重试 2 次，退避递增；单次请求 30s 预算）；用于 XML 等非 JSON 元数据。</summary>
    public static async Task<string> GetTextAsync(string url, CancellationToken ct = default)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException($"请求超时（30 秒）: {url}");
                if (attempt < 2) await Task.Delay(1000 * (attempt + 1), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                last = e;
                if (attempt < 2) await Task.Delay(1000 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException($"请求失败: {url}", last);
    }

    /// <summary>多 URL 版 GetJsonAsync：按顺序尝试，首个成功即返回（版本清单等"官方优先+镜像兜底"场景用）。</summary>
    public static async Task<JsonNode> GetJsonAsync(IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try { return await GetJsonAsync(url, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { last = e; }
        }
        throw new InvalidOperationException($"请求失败（已尝试 {urls.Count} 个源）: {string.Join(" → ", urls)}", last);
    }

    /// <summary>
    /// 按顺序尝试各 URL 下载到目标文件（.part 临时文件 + 原子改名，避免留半截文件）。
    /// sha1 非空时对下载结果校验（.part 落盘后、改名前），不匹配按该源失败继续下一源——防坏内容被当正式文件/缓存永久复用。
    /// 单源预算 10 分钟（慢速网络宽限）。
    /// progress 可选：每累计约 1MB 与完成时回报一次 (已下载字节, 总字节)；总长未知（无 Content-Length）时 Total 为 -1。
    /// </summary>
    public static async Task DownloadFileAsync(IReadOnlyList<string> urls, string dest,
        CancellationToken ct = default, string? sha1 = null,
        IProgress<(long Done, long Total)>? progress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Exception? last = null;
        foreach (var url in urls)
        {
            var tmp = dest + ".part";
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(10)); // 单文件总超时（慢速网络宽限）
                using var resp = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url),
                    HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1;
                long done = 0, lastReported = 0;
                // 注意：写入句柄必须先释放再 File.Move，否则 Windows 上 .part 文件被自身占用导致改名失败
                await using (var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
                await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                    65536, useAsync: true))
                {
                    // 手动 64KB 循环：CopyToAsync 拿不到中途字节数，无法驱动进度显示（v40）
                    var buf = new byte[65536];
                    int read;
                    while ((read = await src.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                        done += read;
                        if (done - lastReported >= 1048576)
                        {
                            lastReported = done;
                            progress?.Report((done, total));
                        }
                    }
                    progress?.Report((done, total));
                }
                if (!string.IsNullOrEmpty(sha1) && !await VerifySha1Async(tmp, sha1, ct).ConfigureAwait(false))
                {
                    last = new InvalidOperationException($"SHA1 校验失败（内容与官方清单不符）: {url}");
                    try { File.Delete(tmp); } catch { }
                    continue;
                }
                File.Move(tmp, dest, overwrite: true);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                last = e;
                try { File.Delete(tmp); } catch { /* 换下个源重试 */ }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException($"下载超时（10 分钟）: {url}");
                try { File.Delete(tmp); } catch { }
            }
        }
        throw new InvalidOperationException(
            $"下载失败（已尝试 {urls.Count} 个源）: {Path.GetFileName(dest)}\n最后错误：{Describe(last)}", last);
    }

    /// <summary>校验文件 SHA1（期望值为 40 位十六进制，大小写不敏感；文件不可读按不匹配处理）。</summary>
    public static async Task<bool> VerifySha1Async(string path, string expected, CancellationToken ct)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var hex = Convert.ToHexString(await System.Security.Cryptography.SHA1.HashDataAsync(fs, ct)
                .ConfigureAwait(false)).ToLowerInvariant();
            return string.Equals(hex, expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>把下载/校验失败的最后异常转成一句话原因（弹窗直接可见，不再吞进 InnerException）。</summary>
    private static string Describe(Exception? last)
    {
        if (last is null) return "无";
        if (last is TimeoutException) return "超时（网络过慢或源不可达）";
        if (last is HttpRequestException hre)
            return $"HTTP 错误：{hre.Message}";
        if (last is InvalidOperationException { Message: var m } && m.Contains("SHA1"))
            return "SHA1 校验失败（源返回了错误内容）";
        return last.Message.Length > 200 ? last.Message[..200] + "…" : last.Message;
    }

    // ---------------------------------------------------------------- 版本元数据

    /// <summary>取原版版本详情 json（含 downloads/libraries/arguments）。
    /// 清单官方优先 + BMCLAPI 兜底（piston-meta 直连不稳时仍有入口；BMCLAPI 清单的 versions[].url 自带镜像域名，后续下载自动走镜像）。</summary>
    public static async Task<JsonNode> GetVanillaVersionJsonAsync(string mc, CancellationToken ct = default)
    {
        var manifest = await GetJsonAsync(new List<string>
        {
            PistonManifest,
            Bmclapi + "/mc/game/version_manifest_v2.json",
        }, ct).ConfigureAwait(false);
        string? url = null;
        if (manifest["versions"] is JsonArray versions)
            foreach (var v in versions)
            {
                if (v?["id"]?.GetValue<string>() != mc) continue;
                url = v["url"]!.GetValue<string>();
                break;
            }
        if (url is null)
            throw new InvalidOperationException($"官方清单中不存在版本 {mc}");
        return await GetJsonAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>原版服务端 jar 下载地址与官方 sha1（piston-meta，不经自有服务器）。</summary>
    public static async Task<(string Url, string? Sha1)> GetVanillaServerJarAsync(string mc,
        CancellationToken ct = default)
    {
        var detail = await GetVanillaVersionJsonAsync(mc, ct).ConfigureAwait(false);
        return (detail["downloads"]?["server"]?["url"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"版本 {mc} 没有官方服务端 jar"),
            detail["downloads"]?["server"]?["sha1"]?.GetValue<string>());
    }

    public sealed record FabricVersions(string Installer, string Loader);

    /// <summary>Fabric 最新 installer 与 loader 版本号。</summary>
    public static async Task<FabricVersions> GetFabricVersionsAsync(CancellationToken ct = default)
    {
        var installer = (await GetJsonAsync($"{FabricMeta}/versions/installer", ct).ConfigureAwait(false))
            [0]!["version"]!.GetValue<string>();
        var loader = (await GetJsonAsync($"{FabricMeta}/versions/loader", ct).ConfigureAwait(false))
            [0]!["version"]!.GetValue<string>();
        return new FabricVersions(installer, loader);
    }

    /// <summary>Fabric loader 详情（含 launcherMeta 与 intermediary 映射信息）。</summary>
    public static Task<JsonNode> GetFabricLoaderMetaAsync(string mc, string loader,
        CancellationToken ct = default)
        => GetJsonAsync($"{FabricMeta}/versions/loader/{mc}/{loader}", ct);

    /// <summary>Fabric 官方服务端 jar 下载地址（自带依赖的服务端启动器）。</summary>
    public static async Task<string> GetFabricServerJarUrlAsync(string mc, CancellationToken ct = default)
    {
        var v = await GetFabricVersionsAsync(ct).ConfigureAwait(false);
        return $"{FabricMeta}/versions/loader/{mc}/{v.Loader}/{v.Installer}/server/jar";
    }

    // ---------------------------------------------------------------- 依赖库与模组

    /// <summary>maven 坐标 group:artifact:version[:classifier] → group/artifact/version/artifact-version[-classifier].jar。</summary>
    public static string LibRelPath(string coords)
    {
        var parts = coords.Split(':');
        if (parts.Length < 3) throw new ArgumentException($"非法 maven 坐标: {coords}");
        var g = parts[0].Replace('.', '/');
        var file = parts.Length >= 4 && parts[3].Length > 0
            ? $"{parts[1]}-{parts[2]}-{parts[3]}.jar"
            : $"{parts[1]}-{parts[2]}.jar";
        return $"{g}/{parts[1]}/{parts[2]}/{file}";
    }

    /// <summary>ProjectId（v50.8）：sha1 反查响应里自带 project_id，包级对齐据此定位「这是哪个包」，
    /// 无需再按文件名猜。可选参数放最后——v50.6.9 老调用点零破坏。</summary>
    public sealed record ModrinthFile(string Url, string Filename, string Version,
        string VersionId = "", string Sha1 = "", string ProjectId = "");

    /// <summary>按项目 ID/slug 找 mc+loader 兼容的模组文件（Modrinth 返回顺序即最新优先）。</summary>
    public static async Task<ModrinthFile?> FindModFileAsync(string project, string mc, string loader,
        CancellationToken ct = default)
    {
        var versions = await GetJsonAsync(
            $"{ModrinthApi}/project/{Uri.EscapeDataString(project)}/version", ct).ConfigureAwait(false);
        if (versions is not JsonArray arr) return null;
        foreach (var v in arr)
        {
            if (v?["game_versions"] is not JsonArray gv) continue;
            if (v["loaders"] is not JsonArray ld) continue;
            if (!gv.Any(x => x?.GetValue<string>() == mc)) continue;
            if (!ld.Any(x => x?.GetValue<string>() == loader)) continue;
            var file = (v["files"] as JsonArray)?[0];
            if (file?["url"]?.GetValue<string>() is not { Length: > 0 } url) continue;
            if (file["filename"]?.GetValue<string>() is not { Length: > 0 } filename) continue;
            // v50.6.8：补齐 version_id 与 sha1——房主清单精确化（朋友端按房主实际版本安装，不再猜最新）
            var vid = v["id"]?.GetValue<string>() ?? "";
            var sha1 = (file["hashes"] as JsonObject)?["sha1"]?.GetValue<string>() ?? "";
            return new ModrinthFile(url, filename, v["version_number"]?.GetValue<string>() ?? "", vid, sha1, project);
        }
        return null;
    }

    /// <summary>v50.6.9：按 sha1 批量反查 Modrinth 版本（POST /v2/version_files，单次 ≤512 个）。
    /// 用于房主实例 mods 目录里「清单外/手动安装」的 jar——只要发行于 Modrinth 即可反查出
    /// 直链与精确版本， LAN 房与手动放置的模组因此也能生成可自动安装的精确清单。</summary>
    public static async Task<Dictionary<string, ModrinthFile>> FindModFilesByHashAsync(
        IEnumerable<string> sha1s, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ModrinthFile>(StringComparer.OrdinalIgnoreCase);
        var batch = sha1s.Where(s => s is { Length: 40 }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var chunk in batch.Chunk(512))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var body = new JsonObject
                {
                    ["hashes"] = new JsonArray(chunk.Select(s => (JsonNode)s).ToArray()),
                    ["algorithm"] = "sha1",
                };
                var resp = await PostJsonAsync($"{ModrinthApi}/version_files", body, ct).ConfigureAwait(false);
                if (resp is not JsonObject obj) continue;
                foreach (var (sha1, node) in obj)
                {
                    if (node is not JsonObject v) continue;
                    var file = (v["files"] as JsonArray)?.FirstOrDefault(f =>
                        f?["primary"]?.GetValue<bool>() == true) ?? (v["files"] as JsonArray)?.FirstOrDefault();
                    if (file?["url"]?.GetValue<string>() is not { Length: > 0 } url) continue;
                    if (file["filename"]?.GetValue<string>() is not { Length: > 0 } filename) continue;
                    var sha1This = (file["hashes"] as JsonObject)?["sha1"]?.GetValue<string>() ?? sha1;
                    result[sha1] = new ModrinthFile(url, filename,
                        v["version_number"]?.GetValue<string>() ?? "",
                        v["id"]?.GetValue<string>() ?? "", sha1This,
                        v["project_id"]?.GetValue<string>() ?? "");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.WriteLine($"[McSources] sha1 反查失败（{chunk.Length} 个）: {e.Message}");
            }
        }
        return result;
    }
}
