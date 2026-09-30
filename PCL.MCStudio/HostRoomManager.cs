using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.MCStudio;

public sealed record HostOptions
{
    public string Mc { get; init; } = "1.21.1";
    /// <summary>vanilla | fabric（决定服务端 jar 下载源）。</summary>
    public string Loader { get; init; } = "fabric";
    /// <summary>服务端工作目录（server.jar 与世界数据所在地，存在即复用）。</summary>
    public string ServerDir { get; init; } = "";
    public string JavaExe { get; init; } = "java";
    /// <summary>房间名（端口租约与房间码的键，≤20 字符）；缺省取目录名。</summary>
    public string? RoomName { get; init; }
    public int PreferredPort { get; init; } = 25580;
    public int MaxMemoryMb { get; init; } = 2048;
    /// <summary>开房人的玩家登录令牌（注册房间时上报，云端绑定开房人供好友查看；空=匿名开房）。</summary>
    public string OwnerToken { get; init; } = "";
    /// <summary>重开已存服务器档案时置 true：跳过暂存区播种（服务端目录自持 mods/meta 清单），
    /// 防止用户中途导入的其他整合包内容被误灌进老服目录。</summary>
    public bool SkipSeed { get; init; }
    /// <summary>客户端整合包（v44）：随房间注册上云，朋友加入时整包下载安装；local 包仅本机可用。</summary>
    public ClientPackInfo? ClientPack { get; init; }
    /// <summary>房主客户端实例根目录（v50.7 全类型对齐）：扫 resourcepacks/shaderpacks/datapacks
    /// 进精确清单，朋友端自动对齐；空=只按服务端 mods 目录生成（与 v50.6.9 行为一致）。</summary>
    public string? ClientInstanceDir { get; init; }
    /// <summary>v50.8 包身份：整合包 + 资源包/光影包/数据包身份（&lt;1KB）。玩家端据此用 PCL 原生
    /// 下载/安装管线装同款包；与 ClientPack 的逐文件清单互不排斥（识别得出走包级，散件仍由清单兜底）。</summary>
    public RoomPacks? Packs { get; init; }
}

public enum HostStep { Preparing, ServerJar, Starting, WaitingDone, Tunnel, Register, Running, Stopping }

public sealed record HostProgress(HostStep Step, string Detail);

/// <summary>一键开房结果：房间码 + 玩家连接地址。</summary>
public sealed record HostedRoom(string Room, string Code, string Address, int RemotePort, int LocalPort,
    string ServerDir, string Mc, string Loader);

/// <summary>
/// 一键开房编排（与已验证的 Python CLI create/start/tunnel 链路一致）：
/// 准备服务端文件（幂等复用，jar 一律官方源）→ Java 启动并等 Done → frpc 开隧道
/// → 注册房间码（朋友端凭码加入）。
/// 关房：向 stdin 发 stop 优雅停服 → 关隧道 → 释放端口租约。
/// </summary>
public sealed class HostRoomManager : IDisposable
{
    private readonly StudioApiClient _api;
    private readonly FrpcManager _frpc;
    private readonly string _frpsHost;

    private Process? _server;
    private HostedRoom? _current;
    private HostOptions _activeOptions = new();
    private volatile bool _stopping;             // StopAsync 置位：崩溃监控据此区分「正常关服」与「意外崩溃」
    private CancellationTokenSource? _roomCts;   // 房间生命周期（崩溃监控取消用）
    private int _restartCount;

    /// <summary>服务端连续崩溃自动重启上限；超过即关房（防崩溃循环烧 CPU）。</summary>
    private const int MaxAutoRestarts = 3;

    private readonly ConcurrentQueue<string> _pendingLines = new();
    private readonly Queue<string> _recentLines = new(); // 诊断环形缓冲（仅输出回调线程写入）

    /// <summary>服务端输出回调（在线程池线程触发，UI 需自行调度）。</summary>
    public event Action<string>? ServerLine;

    public HostRoomManager(StudioApiClient api, string frpcExe, string frpsHost, int frpsPort, string token)
    {
        _api = api;
        _frpsHost = frpsHost;
        _frpc = new FrpcManager(api, frpcExe, frpsHost, frpsPort, token);
    }

    /// <summary>当前运行中的房间；未开房为 null。</summary>
    public HostedRoom? Current => _current;

    /// <summary>服务端进程是否仍在运行。</summary>
    public bool ServerRunning => _server is { HasExited: false };

    public async Task<HostedRoom> StartAsync(HostOptions options, IProgress<HostProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (_current is not null)
            throw new InvalidOperationException("已有房间在运行，请先关房");

        var dir = Path.GetFullPath(options.ServerDir is { Length: > 0 } ? options.ServerDir : "mcstudio-room");
        Directory.CreateDirectory(dir);
        // v45.2 遗孤清理：启动器直接退出时上一次的 java/frpc 不会被终止，会锁住 world/session.lock，
        // 导致同档重开必败（DirectoryLock IOException）。按命令行包含服务端目录识别并终止。
        KillOrphansForDir(dir);
        // 锁探测：仍被占用则立刻报人话错误，避免加载完上百个模组 30 秒后才失败
        ProbeWorldLock(dir);
        var room = RoomNameOf(options.RoomName, dir);
        _activeOptions = options;
        _restartCount = 0;
        _stopping = false;

        // 0) 世界备份（开服前快照；崩溃重启前与关房时同样备份，每世界保留最近 5 份）
        await BackupWorldAsync(dir, progress, ct).ConfigureAwait(false);

        // 1) 服务端文件准备（幂等：jar/eula/世界数据已存在则复用）
        progress?.Report(new(HostStep.Preparing, dir));
        var metaPath = Path.Combine(dir, ".mcstudio.json");
        JsonObject meta;
        if (File.Exists(metaPath))
        {
            meta = (JsonNode.Parse(await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false)) as JsonObject)
                ?? throw new InvalidOperationException($".mcstudio.json 已损坏: {metaPath}");

            // 版本冲突自动迁移：目录里已有旧版本（如导入了 1.20.1 模组但房间是 1.21.1），
            // 则整目录改名备份后重建，避免新旧版本文件混装导致服务端起不来
            var metaMc = meta["mc"]?.GetValue<string>() ?? "";
            var metaLoader = meta["loader"]?.GetValue<string>() ?? "";
            if (metaMc != options.Mc || metaLoader != options.Loader)
            {
                var backup = dir.TrimEnd(Path.DirectorySeparatorChar) +
                             $".bak-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
                Directory.Move(dir, backup);
                Directory.CreateDirectory(dir);
                progress?.Report(new(HostStep.Preparing,
                    $"房间版本从 {metaMc}/{metaLoader} 切换为 {options.Mc}/{options.Loader}，旧目录已备份"));
                meta = new JsonObject
                {
                    ["mc"] = options.Mc,
                    ["loader"] = options.Loader,
                    ["port"] = 0,
                    ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["mods"] = new JsonArray(),
                };
            }
        }
        else
        {
            meta = new JsonObject
            {
                ["mc"] = options.Mc,
                ["loader"] = options.Loader,
                ["port"] = 0,
                ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["mods"] = new JsonArray(),
            };
        }
        await File.WriteAllTextAsync(metaPath, meta.ToJsonString(), ct).ConfigureAwait(false);
        var mc = meta["mc"]!.GetValue<string>();
        var loader = meta["loader"]!.GetValue<string>();

