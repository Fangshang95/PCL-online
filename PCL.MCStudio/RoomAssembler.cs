using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace PCL.MCStudio;

public enum AssembleStep { Resolving, VersionJson, ClientJar, Libraries, Mods, PackDownload, PackInstall, Done }

public sealed record AssembleProgress(AssembleStep Step, string Detail);

/// <summary>加入房间装配结果（Missing 为未能自动获取的模组文件名，需手动补齐或向房主索取）。
/// Reused=true 表示命中本机可复用实例直接启动（跳过装配），调用方不得再走二次启动流程（防双重启动）。
/// PackName（v47.1）：来源整合包的显示名（如"秘法漫纪 0.1.4"）；空 = 本机实例复用/绑定（无包名）。</summary>
public sealed record AssembleResult(string InstanceId, string InstanceDir, int Mods, int NewLibraries,
    IReadOnlyList<string> Missing, bool Reused = false, string PackName = "");

/// <summary>
/// 加入房间自动装配：
/// 解析房间 → 原版 json（fabric 房间再并入 fabric launcherMeta 依赖）→ 烤入直连参数
/// → 下载客户端 jar 与缺失依赖库 → 按房间清单同步模组（优先从房主本地服务端目录复制，缺的才走网络）。
/// 产物落入 .minecraft/versions/mcstudio-{code}/，PCL 启动该实例即自动进服
/// （资源文件 assets 由 PCL 启动时自动补齐）。
/// </summary>
public sealed class RoomAssembler
{
    /// <summary>Fabric meta 不可达时兜底的稳定 loader 版本（与 Python CLI 一致）。</summary>
    private const string FallbackLoader = "0.16.14";

