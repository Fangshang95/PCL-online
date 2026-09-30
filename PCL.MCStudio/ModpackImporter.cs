using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.MCStudio;

/// <summary>导入解析结果。SuggestedMc/SuggestedLoader 为整合包自带的版本/加载器建议（纯模组包为 null）。</summary>
public sealed record ImportResult(int ModCount, int DownloadCount, int SkippedCount, string Detail,
    string? SuggestedMc = null, string? SuggestedLoader = null);

/// <summary>seed 结果：并入清单的模组数 + 因 MC 版本不符被过滤的 jar 文件名（提示用）。</summary>
public sealed record SeedResult(int Merged, IReadOnlyList<string> Filtered, string FilterDetail);

/// <summary>
/// 服务端整合包识别结果（v47 通用化）：
///   Forge/NeoForge 已装型：Build 非空，libraries 里有 args 文件，开房零安装直接启动；
///   -jar 核心型（v47）：CoreJar 非空 —— fabric-server-launch / paper / mohist / arclight /
///   catserver / server.jar 等，开房直接 -jar 启动，ExtraJvm 为启动脚本提取的 -D 参数；
///   半安装型：只有启动脚本但包内核心/args 俱全度未知 —— 展开后按 loader 走核心安装链兜底。
/// </summary>
public sealed record ServerPackInfo(
    string Mc, string Loader, string Build, string RootPrefix, string Detail,
    string CoreJar = "", string ExtraJvm = "", string CoreLabel = "", string ZipPath = "");