        // 1.4) 服务端整合包（v43）：导入登记的 serverpack zip 整包展开为服务端根目录。
        // 典型形态 = 作者发布的"xxx服务端.zip"（已装好 Forge/Neo + mods + config + run 脚本）——
        // 展开后 FindForgeArgsFile 直接命中，完全复用现有 Forge 启动链，零安装。
        // 版本不符（用户开的是别的实例/档案）时跳过展开，防止污染其他房间。
        if (ModpackImporter.GetServerPackImport() is { } sp)
        {
            if (File.Exists(sp.ZipPath) && (sp.Mc.Length == 0 || sp.Mc == mc)
                && (sp.Loader.Length == 0 || sp.Loader == loader))
            {
                var alreadyExpanded = FindForgeArgsFile(dir) is not null
                    || File.Exists(Path.Combine(dir, "server.jar"))
                    || (sp.CoreJar.Length > 0 && File.Exists(Path.Combine(dir, sp.CoreJar.Replace('/', '\\'))));
                if (!alreadyExpanded)
                {
                    progress?.Report(new(HostStep.Preparing, "展开服务端整合包…"));
                    var files = await ExpandZipAsync(sp.ZipPath, sp.RootPrefix, dir, progress, ct).ConfigureAwait(false);
                    // 包内 eula 可能是 false/缺省：专用服务端一律视为已同意
                    await File.WriteAllTextAsync(Path.Combine(dir, "eula.txt"), "eula=true\r\n", ct)
                        .ConfigureAwait(false);
                    if (sp.Build.Length > 0 && loader is "forge" or "neoforge")
                        meta["loader_full"] = $"{(loader == "neoforge" ? "neo" : "forge")}:{sp.Build}";
                    // v47：-jar 核心（fabric-launcher/paper/mohist/arclight/catserver/server.jar 等）登记进
                    // meta，LaunchServerProcess 优先用包自带核心直接启动（不再另下载 server.jar）
                    if (sp.CoreJar.Length > 0)
                    {
                        meta["core_jar"] = sp.CoreJar;
                        if (sp.ExtraJvm.Length > 0) meta["extra_jvm"] = sp.ExtraJvm;
                    }
                    progress?.Report(new(HostStep.Preparing, $"服务端整合包已展开（{files} 个文件），复用其自带服务端"));
                }
                else
                {
                    progress?.Report(new(HostStep.Preparing, "服务端整合包已展开过，直接复用"));
                }
            }
            else
            {
                progress?.Report(new(HostStep.Preparing, "导入的服务端整合包与当前房间版本不符，跳过展开"));
            }
        }

        // 1.5) 应用导入的模组 / 整合包（暂存区 → 服务端目录 + meta["mods"] 清单）。
        // 重开已存档案时跳过：暂存区里可能是后来导入的其他整合包，灌进老服目录会污染模组；
        // 服务端目录自持（mods/ 已在 + .mcstudio.json 清单完整），注册房间照常带上原清单。
        if (options.SkipSeed)
        {
            progress?.Report(new(HostStep.Preparing, "重开已有服务器：沿用目录内模组与配置，跳过导入内容应用"));
        }
        else
        {
            try
            {
                var seed = await ModpackImporter.SeedToServerAsync(dir, meta, null, ct).ConfigureAwait(false);
                if (seed.FilterDetail.Length > 0)
                    progress?.Report(new(HostStep.Preparing, seed.FilterDetail));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // 导入应用失败不阻塞开房（服务端仍可用），记录后继续
                progress?.Report(new(HostStep.Preparing, "导入内容应用失败：" + e.Message));
            }
        }

