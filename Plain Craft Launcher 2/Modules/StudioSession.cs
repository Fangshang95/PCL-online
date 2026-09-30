using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.MCStudio;

namespace PCL;

/// <summary>
/// MCStudio 联机会话：登录态 / 好友 / 加入 / 开房的共享业务层。
/// 状态以磁盘（StudioAccount 本地令牌）与云端为准，各页面通过 SessionChanged 事件同步 UI。
/// </summary>
public static class StudioSession
{
    private static UserIdentity? _me;
    private static IReadOnlyList<FriendEntry>? _friends;
    private static StudioApiClient? _api;
    private static bool _initializing;
    private static System.Threading.Timer? _presenceTimer;
    private static bool _presenceBusy;
    private static string _friendsSignature = "";

    /// <summary>v50.6.3：隧道层关键事件 → 主日志。此前隧道日志只在 P2pTrace 独立文件
    /// （{exe}\mcstudio\logs\p2p-*.log），PCL 主日志零痕迹，排障必须两头翻——消除该盲区。</summary>
    static StudioSession()
    {
        P2pTunnel.LauncherLog += m => ModBase.Log("[隧道] " + m);
    }

    public static UserIdentity? Me => _me;
    public static IReadOnlyList<FriendEntry>? Friends => _friends;

    /// <summary>登录态或好友列表发生变化（供主页左栏刷新）。</summary>
    public static event Action? SessionChanged;

    public static void NotifyChanged() => SessionChanged?.Invoke();

    /// <summary>开房下拉框"自动"选项的显示文案（选中后开房时自动识别版本/加载器）。</summary>
    public const string AutoHostLabel = "自动";

    /// <summary>
    /// "自动"选项的解析链（v39）：导入整合包的 manifest 建议 → 散装模组 jar 元数据识别（loader 多数决 +
    /// 版本范围推断）→ 当前选中实例 → 兜底（未识别到任何信息时按最常见配置 Forge 1.21.1）。
    /// 返回解析结果与来源说明（供提示展示）。
    /// </summary>
    public static (string Mc, string Loader, string Source) ResolveAutoHostOptions()
    {
        string? mc = null, loader = null, source = "";
        try
        {
            // 1) 导入整合包的 manifest 建议（CurseForge/Modrinth/ MMC 自带元数据，最可信）
            var (impMc, impLoader) = ModpackImporter.GetImportSuggestion();
            if (impLoader == "quilt") impLoader = "";
            if (impMc.Length > 0 || impLoader.Length > 0)
            {
                mc = impMc.Length > 0 ? impMc : null;
                loader = impLoader.Length > 0 ? impLoader : null;
                source = "导入的整合包";
            }
        }
        catch { /* 忽略，走下一层 */ }

        try
        {
            // 2) 散装模组 jar 元数据识别（v39：修复散装 Forge 模组被默认开成 Fabric 房的问题）
            var (jarLoader, jarMc) = ModpackImporter.DetectImportedModsMeta();
            if (jarLoader == "quilt") jarLoader = null;
            if (mc is null && jarMc is not null) { mc = jarMc; source = string.IsNullOrEmpty(source) ? "导入模组的文件声明" : source; }
            if (loader is null && jarLoader is not null) { loader = jarLoader; source = "导入模组的文件声明"; }
        }
        catch { /* 忽略，走下一层 */ }

        try
        {
            // 3) 当前选中实例（版本 + 加载器；原版实例 → vanilla 房）
            if (mc is null || loader is null)
            {
                var (instMc, instLoader) = ResolveInstanceHostTarget(ModInstanceList.McMcInstanceSelected);
                if (mc is null && instMc is not null) { mc = instMc; source = string.IsNullOrEmpty(source) ? "当前实例" : source; }
                if (loader is null && instLoader is not null) { loader = instLoader; source = "当前实例"; }
            }
        }
        catch { /* 实例列表未就绪等：走兜底 */ }

        // 4) 兜底：什么信息都没有（无导入、无实例）→ 按最常见配置
        mc ??= "1.21.1";
        loader ??= "forge";
        if (string.IsNullOrEmpty(source)) source = "未识别到整合包与实例，已按最常见配置";
        return (mc, loader.ToLowerInvariant(), source);
    }