    /// <summary>localModsDir：房主本机服务端 mods 目录（v37 房主快速通道）。开房播种的模组文件名
    /// 与房间清单 Filename 一致（同一清单驱动），命中即本地复制代替网络下载，缺的仍走网络。</summary>
    public async Task<AssembleResult> AssembleAsync(RoomInfo info, string mcDir,
        IProgress<AssembleProgress>? progress = null, CancellationToken ct = default,
        string? localModsDir = null)
    {
        // loader 可带构建号：forge:52.1.0 / neo:21.1.186（房主端开房时解析后编码进房间清单）
        var loaderRaw = info.Loader.ToLowerInvariant();
        var sep = loaderRaw.IndexOf(':');
        var loader = sep >= 0 ? loaderRaw[..sep] : loaderRaw;
        var loaderBuild = sep >= 0 ? loaderRaw[(sep + 1)..] : "";
        if (loader is not ("fabric" or "vanilla" or "forge" or "neo"))
            throw new NotSupportedException($"当前仅支持 fabric/vanilla/forge/neoforge 房间（{info.Loader} 暂不支持）");

        var instId = $"mcstudio-{info.Code.ToLowerInvariant()}";
        var instDir = Path.Combine(mcDir, "versions", instId);
        Directory.CreateDirectory(instDir);
        progress?.Report(new(AssembleStep.Resolving, $"{info.Loader} {info.Mc} @ {info.Address}"));

        // 1) 版本 json：vanilla 直接取官方 json；fabric 并入 launcherMeta 依赖；
        //    forge/neo 用 installer 内的 version.json 与原版合并（forge 库优先去重、args 拼接）
        var vanilla = await McSources.GetVanillaVersionJsonAsync(info.Mc, ct).ConfigureAwait(false);
        var libs = new JsonArray();

        if (loader == "fabric")
        {
            // fabric meta 的 profile/json 端点已 404，改用 loader 详情端点的 launcherMeta 自拼
            string floader;
            JsonNode loaderMeta;
            try
            {
                floader = (await McSources.GetFabricVersionsAsync(ct).ConfigureAwait(false)).Loader;
                loaderMeta = await McSources.GetFabricLoaderMetaAsync(info.Mc, floader, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                floader = FallbackLoader;
                loaderMeta = await McSources.GetFabricLoaderMetaAsync(info.Mc, floader, ct).ConfigureAwait(false);
            }

            var lm = loaderMeta["launcherMeta"]!;
            var flibs = new List<JsonNode>();
            if (lm["libraries"]?["common"] is JsonArray common)
                foreach (var lib in common)
                    if (lib is not null) flibs.Add(lib.DeepClone());
            if (lm["libraries"]?["client"] is JsonArray client)
                foreach (var lib in client)
                    if (lib is not null) flibs.Add(lib.DeepClone());
            flibs.Add(new JsonObject
            {
                ["name"] = $"net.fabricmc:fabric-loader:{floader}",
                ["url"] = "https://maven.fabricmc.net/",
            });
            var intermediary = loaderMeta["intermediary"];
            if (intermediary?["version"]?.GetValue<string>() is { Length: > 0 } interVersion)
                flibs.Add(new JsonObject
                {
                    ["name"] = $"net.fabricmc:intermediary:{interVersion}",
                    ["url"] = intermediary["maven"]?.GetValue<string>() is { Length: > 0 } maven
                        ? maven
                        : "https://maven.fabricmc.net/",
                });

            progress?.Report(new(AssembleStep.VersionJson, $"loader {floader} / fabric 依赖 {flibs.Count} 个"));

            // 合并依赖：fabric 在前、按 name 去重（重复项 fabric 优先）
            var seen = new HashSet<string>();
            var merged = new List<JsonNode>(flibs);
            if (vanilla["libraries"] is JsonArray vanillaLibs)
                foreach (var lib in vanillaLibs)
                    if (lib is not null) merged.Add(lib);
            foreach (var lib in merged)
            {
                var name = lib["name"]!.GetValue<string>();
                if (seen.Add(name)) libs.Add(lib.DeepClone());
            }

            vanilla["mainClass"] = lm["mainClass"]!["client"]!.GetValue<string>();
            vanilla["libraries"] = libs;
        }
        else if (loader is "forge" or "neo")
        {
            // Forge/NeoForge：installer 自带 version.json（全量库表 + Bootstrap mainClass），
            // 与原版 json 合并即可 —— 无需本机跑安装器（服务端由房主端无头安装）
            if (string.IsNullOrEmpty(loaderBuild))
                throw new NotSupportedException("房间清单缺少 Forge 构建号（房主端版本过旧，请房主更新后重开房间）");
            var art = loader == "forge" ? $"forge-{info.Mc}-{loaderBuild}" : $"neoforge-{loaderBuild}";
            var groupId = loader == "forge" ? "net/minecraftforge/forge" : "net/neoforged/neoforge";
            // maven 目录是不带前缀的版本号（forge: 1.20.1-52.1.0；neo: 21.1.x），文件名才带前缀（v38 修复双源 404）
            var mavenVer = loader == "forge" ? $"{info.Mc}-{loaderBuild}" : loaderBuild;
            var installerPath = Path.Combine(instDir, "mcstudio-installer.jar");
            var urls = new List<string>
            {
                $"https://bmclapi2.bangbang93.com/maven/{groupId}/{mavenVer}/{art}-installer.jar",
                loader == "forge"
                    ? $"https://maven.minecraftforge.net/{groupId}/{mavenVer}/{art}-installer.jar"
                    : $"https://maven.neoforged.net/releases/{groupId}/{mavenVer}/{art}-installer.jar",
            };
            progress?.Report(new(AssembleStep.VersionJson, $"下载 {loader} 安装器…"));
            await McSources.DownloadFileAsync(urls, installerPath, ct).ConfigureAwait(false);
            JsonObject forgeJson;
            using (var izip = ZipFile.OpenRead(installerPath))
            {
                var entry = izip.GetEntry("version.json") ?? izip.GetEntry("install_profile.json")
                    ?? throw new InvalidDataException("安装器内无版本描述");
                await using var s = entry.Open();
                var node = JsonNode.Parse(s) as JsonObject
                    ?? throw new InvalidDataException("安装器版本描述解析失败");
                // 老安装器把版本信息放在 install_profile.json 的 versionInfo 里
                forgeJson = node["versionInfo"] as JsonObject ?? node;
            }
            try { File.Delete(installerPath); } catch { }

            var mergedLibs = new JsonArray();
            var seen = new HashSet<string>();
            if (forgeJson["libraries"] is JsonArray fls)
                foreach (var lib in fls)
                    if (lib?["name"]?.GetValue<string>() is { } n && seen.Add(n))
                        mergedLibs.Add(lib.DeepClone());
            if (vanilla["libraries"] is JsonArray vls)
                foreach (var lib in vls)
                    if (lib?["name"]?.GetValue<string>() is { } n2 && seen.Add(n2))
                        mergedLibs.Add(lib.DeepClone());

            vanilla["mainClass"] = forgeJson["mainClass"]?.DeepClone();
            vanilla["libraries"] = mergedLibs;
            var game = new JsonArray();
            foreach (var a in vanilla["arguments"]?["game"] as JsonArray ?? new JsonArray())
                game.Add(a?.DeepClone());
            foreach (var a in forgeJson["arguments"]?["game"] as JsonArray ?? new JsonArray())
                game.Add(a?.DeepClone());
            var jvm = new JsonArray();
            foreach (var a in vanilla["arguments"]?["jvm"] as JsonArray ?? new JsonArray())
                jvm.Add(a?.DeepClone());
            foreach (var a in forgeJson["arguments"]?["jvm"] as JsonArray ?? new JsonArray())
                jvm.Add(a?.DeepClone());
            vanilla["arguments"] = new JsonObject { ["game"] = game, ["jvm"] = jvm };
            if (forgeJson["logging"] is { } lg) vanilla["logging"] = lg.DeepClone();
            progress?.Report(new(AssembleStep.VersionJson, $"{loader} {info.Mc}（{mergedLibs.Count} 个依赖库）"));
        }
        else
        {
            // vanilla：官方 json 自带 mainClass/libraries，无需覆盖
            if (vanilla["libraries"] is JsonArray vanillaLibs)
                foreach (var lib in vanillaLibs)
                    if (lib is not null) libs.Add(lib.DeepClone());
        }

        vanilla["id"] = instId;
        if (vanilla is JsonObject vanillaObj) vanillaObj.Remove("inheritsFrom");
        AddDirectConnectArgs(vanilla, info);
        vanilla["mcstudio"] = new JsonObject
        {
            ["room_code"] = info.Code,
            ["address"] = info.Address,
            ["joined_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        var jsonPath = Path.Combine(instDir, instId + ".json");
        await File.WriteAllTextAsync(jsonPath, vanilla.ToJsonString(), ct).ConfigureAwait(false);

        // 2) 客户端 jar（按版本共享缓存：第一个房间下载，之后所有房间本地复制秒级完成；
        //    修复"每开一个房间就看到重新下载一遍游戏"的观感——实例目录按房间隔离，jar 不必重复下载）。
        //    三源：BMCLAPI 镜像 → 官方 piston-data → 官方二次（新版本 BMCLAPI 常滞后 404，官方直连慢时重试一次）；
        //    全程 sha1 校验（官方清单值）：缓存命中也校验，坏缓存当场删除重下，防错误内容被永久复用。
        var jarPath = Path.Combine(instDir, instId + ".jar");
        if (!File.Exists(jarPath))
        {
            progress?.Report(new(AssembleStep.ClientJar, info.Mc));
            var cachePath = Path.Combine(mcDir, "mcstudio", "cache", $"client-{info.Mc}.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var officialUrl = vanilla["downloads"]?["client"]?["url"]?.GetValue<string>();
            var clientSha1 = vanilla["downloads"]?["client"]?["sha1"]?.GetValue<string>();
            var cacheValid = false;
            if (File.Exists(cachePath))
            {
                if (string.IsNullOrEmpty(clientSha1) || await McSources.VerifySha1Async(cachePath, clientSha1, ct).ConfigureAwait(false))
                {
                    cacheValid = true;
                    File.Copy(cachePath, jarPath);
                    progress?.Report(new(AssembleStep.ClientJar, $"{info.Mc}（已有缓存，本地复制完成）"));
                }
                else
                {
                    try { File.Delete(cachePath); } catch { }
                    progress?.Report(new(AssembleStep.ClientJar, $"{info.Mc}（缓存校验失败，重新下载）"));
                }
            }
            if (!cacheValid)
            {
                var urls = new List<string> { $"{McSources.Bmclapi}/version/{info.Mc}/client" };
                if (vanilla["downloads"]?["client"]?["url"]?.GetValue<string>() is { Length: > 0 } official)
                {
                    urls.Add(official);
                    urls.Add(official); // 官方二次尝试：首试超时/抖动时兜底（与首源重复无害）
                }
                await McSources.DownloadFileAsync(urls, cachePath, ct, clientSha1).ConfigureAwait(false);
                File.Copy(cachePath, jarPath);
            }
        }

        // 3) 依赖库补缺（共享 libraries 目录，缺失才下；native 库按 rules 只下当前 OS，产物路径用 downloads.artifact.path 保 classifier）
        var newLibraries = 0;
        var index = 0;
        foreach (var lib in libs)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            if (lib is null) continue;
            if (!AppliesToCurrentOs(lib)) continue; // 跳过非当前 OS 的 native 库（如 linux/macos natives）
            var rel = ArtifactRelPath(lib);
            var dest = Path.Combine(mcDir, "libraries", rel);
            if (File.Exists(dest)) continue;
            var urls = new List<string>();
            if (lib["downloads"]?["artifact"]?["url"]?.GetValue<string>() is { Length: > 0 } official)
                urls.Add(official);
            if (lib["url"]?.GetValue<string>() is { Length: > 0 } maven)
                urls.Add(maven.TrimEnd('/') + "/" + rel);
            urls.Add($"{McSources.Bmclapi}/maven/{rel}");
            progress?.Report(new(AssembleStep.Libraries, $"{index}/{libs.Count}"));
            await McSources.DownloadFileAsync(urls, dest, ct).ConfigureAwait(false);
            newLibraries++;
        }

        // 4) 模组同步：清单条目带直链（整合包导入）则直接下载，否则按房间清单从 Modrinth 拉兼容版本到实例 mods/；
        //    v37 房主快速通道：localModsDir（本机服务端 mods）已有清单同名文件时本地复制，免网络下载
        var modsDir = Path.Combine(instDir, "mods");
        Directory.CreateDirectory(modsDir);
        var missing = new List<string>();
        foreach (var mod in info.Mods)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(modsDir, mod.Filename ?? mod.Project + ".jar");
            if (File.Exists(target)) continue;

            // 房主快速通道：服务端目录里的模组正是按本清单播种的（文件名含版本号，同名即同文件），
            // 本地磁盘复制代替逐个走网络——模组同步从分钟级下载变成秒级复制；失败不阻塞，回落网络
            if (!string.IsNullOrEmpty(localModsDir))
            {
                try
                {
                    var localFile = Path.Combine(localModsDir, Path.GetFileName(target));
                    if (File.Exists(localFile))
                    {
                        File.Copy(localFile, target, overwrite: true);
                        progress?.Report(new(AssembleStep.Mods, Path.GetFileName(target) + "（本地复制）"));
                        continue;
                    }
                }
                catch
                {
                    // 本地源异常（占用/权限等）：回落网络下载
                }
            }

            var urls = new List<string>();
            if (!string.IsNullOrEmpty(mod.Url))
            {
                // 整合包导入的直链：镜像优先（cdn.modrinth.com → mcimirror），原链兜底
                urls.Add(mod.Url.Replace("https://cdn.modrinth.com", "https://mod.mcimirror.top"));
                urls.Add(mod.Url);
            }
            else
            {
                var file = await McSources.FindModFileAsync(mod.Project, info.Mc, info.Loader, ct)
                    .ConfigureAwait(false);
                if (file is null)
                {
                    missing.Add(mod.Filename ?? mod.Project);
                    continue;
                }
                target = Path.Combine(modsDir, file.Filename);
                if (File.Exists(target)) continue;
                urls.Add(file.Url);
            }
            progress?.Report(new(AssembleStep.Mods, Path.GetFileName(target)));
            try
            {
                await McSources.DownloadFileAsync(urls, target, ct).ConfigureAwait(false);
            }
            catch
            {
                missing.Add(Path.GetFileName(target));
            }
        }
        var modCount = Directory.EnumerateFiles(modsDir, "*.jar").Count();

        progress?.Report(new(AssembleStep.Done, instId));
        return new AssembleResult(instId, instDir, modCount, newLibraries, missing);
    }

    // ---------------------------------------------------------------- 内部实现

    /// <summary>库是否适用于当前 OS。native 库带 rules（allow/disallow os.name）；无 rules 视为通用。</summary>
    private static bool AppliesToCurrentOs(JsonNode lib)
    {
        if (lib["rules"] is not JsonArray rules || rules.Count == 0) return true;
        var osName = CurrentOsName();
        var allowed = false;
        foreach (var r in rules)
        {
            var action = r?["action"]?.GetValue<string>();
            var name = r?["os"]?["name"]?.GetValue<string>();
            var matches = name is null || name == osName;
            if (action == "allow" && matches) allowed = true;
            if (action == "disallow" && matches) return false;
        }
        return allowed;
    }

    /// <summary>库产物相对路径：优先 downloads.artifact.path（原版含 classifier 的精确路径），否则按 maven 坐标推导（fabric 库）。</summary>
    private static string ArtifactRelPath(JsonNode lib)
    {
        if (lib["downloads"]?["artifact"]?["path"]?.GetValue<string>() is { Length: > 0 } path)
            return path;
        return McSources.LibRelPath(lib["name"]!.GetValue<string>());
    }

    /// <summary>烤入直连参数：1.20+ 用 Quick Play 的 --quickPlayMultiplayer；1.19 及以下用 --server/--port（1.20 起 server/port 已移除）。</summary>
    private static void AddDirectConnectArgs(JsonNode vanilla, RoomInfo info)
    {
        var arguments = vanilla["arguments"] as JsonObject ?? new JsonObject();
        vanilla["arguments"] = arguments;
        var game = arguments["game"] as JsonArray ?? new JsonArray();
        arguments["game"] = game;

        if (Version.TryParse(info.Mc, out var v) && v >= new Version(1, 20))
        {
            game.Add("--quickPlayMultiplayer");
            game.Add(info.Address);
        }
        else
        {
            var (host, port) = SplitAddress(info.Address);
            game.Add("--server");
            game.Add(host);
            game.Add("--port");
            game.Add(port);
        }
    }

    private static (string Host, string Port) SplitAddress(string address)
    {
        var idx = address.LastIndexOf(':');
        if (idx <= 0) return (address, "25565");
        return (address[..idx], address[(idx + 1)..]);
    }

    private static string CurrentOsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx";
        return "linux";
    }
}