        // 1.6) Fabric 房自动装 fabric-api（绝大多数 Fabric 模组的前置；已装或非 Fabric 跳过）
        if (loader == "fabric" && !HasFabricApi(dir, meta))
        {
            try
            {
                progress?.Report(new(HostStep.Preparing, "自动安装前置 mod：fabric-api"));
                await InstallServerModAsync(dir, mc, loader, "fabric-api", ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // 下载失败不阻塞开房：无前置时服务端仍可启动（纯 Fabric 前置缺失只在有模组依赖时才报错）
                progress?.Report(new(HostStep.Preparing, "fabric-api 自动安装失败（继续启动）：" + e.Message));
            }
        }


        // 服务端准备：forge/neoforge 走 installer 无头安装（其余下 server.jar）
        // 注意：meta["loader"] 恒存裸值（forge/neoforge），build 号存 meta["loader_full"]（forge:52.1.0），
        // 否则重开房时版本冲突迁移逻辑会误判；已安装过（存在 args 文件）直接复用。
        string registeredLoader = loader;
        if (loader is "forge" or "neoforge")
        {
            var reuseFull = FindForgeArgsFile(dir) is not null
                ? meta["loader_full"]?.GetValue<string>() : null;
            if (!string.IsNullOrEmpty(reuseFull))
            {
                registeredLoader = reuseFull!;
                progress?.Report(new(HostStep.ServerJar, "Forge 服务端已安装，直接复用"));
            }
            else
            {
                var build = await InstallForgeServerAsync(dir, mc, loader, options.JavaExe, progress, ct)
                    .ConfigureAwait(false);
                registeredLoader = $"{(loader == "neoforge" ? "neo" : "forge")}:{build}";
                meta["loader_full"] = registeredLoader;
            }
        }
        else
        {
            // v47：服务端包自带 -jar 核心（paper/mohist/arclight/fabric-launcher/server.jar 等）时
            // 直接复用，不再另下载 server.jar（LaunchServerProcess 会优先用 core_jar 启动）
            if (ReadCoreInfo(dir).CoreJar is not null)
            {
                progress?.Report(new(HostStep.ServerJar, "复用整合包自带服务端核心"));
            }
            else
            {
                var jarPath = Path.Combine(dir, "server.jar");
                if (!File.Exists(jarPath))
                {
                    progress?.Report(new(HostStep.ServerJar, $"{loader} {mc}"));
                    if (loader == "fabric")
                    {
                        // BMCLAPI 无 fabric-meta 端点（v36 实测 404），fabric 保持官方单源
                        var fabricUrl = await McSources.GetFabricServerJarUrlAsync(mc, ct).ConfigureAwait(false);
                        await McSources.DownloadFileAsync(new[] { fabricUrl }, jarPath, ct,
                            progress: ByteProgress(progress, "下载服务端 jar")).ConfigureAwait(false);
                    }
                    else
                    {
                        // vanilla 双源：BMCLAPI 镜像优先 + 官方兜底（与客户端下载对称），带官方 sha1 校验
                        var (serverUrl, serverSha1) = await McSources.GetVanillaServerJarAsync(mc, ct).ConfigureAwait(false);
                        var urls = new List<string> { $"{McSources.Bmclapi}/version/{mc}/server", serverUrl };
                        await McSources.DownloadFileAsync(urls, jarPath, ct, serverSha1,
                            ByteProgress(progress, "下载服务端 jar")).ConfigureAwait(false);
                    }
                }
            }
        }

        var eula = Path.Combine(dir, "eula.txt");
        if (!File.Exists(eula))
            await File.WriteAllTextAsync(eula, "eula=true\r\n", ct).ConfigureAwait(false);

        var port = FreeLocalPort(options.PreferredPort);
        await File.WriteAllTextAsync(Path.Combine(dir, "server.properties"),
            $"server-port={port}\r\nmotd=MCStudio Room\r\nonline-mode=false\r\nmax-players=10\r\nview-distance=6\r\n",
            ct).ConfigureAwait(false);
        meta["port"] = port;
        await File.WriteAllTextAsync(metaPath, meta.ToJsonString(), ct).ConfigureAwait(false);
        EnsureServerTcpFirewallRule(port, progress);

        // 2) 启动 Java 服务端
        // Java 版本提示（v40）：1.20.5+ 需 Java 21、1.17+ 需 Java 17；仅提示不改默认（探测失败静默跳过）。
        // 背景：便携分发无 host.json 时 javaExe 回落到 PATH 的 java，老 Java 启动即 UnsupportedClassVersionError
        var javaMajor = await ProbeJavaMajorAsync(options.JavaExe, ct).ConfigureAwait(false);
        var requiredJava = RequiredJavaMajor(mc);
        if (javaMajor is { } jm && jm < requiredJava)
            progress?.Report(new(HostStep.Starting,
                $"警告：当前 Java {jm} 可能无法运行 MC {mc}（需要 Java {requiredJava}+），" +
                "启动报 UnsupportedClassVersionError 时请在 mcstudio/host.json 配置 javaExe 指向新版 Java"));
        progress?.Report(new(HostStep.Starting, javaMajor is { } ok
            ? $"-Xmx{options.MaxMemoryMb}M · Java {ok}"
            : $"-Xmx{options.MaxMemoryMb}M"));
        LaunchServerProcess(dir, options);

        // 3) 等待启动完成（首开要解压依赖库 + 生成世界，最长 300 秒；暖启动通常 30 秒内）
        progress?.Report(new(HostStep.WaitingDone, "300s"));
        try
        {
            await WaitForDoneAsync(TimeSpan.FromSeconds(300), ct).ConfigureAwait(false);
        }
        catch
        {
            TryKillServer();
            throw;
        }

        // 4) 开隧道（云端分配端口 → frpc；免费档限速 2MB）
        progress?.Report(new(HostStep.Tunnel, $"127.0.0.1:{port}"));
        var (remotePort, _) = await _frpc.OpenTunnelAsync(room, port, dir, ct).ConfigureAwait(false);

        // 5) 注册房间元信息，生成房间码（朋友端凭码解析并自动装配）
        progress?.Report(new(HostStep.Register, room));
        // v44 客户端整合包：本地包先落缓存（稳定键供房主复用），补齐 sha1/size 后随房间注册上云
        var clientPack = options.ClientPack;
        if (clientPack is { LocalPath: { Length: > 0 } lp } && File.Exists(lp))
        {
            Directory.CreateDirectory(ClientPackInfo.ClientPackRoot());
            var cache = clientPack.CachePath();
            if (!File.Exists(cache)) File.Copy(lp, cache, overwrite: true);
            clientPack = clientPack with
            {
                Sha1 = clientPack.Sha1 ?? ClientPackInfo.Sha1OfFile(cache),
                Size = clientPack.Size > 0 ? clientPack.Size : new FileInfo(cache).Length,
            };
        }
        // v50.9.4：逐文件清单（v50.6.9/v50.7 mrpack index）整体移除——对齐只走包身份（options.Packs）
        // + 加入端自动装配兜底；云载荷从 O(文件数) 变为常数 <1KB，64KB 拒绝式问题不复存在
        var code = await _api.RegisterRoomAsync(room, mc, registeredLoader, ReadMods(meta),
            string.IsNullOrEmpty(options.OwnerToken) ? null : options.OwnerToken, ct,
            clientPack, hb: true, packs: options.Packs).ConfigureAwait(false);
        var address = $"{_frpsHost}:{remotePort}";

        _current = new HostedRoom(room, code, address, remotePort, port, dir, mc, registeredLoader);
        // 进度里只报房间码：地址/端口不再外泄（杜绝玩家在游戏里直连绕过 P2P）
        progress?.Report(new(HostStep.Running, $"房间码 {code}"));

        // 5.5) 免费档说明（P2-J）：frpc 侧已限速 2MB/s，明示用户并引导 P2P
        progress?.Report(new(HostStep.Running,
            "已启用免费中转通道（限速 2MB/s）；网络条件允许时朋友会自动优先 P2P 直连，不经此通道"));

        // 6) 崩溃守护：服务端意外退出时自动备份 + 重启（同端口，frpc 隧道无需重建）
        _roomCts = new CancellationTokenSource();
        _ = Task.Run(() => MonitorCrashAsync(progress, _roomCts.Token), CancellationToken.None);
        // 7) 用量展示（P2-J）：房主登录时每 60s 拉取中转流量汇报到房间日志
        if (!string.IsNullOrEmpty(options.OwnerToken))
        {
            _ = Task.Run(() => UsagePollerAsync(code, options.OwnerToken, progress, _roomCts.Token),
                CancellationToken.None);
            // v50.6.1 心跳租约：服务端存续期间每 55s 续期房间（云端 TTL 180s）——
            // 服务器关闭走 StopAsync 主动注销；异常掉线由云端 3 分钟兜底回收
            _ = Task.Run(() => RoomHeartbeatAsync(room, options.OwnerToken, progress,
                _roomCts.Token), CancellationToken.None);
        }
        return _current;
    }