    /// <summary>
    /// 从指定实例推导开房用的 MC 版本与加载器（三个开房入口共用的"实例跟随"判定）。
    /// 版本取 Info.VanillaName（实例 json 解析结果，文件夹名不可靠）；
    /// 加载器按实例类型映射（Forge/NeoForge 已支持服务端托管）。
    /// </summary>
    public static (string? Mc, string? Loader) ResolveInstanceHostTarget(McInstance? instance)
    {
        if (instance is null) return (null, null);
        try
        {
            var version = instance.Info?.VanillaName;
            if (string.IsNullOrWhiteSpace(version) || version == "Unknown") return (null, null);
            var loader = instance.state switch
            {
                McInstanceState.Fabric or McInstanceState.LegacyFabric => "fabric",
                McInstanceState.Forge => "forge",
                McInstanceState.NeoForge => "neoforge",
                McInstanceState.Original or McInstanceState.Snapshot or McInstanceState.OptiFine
                    or McInstanceState.Fool or McInstanceState.Old => "vanilla",
                _ => null,
            };
            return (version, loader);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// 导入池里有整合包建议版本时，把开房下拉框自动选中为整合包的游戏版本与加载器（所见即所得）；
    /// 下拉框没有的版本会动态插入一项。Quilt 暂不支持托管，跳过加载器选中（版本仍会选中，便于手动配）。
    /// </summary>
    public static void ApplyImportSuggestionToUi(PCL.MyComboBox mcCombo, PCL.MyComboBox loaderCombo)
    {
        if (mcCombo is null || loaderCombo is null) return;
        try
        {
            var (impMc, impLoader) = ModpackImporter.GetImportSuggestion();
            if (impMc.Length == 0) return;
            if (!mcCombo.Items.OfType<string>().Contains(impMc))
            {
                // 按版本号排序插入（保持下拉列表从新到旧的整洁顺序）
                var insertAt = mcCombo.Items.Count;
                if (Version.TryParse(impMc, out var incoming))
                    for (var i = 0; i < mcCombo.Items.Count; i++)
                        if (mcCombo.Items[i] is string s && Version.TryParse(s, out var existing) && existing > incoming)
                        {
                            insertAt = i;
                            break;
                        }
                mcCombo.Items.Insert(insertAt, impMc);
            }
            mcCombo.SelectedItem = impMc;
            var loaderMap = new Dictionary<string, string>
            {
                ["fabric"] = "Fabric", ["forge"] = "Forge",
                ["neoforge"] = "NeoForge", ["vanilla"] = "Vanilla",
            };
            if (loaderMap.TryGetValue(impLoader, out var loaderText))
            {
                if (!loaderCombo.Items.OfType<string>().Contains(loaderText)) loaderCombo.Items.Add(loaderText);
                loaderCombo.SelectedItem = loaderText;
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "应用导入整合包的版本建议到开房下拉框失败", ModBase.LogLevel.Debug);
        }
    }

    /// <summary>
    /// 读取平台配置（云端 tunnel-api 地址与密钥）。
    /// 查找顺序：启动器目录 mcstudio/api.json → 当前目录 mcstudio/api.json →
    /// v50.9.6 构建期内嵌配置（全新电脑/只换 exe 零配置可用）。
    /// </summary>
    public static StudioApiClient? TryGetApi()
    {
        if (_api is not null) return _api;
        string[] candidates =
        {
            Path.Combine(ModBase.exePath, "mcstudio", "api.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcstudio", "api.json"),
            Path.Combine(Environment.CurrentDirectory, "mcstudio", "api.json"),
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                _api = new StudioApiClient(StudioConfig.Load(path));
                return _api;
            }
            catch
            {
                // 配置文件损坏则继续找下一个
            }
        }
        // v50.9.6：磁盘 api.json 缺失/损坏 → 内嵌配置兜底
        try
        {
            var embedded = StudioSecrets.EmbeddedJson("api.json");
            if (embedded is not null)
            {
                _api = new StudioApiClient(StudioConfig.Parse(embedded));
                ModBase.Log("[MCStudio] mcstudio/api.json 缺失，已使用内嵌平台配置");
                return _api;
            }
        }
        catch { /* 内嵌缺失：保持 null（联机入口会提示） */ }
        return null;
    }

    /// <summary>从磁盘重读登录态（不发起网络请求），用于页面获得焦点时同步。</summary>
    public static void ReloadLocal()
    {
        _me = StudioAccount.Load();
        if (_me is null) _friends = null;
        NotifyChanged();
    }

    /// <summary>
    /// 全量初始化：读登录态 → 云端校验令牌（失效即清除）→ 拉取好友列表。
    /// 结果通过 SessionChanged 事件通知 UI，可安全重复调用。
    /// </summary>
    public static async Task InitializeAsync()
    {
        if (_initializing) return;
        EnsurePresenceTimer();
        _initializing = true;
        try
        {
            _me = StudioAccount.Load();
            NotifyChanged();
            var api = TryGetApi();
            if (_me is null || api is null) return;
            try
            {
                var fresh = await api.GetMeAsync(_me.Token);
                if (fresh is null)
                {
                    StudioAccount.Clear();
                    _me = null;
                    _friends = null;
                    NotifyChanged();
                    return;
                }
                _me = fresh;
                StudioAccount.Save(fresh);
                await RefreshFriendsCoreAsync(api);
            }
            catch
            {
                // 网络不可用时保留本地登录态与旧好友列表
            }
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>重新拉取好友列表并广播。</summary>
    public static async Task RefreshFriendsAsync()
    {
        if (_me is null) return;
        var api = TryGetApi();
        if (api is null) return;
        try
        {
            await RefreshFriendsCoreAsync(api);
        }
        catch
        {
            // 静默失败：保留旧列表
        }
    }

    private static async Task RefreshFriendsCoreAsync(StudioApiClient api)
    {
        if (_me is null) return;
        try
        {
            _friends = await api.FriendListAsync(_me.Token);
        }
        catch (StudioApiException ex) when (ex.StatusCode is 401 or 403)
        {
            // 会话失效：清除登录态
            StudioAccount.Clear();
            _me = null;
            _friends = null;
        }
        NotifyChanged();
    }

    /// <summary>
    ///     在线心跳：每 30s 拉一次好友列表。该请求本身会刷新本端 last_seen（云端 120s 在线窗口的活跃来源），
    ///     同时把好友的最新在线状态带回来；仅在列表实际变化时通知 UI，避免每 30s 重渲染。
    /// </summary>
    private static void EnsurePresenceTimer()
    {
        if (_presenceTimer is not null) return;
        _presenceTimer = new System.Threading.Timer(
            async _ => await PresenceTickAsync(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private static async Task PresenceTickAsync()
    {
        if (_me is null || _presenceBusy) return;
        _presenceBusy = true;
        try
        {
            var api = TryGetApi();
            if (api is null) return;
            var list = await api.FriendListAsync(_me.Token).ConfigureAwait(false);
            var sig = "";
            foreach (var f in list)
                sig += f.Email + ":" + (f.Online ? 1 : 0) + ":" + f.Nickname + "|";
            _friends = list;
            if (sig != _friendsSignature)
            {
                _friendsSignature = sig;
                NotifyChanged();
            }
        }
        catch (StudioApiException ex) when (ex.StatusCode is 401 or 403)
        {
            // 会话失效：清除登录态并通知各页面回到未登录 UI
            StudioAccount.Clear();
            _me = null;
            _friends = null;
            _friendsSignature = "";
            NotifyChanged();
        }
        catch
        {
            // 网络抖动：保留旧列表，下个周期再试
        }
        finally
        {
            _presenceBusy = false;
        }
    }

    // ---------------------------------------------------------------- 加入房间

    /// <summary>装配目标 .minecraft：当前选中的游戏文件夹，缺省回落启动器目录。</summary>
    public static string GetMcFolder()
    {
        var selected = ModFolder.mcFolderSelected;
        if (!string.IsNullOrEmpty(selected) && Directory.Exists(selected)) return selected;
        var fallback = Path.Combine(ModBase.exePath, ".minecraft");
        Directory.CreateDirectory(Path.Combine(fallback, "versions"));
        return fallback;
    }

    /// <summary>
    /// 查找本机可复用的原版实例（v36）：仅 vanilla 房（loader=vanilla 且无模组清单）+ 本机同 MC 版本的纯原版实例。
    /// 命中即免装配直接启动（不再下载客户端与依赖库）；模组房/未命中返回 null，调用方回落 RoomAssembler 装配。
    /// 注意：本函数在主程序侧（PCL.MCStudio 不引用主程序，访问不了 ModInstanceList），三个加入入口共用。
    /// </summary>
    /// <summary>v50.6.2：原 TryFindReusableInstance 的扫描含逐实例 mods 目录枚举（重 IO），
    /// 改 async 并整体移入后台线程——根治「进入房间时主窗口未响应」。</summary>
    public static async Task<McInstance?> TryFindReusableInstanceAsync(RoomInfo info)
    {
        // 模组房一律装配：复用用户实例会往里灌房间模组，污染日常环境
        if (info.Loader.Split(':')[0].ToLowerInvariant() != "vanilla" || info.Mods.Count > 0)
            return null;
        return await Task.Run(() => TryFindReusableInstanceCore(info)).ConfigureAwait(true);
    }

    private static McInstance? TryFindReusableInstanceCore(RoomInfo info)
    {
        try
        {
            foreach (var inst in ModInstanceList.mcInstanceList.Values.SelectMany(v => v))
            {
                // 仅纯原版实例（带任何 loader 的实例有版本链依赖，不动）
                if (inst.state is not (McInstanceState.Original or McInstanceState.Snapshot
                    or McInstanceState.Fool or McInstanceState.OptiFine or McInstanceState.Old))
                    continue;
                var name = inst.Info?.VanillaName;
                if (string.IsNullOrWhiteSpace(name) || name == "Unknown") continue;
                if (!name.Equals(info.Mc, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(inst.PathInstance) || !Directory.Exists(inst.PathInstance)) continue;
                // 防御：实例 mods 目录非空不复用（原版实例被手动塞模组的场景）
                try
                {
                    var modsDir = Path.Combine(inst.PathInstance, "mods");
                    if (Directory.Exists(modsDir) && Directory.EnumerateFiles(modsDir, "*.jar").Any())
                        continue;
                }
                catch { continue; }
                return inst;
            }
        }
        catch (Exception ex)
        {
            // 实例列表未就绪等异常：按未命中处理，回落装配
            ModBase.Log("[Studio] 实例复用查找失败（回落装配）：" + ex.Message);
        }
        return null;
    }

    /// <summary>解析房间码并自动装配客户端；mode 允许时先尝试 P2P 直连（v6 → UDP 打洞），失败按模式回退中转。</summary>
    public static async Task<AssembleResult> JoinAsync(string code, IProgress<AssembleProgress> progress,
        P2pMode p2pMode = P2pMode.Auto)
    {
        var api = TryGetApi() ?? throw new InvalidOperationException("api not configured");
        var info = await api.ResolveAsync(code);

        // P2P 直连尝试（需登录态做信令鉴权；仅中转模式跳过）
        if (p2pMode != P2pMode.RelayOnly)
        {
            if (_me is null && p2pMode == P2pMode.DirectOnly)
            {
                P2pTrace.End("joiner", "no-login", $"房间 {code} 直连模式需要登录，已失败");
                throw new InvalidOperationException("P2P 直连需要先登录账号（信令鉴权）");
            }
            if (_me is null)
                P2pTrace.Log($"[joiner] 房间 {code} 未登录，跳过 P2P 直接中转");
            if (_me is not null)
            {
                P2pTrace.Session("joiner", code, $"模式={p2pMode}");
                var p2pLog = new Progress<string>(msg =>
                    progress.Report(new AssembleProgress(AssembleStep.Resolving, msg)));
                P2pAttempt? p2p = null;
                try
                {
                    // v50.6.7 relay-first：中继上游秒级就绪（0 等待进服），后台打洞打通后升级直连；
                    // 仅直连模式不预建中继（保持原同步打洞语义）
                    p2p = await P2pTunnel.JoinerTryAsync(api, _me.Token, code,
                        p2pMode == P2pMode.DirectOnly ? null : info.Address, p2pLog,
                        CancellationToken.None).ConfigureAwait(true);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ModBase.Log("[P2P] 加入端尝试异常：" + ex.Message);
                    P2pTrace.Log($"[joiner] P2P 尝试异常：{ex.GetType().Name}: {ex.Message}");
                    if (p2pMode == P2pMode.DirectOnly) throw;
                }
                if (p2p is not null)
                {
                    P2pTrace.End("joiner",
                        p2p.Kind == P2pRouteKind.V6 ? "v6"
                        : p2p.Kind == P2pRouteKind.Relay ? "relay-first"
                        : "udp",
                        $"线路 {p2p.LocalAddress}（外层终局，过程明细见上方 result 行）");
                    info = info with { Address = p2p.LocalAddress };
                    progress.Report(new AssembleProgress(AssembleStep.Resolving,
                        p2p.Kind == P2pRouteKind.V6 ? "IPv6 直连已建立 · " + p2p.LocalAddress
                        : p2p.Kind == P2pRouteKind.Relay ? "中继线路已就绪（后台升级直连中） · " + p2p.LocalAddress
                        : "P2P 打洞成功 · " + p2p.LocalAddress));
                }
                else
                {
                    if (p2pMode == P2pMode.DirectOnly)
                    {
                        P2pTrace.End("joiner", "direct-only-fail", $"房间 {code} 仅直连模式不回退中转");
                        throw new InvalidOperationException("P2P 直连未成功（仅直连模式不回退中转）");
                    }
                    P2pTrace.End("joiner", "relay-fallback", "P2P 未打通，已回退 frp 中转（2MB/s 限速）");
                    progress.Report(new AssembleProgress(AssembleStep.Resolving,
                        "P2P 未打通，使用中转线路（免费通道限速 2MB/s，人多或大地图时可能卡顿）"));
                }
            }
        }
        else
        {
            P2pTrace.Log($"[joiner] 房间 {code} 模式=RelayOnly，跳过 P2P 直接中转");
        }

        // 未走 P2P（中转兜底 / 仅中转模式）时也包一层本机回环转发：
        // 烤入启动参数的永远是 127.0.0.1，游戏内服务器列表看不到公网地址，
        // 杜绝"启动游戏后在游戏里直接连接"绕过启动器长期走中转
        if (!info.Address.StartsWith("127.0.0.1", StringComparison.Ordinal))
        {
            var idx = info.Address.LastIndexOf(':');
            if (idx > 0 && int.TryParse(info.Address[(idx + 1)..], out var port))
            {
                var local = LoopbackForwarder.Open(info.Address[..idx], port);
                info = info with { Address = local };
                progress.Report(new AssembleProgress(AssembleStep.Resolving,
                    "已建立本机加速通道（中转线路对游戏透明，无需知道服务器地址）"));
            }
        }

        // v44/v46 云端开服思路：房间携带客户端整合包 → 整包获取 + 安装/绑定本机实例 → 直接进服。
        // 无可下载包时不再死路：AllowLegacyAssembly=true 走旧清单装配；否则进整包流程，
        // 内部会提醒"未找到对应客户端版本"并给出「选择本机实例」入口（取消则不加入）。
        var clientPack = info.ClientPack;
        var hasDownloadablePack = clientPack is not null && clientPack.HasDownload;
        if (hasDownloadablePack || !AllowLegacyAssembly)
        {
            if (!hasDownloadablePack)
            {
                var lanMotd = info.Mode == "lan"
                    ? string.IsNullOrEmpty(info.Lan?.Motd) ? "未命名" : info.Lan!.Motd : "";
                progress.Report(new AssembleProgress(AssembleStep.Resolving,
                    info.Mode == "lan"
                        ? $"虚拟局域网房间：世界《{lanMotd}》。正在匹配本机相同版本实例（无匹配可导入客户端整合包或选择本机实例）…"
                        : "该房间未提供可下载的客户端整合包，可选择本机实例加入（不选则取消）"));
            }
            var packResult = await JoinLocalClientPackAsync(info, clientPack, progress).ConfigureAwait(true);
            // 已登录则上报 membership（好友可看到"所在的房间"）；失败不影响加入结果
            if (_me is not null)
            {
                try { await api.JoinRoomAsync(_me.Token, code).ConfigureAwait(true); }
                catch { /* 上报失败静默忽略 */ }
            }
            return packResult;
        }
        progress.Report(new AssembleProgress(AssembleStep.Resolving,
            "该房间未提供客户端整合包，按旧模式逐个下载模组（可能缺件）"));

        // v36 实例复用：vanilla 房 + 本机已有同版本纯原版实例 → 免装配直接启动（不下载客户端/依赖库）。
        // 此时 info.Address 必为 127.0.0.1（P2P 本地端口或回环转发），可直接作 ServerIp 烤入 quickPlay
        var reuse = await TryFindReusableInstanceAsync(info).ConfigureAwait(true);
        if (reuse is not null)
        {
            progress.Report(new AssembleProgress(AssembleStep.Done, reuse.Name));
            var reuseName = reuse.Name;
            var reuseDir = reuse.PathInstance;
            ModBase.RunInUi(() =>
            {
                var ok = ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions
                {
                    ServerIp = info.Address,
                    instance = reuse,
                });
                HintService.Hint(ok
                    ? string.Format(Lang.Text("Tools.Studio.Join.ReuseHit"), info.Mc, reuseName)
                    : Lang.Text("Minecraft.Launch.Error.LaunchFailed"), ok ? HintType.Info : HintType.Error);
            });
            return new AssembleResult(InstanceDirName(reuseDir), reuseDir, 0, 0,
                Array.Empty<string>(), Reused: true);
        }

        // v37 房主快速通道：加入的是本机正在开的房间 → 模组优先从服务端目录本地复制，缺的才走网络
        var hostModsDir = TryGetHostLocalModsDir(info.Code);
        var result = await new RoomAssembler().AssembleAsync(info, GetMcFolder(), progress,
            localModsDir: hostModsDir);
        // 已登录则上报 membership（好友可看到"所在的房间"）；失败不影响加入结果
        if (_me is not null)
        {
            try
            {
                await api.JoinRoomAsync(_me.Token, code);
            }
            catch
            {
                // 上报失败静默忽略
            }
        }
        return result;
    }

    /// <summary>回退开关：true 时无客户端包的房间退回旧"逐个模组"装配（默认 false = 严格阻断，v44）。</summary>
    public static bool AllowLegacyAssembly { get; set; }

    /// <summary>加入卡「导入本地整合包」：用户手动指定的客户端包 zip（v46），加入时优先使用。</summary>
    public static ClientPackInfo? JoinPickedLocalPack { get; private set; }

    /// <summary>加入卡导入本地整合包：算 sha1 登记为加入优先包（步骤 1 消费，成功后复制进缓存长期复用）。</summary>
    public static string SetJoinPickedLocalPack(string path)
    {
        var fi = new FileInfo(path);
        var sha1 = ClientPackInfo.Sha1OfFile(path);
        JoinPickedLocalPack = new ClientPackInfo(Path.GetFileName(path), ClientPackInfo.SourceLocal,
            null, fi.Length, sha1, "", "", path);
        return fi.Length + "|" + sha1;
    }

    /// <summary>
    /// v44/v46 整包加入（房主本机直连与朋友整包加入共用）。决策链：
    /// 0) 已装实例 marker 命中 → 跨房/换码秒进（无需包文件）；1) 本地包候选（加入卡导入 → 房主导入 → 缓存）；
    /// 2) 有链接 → 下载；3) 都没有 → 「选择本机实例」绑定（取消则不加入）；随后安装（如需）→ 启动进服。
    /// 返回 Reused=true：实例已由本方法启动，调用方不得再走 RefreshVersionsAndLaunch（防双重启动）。
    /// </summary>
    public static async Task<AssembleResult> JoinLocalClientPackAsync(
        RoomInfo info, ClientPackInfo? cp, IProgress<AssembleProgress> progress)
    {
        var markerKey = cp?.PackKey() ?? $"nometa:{info.Mc}:{info.Loader}";
        // v50.2：实例名短键用 cp.Sha1 前 7 字符（与 v50 接手 AI 实际写出的 mcstudio-pack-n207d14 格式对齐）——
        // 老 marker 可能是 v44 短 hash / v50 完整 "s:Sha1" 两种写法，下面 alreadyInstalled 判定兼容
        var shortKey = ShortKeyFor(cp);
        var instName = "mcstudio-pack-" + shortKey;
        var instDir = Path.Combine(GetMcFolder(), "versions", instName);
        var marker = Path.Combine(instDir, ".mcstudio-pack");
        // v50.2：marker 文件升级为多键 OR 表（彻底解决"房主重开 cp 字段漂移→朋友端 marker 不匹配"反复重装）
        // —— 写入"v2|主键|s:sha1|nometa:mc:loader"三条候选键，匹配任一即复用
        var markerKeys = new List<string> { markerKey, $"nometa:{info.Mc}:{info.Loader}" };
        if (cp?.Sha1 is { Length: > 0 } sha1) markerKeys.Add("s:" + sha1);
        // v50.9.4：逐文件清单通道（index/i: 键）已整体移除——对齐只走包身份 + 自动装配兜底

        // 0) 已装命中：同包跨房间/换码复用（无需包文件在本地）
        // v50.2：读 marker 兼容 v2 多键表 + 旧单键两种格式
        bool IsInstanceInstalled(string dir, string fileMarker)
        {
            if (!Directory.Exists(dir) || !File.Exists(fileMarker)) return false;
            var c = File.ReadAllText(fileMarker).Trim();
            var tokens = c.StartsWith("v2|") ? c[3..].Split('|') : new[] { c };
            foreach (var t in tokens) if (markerKeys.Contains(t)) return true;
            return false;
        }
        var alreadyInstalled = IsInstanceInstalled(instDir, marker);
        // v48.6：「选择本机实例」绑定的 marker 写在所选实例目录（instDir 是虚拟名，目录从不存在）
        // —— 扫描实例列表找 marker 命中的绑定实例，命中即重定向，实现跨会话秒进。
        // v50.2：兼容三种历史 marker 写法（markerKey/shortKey/"s:"+shortKey/任意 s: 开头的短 sha1）
        if (!alreadyInstalled)
        {
            try
            {
                var ldr0 = ModInstanceList.mcInstanceListLoader;
                if (ldr0.State == ModBase.LoadState.Waiting && !string.IsNullOrEmpty(ModFolder.mcFolderSelected))
                    ModLoader.LoaderFolderRun(ldr0, ModFolder.mcFolderSelected,
                        ModLoader.LoaderFolderRunType.ForceRun, 1, "versions\\", true);
                // v50.6.2：重扫（WaitForExit）与逐实例读 marker 是重 IO——移后台，UI 不再冻结
                var bound = await Task.Run(() =>
                {
                    if (ldr0.State != ModBase.LoadState.Finished)
                        ldr0.WaitForExit(GetMcFolder(), isForceRestart: true);
                    return ModInstanceList.mcInstanceList.Values.SelectMany(v => v)
                        .FirstOrDefault(i => i is not null && IsInstanceInstalled(i.PathInstance,
                            Path.Combine(i.PathInstance, ".mcstudio-pack")));
                }).ConfigureAwait(true);
                if (bound is not null)
                {
                    instName = bound.Name;
                    instDir = bound.PathInstance;
                    marker = Path.Combine(instDir, ".mcstudio-pack");
                    alreadyInstalled = true;
                    progress.Report(new AssembleProgress(AssembleStep.PackInstall,
                        $"已绑定过的实例「{bound.Name}」命中，直接进服"));
                }
            }
            catch { /* 列表未就绪等：走原流程 */ }
        }

        // v47.1：进度与完成文案优先显示整合包名（而非内部实例名 mcstudio-pack-xxx）
        var packName = cp?.Name is { Length: > 0 } pn0 ? pn0 : "";
        if (JoinPickedLocalPack?.Name is { Length: > 0 } pn1 && packName.Length == 0) packName = pn1;

        var local = (string?)null;
        McInstance? boundByPickerInst = null;   // v48.6：「选择本机实例」绑定的实例（启动时直接用它）
        if (!alreadyInstalled)
        {
            // v50.9.4：逐文件清单分支（cp.Index 包装成 mrpack）已随清单通道整体移除

            // 1) 本地包候选：加入卡导入的 zip（用户显式选择，优先）→ 房主导入登记 → 全局缓存
            if (JoinPickedLocalPack?.LocalPath is { Length: > 0 } pp && File.Exists(pp))
            {
                if (!string.IsNullOrEmpty(cp?.Sha1) && !string.IsNullOrEmpty(JoinPickedLocalPack.Sha1)
                    && cp!.Sha1 != JoinPickedLocalPack.Sha1)
                    HintService.Hint("所选整合包与服务器要求的客户端包不一致（sha1 不同），仍将按你的选择使用", HintType.Warning);
                local = pp;
            }
            if (local is null)
            {
                var imported = ModpackImporter.GetClientPackImport();
                if (imported?.LocalPath is { Length: > 0 } lp && File.Exists(lp)
                    && (string.IsNullOrEmpty(cp?.Sha1) || cp!.Sha1 == imported.Sha1))
                    local = lp;
            }
            if (local is null && cp is not null && File.Exists(cp.CachePath())) local = cp.CachePath();

            // 2) 无本地包 → 下载（3 次整包重试 + sha1 校验 + 缓存落盘）
            if (local is null && cp is not null && cp.HasDownload)
            {
                var root = ClientPackInfo.ClientPackRoot();
                Directory.CreateDirectory(root);
                CheckDiskSpace(root, cp.Size);
                var target = cp.CachePath();
                Exception? last = null;
                for (var attempt = 1; attempt <= 3 && local is null; attempt++)
                {
                    if (attempt > 1)
                    {
                        progress.Report(new AssembleProgress(AssembleStep.PackDownload,
                            $"下载失败，第 {attempt} 次重试…"));
                        await Task.Delay(TimeSpan.FromSeconds(5 * attempt)).ConfigureAwait(true);
                    }
                    try
                    {
                        await McSources.DownloadFileAsync(
                            new[] { cp.Url! }, target + ".part", CancellationToken.None, cp.Sha1,
                            new Progress<(long Done, long Total)>(t => progress.Report(
                                new AssembleProgress(AssembleStep.PackDownload,
                                    t.Total > 0
                                        ? $"下载客户端整合包 {t.Done / 1048576.0:F0}/{t.Total / 1048576.0:F0} MB"
                                        : $"下载客户端整合包 {t.Done / 1048576.0:F0} MB")))).ConfigureAwait(true);
                        if (File.Exists(target)) try { File.Delete(target); } catch { }
                        File.Move(target + ".part", target);
                        local = target;
                        packName = cp.Name;   // 下载的缓存文件名是 key，展示用房间登记的包名
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        last = ex;
                        try { File.Delete(target + ".part"); } catch { }
                    }
                }
                if (local is null)
                    throw new InvalidOperationException(
                        "客户端整合包下载失败：" + (last?.Message ?? "未知错误") + "\n请检查网络后重试。", last);
            }

            // 3) 无包可用 → v50.9.3 自动装配兜底（packs 身份下载 / 合成基础客户端），失败再「选择本机实例」
            if (local is null)
            {
                var auto = await AutoAssembleLocalPackAsync(info, progress).ConfigureAwait(true);
                if (auto is not null)
                {
                    local = auto.Value.Path;
                    instName = auto.Value.InstName;
                    instDir = Path.Combine(GetMcFolder(), "versions", instName);
                    marker = Path.Combine(instDir, ".mcstudio-pack");
                }
            }

            // 3.1) 自动装配也未成功 → 「选择本机实例」绑定，取消则不加入
            if (local is null)
            {
                var bound = await PickLocalInstanceForJoinAsync(info, cp, markerKey, instDir, progress)
                    .ConfigureAwait(true);
                if (bound is null)
                    throw new InvalidOperationException(
                        "未找到对应客户端版本且未选择本机实例，已取消加入。\n" +
                        "可让房主提供客户端整合包下载链接，或在加入卡「导入本地整合包」后重试。");
                boundByPickerInst = bound;
                packName = bound.Name;   // 本机实例绑定：显示所选实例名
            }
            else
            {
                packName = Path.GetFileName(local);   // 实际使用的包文件名（导入/缓存/下载后）
                progress.Report(new AssembleProgress(AssembleStep.PackDownload,
                    $"使用本机已有的客户端整合包：{packName}（免下载）"));
            }

            // 3.5) 安装（选择器绑定的是现成实例，跳过安装）
            if (boundByPickerInst is null)
            {
                if (Directory.Exists(instDir))
                    try { Directory.Delete(instDir, true); } catch { }
                progress.Report(new AssembleProgress(AssembleStep.PackInstall,
                    $"安装整合包 {Path.GetFileName(local)}…（首次约 5-15 分钟，若弹出可选模组请按需选择）"));
                var loader = await Task.Run(() =>
                    ModModpack.ModpackInstall(local, instName, isOnlineInstall: true)).ConfigureAwait(true);
                // 安装器已在工作线程运行，这里只在后台等它结束（不能在 UI 线程轮询）
                await Task.Run(() =>
                {
                    while (loader.State == ModBase.LoadState.Loading) System.Threading.Thread.Sleep(200);
                }).ConfigureAwait(true);
                if (loader.State == ModBase.LoadState.Failed)
                    throw new InvalidOperationException(
                        "整合包安装失败：" + (loader.Error?.Message ?? "未知错误"), loader.Error);
                if (loader.State != ModBase.LoadState.Finished)
                    throw new InvalidOperationException("整合包安装被取消");
                Directory.CreateDirectory(instDir);
                // v50.2：写多键 v2 表（主键+sha1+nometa 三条候选），房主 cp 漂移时仍命中
                var markerContent = "v2|" + string.Join("|", markerKeys);
                await File.WriteAllTextAsync(marker, markerContent).ConfigureAwait(true);
                // 加入卡导入的包复制进缓存：此后同房间免重复导入
                if (JoinPickedLocalPack?.LocalPath is { Length: > 0 } picked && File.Exists(picked)
                    && Path.GetFullPath(picked) != Path.GetFullPath(local))
                    try { File.Copy(picked, cp?.CachePath() ?? local, overwrite: true); } catch { }
            }
        }
        else
        {
            progress.Report(new AssembleProgress(AssembleStep.PackInstall, "该整合包已安装过，直接进服"));
        }

        // 4) 重扫实例列表并取实例（刚装完可能尚未入册，找不到就用 new McInstance 兜底）
        // v48.6：「选择本机实例」绑定时直接用所选实例——虚拟 instDir 目录从不存在，
        // 按 instName 兜底会 new 出不存在的实例导致「未找到实例」启动失败
        await Task.Run(() =>
            ModInstanceList.mcInstanceListLoader.WaitForExit(GetMcFolder(), isForceRestart: true))
            .ConfigureAwait(true);   // v50.6.2：重扫移后台（UI 冻结根治）
        var inst = boundByPickerInst
            ?? ModInstanceList.mcInstanceList.Values.SelectMany(v => v)
                .FirstOrDefault(i => i is not null && string.Equals(
                    i.PathInstance.TrimEnd('\\', '/'), instDir.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
            ?? new McInstance(instName);

        progress.Report(new AssembleProgress(AssembleStep.Done,
            packName.Length > 0 ? packName : inst.Name));
        ModBase.RunInUi(() =>
        {
            var okLaunch = ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions
            {
                ServerIp = info.Address,
                instance = inst,
            });
            HintService.Hint(okLaunch
                ? "整合包客户端已启动，正在进入房间"
                : Lang.Text("Minecraft.Launch.Error.LaunchFailed"),
                okLaunch ? HintType.Info : HintType.Error);
        });
        return new AssembleResult(instName, instDir, 0, 0, Array.Empty<string>(), Reused: true,
            PackName: packName);
    }

        // v50.2：实例名短键（只取 sha1 前 7 字符，兼容 v50 实例名格式 "mcstudio-pack-n207d14"）
        // v50.9.4：IsSafeMrpackIndex（逐文件清单校验）已随清单通道整体移除
        private static string ShortKeyFor(ClientPackInfo? cp)
        {
            if (cp is null) return "";
            if (!string.IsNullOrEmpty(cp.Sha1) && cp.Sha1.Length >= 7) return cp.Sha1[..7];
            if (!string.IsNullOrEmpty(cp.Url))
            {
                var h = ClientPackInfo.Sha1Hex(cp.Url);
                return h.Length >= 7 ? h[..7] : h;
            }
            if (!string.IsNullOrEmpty(cp.Name))
            {
                var h = ClientPackInfo.Sha1Hex(cp.Name);
                return h.Length >= 7 ? h[..7] : h;
            }
            return "";
        }

    /// <summary>
    /// v50.9.3 自动装配兜底（加入端，补齐 v50.8 玩家端安装）：房间清单/客户端包全部不可用时按序尝试——
    /// a) packs.modpack 为 Modrinth 身份（房主 v50.9+ 随房下发）→ 版本接口取主文件直链，下载官方
    ///    mrpack 全量安装（含 overrides，真·同款）；
    /// b) packs.modpack 为 CurseForge 身份 → CF web 直链尝试（302 到 CDN，失败不阻塞）；
    /// c) 合成零文件 mrpack（dependencies = mc + loader）→ 走 PCL 原生 McInstallLoader 安装链
    ///    自动装出匹配版本的基础客户端。fabric 缺 loader 版本时取最新（loader 版本对模组兼容
    ///    影响小）；forge/neo 缺版本无法安全合成 → 放弃（装出坏实例比不装更糟）。
    /// 任一成功返回 (包路径, 实例名)；全部失败返回 null（回落「选择本机实例」）。
    /// </summary>
    private static async Task<(string Path, string InstName)?> AutoAssembleLocalPackAsync(
        RoomInfo info, IProgress<AssembleProgress> progress)
    {
        var root = ClientPackInfo.ClientPackRoot();
        Directory.CreateDirectory(root);

        // a/b) 房间包身份自动下载（v50.8 玩家端安装）
        var mp = info.Packs?.Modpack;
        if (mp is not null && mp.Vid.Length > 0 && mp.Pid.Length > 0)
        {
            var key = ClientPackInfo.Sha1Hex(mp.Pid + "|" + mp.Vid);
            if (mp.Src == "modrinth")
            {
                try
                {
                    progress.Report(new AssembleProgress(AssembleStep.PackDownload,
                        $"自动匹配房主整合包《{(mp.Name.Length > 0 ? mp.Name : mp.Pid)}》…"));
                    var ver = await McSources.GetJsonAsync(
                        $"{McSources.ModrinthApi}/version/{Uri.EscapeDataString(mp.Vid)}").ConfigureAwait(true);
                    if (ver["files"] is not JsonArray fs || fs.Count == 0)
                        throw new InvalidOperationException("Modrinth 版本无可用文件");
                    JsonObject pick = null;
                    foreach (var f in fs)
                        if (f is JsonObject o && o["primary"]?.GetValue<bool>() == true) { pick = o; break; }
                    pick ??= (JsonObject)fs[0]!;
                    var url = pick["url"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("Modrinth 版本文件缺少下载地址");
                    var sha1 = pick["hashes"]?["sha1"]?.GetValue<string>();
                    var ext = (pick["filename"]?.GetValue<string>() ?? "")
                        .EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ? ".mrpack" : ".zip";
                    CheckDiskSpace(root, pick["size"]?.GetValue<long>() ?? 0);
                    var target = Path.Combine(root, "packs-" + key[..12] + ext);
                    Exception last = null;
                    for (var attempt = 1; attempt <= 3 && !File.Exists(target); attempt++)
                    {
                        if (attempt > 1)
                        {
                            progress.Report(new AssembleProgress(AssembleStep.PackDownload,
                                $"下载失败，第 {attempt} 次重试…"));
                            await Task.Delay(TimeSpan.FromSeconds(5 * attempt)).ConfigureAwait(true);
                        }
                        try
                        {
                            await McSources.DownloadFileAsync(new[] { url }, target + ".part",
                                CancellationToken.None, sha1,
                                new Progress<(long Done, long Total)>(t => progress.Report(
                                    new AssembleProgress(AssembleStep.PackDownload,
                                        t.Total > 0
                                            ? $"下载整合包 {t.Done / 1048576.0:F0}/{t.Total / 1048576.0:F0} MB"
                                            : $"下载整合包 {t.Done / 1048576.0:F0} MB")))).ConfigureAwait(true);
                            if (File.Exists(target)) File.Delete(target);
                            File.Move(target + ".part", target);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { last = ex; try { File.Delete(target + ".part"); } catch { } }
                    }
                    if (!File.Exists(target))
                        throw new InvalidOperationException("整合包下载失败：" + (last?.Message ?? "未知错误"));
                    return (target, "mcstudio-pack-" + key[..7]);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ModBase.Log("[MCStudio] Modrinth 整合包自动匹配失败：" + ex.Message);
                }
            }
            else if (mp.Src == "curseforge")
            {
                try
                {
                    progress.Report(new AssembleProgress(AssembleStep.PackDownload,
                        $"自动匹配房主整合包（CurseForge #{mp.Pid}）…"));
                    var url = $"https://www.curseforge.com/api/v1/mods/{mp.Pid}/files/{mp.Vid}/download";
                    var target = Path.Combine(root, "packs-" + key[..12] + ".zip");
                    Exception last = null;
                    for (var attempt = 1; attempt <= 3 && !File.Exists(target); attempt++)
                    {
                        if (attempt > 1)
                            await Task.Delay(TimeSpan.FromSeconds(5 * attempt)).ConfigureAwait(true);
                        try
                        {
                            await McSources.DownloadFileAsync(new[] { url }, target + ".part",
                                CancellationToken.None, null, null).ConfigureAwait(true);
                            if (File.Exists(target)) File.Delete(target);
                            File.Move(target + ".part", target);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { last = ex; try { File.Delete(target + ".part"); } catch { } }
                    }
                    if (!File.Exists(target))
                        throw new InvalidOperationException("整合包下载失败：" + (last?.Message ?? "未知错误"));
                    return (target, "mcstudio-pack-" + key[..7]);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ModBase.Log("[MCStudio] CurseForge 整合包自动匹配失败：" + ex.Message);
                }
            }
        }

        // c) 合成零文件 mrpack → 基础客户端（vanilla + loader，PCL 原生安装链）
        var (lk, lv) = info.Loader.Contains(':')
            ? (info.Loader[..info.Loader.IndexOf(':')],
               info.Loader[(info.Loader.IndexOf(':') + 1)..])
            : (info.Loader, "");
        var deps = new JsonObject { ["minecraft"] = info.Mc };
        var loaderText = "";
        if (lk == "fabric")
        {
            var fabricVer = lv;
            if (string.IsNullOrEmpty(fabricVer))
            {
                try { fabricVer = (await McSources.GetFabricVersionsAsync().ConfigureAwait(true)).Loader; }
                catch (Exception ex)
                {
                    ModBase.Log("[MCStudio] 获取 Fabric 最新版失败，放弃合成基础客户端：" + ex.Message);
                    return null;
                }
            }
            deps["fabric-loader"] = fabricVer;
            loaderText = " · Fabric " + fabricVer;
        }
        else if ((lk == "forge" || lk == "neoforge") && lv.Length > 0)
        {
            deps[lk] = lv;
            loaderText = " · " + lk + " " + lv;
        }
        else if (lk != "vanilla")
        {
            return null;    // forge/neo 缺 loader 版本：无法安全合成
        }
        var nometaKey = ClientPackInfo.Sha1Hex($"nometa:{info.Mc}:{info.Loader}");
        progress.Report(new AssembleProgress(AssembleStep.PackDownload,
            $"本机无匹配实例，正在自动安装基础客户端：Minecraft {info.Mc}{loaderText}" +
            "（不含房主模组：房主用整合包开房时，朋友端将自动安装同款整合包）"));
        var mrpack = Path.Combine(root, "index-nometa-" + nometaKey[..12] + ".mrpack");
        if (!File.Exists(mrpack))
        {
            var index = new JsonObject
            {
                ["formatVersion"] = 1,
                ["game"] = "minecraft",
                ["name"] = "MCStudio 基础客户端 " + info.Mc,
                ["versionId"] = "nometa",
                ["files"] = new JsonArray(),
                ["dependencies"] = deps,
            };
            using var fs2 = File.Create(mrpack);
            using var zip = new ZipArchive(fs2, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("modrinth.index.json");
            using var w = new StreamWriter(entry.Open());
            w.Write(index.ToJsonString());
        }
        return (mrpack, "mcstudio-pack-nometa-" + nometaKey[..7]);
    }

    /// <summary>
    /// 「选择本机实例」绑定（v46）：无包可用时列出与房间版本/加载器匹配的本机实例，选中即写
    /// .mcstudio-pack 标记（此后同包秒进）并直接用该实例进服；取消返回 null。全程 RunInUiWait，UI 线程安全。
    /// </summary>
    /// v50.6.2：扫描/候选构造（重扫 + 逐实例版本解析）移入后台线程；仅弹窗在 UI 线程。
    private static async Task<McInstance?> PickLocalInstanceForJoinAsync(RoomInfo info, ClientPackInfo? cp,
        string markerKey, string instDir, IProgress<AssembleProgress> progress)
    {
        progress.Report(new AssembleProgress(AssembleStep.PackDownload,
            "未找到对应客户端版本，正在列出本机实例…"));
        var loader = ModInstanceList.mcInstanceListLoader;
        if (loader.State == ModBase.LoadState.Waiting && !string.IsNullOrEmpty(ModFolder.mcFolderSelected))
            ModLoader.LoaderFolderRun(loader, ModFolder.mcFolderSelected,
                ModLoader.LoaderFolderRunType.ForceRun, 1, "versions\\", true);
        // v50.6.2：重扫 + 逐实例 ResolveInstanceHostTarget（读版本 json）是重活——整体后台执行
        var candidates = await Task.Run(() =>
        {
            if (loader.State != ModBase.LoadState.Finished)
                loader.WaitForExit(GetMcFolder(), isForceRestart: true);
            var list = new List<McInstance>();
            try
            {
                foreach (var inst in ModInstanceList.mcInstanceList.Values.SelectMany(v => v))
                {
                    if (inst is null) continue;
                    var (imc, ilo) = ResolveInstanceHostTarget(inst);
                    if (imc == info.Mc && ilo == info.Loader) list.Add(inst);
                }
            }
            catch { /* 列表未就绪：按空处理 */ }
            return list;
        }).ConfigureAwait(true);
        if (candidates.Count == 0)
            throw new InvalidOperationException(
                "本机没有与该服务器版本匹配的实例，且该房间未提供可下载的客户端整合包，自动装配也未成功，无法加入。\n" +
                "请先在 PCL 中安装对应版本的客户端（或导入整合包），或让房主提供整合包下载链接。\n" +
                "若房主已导入整合包/模组：请让房主更新 PClonline 至 v50.9.4 或以上版本并重新开房" +
                "（旧版本房主不会下发包身份，朋友端无从自动装配）。");

        // 实例目录与 instDir 相同的（此前绑定过的）排最前
        candidates = candidates
            .OrderBy(v => string.Equals(v.PathInstance.TrimEnd('\\', '/'), instDir.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int? idx = ModBase.RunInUiWait(() =>
        {
            var options = candidates
                .Select(v => (IMyRadio)new MyRadioBox { Text = $"{v.Name}（{info.Mc} / {info.Loader}）" })
                .ToList();
            return ModMain.MyMsgBoxSelect(options,
                "选择本机实例加入服务器（请确保实例内容与服务器整合包一致，选错可能进服崩溃；删除实例目录或 .mcstudio-pack 标记可解除绑定）",
                button1: "绑定并进入", button2: "取消");
        });
        if (idx is null)
        {
            HintService.Hint("已取消：未选择客户端实例，本次不加入", HintType.Warning);
            return null;
        }
        var chosen = candidates[idx.Value];
        try
        {
            Directory.CreateDirectory(chosen.PathInstance);
            File.WriteAllText(Path.Combine(chosen.PathInstance, ".mcstudio-pack"), markerKey);
        }
        catch (Exception ex)
        {
            HintService.Hint("绑定标记写入失败，本次仍可进入，下次将重新检查：" + ex.Message, HintType.Warning);
        }
        progress.Report(new AssembleProgress(AssembleStep.PackInstall,
            $"已绑定本机实例「{chosen.Name}」，直接进服"));
        return chosen;
    }

    /// <summary>房主填写的客户端包链接预检（v46）：可达性 + 大小 + 疑似网页警告；HEAD 失败回落 GET Range。</summary>
    public static async Task<(long Size, string? Warning)> ValidateClientPackUrlAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        long size = 0;
        string? warning = null;
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await http.SendAsync(head).ConfigureAwait(true);
            if (resp.IsSuccessStatusCode)
            {
                size = resp.Content.Headers.ContentLength ?? 0;
                var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (ct.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                    warning = "该链接返回的是网页而非文件，朋友下载会失败；请复制文件的“直接下载地址”";
                return (size, warning);
            }
        }
        catch { /* 落到 GET Range */ }
        using var get = new HttpRequestMessage(HttpMethod.Get, url);
        get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var gresp = await http.SendAsync(get).ConfigureAwait(true);
        if (!gresp.IsSuccessStatusCode && (int)gresp.StatusCode != 206)
            throw new InvalidOperationException("链接无法访问，请检查是否为公开可下载的文件直链");
        size = gresp.Content.Headers.ContentRange?.Length ?? gresp.Content.Headers.ContentLength ?? 0;
        return (size, warning);
    }

    /// <summary>磁盘剩余空间检查（需约 size×2.5：zip 缓存 + 解压展开 + 客户端本体）。</summary>
    private static void CheckDiskSpace(string dir, long needBytes)
    {
        try
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(dir));
            if (drive is null) return;
            var di = new DriveInfo(drive);
            var required = needBytes * 5 / 2;
            if (di.AvailableFreeSpace < required)
                throw new InvalidOperationException(
                    $"磁盘空间不足：下载并安装客户端包约需 {required / 1048576.0:F0}MB，" +
                    $"{drive} 剩余 {di.AvailableFreeSpace / 1048576.0:F0}MB");
        }
        catch (InvalidOperationException) { throw; }
        catch { /* 探测失败不阻塞 */ }
    }

    // ---------------------------------------------------------------- 好友房间

    /// <summary>查询好友所开与所在的房间；非好友或未登录时抛出。</summary>
    public static async Task<(IReadOnlyList<RoomBrief> Hosted, IReadOnlyList<RoomBrief> Joined)> GetFriendRoomsAsync(
        string email)
    {
        if (_me is null) throw new InvalidOperationException("未登录");
        var api = TryGetApi() ?? throw new InvalidOperationException("api not configured");
        return await api.FriendRoomsAsync(_me.Token, email).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- 一键开房

    private static HostRoomManager? _host;
    private static P2pTunnel.HostService? _p2pHost;
    /// <summary>应用退出钩子是否已注册（v45.2：退出时同步关房，防遗孤 java 锁世界目录）。</summary>
    private static bool _hostExitHook;

    /// <summary>当前是否已有运行中/启动中的房间。</summary>
    public static bool IsHosting => _host is not null;

    // ---------------------------------------------------------------- 虚拟局域网联机（v48）

    private static LanHostSession? _lanSession;
    private static LanSniffer? _lanSniffer;
    private static SemaphoreSlim? _lanGate;
    private static bool _sawMulticast;

    /// <summary>LAN 桥接会话就绪后的房间码（未就绪为 null）。</summary>
    public static string? LanRoomCode => _lanSession?.RoomCode;

    private static string PickLanLogFile(McInstance inst)
    {
        // 版本隔离实例的 logs 在实例目录内；全局模式回落 mcFolder/logs
        var candidates = new[]
        {
            Path.Combine(inst.PathInstance, "logs", "latest.log"),
            Path.Combine(GetMcFolder(), "logs", "latest.log"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static IReadOnlyList<ModEntry> ReadInstanceMods(McInstance inst)
    {
        try
        {
            var modsDir = Path.Combine(inst.PathInstance, "mods");
            if (!Directory.Exists(modsDir)) return Array.Empty<ModEntry>();
            return Directory.EnumerateFiles(modsDir, "*.jar")
                .Select(Path.GetFileName)
                .Where(f => !string.IsNullOrEmpty(f))
                .Select(f => new ModEntry(f!, null, f!))
                .ToList();
        }
        catch { return Array.Empty<ModEntry>(); }
    }

    /// <summary>v50.9.5：frpc 自愈——分发包自带 mcstudio\frpc\frpc.exe，但玩家只拷 exe 升级时
    /// 该文件缺失，LAN 桥接/开房会崩在 Process.Start（报错晦涩）。frpc.exe 已内嵌进主程序资源
    /// （Resources/frpc.exe，与 lan-offline-agent 同模式），缺失时自动释放到默认便携路径。
    /// host.json 自定义 frpcExe 指向别处时不代管（保持显式配置语义）。</summary>
    public static void EnsureFrpcExtracted()
    {
        try
        {
            var env = HostEnvironment.Load();
            var defaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcstudio", "frpc", "frpc.exe");
            if (File.Exists(env.FrpcExe) || !string.Equals(env.FrpcExe, defaultPath, StringComparison.OrdinalIgnoreCase))
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(defaultPath)!);
            ModBase.WriteFile(defaultPath, ModBase.GetResourceStream("Resources/frpc.exe"));
            ModBase.Log("[MCStudio] 检测到 frpc.exe 缺失，已从内嵌资源自动释放：" + defaultPath);
        }
        catch (Exception ex)
        {
            ModBase.Log("[MCStudio] frpc.exe 自动释放失败：" + ex.Message);
        }
    }

    /// <summary>虚拟局域网联机：启动选中实例并开始侦测「对局域网开放」的世界。
    /// 侦测/桥接结果经 status 上报（端口变化自动热切换，世界关闭回到等待状态）。
    /// 同步清掉上一次会话（幂等，可反复点）。</summary>
    public static async Task LanLaunchAsync(McInstance inst, IProgress<string> status)
    {
        var api = TryGetApi() ?? throw new InvalidOperationException("api not configured");
        if (_me is null)
            throw new InvalidOperationException("虚拟局域网联机需要先登录账号（房间绑定开房人与 P2P 信令）");
        // v49.2：已在运行的游戏未注入离线放行 agent（MC 26.2 起「对局域网开放」对加入者
        // 强制正版验证，旧进程开的房间会拒绝离线好友），必须重启游戏走注入流程
        if (ModLaunch.mcLaunchProcess is { HasExited: false })
            throw new InvalidOperationException("检测到游戏正在运行：请先完全退出当前游戏，再点「启动游戏并开始联机」（旧进程未注入离线放行组件，直接开房会拒绝离线好友加入）");
        await StopLanAsync().ConfigureAwait(true);

        var env = HostEnvironment.Load();
        EnsureFrpcExtracted();   // v50.9.5：frpc.exe 缺失自愈（只拷 exe 升级的机器）
        // v50.9.7：非管理员提示（管理员下静默完成防火墙放行/Defender 排除，不重复弹）
        StudioFirewall.EnsureAdminInteractive("虚拟局域网联机");
        var mods = ReadInstanceMods(inst);
        var (mc, loader) = ResolveInstanceHostTarget(inst);
        var logFile = PickLanLogFile(inst);
        var gate = _lanGate = new SemaphoreSlim(1, 1);

        LanHostSession session = null!;
        var sniffer = new LanSniffer(logFile,
            () => ModLaunch.mcLaunchProcess is { HasExited: false } lp ? lp.Id : null);
        sniffer.Trace += t =>
        {
            // 高频组播包内容不上屏也不进日志（1.5s 一条会刷爆日志）；其余侦测状态写 PCL 日志
            // （v50.9：玩家远程反馈时启动日志里可直接看到侦测链路，不再依赖口述截图）并上屏自查
            if (t.StartsWith("收到组播包"))
            {
                if (!_sawMulticast)
                {
                    _sawMulticast = true;
                    status.Report("已收到游戏局域网公告，解析中…");
                }
                return;
            }
            ModBase.Log("[Lan] " + t, ModBase.LogLevel.Debug);
            status.Report(t);
        };
        sniffer.Detected += world =>
        {
            _ = Task.Run(async () =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    ModBase.Log("[Lan] Detected：" + (world is null ? "null（世界关闭）" : $"端口 {world.Port}，motd「{world.Motd}」"), ModBase.LogLevel.Debug);
                    _sawMulticast = true;   // 触发过 Detected 说明侦测链路已工作
                    if (world is null)
                    {
                        // v50.6.1 世界关闭/游戏退出 = 注销房间（好友立即不可加入，房间码作废）；
                        // 会话保留，玩家重新对局域网开放时自动开新房间（新房间码）
                        status.Report("⚠ 世界已关闭（检测到游戏退出或端口停止广播），房间已注销。" +
                                      "重新点「启动游戏并开始联机」或重新对局域网开放将自动开新房间");
                        if (session is not null)
                            _ = Task.Run(() => session.DropRoomAsync());
                        return;
                    }
                    var motd = string.IsNullOrEmpty(world.Motd) ? "未命名" : world.Motd;
                    status.Report($"已捕获世界《{motd}》· 端口 {world.Port}，正在建立虚拟局域网…");
                    session ??= new LanHostSession(api, env.FrpcExe, env.FrpsHost, env.FrpsPort, env.Token);
                    // v50.9.13：写回静态字段——此前只改了闭包局部变量，_lanSession 恒为 null，
                    // 导致 LanRoomCode 永远为 null（复制房间码弹「尚未生成」、邀请自动复制
                    // 静默失败、「结束联机」清理从未真正执行，房间只能等云端 TTL 兜底回收）
                    _lanSession = session;
                    // v50.9.13：桥接阶段进度（建立隧道/注册房间/端口切换…）转发到状态行
                    session.Progress += p => status.Report(p);
                    // v50.6.9：附带房主实例 mods 目录——精确清单生成来源（sha1 反查 Modrinth）
                    // v50.7：再传实例根目录——resourcepacks/shaderpacks/datapacks 一并进清单（全类型对齐）
                    // v50.8：包身份随房下发——玩家端据此用 PCL 原生管线装同款包
                    var packs = await HostPackCollector.CollectOrNullAsync(inst.PathInstance)
                        .ConfigureAwait(false);
                    // v50.9.4：不再传 modsDir/clientInstanceDir（逐文件清单通道已移除）
                    await session.BridgeAsync(world, mc, loader, mods, _me!.Token,
                        CancellationToken.None, packs).ConfigureAwait(false);
                    status.Report($"✅ 虚拟局域网就绪：房间码 {session.RoomCode} · 世界《{motd}》" +
                                  "（把房间码发给好友一键加入）");
                }
                catch (Exception ex)
                {
                    status.Report("❌ 桥接失败：" + ex.Message);
                }
                finally
                {
                    gate.Release();
                }
            });
        };
        _lanSniffer = sniffer;
        // v50.9.13：不再在此处赋值——此刻会话尚未创建（恒 null），真实赋值移至
        // Detected 回调里创建会话时同步写回（见上）；_lanSession 的清理统一由 StopLanAsync 负责

        // 启动游戏（不带 ServerIp：进单人世界）并开始侦测
        ModBase.RunInUi(() =>
        {
            var ok = ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions { instance = inst, StudioLan = true });
            HintService.Hint(ok ? "游戏已启动：请打开要联机的存档，然后按 ESC 点「对局域网开放」"
                                : Lang.Text("Minecraft.Launch.Error.LaunchFailed"),
                ok ? HintType.Info : HintType.Error);
        });
        sniffer.Start();
        status.Report("① 游戏已启动 → 请打开要联机的存档");
        status.Report("② 打开存档后按 ESC → 点「对局域网开放」→ 回到启动器等待房间码");
    }

    /// <summary>结束虚拟局域网联机（停侦测 + 关隧道/房间/P2P）。幂等。</summary>
    public static async Task StopLanAsync()
    {        try { _lanSniffer?.Dispose(); } catch { }
        _lanSniffer = null;
        try { if (_lanSession is not null) await _lanSession.StopAsync().ConfigureAwait(false); } catch { }
        try { _lanSession?.Dispose(); } catch { }
        _lanSession = null;
        try { _lanGate?.Dispose(); } catch { }
        _lanGate = null;
    }

    /// <summary>
    /// 扫描历史装配实例（v48.4）：`mcstudio-` 开头但非 `mcstudio-pack-` 新命名、且无
    /// .mcstudio-pack 标记的实例——旧版本按房间码命名时代（换房间码即新建）的遗留，
    /// 新流程无法复用，可安全清理。返回 (实例名列表, 目录列表, 总字节数)。
    /// </summary>
    public static (List<string> Names, List<string> Dirs, long Bytes) ScanLegacyPackInstances()
    {
        var names = new List<string>();
        var dirs = new List<string>();
        long bytes = 0;
        try
        {
            foreach (var inst in ModInstanceList.mcInstanceList.Values.SelectMany(v => v))
            {
                if (inst is null) continue;
                var name = inst.Name ?? "";
                if (!name.StartsWith("mcstudio-", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("mcstudio-pack-", StringComparison.OrdinalIgnoreCase)) continue;
                var dir = inst.PathInstance;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                if (File.Exists(Path.Combine(dir, ".mcstudio-pack"))) continue;   // 新流程实例：保留
                long size = 0;
                try
                {
                    size = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                }
                catch { }
                names.Add(name);
                dirs.Add(dir);
                bytes += size;
            }
        }
        catch { }
        return (names, dirs, bytes);
    }

    /// <summary>删除指定实例目录（运行中被锁的自动跳过），完成后重扫实例列表。
    /// 返回 (成功数, 失败实例名列表)。</summary>
    public static async Task<(int Deleted, List<string> Failed)> DeleteInstancesAsync(
        IEnumerable<string> dirs, CancellationToken ct = default)
    {
        int deleted = 0;
        var failed = new List<string>();
        foreach (var d in dirs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // v50.6.2：目录删除（可能含大量文件）也移出 UI 线程
                await Task.Run(() => { if (Directory.Exists(d)) Directory.Delete(d, true); },
                    ct).ConfigureAwait(true);
                deleted++;
            }
            catch
            {
                failed.Add(Path.GetFileName(d.TrimEnd('\\', '/')));   // 运行中/被锁：跳过
            }
        }
        // v50.6.2：重扫移后台（UI 冻结根治）
        await Task.Run(() =>
            ModInstanceList.mcInstanceListLoader.WaitForExit(GetMcFolder(), isForceRestart: true))
            .ConfigureAwait(true);
        return (deleted, failed);
    }

    /// <summary>一键开房（下载服务端 → 启动 → 开隧道 → 注册房间码）。
    /// 已登录时自动把登录令牌作为 owner_token 传入，云端绑定开房人；同时启动 P2P 直连服务（中转始终保留兜底）。</summary>
    public static async Task<HostedRoom> HostStartAsync(HostOptions options, IProgress<HostProgress> progress)
    {
        if (_host is not null) throw new InvalidOperationException("room already running");
        var api = TryGetApi() ?? throw new InvalidOperationException("api not configured");
        var env = HostEnvironment.Load();
        EnsureFrpcExtracted();   // v50.9.5：frpc.exe 缺失自愈
        var effective = _me is not null && string.IsNullOrEmpty(options.OwnerToken)
            ? options with { OwnerToken = _me.Token }
            : options;
        _host = new HostRoomManager(api, env.FrpcExe, env.FrpsHost, env.FrpsPort, env.Token);
        try
        {
            var room = await _host.StartAsync(effective, progress);
            // v45.2 退出钩子：启动器关闭时同步关房（stop → 超时强杀 → 清 frpc），
            // 否则遗孤 java 会锁住 world/session.lock，导致同一档案之后永远开不了房
            if (!_hostExitHook)
            {
                _hostExitHook = true;
                System.Windows.Application.Current.Exit += (_, _) =>
                {
                    try { _host?.ShutdownSync(10000); } catch { }
                    try { _p2pHost?.Dispose(); } catch { }
                    // v48：虚拟局域网会话同步清理（frpc/侦测器）
                    try { _lanSniffer?.Dispose(); } catch { }
                    try { _lanSession?.Dispose(); } catch { }
                };
            }
            // P2P 直连服务：候选上报 + 双向打洞（与 frp 中转并存，加入端自动择优）
            if (_me is not null)
            {
                _p2pHost?.Dispose();
                var svc = new P2pTunnel.HostService(api, _me.Token, room.Code, room.LocalPort);
                svc.Log += msg => ModBase.Log(msg);
                P2pTunnel.EnsureFirewallRule(options.JavaExe);
                _p2pHost = svc;
            }
            return room;
        }
        catch
        {
            _p2pHost?.Dispose();
            _p2pHost = null;
            _host.Dispose();
            _host = null;
            throw;
        }
    }

    /// <summary>关闭当前房间（停服 + 关隧道 + 释放租约 + 停 P2P 服务）。</summary>
    public static async Task HostStopAsync(IProgress<HostProgress> progress)
    {        if (_host is null) return;
        try
        {
            await _host.StopAsync(progress);
        }
        finally
        {
            _p2pHost?.Dispose();
            _p2pHost = null;
            _host.Dispose();
            _host = null;
        }
    }

    /// <summary>房主本机直连信息（127.0.0.1:本地端口，零延迟不占中转）；房间未运行时抛出。</summary>
    public static RoomInfo HostLocalRoomInfo()
    {
        if (_host is null) throw new InvalidOperationException("房间未在运行");
        return _host.GetLocalRoomInfo();
    }

    /// <summary>房主本地快速通道（v37）：加入的房间码正是本机在开的房间时返回服务端 mods 目录，
    /// 供装配器把模组从本地复制进客户端实例（免网络下载）；否则 null。三个加入入口共用。</summary>
    public static string? TryGetHostLocalModsDir(string code)
    {
        try
        {
            return _host?.TryGetLocalModsDir(code);
        }
        catch (Exception ex)
        {
            ModBase.Log("[Studio] 本地模组源探测失败（回落网络下载）：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 一键加入的收尾：重扫版本列表 → 选中新装配的实例 → 直接走启动链。
    /// 在后台线程调用安全；实例找不到时仅提示（用户可手动在版本列表启动）。
    /// </summary>
    public static void RefreshVersionsAndLaunch(string instanceId)
    {
        ModBase.RunInNewThread(() =>
        {
            // 实例列表重扫可能有竞态（新目录刚写完、扫描器尚未收录），找不到时重试两次
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                // 必须显式传入 MC 文件夹：WaitForExit 不带 input 会以 null 重启加载器，
                // 导致 InitMcInstanceList 的 Path.Combine(null, "versions") 抛 ArgumentNullException
                ModInstanceList.mcInstanceListLoader.WaitForExit(
                    GetMcFolder(), isForceRestart: true);
                object? launchErr = null;
                var launched = ModBase.RunInUiWait(() =>
                {
                    var inst = ModInstanceList.mcInstanceList.Values.SelectMany(v => v)
                        .FirstOrDefault(i => InstanceDirName(i.PathInstance)
                            .Equals(instanceId, StringComparison.OrdinalIgnoreCase));
                    if (inst is null) return false;
                    ModInstanceList.McMcInstanceSelected = inst;
                    States.Game.SelectedInstance = inst.Name;
                    ModMain.frmLaunchLeft.LabVersion.Text = inst.Name;
                    ModMain.frmLaunchLeft.RefreshButtonsUI();
                    try
                    {
                        ModMain.frmLaunchLeft.LaunchButtonClick();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        launchErr = ex;
                        return false;
                    }
                });
                if (launched)
                {
                    HintService.Hint(Lang.Text("Tools.Studio.Join.Launching"), HintType.Info);
                    return;
                }
                if (attempt < 3)
                {
                    Thread.Sleep(3000);
                    continue;
                }
                HintService.Hint(launchErr is Exception e
                    ? $"自动启动失败：{e.Message}，请在版本列表选择 mcstudio-{instanceId} 手动启动"
                    : $"实例 {instanceId} 已生成，请在版本列表选择它启动即可进入房间", HintType.Info);
            }
        }, "StudioJoinLaunch");
    }

    /// <summary>实例目录名：PathInstance 末段（.json 结尾则去扩展名），用于匹配装配产物 mcstudio-{code}。</summary>
    private static string InstanceDirName(string path)
    {
        try
        {
            var name = Path.GetFileName(
                path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                name = name[..^5];
            return name;
        }
        catch
        {
            return path ?? "";
        }
    }

    /// <summary>Java 解析：host.json 环境路径 → PCL 扫描到的兼容 Java → 环境配置。
    /// v50.3：按 MC 版本兼容区间选择（1.17–1.20.4 → 17~21；1.20.5+ → 21），
    /// 升序取最小——修复"MajorVersion >= 21 的第一个"永远选中过新 JDK（25/26）
    /// 导致 modlauncher 启动即静默崩（BootstrapLaunchConsumer.accept）的根因。</summary>
    public static string ResolveJavaExe(string? mc = null)
    {
        var env = HostEnvironment.Load();
        if (File.Exists(env.JavaExe)) return env.JavaExe;   // 用户显式配置优先
        int? required = null;
        const int maxJava = 21;
        if (!string.IsNullOrEmpty(mc))
        {
            if (Version.TryParse(mc, out var v) && v.Major == 1)
                required = v.Minor >= 21 || (v.Minor == 20 && v.Build >= 5) ? 21 : (v.Minor >= 17 ? 17 : 8);
            else
                required = 17;
        }
        try
        {
            var java = JavaService.JavaManager.GetSortedJavaList()
                .Where(j => j.IsEnabled)
                .Select(j => j.Installation)
                .Where(i => i.IsStillAvailable)
                .Where(i => !required.HasValue || (i.MajorVersion >= required.Value && i.MajorVersion <= maxJava))
                .OrderBy(i => i.MajorVersion)   // v50.3：升序——兼容区间内选最小（17 优于 25/26）
                .FirstOrDefault();
            if (java is not null) return java.JavaExePath;
        }
        catch
        {
            // Java 管理器未就绪，回落环境配置
        }
        return env.JavaExe;
    }

    /// <summary>v50.4：MC 服务端要求的 Java 主版本（1.20.5+ → 21；1.17–1.20.4 → 17；更老 → 8；未知 → 17）。</summary>
    private static int RequiredMajorForMc(string? mc)
    {
        if (!string.IsNullOrEmpty(mc) && Version.TryParse(mc, out var v) && v.Major == 1)
        {
            if (v.Minor >= 21 || (v.Minor == 20 && v.Build >= 5)) return 21;
            if (v.Minor >= 17) return 17;
            return 8;
        }
        return 17;
    }

    /// <summary>v50.4：轻量探测 java 主版本（java -version 在 stderr；解析失败返回 null）。</summary>
    private static async Task<int?> ProbeJavaMajorAsync(string javaExe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(javaExe, "-version")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            var err = await proc.StandardError.ReadToEndAsync().ConfigureAwait(true);
            proc.WaitForExit(6000);
            var m = System.Text.RegularExpressions.Regex.Match(err, "version \"(?:1\\.)?(\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out var major) ? major : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// v50.4 所有机型通用：确保拿到与 MC 兼容的 Java。
    /// 1) host.json / 已装列表命中兼容区间 → 直接用；
    /// 2) 没有兼容 Java（只有 JDK 25/26 之类）→ 自动下载 Mojang 官方 runtime
    ///    （java-runtime-gamma=17 / java-runtime-delta=21，Mojang+BMCLAPI 双源、sha1 校验、
    ///    落在 %APPDATA% 下的 .minecraft/runtime 目录，，与官方启动器共用）；
    /// 3) 下载失败回落原选择。返回 javaExe 路径或 null。
    /// </summary>
    public static async Task<string?> ResolveOrInstallJavaAsync(string? mc, Action<string>? progress = null)
    {
        var required = RequiredMajorForMc(mc);
        var exe = ResolveJavaExe(mc);
        if (File.Exists(exe))
        {
            var major = await ProbeJavaMajorAsync(exe).ConfigureAwait(true);
            // 兼容（或探测不出——信任原选择）→ 直接用
            if (major is null || (major >= required && major <= 21)) return exe;
            progress?.Invoke($"检测到 Java {major} 与 MC {mc} 不兼容（需要 Java {required}–21），正在自动获取官方 Java {required}…");
        }
        else
        {
            progress?.Invoke($"未找到兼容的 Java（需要 Java {required}–21），正在自动下载官方运行时…");
        }
        // 自动下载官方 runtime（双源 + sha1 校验；与 MC 启动共用同一下载管线）
        try
        {
            var component = required == 21 ? "java-runtime-delta" : "java-runtime-gamma";
            var javaLoader = ModJava.GetJavaDownloadLoader();
            javaLoader.Start(component, true);
            while (javaLoader.State == ModBase.LoadState.Loading)
            {
                progress?.Invoke($"正在下载官方 Java {required}…（{javaLoader.Progress:P0}）");
                await Task.Delay(300).ConfigureAwait(true);
            }
            if (javaLoader.State != ModBase.LoadState.Finished)
            {
                progress?.Invoke("Java 自动下载未完成，将使用此前检测到的 Java（可能不兼容）");
                return File.Exists(exe) ? exe : null;
            }
            // 下载完成：GetJavaDownloadLoader 完成时已自动重扫 Java 列表
            exe = ResolveJavaExe(mc);
            if (File.Exists(exe))
            {
                progress?.Invoke($"已就绪官方 Java {required}（自动下载完成）");
                return exe;
            }
        }
        catch (Exception ex)
        {
            progress?.Invoke("Java 自动下载失败：" + ex.Message);
        }
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>开房房间名：host-{登录邮箱前缀}，未登录回落 host-{机器名}。</summary>
    public static string HostSlug()
    {
        var me = StudioAccount.Load();
        var seed = me is not null ? me.Email.Split('@')[0] : Environment.MachineName;
        var sb = new System.Text.StringBuilder("host-");
        foreach (var ch in seed.ToLowerInvariant())
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
        var name = sb.ToString();
        return name.Length > 20 ? name[..20] : name;
    }

    /// <summary>本机局域网 IPv4（UDP connect 探测出口网卡，不实际发包）；失败返回 null。</summary>
    public static string? GetLanIPv4()
    {
        try
        {
            using var s = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            return (s.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>开房成功后的附加提示：同一 WiFi 的朋友可局域网直连（满速、不占中转带宽）。</summary>
    public static string LanHint(int localPort)
    {
        var ip = GetLanIPv4();
        return ip is null
            ? ""
            : $"\n局域网直连：{ip}:{localPort}（同一 WiFi 的朋友可在游戏多人连接里直接输入，不限速且不占服务器带宽）";
    }

    // ---------------------------------------------------------------- 防火墙放行（P2P 打洞 UDP 入站）

    private static bool _firewallEnsured;

    /// <summary>
    /// 确保防火墙放行本程序的 UDP 入站（P2P 打洞必需）。幂等：进程内只执行一次，规则已存在则跳过。
    /// 埋点显示 cone×cone 打洞全 timeout 的根因是 Windows 防火墙对新编译 exe 的 UDP 入站静默丢弃；
    /// 管理员运行时 netsh 直接生效，非管理员失败则静默跳过（仅记日志，不影响主流程）。
    /// </summary>
    public static void EnsureFirewallUdpRule()
    {
        if (_firewallEnsured) return;
        _firewallEnsured = true;
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
            const string ruleName = "MCStudio-UDP-In";
            // 幂等：规则已存在则不重复添加（netsh show 找不到规则时 exit 1）
            var (showExit, showOutput) = RunNetsh($"advfirewall firewall show rule name=\"{ruleName}\"");
            if (showExit == 0)
            {
                // v50.6.3：规则存在 ≠ 规则有效——规则绑定创建时的 exe 路径，换目录部署
                // （网盘解压出新文件夹是常态）后旧规则对新 exe 无效，UDP 入站被静默丢弃。
                // 校验 program 是否为当前 exe，不一致则删旧建新（自愈）。
                if (showOutput.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
                    var addCode = RunNetsh(
                        $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow " +
                        $"program=\"{exe}\" protocol=udp enable=yes profile=any").Exit;
                    ModBase.Log(addCode == 0
                        ? $"防火墙 UDP 规则绑定的旧程序路径已失效，已按当前程序重建（{ruleName}）"
                        : $"防火墙规则重建失败（netsh exit {addCode}），可能未以管理员运行；P2P 打洞或回落中转");
                }
                else
                {
                    ModBase.Log("防火墙 UDP 放行规则已存在且匹配当前程序，跳过：" + ruleName);
                }
                return;
            }
            var code = RunNetsh(
                $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow " +
                $"program=\"{exe}\" protocol=udp enable=yes profile=any").Exit;
            ModBase.Log(code == 0
                ? $"已添加防火墙 UDP 入站放行规则（{ruleName}），P2P 打洞可用"
                : $"防火墙放行规则添加失败（netsh exit {code}），可能未以管理员运行；P2P 打洞或回落中转");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "EnsureFirewallUdpRule 异常（忽略）", ModBase.LogLevel.Debug);
        }
    }

    /// <summary>v50.6.3：返回 (退出码, 标准输出)——防火墙规则自校验需要读 Program 行。</summary>
    private static (int Exit, string Output) RunNetsh(string args)
    {
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (proc is null) return (-1, "");
        var output = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(5000))
        {
            try { proc.Kill(); } catch { /* 已退出的竞争忽略 */ }
            return (-1, output);
        }
        return (proc.ExitCode, output);
    }

    // ---------------------------------------------------------------- 好友房间一键加入（主页好友列表与工具页共用）

    /// <summary>
    /// 一键加入房间：P2P 优先、失败回落中转；装配完成后自动选版本并启动，缺模组则提示不启动。
    /// 返回装配结果（null = 加入/装配失败，已 Hint 错误）。
    /// onProgress：行内进度文案回调（主页/工具页房间行用，避免"点了没反应"被当成卡死）。
    /// </summary>
    public static async Task<AssembleResult?> JoinRoomWithFeedbackAsync(string code,
        IProgress<AssembleProgress>? progress = null, MyTextButton? btn = null,
        Action<string>? onProgress = null)
    {
        if (Me is null)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NeedLogin"), HintType.Error);
            return null;
        }
        EnsureFirewallUdpRule();
        // v50.9.7：加入端静默配规则（管理员下生效一次；非管理员不打断加入流程，仅打日志）
        StudioFirewall.EnsureRules(interactive: false);
        // 按钮即进度条：调用方没传进度接收器时，把装配步骤实时写进按钮文字，
        // 避免"点了一键加入像卡死"——每一步都有可见反馈
        if (btn is not null)
        {
            btn.Text = "解析房间…";
            if (progress is null)
            {
                var btnText = new Progress<AssembleProgress>(p =>
                {
                    var label = p.Step switch
                    {
                        AssembleStep.Resolving => string.IsNullOrEmpty(p.Detail) ? "解析房间…"
                            : p.Detail.Contains("打洞") || p.Detail.Contains("IPv6") ? "直连协商…"
                            : p.Detail.Contains("加速通道") ? "建通道…" : "解析房间…",
                        AssembleStep.VersionJson => "生成配置…",
                        AssembleStep.ClientJar => "准备客户端…",
                        AssembleStep.Libraries => $"下载依赖 {p.Detail}",
                        AssembleStep.Mods => "同步模组…",
                        AssembleStep.PackDownload => "下载整合包…",
                        AssembleStep.PackInstall => "安装整合包…",
                        _ => "启动游戏…",
                    };
                    btn.Text = label;
                });
                progress = btnText;
            }
        }
        IProgress<AssembleProgress> merged = progress is null && onProgress is null
            ? new Progress<AssembleProgress>(_ => { })
            : new Progress<AssembleProgress>(p =>
            {
                progress?.Report(p);
                onProgress?.Invoke(FormatAssembleProgress(p));
            });
        try
        {
            if (btn is not null) btn.IsEnabled = false;
            var result = await JoinAsync(code, merged, P2pMode.Auto);
            if (result.Missing.Count > 0)
            {
                var msg = $"有 {result.Missing.Count} 个模组需手动补齐（实例已创建，补齐后手动启动即可）：" +
                          string.Join("、", result.Missing.Take(5)) + (result.Missing.Count > 5 ? " 等" : "");
                HintService.Hint(msg, HintType.Warning);
                onProgress?.Invoke(msg);
                return result;
            }
            if (result.Reused)
            {
                // 复用路径已在 JoinAsync 内直接启动（带 ServerIp 并 Hint 复用提示），
                // 此处不能再走 RefreshVersionsAndLaunch（防双重启动），只同步进度文案
                var launchingText = Lang.Text("Tools.Studio.Join.Launching");
                onProgress?.Invoke(launchingText);
                return result;
            }
            HintService.Hint(Lang.Text("Tools.Studio.Join.Launching"), HintType.Info);
            onProgress?.Invoke(Lang.Text("Tools.Studio.Join.Launching"));
            RefreshVersionsAndLaunch(result.InstanceId);
            return result;
        }
        catch (StudioApiException)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NotFound"), HintType.Error);
            onProgress?.Invoke(Lang.Text("Tools.Studio.Hint.NotFound"));
        }
        catch (NotSupportedException)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.JoinUnsupported"), HintType.Error);
            onProgress?.Invoke(Lang.Text("Tools.Studio.Hint.JoinUnsupported"));
        }
        catch (Exception ex)
        {
            var msg = Lang.Text("Tools.Studio.Hint.JoinAssembleFailed") + "：" + ex.Message;
            HintService.Hint(msg, HintType.Error);
            onProgress?.Invoke(msg);
        }
        finally
        {
            if (btn is not null)
            {
                btn.IsEnabled = true;
                btn.Text = "一键加入";
            }
        }
        return null;
    }

    /// <summary>装配进度的人类可读文案（房间行内进度与页面进度条共用同一套措辞）。</summary>
    public static string FormatAssembleProgress(AssembleProgress p)
    {
        var stepText = p.Step switch
        {
            AssembleStep.Resolving => Lang.Text("Tools.Studio.Join.StepResolving"),
            AssembleStep.VersionJson => Lang.Text("Tools.Studio.Join.StepVersionJson"),
            AssembleStep.ClientJar => Lang.Text("Tools.Studio.Join.StepClientJar"),
            AssembleStep.Libraries => Lang.Text("Tools.Studio.Join.StepLibraries"),
            AssembleStep.Mods => Lang.Text("Tools.Studio.Join.StepMods"),
            AssembleStep.PackDownload => "下载客户端整合包",
            AssembleStep.PackInstall => "安装客户端整合包",
            _ => Lang.Text("Tools.Studio.Join.StepDone"),
        };
        return string.IsNullOrEmpty(p.Detail) ? stepText : $"{stepText} · {p.Detail}";
    }

    /// <summary>好友房间行（主页好友展开区与工具页共用）：房间码 · 版本 · 剩余时间 + 一键加入按钮 + 行内进度。
    /// v50.9.5：joined=true 标注「TA 在的房间」（成员记录）——与「TA 开的房间」区分，避免两个好友
    /// 显示同一房间码时被误解为串房（成员记录来自 join/P2P 上报，退出游戏不清除）。</summary>
    public static UIElement BuildFriendRoomRow(RoomBrief room, bool joined = false)
    {
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 2, 0, 2) };

        if (joined)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "└ 在的房间（TA 加入过此房间）",
                FontSize = 11,
                Opacity = 0.55,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

        var text = new System.Windows.Controls.TextBlock
        {
            Text = $"{room.Code} · {room.Mc} {room.Loader} · {Lang.Text("Tools.Studio.Friends.RemainMinutes")} {Math.Max(0, room.ExpiresIn) / 60}"
                   + (room.HasClientPack ? "" : " · 未提供客户端包（可导入本地整合包加入）"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12,
            Opacity = 0.85,
        };
        System.Windows.Controls.Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        // 行内进度：一键加入要下载客户端与依赖（数百 MB），没有反馈会被当成卡死
        var progress = new System.Windows.Controls.TextBlock
        {
            FontSize = 11,
            Opacity = 0.7,
            Margin = new Thickness(2, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var join = new MyTextButton
        {
            Text = Lang.Text("Tools.Studio.Friends.JoinRoom"),
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        join.Click += async (_, _) =>
        {
            // v46.1：不再按云端 has_clientpack 提前拦截——房主只给了本地包（url 空）时该标记为 false，
            // 但 JoinAsync 决策链本就支持导入本地整合包 / 选择本机实例 / 精确报错，提前拦截反而挡住已导入的包
            progress.Text = FormatAssembleProgress(new AssembleProgress(AssembleStep.Resolving, ""));
            progress.Visibility = Visibility.Visible;
            await JoinRoomWithFeedbackAsync(room.Code, btn: join, onProgress: t =>
            {
                progress.Text = t;
                progress.Visibility = Visibility.Visible;
            });
        };
        System.Windows.Controls.Grid.SetColumn(join, 1);
        grid.Children.Add(join);

        panel.Children.Add(grid);
        panel.Children.Add(progress);
        return panel;
    }
}