/// <summary>
/// 模组 / 整合包导入器（v33 全面适配）：
///   CurseForge zip      manifest.json 的 projectID/fileID 经 cfwidget 解析文件名后从
///                       mediafilez.forgecdn.net CDN 直下（免 API key）；overrides/ 同步展开；
///   Modrinth .mrpack    modrinth.index.json 直链下载；env.server=unsupported 的客户端专属模组
///                       只记入清单（加入端安装），不进服务端 mods；dependencies 识别 loader；
///   MultiMC/Prism zip   mmc-pack.json 解析版本与 loader，.minecraft/mods 入库、其余进 override；
///   服务端包/根目录 zip  根下 mods/*.jar 入库，config/kubejs 等进 override；
///   纯 mods zip / 目录   提取所有 jar。
/// 统一暂存到 {exe}\mcstudio\imports\：mods/*.jar、override/**、import.json（含建议 mc/loader）。
/// 开房时由 HostRoomManager 调 SeedToServerAsync 把暂存内容落到服务端目录并合并清单。
/// </summary>
public static class ModpackImporter
{
    /// <summary>暂存根目录。</summary>
    public static string ImportRoot { get; } = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "mcstudio", "imports");

    private static string ModsDir => Path.Combine(ImportRoot, "mods");
    private static string OverrideDir => Path.Combine(ImportRoot, "override");
    private static string ManifestPath => Path.Combine(ImportRoot, "import.json");

    /// <summary>已导入的模组数（供开房 UI 显示；服务端整合包按 1 计，让"清空导入"可用）。</summary>
    public static int ImportedCount
    {
        get
        {
            try
            {
                if (IsServerPackImport) return 1;
                return Directory.Exists(ModsDir) ? Directory.EnumerateFiles(ModsDir, "*.jar").Count() : 0;
            }
            catch { return 0; }
        }
    }

    /// <summary>当前暂存导入是否为服务端整合包（v43：整包 zip 开房时直接展开为服务端根目录）。</summary>
    public static bool IsServerPackImport
    {
        get
        {
            try
            {
                return File.Exists(ManifestPath) && JsonNode.Parse(File.ReadAllText(ManifestPath))
                    ?["packtype"]?.GetValue<string>() == "serverpack";
            }
            catch { return false; }
        }
    }

    /// <summary>读取服务端整合包登记（v47：含 -jar 核心信息）；非服务端包导入返回 null。</summary>
    public static ServerPackInfo? GetServerPackImport()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return null;
            if (JsonNode.Parse(File.ReadAllText(ManifestPath)) is not JsonObject m
                || m["packtype"]?.GetValue<string>() != "serverpack"
                || m["zipPath"]?.GetValue<string>() is not { Length: > 0 } zip) return null;
            return new ServerPackInfo(
                m["mc"]?.GetValue<string>() ?? "",
                m["loader"]?.GetValue<string>() ?? "",
                m["serverBuild"]?.GetValue<string>() ?? "",
                m["rootPrefix"]?.GetValue<string>() ?? "",
                "",
                m["coreJar"]?.GetValue<string>() ?? "",
                m["extraJvm"]?.GetValue<string>() ?? "",
                m["coreLabel"]?.GetValue<string>() ?? "",
                zip);
        }
        catch { return null; }
    }

    /// <summary>读取随服务端包配对的客户端整合包（v44）；未配对返回 null。</summary>
    public static ClientPackInfo? GetClientPackImport()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return null;
            return ClientPackInfo.FromJson(
                (JsonNode.Parse(File.ReadAllText(ManifestPath)) as JsonObject)?["clientpack"]);
        }
        catch { return null; }
    }

    /// <summary>更新暂存导入的客户端整合包（v44：UI 手动指定/清除时调用；无导入清单时静默忽略）。</summary>
    public static void SetClientPackImport(ClientPackInfo? cp)
    {
        try
        {
            if (!File.Exists(ManifestPath)) return;
            if (JsonNode.Parse(File.ReadAllText(ManifestPath)) is not JsonObject m) return;
            m["clientpack"] = cp?.ToJson();
            File.WriteAllText(ManifestPath, m.ToJsonString());
        }
        catch { /* 清单损坏时不阻塞 UI */ }
    }

    /// <summary>客户端整合包轻量识别（v44 配对用）：zip 内含 CF/modrinth/MMC/HMCL 清单即算。</summary>
    public static bool IsClientPackZip(string path)
    {
        try
        {
            using var zip = OpenZipSmart(path);
            return zip.Entries.Any(e =>
            {
                var n = e.FullName.TrimStart('/');
                return n.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("mmc-pack.json", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("modpack.json", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch { return false; }
    }

    /// <summary>在服务端包同目录找最匹配的客户端整合包（文件名公共前缀最长、≥2 字符）；找不到返回 null。</summary>
    public static string? FindClientPackNear(string serverPackPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(serverPackPath);
            if (dir is null || !Directory.Exists(dir)) return null;
            var baseName = Path.GetFileNameWithoutExtension(serverPackPath);
            string? best = null;
            var bestScore = 1;
            foreach (var f in Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.Equals(f, serverPackPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsClientPackZip(f)) continue;
                var other = Path.GetFileNameWithoutExtension(f);
                var score = CommonPrefix(baseName, other);
                if (score > bestScore) { bestScore = score; best = f; }
            }
            return best;
        }
        catch { return null; }
    }

    private static int CommonPrefix(string a, string b)
    {
        var norm = new Func<string, string>(s => s.ToLowerInvariant());
        var x = norm(a); var y = norm(b);
        int i = 0;
        while (i < x.Length && i < y.Length && x[i] == y[i]) i++;
        return i;
    }

    /// <summary>由本地 zip 构造客户端整合包描述（v44；sha1/缓存延后到开房时处理）。</summary>
    private static ClientPackInfo BuildClientPackFromFile(string path, string mc, string loader)
    {
        var fi = new FileInfo(path);
        return new ClientPackInfo(Path.GetFileName(path), ClientPackInfo.SourceLocal, null,
            fi.Length, null, mc, loader, path);
    }

    /// <summary>清空暂存区（重新导入前调用）。</summary>
    public static void Clear()
    {
        try { if (Directory.Exists(ImportRoot)) Directory.Delete(ImportRoot, recursive: true); }
        catch { /* 删除失败不影响后续导入（会覆盖） */ }
    }

    /// <summary>
    /// 解析选中的导入文件（.zip / .mrpack，可多选）到暂存区。重复调用会先清空再导入。
    /// v44：多选自动配对——选中的文件先分类，服务端包 + 客户端包（CF/mrpack 清单包）成对登记；
    /// 单独导入客户端清单包时维持旧流程（进暂存区当模组源）。
    /// </summary>
    public static async Task<ImportResult> ImportAsync(IEnumerable<string> paths,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Clear();
        Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(OverrideDir);

        var allMods = new List<ModEntry>();
        var downloadCount = 0;
        var skipped = 0;
        var details = new List<string>();
        string? suggestedMc = null;
        string? suggestedLoader = null;

        // ---- v44 先分类：服务端包 / 客户端清单包 / 其余（暂存区流程）
        var serverPacks = new List<string>();
        var clientPackFiles = new List<string>();
        var stagingFiles = new List<string>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path)) { stagingFiles.Add(path); continue; }
            if (!File.Exists(path)) { skipped++; continue; }
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".zip" or ".mrpack")) { skipped++; continue; }
            if (DetectServerPackZip(path) is not null) serverPacks.Add(path);
            else if (IsClientPackZip(path)) clientPackFiles.Add(path);
            else stagingFiles.Add(path);
        }

        // ---- 服务端包流程：登记 zip（开房时整包展开）+ 自动配对客户端包
        if (serverPacks.Count > 0)
        {
            var spPath = serverPacks[0];
            var sp = DetectServerPackZip(spPath)!;
            progress?.Report(sp.Detail);
            var cpPath = clientPackFiles.FirstOrDefault();
            var cpSource = cpPath is not null ? "随导入提供" : null;
            if (cpPath is null)
            {
                cpPath = FindClientPackNear(spPath);
                cpSource = cpPath is not null ? "自动配对" : null;
            }
            var clientPack = cpPath is null ? null : BuildClientPackFromFile(cpPath, sp.Mc, sp.Loader);
            var spManifest = new JsonObject
            {
                ["imported_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["mc"] = sp.Mc,
                ["loader"] = sp.Loader,
                ["packtype"] = "serverpack",
                ["zipPath"] = spPath,
                ["rootPrefix"] = sp.RootPrefix,
                ["serverBuild"] = sp.Build,
                ["coreJar"] = sp.CoreJar,
                ["extraJvm"] = sp.ExtraJvm,
                ["coreLabel"] = sp.CoreLabel,
                ["clientpack"] = clientPack?.ToJson(),
                ["mods"] = new JsonArray(),
            };
            await File.WriteAllTextAsync(ManifestPath, spManifest.ToJsonString(), ct).ConfigureAwait(false);
            var detail = sp.Detail + (clientPack is not null
                ? $"；客户端包已配对（{cpSource}）：{clientPack.Name}（{clientPack.Size / 1048576.0:F0}MB）"
                : "；未找到客户端整合包，可在开房卡「客户端包」行手动指定");
            details.Add(detail);
            return new ImportResult(0, 0, 0, detail, sp.Mc.Length > 0 ? sp.Mc : null,
                sp.Loader.Length > 0 ? sp.Loader : null);
        }

        // v47：没有服务端包时，单独导入的客户端整合包（CF manifest / .mrpack / MultiMC）也按导入
        // 流程处理——下载 mods + 展开 overrides + 产出 mc/loader 建议（开房自动装对应核心）。
        // 此前这些包仅在"与服务端包配对"时被使用，单独导入会被静默忽略。
        foreach (var cp in clientPackFiles)
            stagingFiles.Add(cp);

        foreach (var path in stagingFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                // 目录按 mods 文件夹处理（复制其中所有 jar）
                var jars = Directory.EnumerateFiles(path, "*.jar").ToList();
                progress?.Report($"复制 mods 文件夹：{Path.GetFileName(path.TrimEnd('\\', '/'))}（{jars.Count} 个模组）");
                foreach (var jar in jars)
                    File.Copy(jar, Path.Combine(ModsDir, Path.GetFileName(jar)), overwrite: true);
                allMods.AddRange(jars.Select(j => new ModEntry(
                    Path.GetFileNameWithoutExtension(j), null, Path.GetFileName(j))));
                details.Add($"mods 文件夹 {Path.GetFileName(path.TrimEnd('\\', '/'))}：{jars.Count} 个模组");
                continue;
            }

            progress?.Report($"解析 {Path.GetFileName(path)}…");

            var (mods, dl, skip, detail, mc, loader) =
                await ImportArchiveAsync(path, progress, ct).ConfigureAwait(false);
            allMods.AddRange(mods);
            downloadCount += dl;
            skipped += skip;
            details.Add(detail);
            suggestedMc ??= mc;
            suggestedLoader ??= loader;
        }

        // 散装 mods 目录 / 裸 jar 导入没有 manifest 元数据（suggestedMc/Loader 为空）——
        // 扫描 jar 内声明（mods.toml / fabric.mod.json）识别加载器与版本，让开房自动选项有据可依
        if ((suggestedMc is null || suggestedLoader is null) && allMods.Count > 0)
        {
            try
            {
                var (detLoader, detMc) = DetectImportedModsMeta();
                if (suggestedLoader is null && detLoader is not null && detLoader != "quilt")
                {
                    suggestedLoader = detLoader;
                    progress?.Report($"已从模组文件识别加载器：{detLoader}");
                }
                if (suggestedMc is null && detMc is not null)
                {
                    suggestedMc = detMc;
                    progress?.Report($"已从模组文件推断游戏版本：{detMc}");
                }
            }
            catch
            {
                // 元数据识别失败不影响导入（开房时回落默认值）
            }
        }

        // 写导入清单（seed 时并入 meta["mods"]；含建议版本，开房 UI 提示用）
        var manifest = new JsonObject
        {
            ["imported_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["mc"] = suggestedMc ?? "",
            ["loader"] = suggestedLoader ?? "",
            ["mods"] = new JsonArray(allMods.Select(m => (JsonNode)new JsonObject
            {
                ["project"] = m.Project,
                ["version"] = m.Version,
                ["filename"] = m.Filename,
                ["url"] = m.Url,
            }).ToArray()),
        };
        await File.WriteAllTextAsync(ManifestPath, manifest.ToJsonString(), ct).ConfigureAwait(false);

        var summary = string.Join("；", details.Where(d => d.Length > 0));
        return new ImportResult(allMods.Count, downloadCount, skipped, summary, suggestedMc, suggestedLoader);
    }

    /// <summary>
    /// 智能打开 zip（v43）：国内打包器常用 GBK 文件名且不设 UTF-8 标志，.NET 默认解码会把中文名
    /// 变成 U+FFFD 替换符（不可逆，解压即坏名）。检测到替换符就用 GBK 重新打开 zip 条目表。
    /// 需要主程序已注册 CodePagesEncodingProvider（PCL 启动时注册）。
    /// </summary>
    public static ZipArchive OpenZipSmart(string path)
    {
        var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Any(e => e.FullName.Contains('\uFFFD')))
        {
            zip.Dispose();
            zip = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read, leaveOpen: false,
                entryNameEncoding: Encoding.GetEncoding(936));
        }
        return zip;
    }

    /// <summary>
    /// 服务端整合包识别（v43）：签名 = run/start 脚本 或 已安装的 Forge/Neo args 文件 或 根 server.jar。
    /// 兼容作者把全部内容套一层"xxx服务端/"文件夹的打包习惯（自动剥离单一公共根目录）。
    /// 版本解析优先级：libraries/**/{win,unix}_args.txt 路径 &gt; run 脚本内容 &gt; 根 server.jar（vanilla，版本未知）。
    /// 客户端整合包（manifest.json / modrinth.index.json / mmc-pack.json）不是服务端包，直接排除。
    /// </summary>
    private static ServerPackInfo? DetectServerPackZip(string path)
    {
        try
        {
            using var zip = OpenZipSmart(path);
            var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList(); // 目录条目 Name 为空
            if (entries.Count == 0) return null;

            // 客户端整合包不是服务端包（manifest/mrpack/mmc 走 ImportArchiveAsync 客户端链，
            // 那条链对 CF/MR/MMC 的支持更完整且带 env 过滤）
            if (entries.Any(e => e.FullName is
                "manifest.json" or "modrinth.index.json" or "mmc-pack.json" or "instance.cfg"))
                return null;

            // 公共根目录剥离：所有条目都在同一个首段目录下时剥掉（作者打包习惯）
            var first = entries[0].FullName.Split('/')[0];
            var rootPrefix = entries.All(e => e.FullName.StartsWith(first + "/", StringComparison.Ordinal))
                ? first + "/" : "";
            string Strip(string n)
                => rootPrefix.Length > 0 && n.StartsWith(rootPrefix, StringComparison.Ordinal)
                    ? n[rootPrefix.Length..] : n;

            var rel = entries.Select(e => Strip(e.FullName)).ToList();
            var hasScripts = rel.Any(n => n.Equals("run.bat", StringComparison.OrdinalIgnoreCase)
                || n.Equals("run.sh", StringComparison.OrdinalIgnoreCase)
                || n.Equals("start.bat", StringComparison.OrdinalIgnoreCase)
                || n.Equals("start.sh", StringComparison.OrdinalIgnoreCase));
            var argsPath = rel.FirstOrDefault(n => n.EndsWith("win_args.txt", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith("unix_args.txt", StringComparison.OrdinalIgnoreCase));
            var hasServerJar = rel.Any(n => n.Equals("server.jar", StringComparison.OrdinalIgnoreCase));
            var modCount = rel.Count(n => n.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
                && n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

            // v47：启动脚本文本一次读取（forge 路径引用 / -jar 核心 / -D 参数 都从这一份解析）
            string scriptText = "";
            if (hasScripts)
            {
                var script = entries.FirstOrDefault(e =>
                    Strip(e.FullName) is "run.bat" or "run.sh" or "start.bat" or "start.sh");
                if (script is not null)
                    using (var s = script.Open())
                    using (var sr = new StreamReader(s, Encoding.UTF8))
                        scriptText = sr.ReadToEnd();
            }
            // -jar 核心解析（paper/mohist/arclight/catserver/fabric-launcher 等通用形态）
            string? scriptCore = null, scriptJvm = null;
            if (scriptText.Length > 0)
            {
                var jm = Regex.Match(scriptText, @"-jar\s+""?([^""\r\n]+\.jar)""?", RegexOptions.IgnoreCase);
                if (jm.Success)
                {
                    var cand = jm.Groups[1].Value.Trim().Replace('\\', '/');
                    // 安装器类 jar（mohist installer 等）不是可直接跑的核心；引用包内不存在的 jar 也不算
                    var candRel = rel.FirstOrDefault(n =>
                        n.Equals(cand, StringComparison.OrdinalIgnoreCase)
                        || n.EndsWith("/" + cand, StringComparison.OrdinalIgnoreCase));
                    if (candRel is not null && !Regex.IsMatch(cand, @"install|setup|patch", RegexOptions.IgnoreCase))
                    {
                        scriptCore = candRel;
                        var javaIdx = scriptText.LastIndexOf("-jar", jm.Index, StringComparison.OrdinalIgnoreCase);
                        var javaPart = javaIdx >= 0 ? scriptText[..jm.Index] : "";
                        var jvmParts = Regex.Matches(javaPart, @"(?:^|[\s&=])(-D\S+)")
                            .Select(x => x.Groups[1].Value.Trim())
                            .Where(x => x.Length > 2 && !x.Contains('%') && !x.Contains('~'))
                            .Distinct().ToList();
                        if (jvmParts.Count > 0) scriptJvm = string.Join(" ", jvmParts);
                    }
                }
            }
            var fabricLaunch = rel.FirstOrDefault(n =>
                n.Equals("fabric-server-launch.jar", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith("/fabric-server-launch.jar", StringComparison.OrdinalIgnoreCase));

            // 签名：args 文件 / 启动脚本 / 已装核心（server.jar ∨ fabric-launcher ∨ 脚本可解析的 -jar 核心）
            // 满足其一即可；但纯脚本而无任何可启动内容/模组/args 的（半安装空壳）不算
            var hasCore = argsPath is not null || hasServerJar || scriptCore is not null || fabricLaunch is not null;
            if (!hasScripts && !hasCore) return null;
            if (modCount == 0 && !hasCore) return null;

            // 版本与加载器解析
            string loader = "", build = "", mc = "";
            if (argsPath is not null)
            {
                var m = Regex.Match(argsPath, @"libraries/net/(minecraftforge/forge|neoforged/neoforge)/([^/]+)/",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var isForge = m.Groups[1].Value.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase);
                    loader = isForge ? "forge" : "neoforge";
                    build = m.Groups[2].Value;
                    // Forge：目录 {mc}-{build}（拆出 mc 存 build，与开房安装链的 loader_full 编码一致）；
                    // NeoForge：目录 {build}（21.1.x → 1.21.1，47.1.x → 1.20.1 特例）
                    if (isForge && build.Contains('-'))
                    {
                        mc = build.Split('-')[0];
                        build = build[(mc.Length + 1)..];
                    }
                    else
                    {
                        mc = NeoMcOf(build);
                    }
                }
            }
            if (loader.Length == 0 && scriptText.Length > 0)
            {
                // run 脚本兜底：@libraries/net/minecraftforge/forge/{ver}/win_args.txt
                var m = Regex.Match(scriptText, @"libraries/net/(minecraftforge/forge|neoforged/neoforge)/([^/]+)/",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var isForge = m.Groups[1].Value.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase);
                    loader = isForge ? "forge" : "neoforge";
                    build = m.Groups[2].Value;
                    mc = isForge ? build.Split('-')[0] : NeoMcOf(build);
                }
            }

            // v47：-jar 核心服务端包识别（fabric-server-launch / paper / mohist / arclight /
            // catserver / purpur / vanilla server.jar 等）。args 链命中时不进入（forge 走专属启动链）。
            // 脚本核心/forge 引用已在上方统一解析（scriptCore/scriptJvm）。
            var coreJar = "";
            var extraJvm = "";
            var coreLabel = "";
            if (loader.Length == 0)
            {
                var chosen = fabricLaunch ?? scriptCore ?? (hasServerJar ? "server.jar" : "");
                if (chosen.Length > 0)
                {
                    coreJar = chosen;
                    var coreName = Path.GetFileName(chosen);
                    coreLabel = Regex.Match(coreName.ToLowerInvariant(), @"paper|purpur|folia|mohist|arclight|catserver|fabric|quilt|vanilla").Value switch
                    {
                        "paper" => "Paper",
                        "purpur" => "Purpur",
                        "folia" => "Folia",
                        "mohist" => "Mohist",
                        "arclight" => "Arclight",
                        "catserver" => "CatServer",
                        "fabric" => "Fabric",
                        "quilt" => "Quilt",
                        _ => hasServerJar && coreName == "server.jar" ? "Vanilla" : "Java 服务端",
                    };
                    if (fabricLaunch is not null) coreLabel = "Fabric";
                    var vm = Regex.Match(coreName, @"1\.\d{1,2}(?:\.\d+)?");
                    if (vm.Success) mc = vm.Value;
                    if (scriptJvm is not null) extraJvm = scriptJvm;
                    // fabric 核心按 fabric 走开房链（fabric-api 前置自动装）；其余 -jar 核心按 vanilla 通道
                    loader = coreLabel == "Fabric" ? "fabric" : "vanilla";
                }
            }

            var head = mc.Length > 0 ? $"MC {mc} / {(coreLabel.Length > 0 ? coreLabel : loader)}" : loader;
            var detail = $"服务端整合包 {Path.GetFileName(path)}：{head}" +
                         (modCount > 0 ? $"，{modCount} 个模组" : "") +
                         (coreJar.Length > 0
                             ? $"（自带 {coreLabel} 服务端核心，开房时自动展开直接启动）"
                             : "（服务端已安装完成，开房时自动展开；请勿移动或删除原压缩包）");
            return new ServerPackInfo(mc, loader, build, rootPrefix, detail, coreJar, extraJvm, coreLabel, path);
        }
        catch
        {
            return null; // 打不开/读不动：按普通压缩包走既有流程
        }
    }

    /// <summary>NeoForge 构建号反推 MC 版本：21.1.x → 1.21.1（主-1.次.尾）；47.1.x 特例 → 1.20.1。</summary>
    private static string NeoMcOf(string build)
    {
        var parts = build.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b))
        {
            if (a == 47 && b == 1) return "1.20.1";
            return $"1.{a - 1}.{b}";
        }
        return "";
    }

    /// <summary>解析单个归档。返回 (mods, 下载数, 跳过数, 详情, 建议MC版本, 建议loader)。</summary>
    private static async Task<(List<ModEntry> Mods, int Downloads, int Skipped, string Detail,
        string? Mc, string? Loader)> ImportArchiveAsync(
        string path, IProgress<string>? progress, CancellationToken ct)
    {
        var mods = new List<ModEntry>();
        var downloads = 0;
        var skipped = 0;
        var name = Path.GetFileName(path);

        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.ToList();

        // ---- Modrinth .mrpack：modrinth.index.json 描述直链，overrides/ 为覆盖文件
        var indexEntry = entries.FirstOrDefault(e =>
            e.FullName.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase));
        if (indexEntry is not null)
        {
            var index = JsonNode.Parse(await ReadEntryAsync(indexEntry, ct).ConfigureAwait(false)) as JsonObject
                ?? throw new InvalidDataException("modrinth.index.json 解析失败");
            var mc = index["dependencies"]?.AsObject().FirstOrDefault(kv =>
                    kv.Key.Contains("minecraft", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();
            var loader = index["dependencies"] is JsonObject deps
                ? deps.ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>())
                    .Select(kv => NormalizeLoader(kv.Key switch
                    {
                        "fabric-loader" => "fabric",
                        "quilt-loader" => "quilt",
                        "forge" => "forge",
                        "neoforge" => "neoforge",
                        _ => null,
                    }) ?? null)
                    .FirstOrDefault(x => x is not null)
                : null;
            var clientOnly = 0;
            if (index["files"] is JsonArray files)
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (f is null) continue;
                    var filename = f["path"]?.GetValue<string>() is { Length: > 0 } p
                        ? Path.GetFileName(p.Replace('/', '\\')) : null;
                    var url = f["downloads"] is JsonArray dls && dls.Count > 0
                        ? dls[0]?.GetValue<string>() : null;
                    if (filename is null) { skipped++; continue; }
                    if (!filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    // env.server=unsupported = 客户端专属（小地图/光影等）：不进服务端，
                    // 只记入清单让加入端按直链安装（host 无需留档）
                    if (f["env"]?["server"]?.GetValue<string>() == "unsupported")
                    {
                        mods.Add(new ModEntry(Path.GetFileNameWithoutExtension(filename),
                            null, filename, url));
                        clientOnly++;
                        continue;
                    }
                    if (url is null) { skipped++; continue; }
                    progress?.Report($"下载 Modrinth 模组：{filename}");
                    try
                    {
                        await McSources.DownloadFileAsync(new[] { url },
                            Path.Combine(ModsDir, filename), ct).ConfigureAwait(false);
                        downloads++;
                        mods.Add(new ModEntry(Path.GetFileNameWithoutExtension(filename),
                            null, filename, url));
                    }
                    catch
                    {
                        skipped++;
                    }
                }
            await ExpandOverridesAsync(entries, "overrides/", progress, ct).ConfigureAwait(false);
            // v47：server-overrides/ 服务端层级覆盖（Modrinth 规范：在 overrides 之后应用、同名覆盖）
            var srvCount = await ExpandOverridesAsync(entries, "server-overrides/", progress, ct).ConfigureAwait(false);
            if (srvCount > 0) progress?.Report($"已展开 server-overrides {srvCount} 个文件");
            return (mods, downloads, skipped,
                $"Modrinth 整合包 {name}：{mods.Count - clientOnly} 个服务端可用模组" +
                (clientOnly > 0 ? $" + {clientOnly} 个客户端专属（仅加入端安装）" : "") +
                (mc is null ? "" : $"（MC {mc}）"),
                mc, loader);
        }

        // ---- MultiMC / Prism 启动器实例包：mmc-pack.json + .minecraft/（或 minecraft/）
        var mmcEntry = entries.FirstOrDefault(e =>
            e.FullName.Equals("mmc-pack.json", StringComparison.OrdinalIgnoreCase));
        if (mmcEntry is not null)
        {
            var pack = JsonNode.Parse(await ReadEntryAsync(mmcEntry, ct).ConfigureAwait(false)) as JsonObject
                ?? throw new InvalidDataException("mmc-pack.json 解析失败");
            string? mc = null, loader = null;
            if (pack["components"] is JsonArray comps)
                foreach (var c in comps)
                {
                    if (c is not JsonObject co) continue;
                    var uid = co["uid"]?.GetValue<string>();
                    var ver = co["version"]?.GetValue<string>();
                    if (uid == "net.minecraft") mc = ver;
                    else if (uid == "net.fabricmc.fabric-loader") loader = "fabric";
                    else if (uid == "org.quiltmc.quilt-loader") loader = "quilt";
                    else if (uid == "net.minecraftforge") loader = "forge";
                    else if (uid is "net.neoforged.neoforge" or "net.neoforged") loader = "neoforge";
                }
            var count = await ExpandInstanceDirAsync(entries, progress, ct).ConfigureAwait(false);
            return (mods, downloads, skipped,
                $"MultiMC/Prism 整合包 {name}：展开 {count} 个文件（MC {mc ?? "?"} {loader ?? ""}）",
                mc, loader);
        }

        // ---- CurseForge 整合包：manifest.json（projectID/fileID 需经 cfwidget 解析后 CDN 直下）
        var manifestEntry = entries.FirstOrDefault(e =>
            e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is not null)
        {
            var mf = JsonNode.Parse(await ReadEntryAsync(manifestEntry, ct).ConfigureAwait(false)) as JsonObject
                ?? throw new InvalidDataException("manifest.json 解析失败");
            var mc = mf["minecraft"]?["version"]?.GetValue<string>();
            // CF 官方 manifest 字段是 modLoaders（大写 L）；旧代码只查小写导致 loader 建议恒空
            var loaderId = mf["minecraft"] is JsonObject mfMc
                ? (mfMc["modLoaders"] ?? mfMc["modloaders"]) is JsonArray mls
                    ? mls.Where(x => x?["primary"] is { } p && p.GetValue<bool>())
                        .Select(x => x?["id"]?.GetValue<string>()).FirstOrDefault(x => x is not null)
                    : null
                : null;
            var loader = NormalizeLoader(loaderId);
            var count = await ExpandOverridesAsync(entries, "overrides/", progress, ct).ConfigureAwait(false);

            var resolved = 0;
            if (mf["files"] is JsonArray arr)
            {
                // cfwidget 按项目缓存：同一项目多文件只查一次
                var widgetCache = new Dictionary<int, JsonNode?>();
                foreach (var f in arr)
                {
                    ct.ThrowIfCancellationRequested();
                    if (f is not JsonObject fo) continue;
                    if (fo["required"] is { } req && !req.GetValue<bool>()) { skipped++; continue; }  // 可选模组跳过
                    var pid = fo["projectID"]?.GetValue<int>() ?? 0;
                    var fid = fo["fileID"]?.GetValue<int>() ?? 0;
                    if (pid <= 0 || fid <= 0) { skipped++; continue; }
                    if (!widgetCache.TryGetValue(pid, out var widget))
                    {
                        widget = await CfWidgetProjectAsync(pid, progress, ct).ConfigureAwait(false);
                        widgetCache[pid] = widget;
                    }
                    var fname = widget?["files"] is JsonArray wfs
                        ? wfs.FirstOrDefault(w => w?["id"] is { } wid && wid.GetValue<int>() == fid)
                            ?["name"]?.GetValue<string>()
                        : null;
                    // mediafilez CDN 规则：files/{fileId 前 4 位}/{余下}/{文件名}（免 API key）
                    var cdn = fname is null || fid < 10000
                        ? null
                        : $"https://mediafilez.forgecdn.net/files/{fid.ToString()[..4]}/{fid.ToString()[4..]}/{fname}";
                    if (cdn is null) { skipped++; continue; }
                    progress?.Report($"下载 CurseForge 模组：{fname}");
                    try
                    {
                        await McSources.DownloadFileAsync(new[] { cdn },
                            Path.Combine(ModsDir, fname), ct).ConfigureAwait(false);
                        downloads++;
                        resolved++;
                        mods.Add(new ModEntry(Path.GetFileNameWithoutExtension(fname),
                            null, fname, cdn));
                    }
                    catch
                    {
                        skipped++;
                    }
                }
            }
            return (mods, downloads, skipped,
                $"CurseForge 整合包 {name}：下载 {resolved} 个模组，展开 {count} 个覆盖文件" +
                (skipped > 0 ? $"，{skipped} 个未解析/可选" : "") +
                (mc is null ? "" : $"（MC {mc}）"),
                mc, loader);
        }

        // ---- 服务端包 / 根目录布局：mods/、config/、kubejs/ 等顶层目录
        var hasRootLayout = entries.Any(e =>
            (e.FullName.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
             || e.FullName.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
            && !e.FullName.EndsWith("/", StringComparison.Ordinal));
        if (hasRootLayout)
        {
            var count = await ExpandOverridesAsync(entries, "", progress, ct).ConfigureAwait(false);
            return (mods, downloads, skipped,
                $"服务端包 {name}：入库 {count} 个文件（模组进 mods，其余展开到服务端根）",
                mc: null, loader: null);
        }

        // ---- 纯 mods 压缩包：提取所有 .jar
        var jarEntries = entries.Where(e =>
            e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
            !e.FullName.EndsWith("/", StringComparison.Ordinal)).ToList();
        if (jarEntries.Count > 0)
        {
            foreach (var e in jarEntries)
            {
                ct.ThrowIfCancellationRequested();
                var filename = Path.GetFileName(e.FullName.Replace('/', '\\'));
                await using var src = e.Open();
                await using var dst = File.Create(Path.Combine(ModsDir, filename));
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                mods.Add(new ModEntry(Path.GetFileNameWithoutExtension(filename), null, filename));
            }
            return (mods, downloads, skipped, $"模组压缩包 {name}：{mods.Count} 个模组",
                mc: null, loader: null);
        }

        return (mods, downloads, skipped + entries.Count, $"压缩包 {name}：未识别出模组内容",
            mc: null, loader: null);
    }

    /// <summary>展开 overrides/ 到暂存区；mods 下的 jar 直接进 imports/mods（避免重复），其余进 imports/override。</summary>
    private static async Task<int> ExpandOverridesAsync(List<ZipArchiveEntry> entries, string prefix,
        IProgress<string>? progress, CancellationToken ct)
    {
        var count = 0;
        foreach (var e in entries)
        {
            if (!e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (e.FullName.EndsWith("/", StringComparison.Ordinal) || e.Length == 0 && e.FullName.EndsWith("/")) continue;
            if (IsJunkEntry(e.FullName)) continue;
            ct.ThrowIfCancellationRequested();
            var rel = e.FullName[prefix.Length..].Replace('/', '\\');
            var filename = Path.GetFileName(rel);
            if (rel.StartsWith("mods\\", StringComparison.OrdinalIgnoreCase) &&
                filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                // 模组本体：直接进 imports/mods
                await using var src = e.Open();
                await using var dst = File.Create(Path.Combine(ModsDir, filename));
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
            else
            {
                var dest = Path.Combine(OverrideDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await using var src = e.Open();
                await using var dst = File.Create(dest);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
            count++;
            if (count % 20 == 0) progress?.Report($"展开 overrides…{count}");
        }
        return count;
    }

    /// <summary>展开 MultiMC/Prism 实例的 .minecraft/（或 minecraft/）目录：mods 入库、其余进 override。</summary>
    private static async Task<int> ExpandInstanceDirAsync(List<ZipArchiveEntry> entries,
        IProgress<string>? progress, CancellationToken ct)
    {
        // 兼容两种目录名，取实际存在内容的那个
        var prefix = entries.Any(e => e.FullName.StartsWith(".minecraft/", StringComparison.OrdinalIgnoreCase))
            ? ".minecraft/"
            : "minecraft/";
        return await ExpandOverridesAsync(entries, prefix, progress, ct).ConfigureAwait(false);
    }

    /// <summary>macOS 打包垃圾与系统文件过滤。</summary>
    private static bool IsJunkEntry(string fullName)
    {
        var segs = fullName.Split('/', '\\');
        return segs.Any(s => s is "__MACOSX" or ".DS_Store" or "Thumbs.db");
    }

    /// <summary>modloader id 归一化：fabric-0.15.11 / forge-47.2.0 / neoforge-20.4.x / quilt-… → 统一名称。</summary>
    private static string? NormalizeLoader(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var s = id.Trim().ToLowerInvariant();
        if (s.StartsWith("fabric")) return "fabric";
        if (s.StartsWith("quilt")) return "quilt";
        if (s.StartsWith("neo")) return "neoforge";
        if (s.StartsWith("forge")) return "forge";
        return null;
    }

    private static readonly Dictionary<int, JsonNode?> _cfWidgetCache = new();

    /// <summary>
    /// 查询 CurseForge 项目信息（cfwidget.com 免费接口，无需 API key；项目按 id 缓存）。
    /// 未缓存的项目首次查询会被排队生成（202/超时），带退避重试；三次失败返回 null（调用方计跳过）。
    /// </summary>
    private static async Task<JsonNode?> CfWidgetProjectAsync(int projectId,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (_cfWidgetCache.TryGetValue(projectId, out var cached)) return cached;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var node = await McSources.GetJsonAsync(
                    $"https://api.cfwidget.com/{projectId}", ct).ConfigureAwait(false);
                _cfWidgetCache[projectId] = node;
                return node;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                progress?.Report($"CurseForge 项目 {projectId} 查询重试 {attempt + 1}/3（{e.Message}）");
                await Task.Delay(3000 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
        _cfWidgetCache[projectId] = null;
        return null;
    }

    // ---------------------------------------------------------------- 开房时落盘

    /// <summary>导入池元数据里的建议版本（整合包导入时解析的 mc/loader）；无导入记录返回空串。</summary>
    public static (string Mc, string Loader) GetImportSuggestion()
    {
        try
        {
            if (File.Exists(ManifestPath)
                && JsonNode.Parse(File.ReadAllText(ManifestPath)) is JsonObject m)
                return (m["mc"]?.GetValue<string>() ?? "", m["loader"]?.GetValue<string>() ?? "");
        }
        catch
        {
            // 清单损坏按无建议处理
        }
        return ("", "");
    }

    /// <summary>
    /// 扫描暂存区 mods/*.jar 的元数据，识别加载器（多数决）与 MC 版本推断（散装导入的兜底识别）。
    /// 判据：META-INF/neoforge.mods.toml→neoforge、META-INF/mods.toml→forge、quilt.mod.json→quilt、
    /// fabric.mod.json→fabric；MC 版本从依赖声明推断（toml 的 versionRange / fabric depends.minecraft）：
    /// 精确声明优先（取多数），范围声明取下界（两位数下界按生态惯例补 ".1"，如 [1.20,1.21)→1.20.1）。
    /// 识别不出返回 (null, null)。
    /// </summary>
    public static (string? Loader, string? Mc) DetectImportedModsMeta()
    {
        try
        {
            if (!Directory.Exists(ModsDir)) return (null, null);
            var loaderVotes = new Dictionary<string, int>();
            var exact = new List<Version>();
            var lows = new List<Version>();
            var highs = new List<Version>();   // 排他上界
            foreach (var jar in Directory.EnumerateFiles(ModsDir, "*.jar"))
            {
                try
                {
                    using var zip = ZipFile.OpenRead(jar);
                    string? loaderTag = null;
                    string? mcDecl = null;
                    var neo = zip.GetEntry("META-INF/neoforge.mods.toml");
                    var toml = neo ?? zip.GetEntry("META-INF/mods.toml");
                    var quilt = zip.GetEntry("quilt.mod.json");
                    var fabric = zip.GetEntry("fabric.mod.json");
                    if (toml is not null)
                    {
                        loaderTag = neo is not null ? "neoforge" : "forge";
                        mcDecl = ExtractTomlMinecraftRange(new StreamReader(toml.Open()).ReadToEnd());
                    }
                    else if (quilt is not null)
                    {
                        loaderTag = "quilt";
                    }
                    else if (fabric is not null)
                    {
                        loaderTag = "fabric";
                        if (JsonNode.Parse(new StreamReader(fabric.Open()).ReadToEnd()) is JsonObject o)
                        {
                            var dep = o["depends"]?["minecraft"];
                            mcDecl = dep switch
                            {
                                JsonValue v when v.TryGetValue<string>(out var s) => s,
                                JsonArray arr => arr.FirstOrDefault()?.GetValue<string>(),
                                _ => null,
                            };
                        }
                    }
                    if (loaderTag is not null)
                        loaderVotes[loaderTag] = loaderVotes.GetValueOrDefault(loaderTag) + 1;
                    if (!string.IsNullOrWhiteSpace(mcDecl))
                        ParseMcDecl(mcDecl!, exact, lows, highs);
                }
                catch
                {
                    // 单个 jar 解析失败忽略（损坏/非模组 jar）
                }
            }

            var loader = loaderVotes.Count == 0
                ? null
                : loaderVotes.OrderByDescending(kv => kv.Value).First().Key;
            string? mc = null;
            if (exact.Count > 0)
                mc = exact.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key.ToString();
            else if (lows.Count > 0)
            {
                var low = lows.Max();
                // 交集为空（下界不小于某个上界）→ 放弃推断
                if (highs.Count == 0 || highs.Min() > low)
                    mc = low.Build == -1 ? $"{low.Major}.{low.Minor}.1" : low.ToString();
            }
            return (loader, mc);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>从 forge/neoforge 的 mods.toml 抽 minecraft 依赖的 versionRange：按 [[dependencies]] 分块，
    /// 块内含 modId="minecraft" 时取 versionRange（避开顶层 loaderVersion）。</summary>
    private static string? ExtractTomlMinecraftRange(string toml)
    {
        foreach (var block in Regex.Split(toml, @"\[\["))
        {
            if (!Regex.IsMatch(block, @"(?i)modId\s*=\s*""minecraft""")) continue;
            var m = Regex.Match(block, @"(?i)versionRange\s*=\s*""([^""]+)""");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>解析 MC 版本声明（精确 / [a] / [a,b) / >=a &lt;b 等）归入精确表/下界表/上界表。</summary>
    private static void ParseMcDecl(string decl, List<Version> exact, List<Version> lows, List<Version> highs)
    {
        var s = decl.Trim();
        var tokens = Regex.Matches(s, @"(\d+\.\d+(?:\.\d+)?)");
        if (tokens.Count == 0) return;
        if (!s.Contains('[') && !s.Contains('('))
        {
            var op = s[..tokens[0].Index].Trim();
            var first = Version.Parse(tokens[0].Groups[1].Value);
            if (tokens.Count == 1)
            {
                if (op is "" or "=" or ">=" or ">") { if (op is "" or "=") exact.Add(first); else lows.Add(first); }
                else highs.Add(first);   // <x 或 <=x
            }
            else
            {
                lows.Add(first);
                highs.Add(Version.Parse(tokens[^1].Groups[1].Value));
            }
            return;
        }
        // 区间形式 [a] / [a,b) / [a,)
        var inner = s.Trim('[', ']', '(', ')');
        var vs = inner.Split(',')
            .Select(p => p.Trim())
            .Where(p => Regex.IsMatch(p, @"^\d+(\.\d+){1,3}$"))
            .Select(Version.Parse)
            .ToList();
        if (vs.Count == 1)
        {
            if (s.StartsWith('[') && s.EndsWith(']')) exact.Add(vs[0]);
            else lows.Add(vs[0]);
        }
        else if (vs.Count >= 2)
        {
            lows.Add(vs[0]);
            highs.Add(vs[^1]);
        }
    }

    /// <summary>
    /// 把暂存内容 seed 到服务端目录：mods jar 逐个校验版本声明后复制到 serverDir\mods，
    /// override 展开到 serverDir，导入清单合并进 meta["mods"]（随房间注册同步给加入者）。
    /// 声明了 minecraft 依赖且与房间版本不符的 jar 跳过并记入过滤名单（无声明/解析失败放行）。
    /// </summary>
    public static async Task<SeedResult> SeedToServerAsync(string serverDir, JsonObject meta,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(ImportRoot)) return new SeedResult(0, Array.Empty<string>(), "");
        var merged = 0;
        var filtered = new List<string>();
        var mcVer = meta["mc"]?.GetValue<string>() ?? "";

        // 1) 导入清单读出（供并入房间注册）
        var importedMods = new List<ModEntry>();
        if (File.Exists(ManifestPath))
            try
            {
                if (JsonNode.Parse(await File.ReadAllTextAsync(ManifestPath, ct).ConfigureAwait(false))
                        is JsonObject m && m["mods"] is JsonArray arr)
                    foreach (var n in arr)
                    {
                        if (n?["filename"]?.GetValue<string>() is not { Length: > 0 } fn) continue;
                        importedMods.Add(new ModEntry(
                            n["project"]?.GetValue<string>() ?? fn[..^4],
                            n["version"]?.GetValue<string>(),
                            fn,
                            n["url"]?.GetValue<string>()));
                    }
            }
            catch
            {
                // 清单损坏则按空处理
            }

        // 2) mods jar 逐个校验后落到服务端（版本不符 / 纯客户端 的跳过）
        if (Directory.Exists(ModsDir))
        {
            var serverMods = Path.Combine(serverDir, "mods");
            Directory.CreateDirectory(serverMods);
            var roomLoader = meta["loader"]?.GetValue<string>() ?? "";
            foreach (var jar in Directory.EnumerateFiles(ModsDir, "*.jar"))
            {
                ct.ThrowIfCancellationRequested();
                var fn = Path.GetFileName(jar);
                // v42 纯客户端模组过滤：装进专用服务端必崩（ETF 实测）或纯无用
                var (clientSkip, reason) = ModSideDetector.Inspect(jar, roomLoader);
                if (clientSkip)
                {
                    filtered.Add($"{fn}（{reason}）");
                    progress?.Report($"跳过{reason}：{fn}");
                    continue;
                }
                var constraint = GetMcDependency(jar);
                if (constraint is not null && !IsMcCompatible(constraint, mcVer))
                {
                    filtered.Add($"{fn}（要求 {constraint}，房间 {mcVer}）");
                    progress?.Report($"跳过版本不符模组：{fn}（要求 {constraint}，房间 {mcVer}）");
                    continue;
                }
                progress?.Report($"导入模组：{fn}");
                File.Copy(jar, Path.Combine(serverMods, fn), overwrite: true);
            }
        }

        // 3) override 展开到服务端根（跳过服务端关键文件，不覆盖世界/配置之外的内容判断从简：只保护这几样）
        if (Directory.Exists(OverrideDir))
        {
            string[] protectedFiles = { "server.jar", "eula.txt", "server.properties", ".mcstudio.json" };
            foreach (var src in Directory.EnumerateFiles(OverrideDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(OverrideDir, src);
                if (protectedFiles.Contains(Path.GetFileName(rel), StringComparer.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(serverDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: true);
            }
        }

        // 4) 并入 meta["mods"]（按 filename 去重）
        var existing = meta["mods"] as JsonArray ?? new JsonArray();
        meta["mods"] = existing;
        var known = new HashSet<string>(existing
            .Where(m => m?["filename"]?.GetValue<string>() is { Length: > 0 } f)
            .Select(m => m!["filename"]!.GetValue<string>()), StringComparer.OrdinalIgnoreCase);
        foreach (var mod in importedMods)
        {
            if (mod.Filename is null || !known.Add(mod.Filename)) continue;
            existing.Add(new JsonObject
            {
                ["project"] = mod.Project,
                ["version"] = mod.Version,
                ["filename"] = mod.Filename,
                ["url"] = mod.Url,
            });
            merged++;
        }

        // 服务端 mods 目录里实际存在的 jar 都登记（含旧房间残留的导入模组），保证加入端清单完整
        var serverModsDir = Path.Combine(serverDir, "mods");
        if (Directory.Exists(serverModsDir))
            foreach (var jar in Directory.EnumerateFiles(serverModsDir, "*.jar"))
            {
                var fn = Path.GetFileName(jar);
                if (!known.Add(fn)) continue;
                existing.Add(new JsonObject
                {
                    ["project"] = Path.GetFileNameWithoutExtension(fn),
                    ["filename"] = fn,
                });
                merged++;
            }

        progress?.Report(filtered.Count > 0
            ? $"已导入 {merged} 个模组到服务端（过滤 {filtered.Count} 个：版本不符/纯客户端）"
            : $"已导入 {merged} 个模组到服务端");
        return new SeedResult(merged, filtered,
            filtered.Count > 0 ? $"已跳过 {filtered.Count} 个模组：{string.Join("、", filtered.Take(5))}"
                + (filtered.Count > 5 ? $" 等 {filtered.Count} 个" : "") : "");
    }

    // ---------------------------------------------------------------- jar 版本声明解析

    /// <summary>
    /// 读取模组 jar 内对 minecraft 的版本依赖声明（首个发现者生效；无声明/解析失败返回 null）。
    /// 支持 fabric.mod.json（JSON depends）、Forge/NeoForge mods.toml（versionRange）、旧版 mcmod.info。
    /// </summary>
    private static string? GetMcDependency(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("fabric.mod.json");
            if (entry is not null)
            {
                using var s = entry.Open();
                if (JsonNode.Parse(s) is JsonObject o &&
                    o["depends"] is JsonObject deps && deps["minecraft"] is { } mc)
                {
                    if (mc is JsonValue v && v.TryGetValue<string>(out var str)) return str;
                    if (mc is JsonArray arr)
                        foreach (var item in arr)
                            if (item?.GetValue<string>() is { Length: > 0 } s2) return s2;
                }
                return null;
            }
            foreach (var name in new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml" })
            {
                entry = zip.GetEntry(name);
                if (entry is null) continue;
                using var s = entry.Open();
                using var reader = new StreamReader(s);
                var capture = false;
                while (reader.ReadLine() is { } line)
                {
                    var t = line.Trim();
                    if (t.StartsWith("[[dependencies.", StringComparison.Ordinal))
                    {
                        capture = false;   // 新依赖段，先复位，下面 modId=minecraft 再开启
                        continue;
                    }
                    if (t.StartsWith("modId", StringComparison.OrdinalIgnoreCase)
                        && t.Contains("minecraft", StringComparison.OrdinalIgnoreCase))
                        capture = true;
                    else if (capture && t.StartsWith("versionRange", StringComparison.OrdinalIgnoreCase))
                    {
                        var eq = t.IndexOf('=');
                        if (eq >= 0) return t[(eq + 1)..].Trim().Trim('"', '\'');
                        return null;
                    }
                }
                return null;
            }
            entry = zip.GetEntry("mcmod.info");
            if (entry is not null)
            {
                using var s = entry.Open();
                if (JsonNode.Parse(s) is JsonArray arr && arr.Count > 0
                    && arr[0] is JsonObject first && first["mcversion"] is JsonValue mv
                    && mv.TryGetValue<string>(out var ver))
                    return ver;
            }
        }
        catch
        {
            // 解析失败按无声明处理（宁可多装不误杀）
        }
        return null;
    }

    /// <summary>
    /// 判断 minecraft 版本依赖声明是否与房间版本兼容。通配/空/无法解析一律放行；
    /// 支持常见格式：* / 1.21.1 / ~1.21.1 / ^1.21 / &gt;=1.20 / [1.20,1.22) / 1.21.x。
    /// </summary>
    private static bool IsMcCompatible(string constraint, string mc)
    {
        if (string.IsNullOrWhiteSpace(constraint) || constraint.Trim() == "*") return true;
        var room = ParseVer(mc);
        if (room is null) return true;
        var matches = System.Text.RegularExpressions.Regex.Matches(constraint, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (matches.Count == 0) return true;                     // 纯通配（如 1.x）放行
        var versions = matches.Select(m => ParseVer(m.Value) ?? (1, 0, 0)).ToList();
        var c = constraint.TrimStart();
        if (c.Contains('[') || c.Contains('('))                   // Maven 区间：按上下界夹逼（端点宽容）
            return CompareVer(room.Value, versions.Min()) >= 0 && CompareVer(room.Value, versions.Max()) <= 0;
        if (c.StartsWith(">=", StringComparison.Ordinal)) return CompareVer(room.Value, versions[0]) >= 0;
        if (c.StartsWith('>')) return CompareVer(room.Value, versions[0]) > 0;
        if (c.StartsWith("<=", StringComparison.Ordinal)) return CompareVer(room.Value, versions[0]) <= 0;
        if (c.StartsWith('<')) return CompareVer(room.Value, versions[0]) < 0;
        if (c.StartsWith('~'))                                    // ~1.21.1 → 同 major.minor
            return (room.Value.major, room.Value.minor) == (versions[0].major, versions[0].minor);
        if (c.StartsWith('^')) return room.Value.major == versions[0].major;
        // 裸版本 / x 通配（1.21.x / 1.21）：major.minor 一致即兼容
        var head = versions[0];
        if (matches[0].Groups[2].Success)                         // 声明带 minor → 按 major.minor 比
            return (room.Value.major, room.Value.minor) == (head.major, head.minor);
        return room.Value.major == head.major;                    // 声明只有 major（1.x 之类已被上面放行）
    }

    private static int CompareVer((int major, int minor, int patch) a, (int major, int minor, int patch) b)
        => a.major != b.major ? a.major.CompareTo(b.major)
         : a.minor != b.minor ? a.minor.CompareTo(b.minor)
         : a.patch.CompareTo(b.patch);

    private static (int major, int minor, int patch)? ParseVer(string? ver)
    {
        if (string.IsNullOrWhiteSpace(ver)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(ver, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value),
            m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0,
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var s = entry.Open();
        using var reader = new StreamReader(s);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}

// v50.9.4：ModrinthIndexBuilder（逐文件清单生成）已随清单通道整体移除。