    /// <summary>v50.6.1 房主心跳：服务端托管期间每 55s 续期 hb 房间（云端 TTL 180s）。
    /// 房间被云端回收（失联过久）时汇报并停跳；网络错误静默等下一轮，不影响房间。</summary>
    private async Task RoomHeartbeatAsync(string room, string token,
        IProgress<HostProgress>? progress, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(55), ct).ConfigureAwait(false);
                try
                {
                    if (await _api.HeartbeatAsync(room, token, ct).ConfigureAwait(false) is null)
                    {
                        progress?.Report(new(HostStep.Running,
                            "⚠ 云端房间已失效（与服务器失联过久），房间码不再可用；" +
                            "关闭本次托管后重新开房将获得新房间码"));
                        return;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 网络抖动，下一轮重试 */ }
            }
        }
        catch (OperationCanceledException) { /* 关房退出 */ }
        catch { /* 心跳永不影响房间 */ }
    }

    /// <summary>房主端中转用量轮询：每 60s 从云端拉取本房 frp 流量并汇报（登录用户可见；失败静默重试）。</summary>
    private async Task UsagePollerAsync(string code, string token,
        IProgress<HostProgress>? progress, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                try
                {
                    var u = await _api.UsageAsync(token, code, ct).ConfigureAwait(false);
                    if (u.TotalBytes > 0)
                        progress?.Report(new(HostStep.Running,
                            $"本房中转流量 {FmtBytes(u.TotalBytes)}（发往玩家 {FmtBytes(u.BytesIn)} · 免费档限速 2MB/s）"));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 网络抖动，下一轮重试 */ }
            }
        }
        catch (OperationCanceledException) { /* 关房退出 */ }
        catch { /* 兜底：用量展示永不影响房间 */ }
    }

    /// <summary>流量人性化显示（B/KB/MB/GB，一位小数）。</summary>
    internal static string FmtBytes(long bytes)
    {
        double v = bytes;
        if (v < 1024) return $"{v} B";
        if (v < 1024 * 1024) return $"{v / 1024:F1} KB";
        if (v < 1024L * 1024 * 1024) return $"{v / (1024 * 1024):F1} MB";
        return $"{v / (1024L * 1024 * 1024):F2} GB";
    }

    /// <summary>房主本机直连信息：127.0.0.1 本地端口（零延迟、不占中转带宽），供「一键加入」装配同款客户端。
    /// mods 清单从服务端 .mcstudio.json 读取；房间未运行时抛出。</summary>
    public RoomInfo GetLocalRoomInfo()
    {
        var current = _current ?? throw new InvalidOperationException("房间未在运行");
        JsonObject meta = new JsonObject();
        var metaPath = Path.Combine(current.ServerDir, ".mcstudio.json");
        if (File.Exists(metaPath))
        {
            try
            {
                meta = JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                // 清单损坏按无模组处理（本机直连不依赖 mods 完整性）
            }
        }
        return new RoomInfo(current.Code, $"127.0.0.1:{current.LocalPort}", current.LocalPort,
            current.Mc, current.Loader, ReadMods(meta), 0);
    }

    /// <summary>房主本地快速通道（v37）：加入的房间码正是本机在开的房间时，返回其服务端 mods 目录。
    /// 开房播种的模组文件名与房间清单 Filename 一致，装配器据此本地复制模组、免网络下载；不匹配返回 null。</summary>
    public string? TryGetLocalModsDir(string code)
    {
        var current = _current;
        if (current is null || !string.Equals(current.Code, code, StringComparison.OrdinalIgnoreCase))
            return null;
        var modsDir = Path.Combine(current.ServerDir, "mods");
        return Directory.Exists(modsDir) ? modsDir : null;
    }

    /// <summary>关房：优雅停服（stdin stop，最多 60 秒）→ 关隧道 → 关房备份 → 释放租约。</summary>
    public async Task StopAsync(IProgress<HostProgress>? progress = null, CancellationToken ct = default)
    {
        var current = _current;
        if (current is null)
        {
            TryKillServer();
            return;
        }
        progress?.Report(new(HostStep.Stopping, current.Code));
        _stopping = true;
        try { _roomCts?.Cancel(); } catch { }

        if (_server is { HasExited: false } server)
        {
            try
            {
                await server.StandardInput.WriteLineAsync("stop".AsMemory(), ct).ConfigureAwait(false);
                await server.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (IOException) { /* 进程已在退出 */ }
            catch (ObjectDisposedException) { }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await server.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKillServer(); // 超时强杀
            }
        }
        TryKillServer(); // 已退出时无害，确保进程对象释放

        await _frpc.CloseTunnelAsync(current.Room, current.ServerDir).ConfigureAwait(false);

        // 关房备份：优雅停服后的最终世界快照（失败不阻塞关房）
        try
        {
            await BackupWorldAsync(current.ServerDir, progress, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* 双保险：BackupWorldAsync 内部已吞非取消异常 */ }

        _current = null;
        _roomCts?.Dispose();
        _roomCts = null;
    }

    /// <summary>向服务端 mods/ 安装 Modrinth 模组并登记到房间清单（须在开房前调用）。</summary>
    public static async Task<ModEntry> InstallServerModAsync(string serverDir, string mc, string loader,
        string project, CancellationToken ct = default)
    {
        var file = await McSources.FindModFileAsync(project, mc, loader, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Modrinth 上没有 {loader} {mc} 兼容的 {project}");
        var modsDir = Path.Combine(serverDir, "mods");
        Directory.CreateDirectory(modsDir);
        var dest = Path.Combine(modsDir, file.Filename);
        if (!File.Exists(dest))
            await McSources.DownloadFileAsync(new[] { file.Url }, dest, ct).ConfigureAwait(false);

        var metaPath = Path.Combine(serverDir, ".mcstudio.json");
        JsonObject meta = File.Exists(metaPath)
            ? (JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject)!
            : new JsonObject { ["mc"] = mc, ["loader"] = loader, ["port"] = 0, ["mods"] = new JsonArray() };
        var mods = meta["mods"] as JsonArray ?? new JsonArray();
        meta["mods"] = mods;
        if (!mods.Any(m => m?["project"]?.GetValue<string>() == project))
            mods.Add(new JsonObject
            {
                ["project"] = project,
                ["version"] = file.Version,
                ["filename"] = file.Filename,
                ["url"] = file.Url,
                // v50.6.8：精确清单要素——朋友端按房主实际版本安装（不再猜最新）+ sha1 校验
                ["vid"] = file.VersionId,
                ["sha1"] = file.Sha1,
            });
        File.WriteAllText(metaPath, meta.ToJsonString());
        return new ModEntry(project, file.Version, file.Filename);
    }

    // ---------------------------------------------------------------- 内部实现

    /// <summary>启动服务端进程并接管输出（首次开服与崩溃重启共用；同端口复用，隧道无需重建）。
    /// Forge/NeoForge：java @user_jvm_args.txt @libraries/...win_args.txt nogui；其余 -jar server.jar。</summary>
    private void LaunchServerProcess(string dir, HostOptions options)
    {
        _pendingLines.Clear();   // 清掉上一次进程的残留日志，避免误导 WaitForDone
        // v47：-jar 核心包（fabric-launcher/paper/mohist/arclight/catserver 等）登记在
        // .mcstudio.json 的 core_jar/extra_jvm（服务端包展开时写入），优先于一切下载逻辑。
        var (coreJar, extraJvm) = ReadCoreInfo(dir);
        var winArgs = options.Loader is "forge" or "neoforge" ? FindForgeArgsFile(dir) : null;
        var arguments = winArgs is not null
            ? $"-Xmx{options.MaxMemoryMb}M @user_jvm_args.txt @\"{winArgs}\" nogui"
            : coreJar is not null
                ? $"-Xmx{options.MaxMemoryMb}M{extraJvm} -jar \"{Path.Combine(dir, coreJar.Replace('/', '\\'))}\" nogui"
                : $"-Xmx{options.MaxMemoryMb}M -jar \"{Path.Combine(dir, "server.jar")}\" nogui";
        // v46.3：部分服务端整合包（作者在 Linux 上打好再打包）缺 user_jvm_args.txt——
        // java 处理 @file 时找不到直接退出（Error: could not open `user_jvm_args.txt'）。
        // 自动补一个纯注释文件兜底（-Xmx 已由命令行注入，无需用户填写）。
        if (winArgs is not null && !File.Exists(Path.Combine(dir, "user_jvm_args.txt")))
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "user_jvm_args.txt"),
                    "# 自定义 JVM 参数，每行一个（MCStudio 自动创建；-Xmx 已由启动器在命令行注入）" + Environment.NewLine);
            }
            catch { /* 创建失败时 java 会报同样错误，走 DiagnoseExit 提示 */ }
        }
        var psi = new ProcessStartInfo
        {
            FileName = options.JavaExe,
            Arguments = arguments,
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // v45.2：java 在中文 Windows 下用 GBK 写 stdout，按 UTF-8 读会乱码（报错关键信息不可读）
            StandardOutputEncoding = JavaConsoleEncoding(),
            StandardErrorEncoding = JavaConsoleEncoding(),
        };
        var server = Process.Start(psi) ?? throw new InvalidOperationException("服务端进程启动失败");
        _server = server;
        server.OutputDataReceived += (_, e) => Enqueue(e.Data);
        server.ErrorDataReceived += (_, e) => Enqueue(e.Data);
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
    }

    /// <summary>探测 Java 主版本号（解析 java -version 输出；8 及以下形如 1.8.0_392，9+ 形如 21.0.12）。
    /// 探测失败返回 null——仅作提示用，不阻塞开房。</summary>
    private static async Task<int?> ProbeJavaMajorAsync(string javaExe, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true, // java -version 的版本信息打印在 stderr
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output))
                output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var m = Regex.Match(output, "version \"([^\"]+)\"");
            if (!m.Success) return null;
            var parts = m.Groups[1].Value.Split('.');
            if (parts[0] == "1" && parts.Length > 1 && int.TryParse(parts[1], out var legacy)) return legacy;
            if (int.TryParse(parts[0], out var major)) return major;
            return null;
        }
        catch { return null; }
    }

    /// <summary>v50.1：MC 最高兼容的 Java 主版本（modlauncher/Fabric/NeoForge 官方测试通过的版本）。
    /// 1.20.4 及以下 = 21（modlauncher 10 上限）；1.20.5+ = 21（forge 官方推 21；modlauncher 11+ 不支持 25/26 等新主版本）。
    /// Java 25/26 实际触发 modlauncher.BootstrapLaunchConsumer.accept 静默崩，5 分钟超时毫无线索——此处硬卡。</summary>
    private static int MaxSupportedJavaMajor(string mc) => 21;   // 全段统一 21：modlauncher 9/10/11/12 未对 25+ 测试

    /// <summary>MC 服务端要求的最低 Java 主版本：1.20.5+ → 21，1.17–1.20.4 → 17，更老 → 8。</summary>
    private static int RequiredJavaMajor(string mc)
    {
        if (Version.TryParse(mc, out var v) && v.Major == 1)
        {
            if (v.Minor >= 21 || (v.Minor == 20 && v.Build >= 5)) return 21;
            if (v.Minor >= 17) return 17;
        }
        return 8;
    }

    /// <summary>整包解压 zip 到服务端目录（v43 服务端整合包）：剥离单一顶层根目录，覆盖式展开；跳过 macOS 垃圾。</summary>
    private static async Task<int> ExpandZipAsync(string zipPath, string rootPrefix, string destDir,
        IProgress<HostProgress>? progress, CancellationToken ct)
    {
        var count = 0;
        using var zip = ModpackImporter.OpenZipSmart(zipPath); // GBK 文件名智能回退（v43）
        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList(); // 目录条目 Name 为空
        var total = entries.Count;
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            var rel = e.FullName;
            if (rootPrefix.Length > 0 && rel.StartsWith(rootPrefix, StringComparison.Ordinal))
                rel = rel[rootPrefix.Length..];
            if (rel.Length == 0) continue;
            var dest = Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (dest.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase) || e.Name == ".DS_Store") continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await using var src = e.Open();
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            count++;
            if (count % 100 == 0) progress?.Report(new(HostStep.Preparing, $"展开服务端整合包 {count}/{total}"));
        }
        return count;
    }

    /// <summary>清理目标服务端目录的遗孤进程（v45.2）：启动器直接退出时上一次的 java/frpc 不会被终止，
    /// 会锁住 world/session.lock 导致同档重开必败。按命令行包含服务端目录识别（WMI），只杀本目录相关进程。</summary>
    private static void KillOrphansForDir(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var norm = dir.TrimEnd('\\').ToLowerInvariant() + "\\";
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='java.exe' OR Name='frpc.exe'");
            foreach (var p in searcher.Get())
            {
                var cmdline = (p["CommandLine"] as string ?? "").ToLowerInvariant();
                if (!cmdline.Contains(norm)) continue;
                try
                {
                    using var proc = Process.GetProcessById(Convert.ToInt32(p["ProcessId"]));
                    proc.Kill(entireProcessTree: true);
                }
                catch { /* 已退出/权限不足：跳过 */ }
            }
        }
        catch { /* WMI 不可用等：跳过，交给锁探测报错 */ }
        // 锁释放有延迟：等一小会儿让内核句柄真正关闭
        if (Directory.Exists(Path.Combine(dir, "world")))
            Thread.Sleep(300);
    }

    /// <summary>世界锁探测（v45.2）：session.lock 仍被占用时立刻抛人话错误，不在模组加载 30 秒后失败。</summary>
    private static void ProbeWorldLock(string dir)
    {
        var lockFile = Path.Combine(dir, "world", "session.lock");
        if (!File.Exists(lockFile)) return;
        try
        {
            using var fs = File.Open(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InvalidOperationException(
                "【诊断】服务端目录被另一个仍在运行的服务端进程锁定（通常是上次启动器直接退出时服务器未被关闭）。\n" +
                "已尝试自动清理但未成功：请在任务管理器结束残留的 java.exe（或重启电脑）后重开。");
        }
    }

    /// <summary>java 控制台输出编码：Windows 中文系统下 java 用 GBK 写 stdout（.NET 默认按 UTF-8 读会乱码）。</summary>
    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    private static Encoding JavaConsoleEncoding()
    {
        try
        {
            var acp = OperatingSystem.IsWindows() ? GetACP() : 65001;
            return acp is 0 or 65001 ? Encoding.UTF8 : Encoding.GetEncoding((int)acp);
        }
        catch { return Encoding.UTF8; }
    }

    /// <summary>同步关房（应用退出时调用，v45.2）：stop 命令 → 最多等 timeoutMs → 强杀进程树 → 关 frpc。
    /// 不做备份/释放租约等耗时操作（世界数据随 stop 正常落盘）。</summary>
    public void ShutdownSync(int timeoutMs = 10000)
    {
        try
        {
            if (_server is { HasExited: false } server)
            {
                try
                {
                    server.StandardInput.WriteLine("stop");
                    server.StandardInput.Flush();
                }
                catch { /* 管道已断 */ }
                if (!server.WaitForExit(timeoutMs))
                    TryKillServer();
            }
            TryKillServer();
            if (_current is not null) KillOrphansForDir(_current.ServerDir);
            _frpc.Dispose();
        }
        catch { /* 退出路径尽力而为 */ }
    }

    /// <summary>读取服务端目录 .mcstudio.json 登记的 -jar 核心（core_jar）与额外 JVM 参数（extra_jvm）。
    /// core_jar 在目录中不存在（包未展开/被删）时返回空，回落 server.jar 常规链。读取失败静默。</summary>
    private static (string? CoreJar, string ExtraJvm) ReadCoreInfo(string dir)
    {
        try
        {
            var metaPath = Path.Combine(dir, ".mcstudio.json");
            if (!File.Exists(metaPath)
                || JsonNode.Parse(File.ReadAllText(metaPath)) is not JsonObject m) return (null, "");
            var core = m["core_jar"]?.GetValue<string>();
            if (string.IsNullOrEmpty(core) || !File.Exists(Path.Combine(dir, core.Replace('/', '\\'))))
                return (null, "");
            var jvm = m["extra_jvm"]?.GetValue<string>() ?? "";
            return (core, jvm.Length > 0 ? " " + jvm : "");
        }
        catch { return (null, ""); }
    }

    /// <summary>定位 Forge/NeoForge 安装产物里的服务端 args 文件（win_args.txt；无则非 Forge 目录）。
    /// v47：只有 unix_args.txt 的包（作者在 Linux 上安装/打包）自动生成 win_args.txt ——
    /// classpath 类参数的分隔符 : → ; 并剔除 Linux 专属 natives jar，转换结果幂等复用。</summary>
    internal static string? FindForgeArgsFile(string dir)
    {
        var libDir = Path.Combine(dir, "libraries");
        if (!Directory.Exists(libDir)) return null;
        try
        {
            // v40 修复：部分 Forge 版本的 installer 产物是裸名 win_args.txt（无 forge- 前缀），
            // 此前只匹配 *-win_args.txt → 安装成功（exit=0）被误判失败，开房报错且每次重装
            var win = Directory.GetFiles(libDir, "*-win_args.txt", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(libDir, "win_args.txt", SearchOption.AllDirectories))
                .OrderBy(p => p.Length).FirstOrDefault();
            if (win is not null) return win;

            // v47：unix_args → win_args 转换（同目录生成 win_args.txt，与官方产物命名对齐）
            var unix = Directory.GetFiles(libDir, "*-unix_args.txt", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(libDir, "unix_args.txt", SearchOption.AllDirectories))
                .OrderBy(p => p.Length).FirstOrDefault();
            if (unix is null) return null;
            var target = Path.Combine(Path.GetDirectoryName(unix)!, "win_args.txt");
            File.WriteAllText(target, ConvertUnixArgsToWin(File.ReadAllLines(unix)));
            return target;
        }
        catch { return null; }
    }

    /// <summary>unix_args.txt → win_args.txt 内容转换：-p/-cp/-classpath 与 -DlegacyClassPath 的
    /// classpath 分隔符 : → ;，并剔除 Linux 专属 natives（epoll/aarch 等，Windows 模块路径里多余）。</summary>
    public static string ConvertUnixArgsToWin(IReadOnlyList<string> lines)
    {
        static string ConvertCp(string value)
        {
            var items = value.Split(':')
                .Where(p => p.Length > 0 && !Regex.IsMatch(p, @"linux-(x86_64|aarch_64)|^.*natives-linux", RegexOptions.IgnoreCase));
            return string.Join(";", items);
        }
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("-p ", StringComparison.Ordinal) || line.StartsWith("-cp ", StringComparison.Ordinal)
                || line.StartsWith("-classpath ", StringComparison.Ordinal))
            {
                var sp = line.IndexOf(' ');
                sb.Append(line[..(sp + 1)]).AppendLine(ConvertCp(line[(sp + 1)..]));
            }
            else if (line.StartsWith("-DlegacyClassPath=", StringComparison.Ordinal))
            {
                sb.Append("-DlegacyClassPath=").AppendLine(ConvertCp(line["-DlegacyClassPath=".Length..]));
            }
            else
            {
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    /// <summary>把字节进度转成 HostProgress 汇报器（v40 进度显示：下载 x/y MB；总长未知只报已下载量）。</summary>
    private static IProgress<(long Done, long Total)> ByteProgress(IProgress<HostProgress>? progress, string label)
        => new Progress<(long Done, long Total)>(t => progress?.Report(new(HostStep.ServerJar,
            t.Total > 0 ? $"{label} {t.Done / 1048576.0:F1}/{t.Total / 1048576.0:F1} MB"
                        : $"{label} {t.Done / 1048576.0:F1} MB")));

    /// <summary>
    /// Forge/NeoForge 服务端自动安装：解析推荐构建 → 下载 installer（BMCLAPI 镜像优先）
    /// → java --installServer 无头安装（下载原版服务端与依赖库，实测约 5 分钟）→ 校验产物。
    /// 返回构建号（用于房间清单 loader 编码）。
    /// </summary>
    private static async Task<string> InstallForgeServerAsync(string dir, string mc, string loader,
        string javaExe, IProgress<HostProgress>? progress, CancellationToken ct)
    {
        if (Version.TryParse(mc, out var v) && (v.Major < 1 || v.Minor < 17))
            throw new NotSupportedException($"Forge 托管仅支持 1.17+（当前 {mc}）");

        progress?.Report(new(HostStep.ServerJar, $"解析 {loader} 推荐版本…"));
        var build = loader == "forge"
            ? await ResolveForgeBuildAsync(mc, ct).ConfigureAwait(false)
            : await ResolveNeoForgeBuildAsync(mc, ct).ConfigureAwait(false);
        var art = loader == "forge" ? $"forge-{mc}-{build}" : $"neoforge-{build}";
        var groupId = loader == "forge" ? "net/minecraftforge/forge" : "net/neoforged/neoforge";
        // maven 目录是不带前缀的版本号（forge: 1.20.1-47.4.10；neo: 21.1.x），文件名才带前缀——
        // v38 修复：目录误拼成 forge-/neoforge- 前缀曾致双源 404（官方与 BMCLAPI 已收敛标准布局）
        var mavenVer = loader == "forge" ? $"{mc}-{build}" : build;
        progress?.Report(new(HostStep.ServerJar, $"{loader} {mc}-{build}"));

        var installerPath = Path.Combine(dir, "mcstudio-installer.jar");
        var urls = new List<string>
        {
            $"https://bmclapi2.bangbang93.com/maven/{groupId}/{mavenVer}/{art}-installer.jar",
            loader == "forge"
                ? $"https://maven.minecraftforge.net/{groupId}/{mavenVer}/{art}-installer.jar"
                : $"https://maven.neoforged.net/releases/{groupId}/{mavenVer}/{art}-installer.jar",
        };
        await McSources.DownloadFileAsync(urls, installerPath, ct,
            progress: ByteProgress(progress, "下载安装器")).ConfigureAwait(false);

        // 预下载 installer 内部依赖（installer 硬编码官方 maven，国内直连不稳，尤其 NeoForge 必断）：
        // 按 install_profile.json 的 libraries 清单从 BMCLAPI maven 镜像取，installer 检测本地文件即跳过下载
        progress?.Report(new(HostStep.ServerJar, "预下载安装依赖（镜像加速）…"));
        await PreloadInstallerLibrariesAsync(installerPath, dir, progress, ct).ConfigureAwait(false);

        // 无头安装：installer 自行下载原版服务端与依赖库（BMCLAPI 无法介入其内部下载；
        // NeoForge 硬编码 maven.neoforged.net，国内网络偶发连接重置——失败自动重试一次）
        progress?.Report(new(HostStep.ServerJar, "安装服务端（首次约 3-10 分钟，取决于网络）…"));
        InvalidOperationException? installErr = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (attempt == 2)
            {
                progress?.Report(new(HostStep.ServerJar, "安装中断，自动重试一次…"));
                try { await Task.Delay(2000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = $"-jar \"{installerPath}\" --installServer",
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var proc = Process.Start(psi) ?? throw new InvalidOperationException("安装器启动失败");
            var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                log.Enqueue(e.Data);
                // v40 进度透传：安装器关键行（下载/安装/打补丁/百分比）实时上报，替代固定"安装中…"轮询
                var line = e.Data.Trim();
                var interesting = line.IndexOf("downloading", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.Contains("Installing") || line.Contains("Patching") || line.Contains('%');
                if (interesting || log.Count % 25 == 0)
                {
                    if (line.Length > 80) line = line[..80] + "…";
                    progress?.Report(new(HostStep.ServerJar, "安装中：" + line));
                }
            };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.Enqueue(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("Forge 服务端安装超时（20 分钟）");
            }
            if (proc.ExitCode == 0 && FindForgeArgsFile(dir) is not null)
            {
                installErr = null;
                break;
            }
            installErr = new InvalidOperationException(
                $"Forge 服务端安装失败（exit={proc.ExitCode}）\n{string.Join("\n", log.Reverse().Take(12))}");
        }
        if (installErr is not null) throw installErr;
        try { File.Delete(installerPath); } catch { }
        progress?.Report(new(HostStep.ServerJar, "Forge 服务端安装完成"));
        return build;
    }

    /// <summary>安装前预下载 installer 依赖库：install_profile.json 的 libraries 清单，
    /// BMCLAPI maven 镜像优先，sha1 校验；单项失败不阻塞（installer 会回退官方源重试）。
    /// v40：逐库报进度"依赖库 i/n（文件名）"，~70 库不再全程静默。</summary>
    private static async Task PreloadInstallerLibrariesAsync(string installerPath, string dir,
        IProgress<HostProgress>? progress, CancellationToken ct)
    {
        string profileJson;
        using (var zip = ZipFile.OpenRead(installerPath))
        {
            var entry = zip.GetEntry("install_profile.json");
            if (entry is null) return;
            using var s = entry.Open();
            using var sr = new StreamReader(s, Encoding.UTF8);
            profileJson = await sr.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        if (JsonNode.Parse(profileJson) is not JsonObject node || node["libraries"] is not JsonArray libs) return;
        var libDir = Path.Combine(dir, "libraries");
        var pending = libs.OfType<JsonObject>()
            .Select(l => (Path: l["downloads"]?["artifact"]?["path"]?.GetValue<string>(),
                Sha: l["downloads"]?["artifact"]?["sha1"]?.GetValue<string>()))
            .Where(x => !string.IsNullOrEmpty(x.Path))
            .Where(x => !File.Exists(Path.Combine(libDir, x.Path!.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();
        for (var i = 0; i < pending.Count; i++)
        {
            var (path, sha) = pending[i];
            var dst = Path.Combine(libDir, path!.Replace('/', Path.DirectorySeparatorChar));
            progress?.Report(new(HostStep.ServerJar,
                $"依赖库 {i + 1}/{pending.Count}（{Path.GetFileName(path)}）"));
            try
            {
                await McSources.DownloadFileAsync(
                    new List<string> { $"https://bmclapi2.bangbang93.com/maven/{path}" }, dst, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(sha))
                {
                    using var fs = File.OpenRead(dst);
                    using var sha1 = System.Security.Cryptography.SHA1.Create();
                    var hex = Convert.ToHexString(sha1.ComputeHash(fs)).ToLowerInvariant();
                    if (hex != sha) { try { File.Delete(dst); } catch { } }
                }
            }
            catch { /* 预下载失败不阻塞，installer 会尝试官方源 */ }
        }
    }

    /// <summary>Forge 推荐构建：promotions_slim.json 的 {mc}-recommended，缺省 -latest。</summary>
    private static async Task<string> ResolveForgeBuildAsync(string mc, CancellationToken ct)
    {
        var promo = await McSources.GetJsonAsync(
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct).ConfigureAwait(false);
        var build = promo["promos"]?[$"{mc}-recommended"]?.GetValue<string>()
                 ?? promo["promos"]?[$"{mc}-latest"]?.GetValue<string>();
        return build ?? throw new InvalidOperationException($"Forge 没有 MC {mc} 的版本");
    }

    /// <summary>NeoForge 构建号：maven-metadata.xml 里匹配 MC 版本对应前缀的最新项。
    /// 版本前缀规则：1.21.1 → 21.1.x；1.20.4 → 20.4.x；1.20.1 特例为 47.1.x。</summary>
    private static async Task<string> ResolveNeoForgeBuildAsync(string mc, CancellationToken ct)
    {
        var v = Version.Parse(mc);
        var prefixes = new List<string> { $"{v.Major - 1}.{v.Minor}" };
        if (mc == "1.20.1") prefixes.Add("47.1");
        var meta = await McSources.GetTextAsync(
            "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/maven-metadata.xml", ct)
            .ConfigureAwait(false);
        var versions = System.Text.RegularExpressions.Regex.Matches(meta, "<version>([^<]+)</version>")
            .Select(m => m.Groups[1].Value).ToList();
        foreach (var prefix in prefixes)
        {
            var hit = versions.Where(x => x.StartsWith(prefix + ".", StringComparison.Ordinal))
                .OrderBy(x => Version.TryParse(x, out var b) ? b : new Version(0, 0, 0)).LastOrDefault();
            if (hit is not null) return hit;
        }
        throw new InvalidOperationException($"NeoForge 没有 MC {mc} 的版本（可尝试用 Forge）");
    }

    /// <summary>
    /// 崩溃守护（P2 守护项）：服务端进程意外退出（非关房指令）时，先备份崩溃现场世界，
    /// 再自动重启（同端口，隧道/房间码不变），最多 MaxAutoRestarts 次；耗尽或重启失败即关房。
    /// </summary>
    private async Task MonitorCrashAsync(IProgress<HostProgress>? progress, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = _server;
            if (server is null) return;
            try
            {
                await server.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch { return; }   // 进程对象被 StopAsync 释放 = 正常关房流程
            if (_stopping || _current is null) return;

            // 意外崩溃：进入自动重启
            _server = null;
            var dir = _current.ServerDir;
            if (_restartCount >= MaxAutoRestarts)
            {
                progress?.Report(new(HostStep.Running, "服务端连续崩溃，已达自动重启上限，房间已停止"));
                await StopAsync(progress, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            _restartCount++;
            progress?.Report(new(HostStep.Running,
                $"服务端意外退出，自动重启（第 {_restartCount}/{MaxAutoRestarts} 次）…"));
            try
            {
                // 先备份崩溃现场（世界可能损坏，留证 + 保护数据）
                await BackupWorldAsync(dir, progress, CancellationToken.None).ConfigureAwait(false);
                LaunchServerProcess(dir, _activeOptions);
                progress?.Report(new(HostStep.WaitingDone, "300s"));
                await WaitForDoneAsync(TimeSpan.FromSeconds(300), CancellationToken.None).ConfigureAwait(false);
                progress?.Report(new(HostStep.Running, "服务端已恢复运行（隧道与房间码不变，玩家可重连）"));
            }
            catch (Exception ex)
            {
                progress?.Report(new(HostStep.Running, "自动重启失败：" + ex.Message));
                await StopAsync(progress, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// 世界备份（P2 守护项）：把服务端目录下所有含 level.dat 的世界目录压缩到 backups\，
    /// 每个世界保留最近 5 份。备份失败仅提示不阻塞主流程（开房/重启/关房三处调用）。
    /// </summary>
    private static async Task BackupWorldAsync(string dir, IProgress<HostProgress>? progress, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var worlds = Directory.GetDirectories(dir)
                .Where(d => File.Exists(Path.Combine(d, "level.dat")))
                .ToList();
            if (worlds.Count == 0) return;
            var backupRoot = Path.Combine(dir, "backups");
            Directory.CreateDirectory(backupRoot);
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var world in worlds)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(world);
                progress?.Report(new(HostStep.Preparing, $"备份世界 {name}…"));
                var zipPath = Path.Combine(backupRoot, $"{name}-{stamp}.zip");
                await Task.Run(() => ZipFile.CreateFromDirectory(world, zipPath, CompressionLevel.Fastest, false),
                    ct).ConfigureAwait(false);
                var olds = Directory.GetFiles(backupRoot, $"{name}-*.zip")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).Skip(5);
                foreach (var old in olds)
                {
                    try { File.Delete(old); } catch { /* 被占用则下次再清 */ }
                }
                progress?.Report(new(HostStep.Preparing, $"已备份 {name} → backups\\{Path.GetFileName(zipPath)}"));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            progress?.Report(new(HostStep.Preparing, "世界备份失败（不阻塞，继续）"));
        }
    }

    private async Task WaitForDoneAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (true)
        {
            while (_pendingLines.TryDequeue(out var line))
            {
                lock (_recentLines)
                {
                    _recentLines.Enqueue(line);
                    if (_recentLines.Count > 30) _recentLines.Dequeue();
                }
                ServerLine?.Invoke(line);
                if (line.Contains("Done (", StringComparison.Ordinal))
                    return; // 服务端就绪
            }
            if (_server is not { HasExited: false })
                throw new InvalidOperationException("服务端启动过程中退出：\n" + DiagnoseExit(RecentLines()));
            cts.Token.ThrowIfCancellationRequested();
            try
            {
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("服务端未在 300 秒内完成启动：\n" + RecentLines());
            }
        }
    }

    private void Enqueue(string? line)
    {
        if (line is null) return;
        _pendingLines.Enqueue(line);
    }

    private string RecentLines()
    {
        lock (_recentLines)
            return string.Join("\n", _recentLines);
    }

    /// <summary>服务端启动退出诊断（v42）：识别已知崩溃签名并追加可执行建议。
    /// 纯客户端模组混入的签名 = "invalid dist DEDICATED_SERVER"；
    /// mixin 注入处理器名形如 handler$bbf000$entity_texture_features$etf$xxx，中间段即模组 id。</summary>
    internal static string DiagnoseExit(string recentLines)
    {
        var hint = "";
        if (recentLines.Contains("invalid dist DEDICATED_SERVER", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(recentLines, @"handler\$[a-z0-9]+\$([a-z0-9_]+)\$", RegexOptions.IgnoreCase);
            hint = m.Success
                ? $"\n【诊断】疑似纯客户端模组「{m.Groups[1].Value}」混入服务端导致崩溃（服务端加载了客户端专属类）。请从实例 mods / 导入内容中移除该模组后重开；新版开服已自动过滤常见纯客户端模组，此模组可能未被识别。\n"
                : "\n【诊断】疑似纯客户端模组混入服务端（服务端加载了客户端专属类）。请移除小地图/光影/皮肤/渲染类模组后重开。\n";
        }
        else if (recentLines.Contains("DirectoryLock", StringComparison.OrdinalIgnoreCase)
                 || (recentLines.Contains("Failed to start the minecraft server", StringComparison.OrdinalIgnoreCase)
                     && recentLines.Contains("IOException", StringComparison.Ordinal)))
            hint = "\n【诊断】世界目录被另一个仍在运行的服务端进程锁定（session.lock）——通常是上次启动器直接退出时服务器未被关闭。\n" +
                   "结束残留的 java.exe 进程（或重启电脑）后重开即可；新版开房前会自动清理遗孤进程。\n";
        else if (recentLines.Contains("UnsupportedClassVersionError", StringComparison.Ordinal))
            hint = "\n【诊断】Java 版本过低：请在 mcstudio/host.json 里把 javaExe 指向 Java 17（MC 1.17+）或 Java 21（MC 1.20.5+）。\n";
        return recentLines + hint;
    }

    private void TryKillServer()
    {
        if (_server is null) return;
        try
        {
            if (!_server.HasExited) _server.Kill(entireProcessTree: true);
        }
        catch { /* 已退出 */ }
        _server.Dispose();
        _server = null;
    }

    /// <summary>房间名清洗：小写、仅字母数字-_、空格转 -、截断 20 字符（与云端约定一致）。</summary>
    private static string RoomNameOf(string? roomName, string dir)
    {
        var name = string.IsNullOrWhiteSpace(roomName)
            ? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))
            : roomName;
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
            else if (ch == ' ') sb.Append('-');
        }
        if (sb.Length == 0) sb.Append("room");
        if (sb.Length > 20) sb.Length = 20;
        return sb.ToString();
    }

    /// <summary>服务端 mods 目录或房间清单中是否已有 fabric-api（文件名或 project 判定）。</summary>
    private static bool HasFabricApi(string serverDir, JsonObject meta)
    {
        var modsDir = Path.Combine(serverDir, "mods");
        try
        {
            if (Directory.Exists(modsDir) && Directory.EnumerateFiles(modsDir, "*.jar")
                .Any(j => Path.GetFileName(j).Contains("fabric-api", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        catch { /* 目录不可读按未装处理 */ }
        if (meta["mods"] is JsonArray arr)
            return arr.Any(m => (m?["project"]?.GetValue<string>()
                ?? m?["filename"]?.GetValue<string>() ?? "")
                .Contains("fabric-api", StringComparison.OrdinalIgnoreCase));
        return false;
    }

    private static IReadOnlyList<ModEntry> ReadMods(JsonObject meta)
    {
        var list = new List<ModEntry>();
        if (meta["mods"] is JsonArray arr)
            foreach (var m in arr)
            {
                if (m?["project"]?.GetValue<string>() is not { Length: > 0 } project) continue;
                list.Add(new ModEntry(project,
                    m["version"]?.GetValue<string>(), m["filename"]?.GetValue<string>(),
                    m["url"]?.GetValue<string>()));
            }
        return list;
    }

    private static int FreeLocalPort(int prefer)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, prefer);
            listener.Start();
            listener.Stop();
            return prefer;
        }
        catch (SocketException)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    // v50.9.4：WithMrpackIndexAsync（v50.6.9/v50.7 逐文件清单生成）已随清单通道整体移除——
    // 对齐只走包身份（RoomPacks）+ 加入端自动装配兜底（StudioSession.AutoAssembleLocalPackAsync）。

    /// <summary>v50.6.5：MC 服务端 TCP 入站放行（端口级规则，profile=any）。
    /// 背景：IPv6 直连 = joiner 对房主 [v6]:mcPort 的 TCP 直通；手机热点等场景 Windows
    /// 判为公用网络，入站 TCP 默认全拦——此前只有 UDP 规则（打洞用），这是 v6 直连
    /// 从未成功（云端 metrics v6 成功 0 次）的直接原因。每次开房删旧建新（端口会变），
    /// 无管理员权限时静默失败不阻塞开房。</summary>
    private static void EnsureServerTcpFirewallRule(int port, IProgress<HostProgress>? progress)
    {
        try
        {
            const string rule = "MCStudio-MC-TCP-In";
            RunNetsh($"advfirewall firewall delete rule name=\"{rule}\"");
            var add = RunNetsh($"advfirewall firewall add rule name=\"{rule}\" dir=in action=allow " +
                               $"protocol=TCP localport={port} profile=any");
            progress?.Report(new(HostStep.Preparing, add == 0
                ? $"防火墙已放行 MC 服务端口（TCP {port}，含 IPv6 直连入口）"
                : "防火墙 TCP 规则添加失败（未以管理员运行？）——朋友端 IPv6 直连可能不可用"));
        }
        catch
        {
            // 规则失败不阻塞开房
        }
    }

    private static int RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(); } catch { }
                return -1;
            }
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    public void Dispose()
    {
        TryKillServer();
        _frpc.Dispose();
    }
}
