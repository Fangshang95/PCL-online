using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using KcpSharp;

namespace PCL.MCStudio;

/// <summary>连接方式：Auto=直连优先中转兜底，DirectOnly=仅直连，RelayOnly=仅中转（frp）。</summary>
public enum P2pMode
{
    Auto,
    DirectOnly,
    RelayOnly,
}

/// <summary>P2P 路由结果类型。</summary>
public enum P2pRouteKind
{
    /// <summary>IPv6 直连（TCP 直通）。</summary>
    V6,
    /// <summary>IPv4 UDP 打洞 + KCP 隧道。</summary>
    UdpHole,
    /// <summary>v50.6.7 relay-first：frp 中转作为首条上游立即就绪（0 等待进服），
    /// 后台打洞打通后升级为直连（下一次 MC 重连生效）。</summary>
    Relay,
}

/// <summary>
/// 本机回环转发器：把任意 TCP 上游（如 frp 中转地址）包成本地 127.0.0.1 监听。
/// 用途：中转线路也不向游戏暴露公网地址——烤入启动参数的永远是 127.0.0.1，
/// 游戏内服务器列表看不到云端中转地址:端口，杜绝绕过启动器手动直连中转。
/// 监听随进程存活（数量少、空转开销可忽略），进程退出即全部释放。
/// </summary>
public static class LoopbackForwarder
{
    private static readonly List<TcpListener> KeepAlive = new();

    /// <summary>开启转发：返回形如 127.0.0.1:{port} 的本地地址，连接会被泵到 targetHost:targetPort。</summary>
    public static string Open(string targetHost, int targetPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        lock (KeepAlive) KeepAlive.Add(listener);
        _ = Task.Run(() => AcceptLoopAsync(listener, targetHost, targetPort));
        return $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    }

    private static async Task AcceptLoopAsync(TcpListener listener, string host, int port)
    {
        while (true)
        {
            TcpClient mc;
            try { mc = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch { break; }   // 监听被关闭（进程退出）
            _ = Task.Run(() => ServeAsync(mc, host, port));
        }
    }

    private static async Task ServeAsync(TcpClient mc, string host, int port)
    {
        // v50.6.3：上游连接超时 5s × 3 次重试（间隔 2s）——中转链路抖动/僵尸代理被
        // 云端对账清理的窗口期内撑过 MC 30s 连接超时，而不是立即关游戏连接
        // （此前游戏侧表现为「已建立的连接被您的主机上的软件中止」，误导为杀软问题）。
        // 关键事件落 P2pTrace 并转发启动器主日志（LauncherLog）。
        TcpClient? upstream = null;
        Exception? last = null;
        for (var attempt = 1; attempt <= 3 && upstream is null; attempt++)
        {
            try
            {
                var c = new TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await c.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                upstream = c;
            }
            catch (Exception ex)
            {
                last = ex;
                P2pTrace.Log($"[中转] 上游 {host}:{port} 第 {attempt}/3 次连接失败：{ex.Message}");
                P2pTunnel.RaiseLauncherLog($"中转上游 {host}:{port} 连接失败（第 {attempt}/3 次）");
                if (attempt < 3)
                {
                    try { await Task.Delay(2000).ConfigureAwait(false); } catch { }
                }
            }
        }
        if (upstream is null)
        {
            P2pTrace.Log($"[中转] 上游 {host}:{port} 3 次重试全部失败，已关闭游戏连接：{last?.Message}");
            P2pTunnel.RaiseLauncherLog($"中转线路 {host}:{port} 暂不可用（3 次重试失败），游戏连接已断开——请稍后重连或让房主确认房间状态");
            try { mc.Close(); } catch { }
            return;
        }
        try
        {
            using var mcSocket = mc;
            using var up = upstream;
            var upStream = up.GetStream();
            var mcStream = mcSocket.GetStream();
            var t1 = PumpAsync(mcStream, upStream);
            var t2 = PumpAsync(upStream, mcStream);
            await Task.WhenAny(t1, t2).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            P2pTrace.Log($"[中转] 泵异常 {host}:{port}：{ex.Message}");
            try { mc.Close(); } catch { }
        }
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[65536];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                await to.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
        }
        catch { }
        finally
        {
            try { to.Close(); } catch { }
        }
    }
}

/// <summary>加入端 P2P 尝试结果：LocalAddress 为 MC 客户端应连接的本地回环地址；null 表示未打通（走中转）。</summary>
public sealed record P2pAttempt(P2pRouteKind Kind, string LocalAddress);

/// <summary>
/// P2P 直连引擎（三级降级链：IPv6 直连 → IPv4 UDP 打洞 + KCP 隧道 → frp 中转兜底）。
/// 云端只做信令（候选交换）与 STUN-lite（回显公网映射），数据面全程点对点。
/// </summary>
public static class P2pTunnel
{
    private const int ConvId = 0x4D435050; // "MCPP"
    private const string MagicPrefix = "MCP2P|";

    /// <summary>v51 会话结束哨兵：加入端 MC 客户端断开后，用它作为候选 nat 上报（v4 置空），
    /// 房主端主循环轮询读到即拆除该路直连。原因：KCP 无 wire 级 FIN（KcpSharp 的
    /// SetTransportClosed 仅本地生效），房主端无法感知对端离开；不拆则其 MC 服务器连接
    /// 变成僵尸，对方重连时会话复用同一条流 → 客户端 Netty 报
    /// "Pipeline has no inbound protocol configured"。纯转发字段（nat 自由字符串），旧端不解析。</summary>
    internal const string NatSessionClosed = "closed";

    /// <summary>v50 M6：房间级握手 magic。secret 非空时以 HMAC-SHA256(secret, 房间码)
    /// 派生（双方从信令各拿到同一 secret，无需预先共享），空则回落旧静态 magic（旧端兼容）。
    /// 房间码统一大写——加入端可能传原始大小写，两侧不统一会导致派生值不一致、打洞静默失败。</summary>
    private static string RoomMagic(string code, string secret)
    {
        var norm = code.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(secret)) return MagicPrefix + norm;
        using var h = new HMACSHA256(Encoding.ASCII.GetBytes(secret));
        var mac = Convert.ToHexString(h.ComputeHash(Encoding.ASCII.GetBytes(MagicPrefix + norm)));
        return MagicPrefix + mac[..16];
    }

    /// <summary>P2P 会话时长上限（P0-G1：原 180min 硬超时会长局被切；默认 8h，
    /// 环境变量 MCSTUDIO_SESSIONHOURS 可覆盖）。</summary>
    private static readonly TimeSpan SessionTimeout = LoadSessionTimeout();

    /// <summary>P2P 会话结束事件（reason 区分"游戏客户端断开 / 时长上限 / 异常断开"；
    /// UI 可订阅用于提示，P2-G2 keep-alive 依赖此处的分类数据）。</summary>
    public static event Action<string>? SessionEnded;

    /// <summary>v50.6.3：隧道关键事件转发到启动器主日志（主程序侧订阅 → ModBase.Log）。
    /// 此前隧道层日志只在 P2pTrace 独立文件里，主日志零痕迹，排障必须两头翻——消除该盲区。</summary>
    public static event Action<string>? LauncherLog;

    public static void RaiseLauncherLog(string message)
    {
        try { LauncherLog?.Invoke(message); } catch { /* 订阅方异常不外溢 */ }
    }

    private static void RaiseSession(string reason)
    {
        try { SessionEnded?.Invoke(reason); } catch { /* 订阅方异常不外溢 */ }
    }

    private static TimeSpan LoadSessionTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("MCSTUDIO_SESSIONHOURS");
        var hours = double.TryParse(raw, out var h) && h > 0 && h <= 24 ? h : 8;
        return TimeSpan.FromHours(hours);
    }

    /// <summary>云机 STUN-lite 端口（默认 8802/8803，可用环境变量 MCSTUDIO_STUNPORTS 覆盖，如本地测试）。</summary>
    private static readonly int[] StunPorts =
        (Environment.GetEnvironmentVariable("MCSTUDIO_STUNPORTS") ?? "8802,8803")
        .Split(',').Select(s => int.TryParse(s.Trim(), out var p) ? p : 0).Where(p => p > 0).ToArray();
    /// <summary>公共 STUN 开关（回环测试置 MCSTUDIO_PUBSTUN=0 跳过，避免公网映射污染端口保持性判定）。</summary>
    private static readonly bool UsePublicStun = Environment.GetEnvironmentVariable("MCSTUDIO_PUBSTUN") != "0";

    /// <summary>B2 打洞 socket 池大小（默认 24，MCSTUDIO_PUNCHSOCKETS 可覆盖 2-24）。
    /// 每个 socket 独立 NAT 映射：cone 侧=24 个稳定可直达端口（对端直达任一即胜），
    /// 对称侧=24 路端口预留在对端扫描窗内，命中面较 v49 双 socket 大幅提升。
    /// 默认由 12 提到上限 24：探测包一次性翻倍，但保活间隔 2.5s→15s 使稳态包量降 6 倍，净负载接近持平。</summary>
    private static readonly int PunchSocketCount = LoadPunchSocketCount();

    private static int LoadPunchSocketCount()
    {
        var raw = Environment.GetEnvironmentVariable("MCSTUDIO_PUNCHSOCKETS");
        return int.TryParse(raw, out var n) && n >= 2 && n <= 24 ? n : 24;
    }

    /// <summary>A1 直连优先预算（默认 15s，MCSTUDIO_DIRECTBUDGETMS 可覆盖 1–60s）：
    /// 加入房间时先在此预算内跑完整直连链（IPv6 → UPnP 映射 → UDP 打洞），成功即零中转；
    /// 超预算才落中继兜底。预算需覆盖「NAT 探测 + 信令 + 喷洒打洞窗口」（B2/B5 的窗口最长 12s），
    /// 过短会把对称组合的打洞窗口提前掐断，反而推高 frp 使用率。</summary>
    private static readonly int DirectBudgetMs = LoadDirectBudget();

    private static int LoadDirectBudget()
    {
        var raw = Environment.GetEnvironmentVariable("MCSTUDIO_DIRECTBUDGETMS");
        return int.TryParse(raw, out var n) && n >= 1000 && n <= 60000 ? n : 15000;
    }

    /// <summary>B2 喷洒目标预算：cone×cone 无需喷洒（72）；涉对称 256（生日悖论约 64% 命中）；
    /// 双对称 512（追高，约 90%）。可用 MCSTUDIO_SPRAY_SYM / MCSTUDIO_SPRAY_DUALSYM 覆盖。</summary>
    private static readonly int SprayTargetsSym = LoadIntEnv("MCSTUDIO_SPRAY_SYM", 256, 72, 1024);
    private static readonly int SprayTargetsDualSym = LoadIntEnv("MCSTUDIO_SPRAY_DUALSYM", 512, 128, 2048);

    /// <summary>B2 彩票口扩窗速率上限（每秒新增随机端口数，默认 150；MCSTUDIO_PUNCHPPS 可覆盖）——
    /// 喷洒规模放大后，靠它把「随机彩票口」的累计增速限住，避免持续泛洪。
    /// 注意：它约束的是彩票口的增长速度（每次刷新新增 ≤ PunchPps×1.5s 个），
    /// 稳态结构化目标的发包量由分片轮转决定（sockets×16/180ms），不在本上限内。</summary>
    private static readonly int PunchPps = LoadIntEnv("MCSTUDIO_PUNCHPPS", 150, 40, 2000);

    /// <summary>B2 彩票口扩窗半径（默认 160，MCSTUDIO_LOTTERYSPAN 可覆盖）。</summary>
    private static readonly int LotterySpan = LoadIntEnv("MCSTUDIO_LOTTERYSPAN", 160, 16, 4096);

    /// <summary>v51 观测回灌观测窗（默认 1200ms，MCSTUDIO_OBSERVE_MS 可覆盖）：涉对称组合下，
    /// 打洞先跑一个短观测窗（confirmOnly 只观测不提交）收集对端真实端点并回灌信令，
    /// 再做定向补打；cone×cone 不走此路（快路径不变）。</summary>
    private static readonly int ObserveWindowMs = LoadIntEnv("MCSTUDIO_OBSERVE_MS", 1200, 300, 5000);

    /// <summary>B1 保活间隔（默认 15s，MCSTUDIO_KEEPALIVE_MS 可覆盖）：RFC 4787 要求 UDP 映射
    /// 空闲存活 ≥2 分钟、推荐 5 分钟，原 2.5s 比需要激进约 50 倍，且持续推进 NAT 分配计数器、
    /// 与端口预测互相打架。</summary>
    private static readonly int NatKeepAliveMs = LoadIntEnv("MCSTUDIO_KEEPALIVE_MS", 15000, 2000, 120000);

    private static int LoadIntEnv(string name, int def, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var n) && n >= min && n <= max ? n : def;
    }

    /// <summary>A4 隧道空闲保留（默认 10 分钟，MCSTUDIO_IDLEMINUTES 可覆盖）：
    /// MC 退出后隧道与 NAT 映射保留该时长（期间 keep-alive 维持映射），保留期内再次进入
    /// 同房间直接复用（一次打洞多次复用）；超时释放。RFC 4787 要求 UDP 映射空闲存活 ≥2 分钟、
    /// 推荐 5 分钟，取 10 分钟留余量。</summary>
    private static readonly TimeSpan IdleRetention = LoadIdleRetention();

    private static TimeSpan LoadIdleRetention()
    {
        var raw = Environment.GetEnvironmentVariable("MCSTUDIO_IDLEMINUTES");
        var minutes = double.TryParse(raw, out var m) && m > 0 && m <= 240 ? m : 10;
        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>A4 进程级复用缓存：房间码 → 存活隧道。保留期内再次进入同一房间直接复用，跳过整条直连链。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JoinerRelay> ReuseCache = new();

    /// <summary>SIO_UDP_CONNRESET：Windows 上对端端口未开时 ICMP 不可达会把后续
    /// ReceiveFrom 变成 10054 异常——打洞期间大量发往「猜测端口」，必须关闭。</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>新建打洞 UDP socket：关闭 CONNRESET + 绑定临时端口。</summary>
    private static Socket NewUdpSocket()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { s.IOControl(SioUdpConnReset, new byte[4], null); } catch { /* 非 Windows 忽略 */ }
        s.Bind(new IPEndPoint(IPAddress.Any, 0));
        return s;
    }

    /// <summary>按池大小新建打洞 socket 池（任一创建失败则返回已建好的部分）。</summary>
    private static List<Socket> CreatePunchPool()
    {
        var list = new List<Socket>(PunchSocketCount);
        for (var i = 0; i < PunchSocketCount; i++)
        {
            try { list.Add(NewUdpSocket()); }
            catch { break; }   // 系统端口耗尽等：用已建好的部分
        }
        if (list.Count == 0) list.Add(NewUdpSocket());   // 兜底重试一次
        return list;
    }

    /// <summary>A3 缓存重连用：优先把单个 socket 绑回缓存的本地端口（源端口不变 → 对端 NAT 映射
    /// 与 KCP 传输目标仍指向本机，命中率更高）；端口被占用/权限不足则退回随机端口池。</summary>
    private static List<Socket> BindOrNewPool(int localPort)
    {
        if (localPort <= 0 || localPort > 65535) return CreatePunchPool();
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { s.IOControl(SioUdpConnReset, new byte[4], null); } catch { /* 非 Windows 忽略 */ }
            s.Bind(new IPEndPoint(IPAddress.Any, localPort));
            return new List<Socket> { s };
        }
        catch
        {
            return CreatePunchPool();   // 端口被占用/权限不足 → 随机端口
        }
    }

    /// <summary>v49.4 KCP 游戏流量调优：更新间隔 100→20ms（MC 交互延迟），
    /// 快速重传 2（丢包快速恢复，移动网关键），关闭 KCP 拥塞控制（MC 协议自带
    /// TCP 流控，双层 CC 在丢包链路上会叠加降速），窗口 256→512 吸收区块突发。</summary>
    private static KcpConversationOptions GameKcpOptions() => new()
    {
        Mtu = 1200,
        StreamMode = true,
        NoDelay = true,
        UpdateInterval = 20,
        FastResend = 2,
        DisableCongestionControl = true,
        SendWindow = 512,
        ReceiveWindow = 512,
        RemoteReceiveWindow = 512,
        SendQueueSize = 256,
        ReceiveQueueSize = 256,
    };

    // ---------------------------------------------------------------- 加入端

    /// <summary>
    /// 加入端 P2P 尝试（v51 A1 直连优先）：先在 DirectBudgetMs 有界预算内跑完整直连链
    /// （IPv6 → UPnP 映射 → UDP 打洞），成功即用直连、完全不建中继（frp 使用率为零）；
    /// 预算内失败才建中继兜底，并后台继续尝试直连（打通后下次 MC 重连切换，中继随即释放）。
    /// A4：同房间在保留期内再次进入直接复用已建隧道，跳过整条直连链。
    /// relayAddr 为空（仅直连模式）时不建中继，失败即返回 null（调用方走 LoopbackForwarder 兜底）。
    /// </summary>
    public static async Task<P2pAttempt?> JoinerTryAsync(StudioApiClient api, string token, string code,
        string? relayAddr, IProgress<string>? log, CancellationToken ct)
    {
        var reuseKey = code.Trim().ToUpperInvariant();
        // A4：保留期内再次进入同一房间 → 复用已建隧道（一次打洞多次复用，跳过整条直连链）
        if (ReuseCache.TryGetValue(reuseKey, out var live) && live.TryReuse(out var reuseAddr, out var reuseKind))
        {
            P2pTrace.Log($"[joiner] 复用房间 {code} 的已建隧道 {reuseAddr}（跳过打洞）");
            log?.Report("复用已建立的直连隧道（跳过打洞）");
            return new P2pAttempt(reuseKind, reuseAddr);
        }

        var relay = new JoinerRelay(api, token, code, log);
        // A1：直连优先——有界预算内先跑完整直连链，成功即零中转、不建任何中继
        P2pUpstream? direct = null;
        using (var directCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            directCts.CancelAfter(DirectBudgetMs);
            try
            {
                direct = await relay.ConnectUpstreamAsync(directCts.Token, "initial").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                P2pTrace.Log($"[joiner] 直连链在 {DirectBudgetMs / 1000.0:0.#}s 预算内未打通，转入中继兜底");
            }
            catch (Exception ex)
            {
                P2pTrace.Log($"[joiner] 直连链异常：{ex.GetType().Name}: {ex.Message}");
            }
        }
        if (direct is not null)
        {
            relay.StartWith(direct);
            P2pTrace.Log($"[joiner] 直连已采用（{direct.Kind}），不建立中继（frp 使用率为零）");
            log?.Report(direct.Kind == P2pRouteKind.V6
                ? "IPv6 直连已建立（零中转）"
                : "P2P 打洞成功，走直连（零中转）");
            return new P2pAttempt(direct.Kind, relay.LocalAddress);
        }

        // 直连链预算内失败：建中继兜底（relayAddr 为空=仅直连模式，不回退）
        if (string.IsNullOrWhiteSpace(relayAddr) || !IPEndPoint.TryParse(relayAddr, out var rep))
        {
            relay.Dispose();
            return null;
        }
        try
        {
            var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            await tcp.ConnectAsync(rep.Address, rep.Port, cts.Token).ConfigureAwait(false);
            relay.StartWith(new RelayUpstream(tcp));
            relay.UpgradeInBackgroundAsync();
            P2pTrace.Log("[joiner] 直连未打通，已用中继兜底（后台继续尝试直连升级）");
            log?.Report("直连未打通，已用中继兜底（后台继续尝试直连升级）");
            return new P2pAttempt(P2pRouteKind.Relay, relay.LocalAddress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Report("中继线路也不可用（" + ex.Message + "），P2P 不可用");
            relay.Dispose();
            return null;
        }
    }

    /// <summary>P2P 上游抽象：稳定本地监听背后的实际隧道（v6 TCP 或 KCP）。</summary>
    private abstract class P2pUpstream : IDisposable
    {
        public abstract P2pRouteKind Kind { get; }
        /// <summary>上游当前是否可用。</summary>
        public abstract bool Alive { get; }
        /// <summary>把 MC 客户端 TCP 双向泵到上游。返回 true=MC 侧先断开（正常结束），
        /// false=上游异常断开（调用方负责释放 mc 并触发重连）。</summary>
        public abstract Task<bool> PumpAsync(TcpClient mc, CancellationToken ct);
        public abstract void Dispose();
    }

    /// <summary>IPv6 直连上游（纯 TCP，无需 keep-alive）。</summary>
    private sealed class V6Upstream : P2pUpstream
    {
        private readonly TcpClient _remote;
        public V6Upstream(TcpClient remote) => _remote = remote;
        public override P2pRouteKind Kind => P2pRouteKind.V6;
        public override bool Alive => _remote.Connected;
        public override async Task<bool> PumpAsync(TcpClient mc, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var t1 = CopyStreamToStream(mc.GetStream(), _remote.GetStream(), cts.Token);
            var t2 = CopyStreamToStream(_remote.GetStream(), mc.GetStream(), cts.Token);
            var finished = await Task.WhenAny(t1, t2).ConfigureAwait(false);
            return finished == t1;   // 本地→远端先结束 = MC 侧断开
        }
        public override void Dispose() => _remote.Dispose();
    }

    /// <summary>v50.6.7 relay-first 中继上游：到云端 frp 中转地址的 TCP（与打洞上游同一抽象，
    /// 可作为 JoinerRelay 的首条上游——游戏 0 等待进服，后台打洞打通后升级直连）。</summary>
    private sealed class RelayUpstream : P2pUpstream
    {
        private readonly TcpClient _remote;
        public RelayUpstream(TcpClient remote) => _remote = remote;
        public override P2pRouteKind Kind => P2pRouteKind.Relay;
        public override bool Alive => _remote.Connected;
        public override async Task<bool> PumpAsync(TcpClient mc, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var t1 = CopyStreamToStream(mc.GetStream(), _remote.GetStream(), cts.Token);
            var t2 = CopyStreamToStream(_remote.GetStream(), mc.GetStream(), cts.Token);
            var finished = await Task.WhenAny(t1, t2).ConfigureAwait(false);
            return finished == t1;
        }
        public override void Dispose() => _remote.Dispose();
    }

    /// <summary>KCP 打洞上游：同 socket 会话 + 15s 裸 magic keep-alive（保持本端 NAT 映射；
    /// 移动 CGNAT UDP 映射超时常见 30-60s，v49 的 20s 偏紧，收窄到 15s；
    /// 对端 KcpSharp 对 conv 不匹配的数据报双层静默丢弃，已实测验证）。</summary>
    private sealed class KcpUpstream : P2pUpstream
    {
        private readonly Socket _udp;
        private readonly IPEndPoint _observed;
        private readonly IKcpTransport<KcpConversation> _transport;
        private readonly KcpConversation _conv;
        private readonly CancellationTokenSource _life = new();
        private volatile bool _dead;

        public KcpUpstream(Socket udp, IPEndPoint observed)
        {
            _udp = udp;
            _observed = observed;
            _transport = KcpSocketTransport.CreateConversation(udp, observed, ConvId, GameKcpOptions());
            _transport.Start();
            _conv = _transport.Connection;
            _ = Task.Run(KeepAliveLoopAsync);
        }

        public override P2pRouteKind Kind => P2pRouteKind.UdpHole;
        public override bool Alive => !_dead;

        private async Task KeepAliveLoopAsync()
        {
            // 裸 magic（"MCP2" 开头，与 KCP conv "MCPP" 不同）；对端 KcpSharp 按会话 ID 静默丢弃，
            // 但足以让两端 NAT 刷新各自的出站映射，防止游戏静默期（加载/挂机）映射过期
            var magic = Encoding.ASCII.GetBytes(MagicPrefix + "KA");
            while (!_life.IsCancellationRequested && !_dead)
            {
                try { _udp.SendTo(magic, _observed); }
                catch { _dead = true; break; }
                try { await Task.Delay(15000, _life.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        public override async Task<bool> PumpAsync(TcpClient mc, CancellationToken ct)
        {
            try
            {
                var t1 = PumpTcpToKcp(mc, _conv, ct);
                var t2 = PumpKcpToTcp(_conv, mc, ct);
                var finished = await Task.WhenAny(t1, t2).ConfigureAwait(false);
                var upstreamSide = finished == t2;   // 上游→MC 泵先结束 = 上游断了
                if (upstreamSide) _dead = true;
                return !upstreamSide;
            }
            catch
            {
                _dead = true;
                return false;
            }
        }

        public override void Dispose()
        {
            _life.Cancel();
            try { _transport.Dispose(); } catch { }
            // v51：隧道改为「单次 MC 会话」语义后拆得更频繁，必须连打洞 socket 一起释放，
            // 否则每次断开都泄漏一个本地 UDP 端口（KcpSocketTransport 不接管 socket 生命周期）
            try { _udp.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 加入端稳定中继（P2-G2）：固定 127.0.0.1 监听地址贯穿整个会话（≤SessionTimeout）；
    /// 隧道断开自动重建（保留 LAN/v6/打洞全链路），MC 客户端断开后监听保持待命，
    /// 用户在 MC 的「重新连接」直接进入，隧道未就绪则先重建再接入。
    /// </summary>
    private sealed class JoinerRelay : IDisposable
    {
        private readonly StudioApiClient _api;
        private readonly string _token;
        private readonly string _code;
        private readonly IProgress<string>? _log;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _reconnectLock = new(1, 1);   // 防止并发重建出多条隧道
        private P2pUpstream? _current;
        // 承载时长统计（本方案核心验收指标）：直连占比越高 = frp 使用率越低
        private long _directCarryMs;
        private long _relayCarryMs;
        private int _carryReported;
        // A2/A4：活跃 MC 泵计数（0=空闲）与最近活动时刻——空闲超 IdleRetention 释放隧道
        private int _activePumps;
        private long _lastActivityMs = Environment.TickCount64;
        // A3 直连端点缓存：最近一次成功的直连，重连时先试缓存端点（跳过整轮探测/信令/打洞）
        private DirectCache? _directCache;

        /// <summary>A3 直连端点缓存：路由类型 + 对端观察地址 + 本端命中端口 + 房间 secret + 记录时刻。</summary>
        private sealed record DirectCache(P2pRouteKind Kind, IPEndPoint Peer, int LocalPort, string Secret, long AtMs);
        /// <summary>缓存有效期：会话内 NAT 映射通常仍有效，超过该时长视为过期，回退完整直连链。</summary>
        private const long DirectCacheTtlMs = 90_000;

        public string LocalAddress { get; }

        public JoinerRelay(StudioApiClient api, string token, string code, IProgress<string>? log)
        {
            _api = api;
            _token = token;
            _code = code;
            _log = log;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            LocalAddress = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        }

        private void Log(string message)
        {
            _log?.Report(message);
            P2pTrace.Log(message);   // v46.2 复盘日志：UI 进度同步落盘
        }

        /// <summary>接入首条上游后启动监听循环、会话总时长控制与空闲回收。</summary>
        public void StartWith(P2pUpstream first)
        {
            _current = first;
            Touch();
            // A4：注册进程级复用缓存——保留期内再次进入同房间直接复用本隧道
            ReuseCache[_code.Trim().ToUpperInvariant()] = this;
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SessionTimeout, _cts.Token).ConfigureAwait(false);
                    RaiseSession($"UDP 直连会话达到时长上限（{SessionTimeout.TotalHours:0.#} 小时）自动关闭");
                    Dispose();
                }
                catch (OperationCanceledException) { }
            });
            _ = Task.Run(IdleWatchAsync);
        }

        /// <summary>刷新最近活动时刻（MC 接入 / 断开 / 复用均调用），重置空闲回收计时。</summary>
        private void Touch() => Interlocked.Exchange(ref _lastActivityMs, Environment.TickCount64);

        /// <summary>A4 空闲回收：MC 退出后隧道与 NAT 映射保留 IdleRetention（期间 keep-alive 维持映射），
        /// 保留期内再次接入即续期；超时释放，避免长期空转占用 frp / 端口。</summary>
        private async Task IdleWatchAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (Volatile.Read(ref _activePumps) > 0) { Touch(); continue; }
                if (Environment.TickCount64 - Interlocked.Read(ref _lastActivityMs)
                    < (long)IdleRetention.TotalMilliseconds) continue;
                Log($"隧道空闲超过 {IdleRetention.TotalMinutes:0.#} 分钟，释放隧道与 NAT 映射（下次进入将重新建立）");
                RaiseSession("P2P 隧道空闲超时自动释放");
                Dispose();
                return;
            }
        }

        /// <summary>A4：保留期内再次进入同房间时复用本隧道。false=不可复用（已释放 / 上游已死）。</summary>
        public bool TryReuse(out string localAddress, out P2pRouteKind kind)
        {
            localAddress = LocalAddress;
            kind = P2pRouteKind.Relay;
            if (_cts.IsCancellationRequested) return false;
            var up = _current;
            // v51：隧道已随上次 MC 会话结束而拆除（_current=null）时，仍复用本 relay 的
            // 稳定本地监听（127.0.0.1 端口不变，MC「重新连接」直接可用）；MC 接入时
            // ServeMcAsync 会自动重建隧道。仅 relay 本身已释放才不可复用。
            if (up is not null && up.Alive) kind = up.Kind;
            Touch();
            return true;
        }

        /// <summary>v51：MC 会话结束 → 通知房主端拆除对应直连路。KCP 无 wire 级 FIN，房主端主循环
        /// 每 1.5s 轮询信令，读到 nat=NatSessionClosed 即取消该路并关闭其 MC 服务器连接。
        /// 后台尽力而为、失败静默——房主端也会因 MC 服务器自身超时兜底清理。</summary>
        private void NotifyHostSessionClosed()
        {
            var api = _api;
            var token = _token;
            var code = _code;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await api.P2pSignalAsync(token, code, "joiner",
                        new P2pCandidates(Array.Empty<string>(), "", NatSessionClosed, 0), cts.Token)
                        .ConfigureAwait(false);
                    P2pTrace.Log($"[joiner] 已通知房主端拆除会话（{code}）");
                }
                catch (Exception ex)
                {
                    P2pTrace.Log($"[joiner] 通知房主端拆除会话失败（忽略）：{ex.Message}");
                }
            }, CancellationToken.None);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient mc;
                try { mc = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { break; }
                _ = Task.Run(() => ServeMcAsync(mc, ct), ct);
            }
        }

        private async Task ServeMcAsync(TcpClient mc, CancellationToken ct)
        {
            // A2：整个 MC 会话期间计为「活跃」，供后台升级判断中继是否空闲可立即释放
            Interlocked.Increment(ref _activePumps);
            Touch();
            try
            {
                var up = _current;
                if (up is null || !up.Alive)
                {
                    Log("P2P 隧道已断开，正在重建…");
                    RaiseLauncherLog("P2P 直连隧道已断开，正在重建…");
                    up = await ReconnectAsync(ct).ConfigureAwait(false);
                    if (up is null)
                    {
                        // v50.6.3：不再 Dispose 整个 relay——保留本地监听 + 60s 周期后台重试，
                        // MC 里「重新连接」始终有救；只有 8h 会话上限才真正关闭
                        Log("本轮重连失败（房间可能已关闭），60s 后自动再试；在 MC 里点「重新连接」即可");
                        RaiseLauncherLog("P2P 隧道重连失败，60 秒后自动重试（MC 里点「重新连接」即可）");
                        RaiseSession("P2P 隧道断开且重连失败（持续重试中）");
                        StartBackgroundRetry();
                        return;
                    }
                    RaiseLauncherLog("P2P 直连隧道已重建");
                }
                Log("游戏客户端已接入隧道");
                var carrySw = Stopwatch.StartNew();
                var mcClosed = await up.PumpAsync(mc, ct).ConfigureAwait(false);
                AccumulateCarry(up.Kind, carrySw.ElapsedMilliseconds);
                // v50.6.7：pump 期间 _current 已被升级/重建为其他上游 → 退役本旧上游即可，
                // MC 重连将直接接入更优线路（中继→直连升级后，旧中继连接由此回收）
                if (!ReferenceEquals(_current, up) && _current is { Alive: true })
                {
                    try { up.Dispose(); } catch { }
                    Log("线路已升级/重建，游戏重连时将使用更优线路");
                    return;
                }
                if (mcClosed)
                {
                    // v51 修复：隧道是「单次 MC 会话」语义——MC 客户端断开即整体拆除。
                    // 旧行为「保持待命、复用隧道」会把下一次 MC 握手注入上一会话遗留的僵尸后端连接
                    // （房主端 MC 服务器连接因 KCP 无 FIN 未关闭），客户端 Netty 报
                    // "Pipeline has no inbound protocol configured" 而连不上服务器。
                    Log("游戏客户端断开，隧道已释放（再次进入会自动重建）");
                    RaiseLauncherLog("游戏客户端断开，P2P 隧道已释放（再次进入会自动重建）");
                    _current = null;
                    _directCache = null;   // 旧路由 socket 已随该路结束，缓存端点作废，避免重连白等 2s
                    try { up.Dispose(); } catch { }
                    NotifyHostSessionClosed();   // 让房主端同步拆除其 KCP 会话与 MC 服务器连接
                }
                else
                {
                    // 上游异常断开：立即后台重建，用户在 MC 里点「重新连接」时通常已就绪
                    _current = null;
                    try { up.Dispose(); } catch { }
                    Log("P2P 隧道异常断开，自动重建中…（重建完成后在 MC 里点「重新连接」即可）");
                    RaiseLauncherLog("P2P 直连线路断开，正在自动重建…");
                    _ = Task.Run(async () =>
                    {
                        var fresh = await ReconnectAsync(_cts.Token).ConfigureAwait(false);
                        if (fresh is null)
                        {
                            Log("隧道重建失败，60s 后自动再试；请稍后在 MC 里点「重新连接」");
                            RaiseLauncherLog("P2P 隧道重建失败，60 秒后自动重试");
                            StartBackgroundRetry();
                        }
                        else
                        {
                            Log("隧道已重建，在 MC 里点「重新连接」即可返回游戏");
                            RaiseLauncherLog("P2P 隧道已重建，可在 MC 里点「重新连接」返回游戏");
                        }
                    }, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Log("隧道会话异常：" + ex.Message);
                RaiseLauncherLog("隧道会话异常：" + ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _activePumps);
                Touch();
                try { mc.Dispose(); } catch { }
            }
        }

        /// <summary>v50.6.3：后台周期重试（60s 间隔，直至成功或 relay 被销毁）。
        /// 成功后 _current 已就绪，MC 里点「重新连接」即直接接入。</summary>
        private void StartBackgroundRetry()
        {
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested && _current is null)
                {
                    try { await Task.Delay(60000, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    Log("后台自动重试中…");
                    var up = await ReconnectAsync(_cts.Token).ConfigureAwait(false);
                    if (up is not null)
                    {
                        Log("隧道已自动重建，在 MC 里点「重新连接」即可返回游戏");
                        RaiseLauncherLog("P2P 隧道已自动重建，可在 MC 里点「重新连接」返回游戏");
                        return;
                    }
                }
            }, CancellationToken.None);
        }

        /// <summary>v50.6.7 relay-first 后台升级：中继已供游戏游玩，后台继续完整打洞流程
        /// （信令→LAN→v6→UDP），打通后把 _current 从中继换成直连——下一次 MC 重连生效，
        /// 不断当前连接（MC 是 TCP 流，跨传输热切换会丢字节，故不热切）。与 ReconnectAsync
        /// 共用锁避免并发开两条路；若期间断线重建已建立直连，本次结果直接丢弃。</summary>
        public void UpgradeInBackgroundAsync()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var up = await ConnectUpstreamAsync(_cts.Token, "upgrade").ConfigureAwait(false);
                    if (up is null)
                    {
                        Log("后台直连尝试未成功（保持中继线路，延迟较高）");
                        return;
                    }
                    await _reconnectLock.WaitAsync(_cts.Token).ConfigureAwait(false);
                    try
                    {
                        if (_current is { Alive: true } && _current is not RelayUpstream)
                        {
                            up.Dispose();   // 竞速胜出的已是直连（断线重建等），丢弃本次结果
                            return;
                        }
                        var prev = _current;
                        _current = up;
                        // A2：中继被直连取代 → 若当前无 MC 在泵（空闲中继），立即释放 frp 连接，
                        //     不再等下一次 MC 泵结束才回收；有 MC 在用时由 ServeMcAsync 收尾释放。
                        if (prev is RelayUpstream && Volatile.Read(ref _activePumps) == 0)
                        {
                            try { prev.Dispose(); } catch { }
                            Log("中继线路已释放（直连已就绪，frp 连接已关闭）");
                        }
                        Log("直连已打通（延迟更低）——在 MC 里断开重连一次即可切换直连线路");
                        RaiseLauncherLog("P2P 直连已打通，MC 里重新连接即可切换直连线路");
                    }
                    finally { _reconnectLock.Release(); }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log("后台直连升级异常：" + ex.Message); }
            }, CancellationToken.None);
        }

        /// <summary>重建隧道（最多 3 次，间隔 3s；复用完整 joiner 流程：信令→LAN→v6→打洞）。
        /// 加锁串行化：后台自动重建与 MC 重连触发的重建不会并发开两条路。</summary>
        private async Task<P2pUpstream?> ReconnectAsync(CancellationToken ct)
        {
            await _reconnectLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 等锁期间别人已重建成功 → 直接复用
                if (_current is { Alive: true }) return _current;
                // A3：先试缓存直连端点——会话内 NAT 映射通常仍有效，命中即跳过整轮探测/信令/打洞
                var cached = await TryCachedDirectAsync(ct).ConfigureAwait(false);
                if (cached is not null)
                {
                    _current = cached;
                    return cached;
                }
                for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
                {
                    if (i > 0)
                    {
                        try { await Task.Delay(3000, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return null; }
                    }
                    try
                    {
                        var up = await ConnectUpstreamAsync(ct, "reconnect").ConfigureAwait(false);
                        if (up is not null)
                        {
                            _current = up;
                            return up;
                        }
                    }
                    catch (OperationCanceledException) { return null; }
                    catch (Exception ex)
                    {
                        Log("重连尝试失败：" + ex.Message);
                    }
                }
                return null;
            }
            finally
            {
                _reconnectLock.Release();
            }
        }

        /// <summary>A3：把成功的直连端点记入缓存（供后续重连跳过整条直连链）。</summary>
        private void CacheDirect(P2pRouteKind kind, IPEndPoint? peer, int localPort, string secret)
        {
            if (peer is null) return;
            _directCache = new DirectCache(kind, peer, localPort, secret, Environment.TickCount64);
        }

        /// <summary>A3 快速重连：向缓存的对端端点重发 magic（≤2s），命中即重建 KCP 隧道，
        /// 跳过 NAT 探测 / 信令交换 / 完整打洞。缓存过期、未命中或异常一律返回 null（回退完整直连链），
        /// 因此本路径只可能省时、不会导致连接失败。对端路由端口变化时缓存自然失效。</summary>
        private async Task<P2pUpstream?> TryCachedDirectAsync(CancellationToken ct)
        {
            var c = _directCache;
            if (c is null) return null;
            if (Environment.TickCount64 - c.AtMs > DirectCacheTtlMs) { _directCache = null; return null; }
            try
            {
                if (c.Kind == P2pRouteKind.V6)
                {
                    var tcp = new TcpClient(AddressFamily.InterNetworkV6);
                    var ok = false;
                    try
                    {
                        using var v6cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        v6cts.CancelAfter(2500);
                        await tcp.ConnectAsync(c.Peer, v6cts.Token).ConfigureAwait(false);
                        ok = true;
                    }
                    finally
                    {
                        if (!ok) { try { tcp.Dispose(); } catch { } }   // 连接失败即释放，异常交由外层 catch 作废缓存
                    }
                    Log($"缓存直连命中（IPv6 {c.Peer}），跳过完整直连链");
                    return new V6Upstream(tcp);
                }
                var pool = BindOrNewPool(c.LocalPort);
                try
                {
                    var magic = RoomMagic(_code, c.Secret);
                    var win = await PunchPoolAsync(pool, new[] { c.Peer }, magic + "|J|", magic + "|", 2000, ct)
                        .ConfigureAwait(false);
                    if (win is null)
                    {
                        Log("缓存直连未命中（对端映射可能已失效），回退完整直连链");
                        _directCache = null;
                        return null;
                    }
                    pool.Remove(win.Socket);
                    Log($"缓存直连命中（UDP {win.Observed}），跳过完整直连链");
                    return new KcpUpstream(win.Socket, win.Observed);
                }
                finally { DisposeExcept(pool, null); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _directCache = null;   // 缓存端点异常（IPv6 连接失败等）→ 作废，下次走完整直连链，避免反复白等
                Log("缓存直连尝试异常：" + ex.Message + "，回退完整直连链");
                return null;
            }
        }

        /// <summary>建立一条到房主的新上游。socket 池在本层创建并兜底释放：
        /// 核心方法成功时会把胜出 socket 从池中移除（所有权移交上游），其余一律在此释放，
        /// 覆盖取消（直连优先的预算到期）与异常路径，杜绝 socket 泄漏。</summary>
        public async Task<P2pUpstream?> ConnectUpstreamAsync(CancellationToken ct, string trigger = "initial")
        {
            var pool = CreatePunchPool();
            try
            {
                return await ConnectUpstreamCoreAsync(pool, ct, trigger).ConfigureAwait(false);
            }
            finally
            {
                DisposeExcept(pool, null);
            }
        }

        /// <summary>直连链主体：NAT 探测 → 信令交换 → LAN → IPv6 → UDP 打洞。
        /// trigger 标注触发来源（initial=首次连接 / reconnect=断线重建 / upgrade=后台直连升级），
        /// 供埋点归因——重连与后台升级的命中率远低于首次连接，混算会掩盖真实瓶颈。</summary>
        private async Task<P2pUpstream?> ConnectUpstreamCoreAsync(List<Socket> pool, CancellationToken ct, string trigger)
        {
            var api = _api;
            var token = _token;
            var code = _code;
            var apiHost = api.ApiHost;
            var v6 = CollectV6();
            var lan = CollectV4Lan();
            P2pTrace.Session("joiner", code, $"候选 v6×{v6.Count} lan×{lan.Count}（{apiHost}）");
            Log($"候选收集：v6×{v6.Count} lan×{lan.Count}");

            var sw = Stopwatch.StartNew();      // 全程计时（埋点用）
            var punchSw = new Stopwatch();      // 打洞阶段计时
            // 1) v49.4 打洞 socket 池：由 ConnectUpstreamAsync 创建并兜底释放（此处仅使用，勿再新建），
            //    每 socket 独立映射，先并发探测 NAT（含每 socket 映射 + 步长）
            var udp = pool[0];
            var punchPort = ((IPEndPoint)udp.LocalEndPoint!).Port;
            P2pTrace.Log($"打洞 socket 池 ×{pool.Count}（首口 {punchPort}），开始 NAT 探测…");
            var probe = await Task.Run(() => ProbeNatPool(pool, apiHost, ct), ct).ConfigureAwait(false);
            var mapped = probe.Mapped;
            var preserving = probe.Preserving;
            var natSelf = preserving ? "cone" : "symmetric";
            var predPorts = probe.PredPorts;   // 兼容字段：本端下一次映射的预测窗口（cone 为空）
            P2pTrace.Log(probe.Samples.Count > 0
                ? $"NAT 样本：[{string.Join(" | ", probe.Samples)}] → 判定 {natSelf}，映射 {mapped}，delta {probe.Delta}"
                : "NAT 样本：无任何 STUN 应答（探测目标全部不可达），按 cone 处理但判定不可信");
            Log(predPorts.Count > 0
                ? $"NAT 探测：{natSelf}（映射 {mapped}，步长 {probe.Delta}，预测窗口 ×{predPorts.Count}）"
                : $"NAT 探测：{natSelf}（映射 {mapped}）");
            if (probe.Maps.Count > 0)
                P2pTrace.Log($"每 socket 映射：[{string.Join(" | ", probe.Maps)}]");
            // v50.6.6：映射保活——探测到开窗全程无流量，CGN UDP 超时 15–30s 会先杀映射
            // B1：分档保活（cone 全池续命 / 对称仅 socket0 + 单目标），避免推进其余 socket 的分配序列
            using var kaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var natKeepalive = Task.Run(() => KeepNatMappingsAsync(pool, apiHost, preserving, kaCts.Token), CancellationToken.None);
            var mapTask = NatPortMapper.MapAsync(udp, ct);   // P1-D：UPnP/PMP/PCP 主动映射（并行，不阻塞信令）
            var upnpPort = 0;
            var upnpLogged = false;
            P2pPeer? host = null;
            var v6Tried = false;   // v50.6.5：声明提前供 Report 闭包读取（归因 v6 路径）

            // 埋点：连接终点上报一次（后台尽力，失败静默）
            // phase=inner：本条只描述「直连尝试」的结果，与会话层的 outer 终局分开统计
            void Report(string result)
            {
                kaCts.Cancel();   // v50.6.6：所有终点（含异常后 Report）统一停保活
                P2pTrace.End("joiner", result,
                    $"总耗时 {sw.ElapsedMilliseconds}ms natSelf={natSelf} natPeer={host?.Nat ?? "unknown"} trigger={trigger}",
                    phase: "inner");
                var payload = new
                {
                    role = "joiner",
                    phase = "inner",
                    trigger,
                    nat_self = natSelf,
                    nat_peer = host?.Nat ?? "unknown",
                    result,
                    punch_ms = punchSw.IsRunning || punchSw.ElapsedMilliseconds > 0 ? (int)punchSw.ElapsedMilliseconds : 0,
                    total_ms = (int)sw.ElapsedMilliseconds,
                    v6_count = v6.Count,
                    v6_tried = v6Tried,
                    lan_count = lan.Count,
                    pred_count = host?.PredPorts?.Count ?? 0,
                    upnp = mapTask.IsCompleted && mapTask.Result > 0 ? 1 : 0,
                    sock = pool.Count,
                    maps = probe.Maps.Count,
                };
                _ = Task.Run(async () =>
                {
                    try { await api.P2pMetricAsync(token, payload, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* 埋点失败不影响主流程 */ }
                }, CancellationToken.None);
            }

            // 2) 轮询：上报候选并拉取房主候选（通用候选先试 v6；定向候选到位才打洞）
            var lastSrvNow = 0.0;
            var lastRttMs = 0.0;   // B7：最近一次信令往返 RTT（用于 punch_at 的半 RTT 补偿）
            var secret = "";   // v50 M6：双方 ≥50 时云端下发，HMAC 化握手 magic
            for (var attempt = 0; attempt < 12 && !ct.IsCancellationRequested; attempt++)
            {
                var sigSw = Stopwatch.StartNew();
                var signal = await api.P2pSignalAsync(token, code, "joiner",
                    new P2pCandidates(v6, mapped, natSelf, 0, lan, punchPort, predPorts, upnpPort,
                        pool.Select(s => ((IPEndPoint)s.LocalEndPoint!).Port).ToList(), probe.MapsStrings, probe.Delta),
                    ct).ConfigureAwait(false);
                sigSw.Stop();
                lastRttMs = sigSw.ElapsedMilliseconds;
                if (signal.Secret.Length > 0) secret = signal.Secret;
                upnpPort = mapTask.IsCompleted ? mapTask.Result : 0;
                if (upnpPort > 0 && !upnpLogged)
                {
                    upnpLogged = true;
                    Log($"路由器端口映射成功（外部 {upnpPort}），已随候选上报");
                }
                lastSrvNow = signal.SrvNow;
                host = signal.Peer;
                if (host is not null)
                {
                    // IPv6 直连尽早尝试（零打洞，host 的 v6 地址与定向路相同）
                    if (!v6Tried && host.V6.Count > 0 && host.Port > 0)
                    {
                        v6Tried = true;
                        Log($"尝试 IPv6 直连（{host.V6.Count} 个候选）…");
                        var v6Attempt = await TryV6DirectAsync(host.V6, host.Port, ct).ConfigureAwait(false);
                        if (v6Attempt is not null)
                        {
                            Log($"IPv6 直连成功：{v6Attempt}");
                            // 通知房主本端已走 v6，免其继续为该路打洞（v4 置空 + 标记）
                            try { await api.P2pSignalAsync(token, code, "joiner",
                                new P2pCandidates(v6, "", "direct-v6", 0), ct).ConfigureAwait(false); } catch { /* 尽力通知 */ }
                            Report("v6");
                            CacheDirect(P2pRouteKind.V6, v6Attempt.Client.RemoteEndPoint as IPEndPoint, 0, secret);
                            return new V6Upstream(v6Attempt);
                        }
                        Log("IPv6 直连未成功，继续 P2P 打洞");
                    }
                    // Targeted=True：host 已为该加入者开出独立打洞路，可开始打洞
                    if (host.Targeted) break;
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            if (host is null)
            {
                Log("房主候选不可用（12 次轮询未拿到房主信令，房主可能未开房主服务或信令异常）");
                Report("no_peer");
                return null;
            }

            // 3) LAN 优先：同网段直连（宿舍/家庭双机 0 打洞；AP 隔离时 1~2s 内失败自然落回公网）
            var magic = RoomMagic(code, secret);
            if (host.Targeted && host.PunchPort > 0)
            {
                foreach (var ip in host.V4Lan ?? (IReadOnlyList<string>)Array.Empty<string>())
                {
                    if (!IPAddress.TryParse(ip, out var lanIp)) continue;
                    var lanEp = new IPEndPoint(lanIp, host.PunchPort);
                    Log($"尝试同网段直连（{lanEp}）…");
                    punchSw.Start();
                    var lanObserved = await Task.Run(() => HolePunch(udp, new[] { lanEp }, magic + "|J|",
                        magic + "|", 1500, ct), ct).ConfigureAwait(false);
                    if (lanObserved is not null)
                    {
                        Log($"同网段直连成功（对端 {lanObserved}），建立 KCP 隧道…");
                        Report("lan");
                        CacheDirect(P2pRouteKind.UdpHole, lanObserved,
                            ((IPEndPoint)udp.LocalEndPoint!).Port, secret);
                        pool.Remove(udp);
                        return new KcpUpstream(udp, lanObserved);
                    }
                }
                if ((host.V4Lan?.Count ?? 0) > 0) Log("同网段直连未成功，继续公网打洞");
            }

            // 4) IPv4 UDP 打洞（v49.4 池化引擎）
            //    B4/B5：不再以「双侧对称且不可预测」提前放弃——样本不足或判据不可信时按 cone 处理并照常打洞，
            //    对称组合改用最大喷洒强度打满窗口，仍失败才落 frp。
            var peerSymmetric = string.Equals(host.Nat, "symmetric", StringComparison.OrdinalIgnoreCase);
            var dualSymmetric = !preserving && peerSymmetric;
            var anySym = !preserving || peerSymmetric;
            if (!host.Targeted || string.IsNullOrEmpty(host.V4) || !IPEndPoint.TryParse(host.V4, out var hostEp))
            {
                Log($"打洞条件不满足（Targeted={host.Targeted}，房主 V4='{host.V4}'，房主未就绪打洞路）");
                Report("skipped");
                return null;
            }
            // P1-F 同步起打：双方以服务器锚定的同一绝对时刻开始发 magic（首包命中率↑、窗口全重叠）
            // B7：按最近一次信令 RTT 补偿半程——srv_now 是 RTT/2 之前采样的，不减会让首包整体晚到 RTT/2
            if (await WaitForRendezvousAsync(lastSrvNow, host.PunchAt, lastRttMs, ct).ConfigureAwait(false))
                Log("已与房主同步起打");
            // B5 窗口按 NAT 组合分档：cone×cone 12s；含对称 10s；双对称给足 10s 强喷洒
            var punchTimeout = anySym ? 10000 : 12000;
            // B2 喷洒预算：cone×cone 不喷洒（0）；涉对称 256；双对称 512
            var targetBudget = dualSymmetric ? SprayTargetsDualSym : anySym ? SprayTargetsSym : 0;
            var targets = BuildPunchTargets(host, hostEp, anySym ? 12 : 8,
                targetBudget > 0 ? targetBudget : 72);
            Log($"UDP 打洞中（{pool.Count} 路并发 × 目标 {targets.Count}"
                + (targetBudget > 0 ? $"（喷洒预算 {targetBudget}）" : "")
                + $"（含 {hostEp}" + (host.UpnpPort > 0 ? "+外部映射" : "") + $"，{punchTimeout / 1000}s 窗口）…");
            // B3：任一侧有 UPnP/PCP 外部映射即可作为「对端可直达」的首选直连目标（已进目标组第一层）
            if (host.UpnpPort > 0 || upnpPort > 0)
                Log($"UPnP 直连通道：对端映射 {(host.UpnpPort > 0 ? host.UpnpPort.ToString() : "无")}"
                    + $"，本端映射 {(upnpPort > 0 ? upnpPort.ToString() : "无")}（单侧即可直达）");
            P2pTrace.Log($"打洞目标组（{targets.Count}）：[{string.Join(" | ", targets)}]");
            punchSw.Start();
            // v51 观测回灌（两阶段）：涉对称组合先跑一个短观测窗（confirmOnly 只观测不提交），
            // 收集对端真实端点并回灌信令，再做「观测端点置顶」的定向补打——把端口猜测变成学习，
            // 半对称场景命中后补打几乎立即成功；cone×cone 保持单阶段快路径不变。
            var observed = new List<IPEndPoint>();
            if (anySym)
            {
                await PunchPoolAsync(pool, targets, magic + "|J|", magic + "|", ObserveWindowMs, ct,
                    0, LotterySpan, observed, confirmOnly: true).ConfigureAwait(false);
                if (observed.Count > 0)
                {
                    Log($"打洞观测：收到房主 magic 来自 {observed[0]}（回灌信令）…");
                    try
                    {
                        var sig = await api.P2pSignalAsync(token, code, "joiner",
                            new P2pCandidates(v6, mapped, natSelf, 0, lan, punchPort, predPorts, upnpPort,
                                pool.Select(s => ((IPEndPoint)s.LocalEndPoint!).Port).ToList(), probe.MapsStrings,
                                probe.Delta, Observed: observed[0].ToString()),
                            ct).ConfigureAwait(false);
                        if (sig.Peer is not null) host = sig.Peer;   // 刷新房主候选（含其回灌后的映射）
                        if (sig.Peer is { Observed.Length: > 0 })
                            Log($"房主回灌观测：本端该方向真实映射为 {sig.Peer.Observed}");
                    }
                    catch { /* 回灌失败不致命，按原目标继续补打 */ }
                    if (IPEndPoint.TryParse(host.V4, out var hostEp2)) hostEp = hostEp2;
                    targets = BuildPunchTargets(host, hostEp, anySym ? 12 : 8,
                        targetBudget > 0 ? targetBudget : 72, observed);
                    P2pTrace.Log($"定向补打目标组（{targets.Count}）：[{string.Join(" | ", targets)}]");
                }
            }
            var win = await PunchPoolAsync(pool, targets, magic + "|J|",
                magic + "|", punchTimeout, ct, targetBudget, LotterySpan).ConfigureAwait(false);
            if (win is null)
            {
                Log("UDP 打洞未成功（窗口内未收到房主任何 magic 应答）");
                Report("timeout");
                return null;
            }
            Log($"UDP 打洞成功（对端 {win.Observed}），建立 KCP 隧道…");
            Report("udp");
            CacheDirect(P2pRouteKind.UdpHole, win.Observed,
                ((IPEndPoint)win.Socket.LocalEndPoint!).Port, secret);
            pool.Remove(win.Socket);
            return new KcpUpstream(win.Socket, win.Observed);
        }

        /// <summary>累计各线路承载时长：直连（IPv6 / UDP 打洞）与中继（frp）分开记。
        /// 会话结束输出「直连占比」——判断 frp 使用率是否真的下降，靠的就是这个硬指标。</summary>
        private void AccumulateCarry(P2pRouteKind kind, long ms)
        {
            if (ms <= 0) return;
            if (kind == P2pRouteKind.Relay) Interlocked.Add(ref _relayCarryMs, ms);
            else Interlocked.Add(ref _directCarryMs, ms);
        }

        /// <summary>输出承载时长统计（幂等，只报一次）。</summary>
        private void ReportCarry()
        {
            if (Interlocked.Exchange(ref _carryReported, 1) != 0) return;
            var direct = Interlocked.Read(ref _directCarryMs);
            var relay = Interlocked.Read(ref _relayCarryMs);
            var total = direct + relay;
            if (total <= 0) return;
            var line = $"承载时长统计：直连 {direct / 1000.0:0.#}s / 中继 {relay / 1000.0:0.#}s（直连占比 {direct * 100.0 / total:0.#}%）";
            P2pTrace.Log(line);
            RaiseLauncherLog(line);
        }

        public void Dispose()
        {
            ReportCarry();
            // A4：从复用缓存注销（仅当缓存仍指向本实例，避免误删新建的隧道）
            var key = _code.Trim().ToUpperInvariant();
            if (ReuseCache.TryGetValue(key, out var self) && ReferenceEquals(self, this))
                ReuseCache.TryRemove(key, out _);
            try { _listener.Stop(); } catch { }
            _cts.Cancel();
            try { _current?.Dispose(); } catch { }
        }
    }

    // ---------------------------------------------------------------- 房主端

    /// <summary>
    /// 房主端 P2P 服务（多人）：主循环上报通用候选并发现全部加入者，
    /// 每个加入者开一条独立路线（独立 UDP socket 探测 → 定向上报 → 双向打洞
    /// → KCP 收首包后懒连接本地 MC 服务器并双向泵）。Dispose 停止全部后台活动。
    /// </summary>
    public sealed class HostService : IDisposable
    {
        /// <summary>同时打洞的 P2P 路上限；超出自动走中转线路。</summary>
        public const int MaxRoutes = 8;

        private readonly StudioApiClient _api;
        private readonly string _token;
        private readonly string _code;
        private readonly int _mcPort;
        private readonly Func<int>? _portProvider;   // v48 虚拟局域网：桥接端口动态热取

        /// <summary>当前桥接目标端口（LAN 模式随「对局域网开放」变化；服务端模式恒为 _mcPort）。</summary>
        private int ResolvePort() => _portProvider?.Invoke() ?? _mcPort;
        private readonly CancellationTokenSource _cts = new();
        private Socket? _udp;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _active =
            new(StringComparer.OrdinalIgnoreCase);   // 打洞中的路（主循环与路线任务并发访问）；值为该路 CTS，供「会话结束」标记取消
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _failed =
            new(StringComparer.OrdinalIgnoreCase);   // 失败记录（3 分钟内不重试）
        private Task? _loop;
        private V6Forwarder? _v6Fwd;   // C-a：进程内 IPv6 转发器（不动 MC 绑定，见 V6Forwarder）

        /// <summary>房主端 P2P 活动日志（线程池线程触发，订阅方自行调度）。</summary>
        public event Action<string>? Log;

        private void LogLine(string message)
        {
            Log?.Invoke(message);
            P2pTrace.Log(message);   // v46.2 复盘日志：房主端全部活动同步落盘
        }

        public HostService(StudioApiClient api, string token, string code, int mcPort,
            Func<int>? portProvider = null)
        {
            _api = api;
            _token = token;
            _code = code;
            _mcPort = mcPort;
            _portProvider = portProvider;   // v48 虚拟局域网：桥接目标端口动态热取（服务端模式传 null）
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                var v6 = CollectV6();
                var lan = CollectV4Lan();
                // C-a（安全备选方案）：MC 默认只绑 IPv4（server-ip 为空 → 0.0.0.0），对端无法 v6 直连。
                // 不动 MC 绑定（避免 IPV6_V6ONLY 把 127.0.0.1 / 局域网一起打断），改为进程内 [::] 转发器。
                // 仅服务端模式（_portProvider 为空=端口固定）且端口已知时才建：转发器必须与 MC 同端口，
                // 否则对端按 host.Port 连接会落空；端口为 0（MC 未就绪）时跳过，绝不绑随机端口空转。
                var fwdPort = ResolvePort();
                if (v6.Count > 0 && _portProvider is null && fwdPort > 0)
                {
                    try
                    {
                        _v6Fwd = new V6Forwarder(fwdPort, fwdPort);
                        LogLine($"[P2P] IPv6 直连转发器就绪：[::]:{_v6Fwd.Port} → 127.0.0.1:{fwdPort}"
                            + "（MC 绑定不动，IPv4 路径不受影响）");
                    }
                    catch (Exception ex)
                    {
                        LogLine("[P2P] IPv6 转发器启动失败（MC 可能已绑 v6 或端口占用），v6 直连不可用：" + ex.Message);
                    }
                }
                var udp = NewUdpSocket();
                _udp = udp;
                var probe = await Task.Run(() => ProbeNatPool(new[] { udp }, _api.ApiHost, ct), ct).ConfigureAwait(false);
                var nat = probe.Preserving ? "cone" : "symmetric";
                LogLine($"[P2P] 房主上线：NAT={nat} 映射={probe.Mapped} v6×{v6.Count} lan×{lan.Count} mcPort={ResolvePort()}（多人直连就绪）");
                // v50.6.5：对称 NAT 提示（手机热点/蜂窝网络的 CGNAT 几乎都是对称型）——
                // 此场景 IPv4 打洞成功率天然极低；有 v6 候选时朋友端会优先走 v6 直连（免打洞）
                if (nat == "symmetric")
                    LogLine(v6.Count > 0
                        ? "[P2P] 提示：本机为对称 NAT（手机热点/蜂窝网络典型特征），IPv4 打洞成功率低——已上报 IPv6 候选，朋友端将优先尝试 v6 直连"
                        : "[P2P] 提示：本机为对称 NAT（手机热点/蜂窝网络典型特征）且未检测到 IPv6——IPv4 打洞成功率低，建议确认手机热点已开启 IPv6 或改用宽带网络");

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var signal = await _api.P2pSignalAsync(_token, _code, "host",
                            new P2pCandidates(v6, probe.Mapped, nat, ResolvePort(), lan), ct).ConfigureAwait(false);
                        foreach (var peer in signal.Peers ?? Array.Empty<P2pPeer>())
                        {
                            if (peer.Email.Length == 0) continue;
                            // v51：加入端 MC 会话结束标记 → 拆除该路（KCP 无 wire 级 FIN，靠此及时
                            // 关闭房主端 KCP 会话与其 MC 服务器连接，避免僵尸连接导致对方重连串线）。
                            // 同时清掉失败冷却，保证对方立刻重连时能重新开路。
                            if (string.Equals(peer.Nat, NatSessionClosed, StringComparison.OrdinalIgnoreCase))
                            {
                                _failed.TryRemove(peer.Email, out _);
                                if (_active.TryGetValue(peer.Email, out var dead))
                                {
                                    try { dead.Cancel(); } catch { }
                                    LogLine($"[P2P] {peer.Email} 游戏会话结束，已拆除其直连路（可再次进入）");
                                }
                                continue;
                            }
                            if (_active.ContainsKey(peer.Email)) continue;
                            if (_failed.TryGetValue(peer.Email, out var t))
                            {
                                if ((DateTime.UtcNow - t).TotalMinutes < 3) continue;
                                _failed.TryRemove(peer.Email, out _); // 超过冷却，允许重试（加入者可能已重连）
                            }
                            if (_active.Count >= MaxRoutes)
                            {
                                LogLine($"[P2P] 直连路已达上限 {MaxRoutes}，{peer.Email} 自动走中转");
                                continue;
                            }
                            var routeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            _active[peer.Email] = routeCts;
                            _ = Task.Run(() => ServeRouteAsync(peer, v6, lan, routeCts.Token), CancellationToken.None);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        LogLine("[P2P] 房主循环异常：" + ex.Message);
                    }
                    // v49.4：3s → 1.5s——加入端等待房主定向候选的时间减半（打洞总时延的关键项）
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogLine("[P2P] 房主服务退出：" + ex.Message);
            }
        }

        /// <summary>单个加入者一路：独立 socket 池探测 → 定向上报 → LAN/公网打洞 → KCP 会话（等待其结束）。
        /// v49.4：每路改为多 socket 池（默认 12 路），组合矩阵同 v49（仅双侧对称 NAT 且完全不可预测才放弃）。</summary>
        private async Task ServeRouteAsync(P2pPeer joiner, List<string> v6, List<string> lan, CancellationToken ct)
        {
            var email = joiner.Email;
            var natSelf = "unknown";
            var punchSw = new Stopwatch();
            var upnpPort = 0;   // P1-D 外部映射端口（0=无；Report 闭包也读它，故提前声明）
            var pool = CreatePunchPool();
            var udp = pool[0];
            // v50.6.6：映射保活——路线从建立到开窗可达数十秒，CGN UDP 超时 15–30s 会先杀映射
            // B1：保活需按本端 NAT 分档，故推迟到探测出 preserving 之后再启动（见下）
            using var kaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            NatProbe? probe = null;   // 提前声明供 Report 闭包读取（探测早退时为 null）

            // 埋点：路线终点上报（后台尽力，失败静默）
            // phase=inner：房主侧按「加入者路线」上报，属直连尝试结果，与会话层终局分开统计
            void Report(string result)
            {
                kaCts.Cancel();   // v50.6.6：所有终点统一停保活
                P2pTrace.End("host", result,
                    $"{email} 打洞耗时 {punchSw.ElapsedMilliseconds}ms natSelf={natSelf} natPeer={joiner.Nat}",
                    phase: "inner");
                var payload = new
                {
                    role = "host",
                    phase = "inner",
                    nat_self = natSelf,
                    nat_peer = joiner.Nat,
                    result,
                    punch_ms = punchSw.ElapsedMilliseconds > 0 ? (int)punchSw.ElapsedMilliseconds : 0,
                    v6_count = v6.Count,
                    lan_count = lan.Count,
                    pred_count = joiner.PredPorts?.Count ?? 0,
                    upnp = upnpPort > 0 ? 1 : 0,
                    sock = pool.Count,
                    maps = probe?.Maps.Count ?? 0,
                };
                _ = Task.Run(async () =>
                {
                    try { await _api.P2pMetricAsync(_token, payload, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* 埋点失败不影响主流程 */ }
                }, CancellationToken.None);
            }

            try
            {
                var punchPort = ((IPEndPoint)udp.LocalEndPoint).Port;
                var mapTask = NatPortMapper.MapAsync(udp, ct);   // P1-D：主动映射（与探测并行）
                probe = await Task.Run(() => ProbeNatPool(pool, _api.ApiHost, ct), ct).ConfigureAwait(false);
                var mapped = probe.Mapped;
                var preserving = probe.Preserving;
                natSelf = preserving ? "cone" : "symmetric";
                var predPorts = probe.PredPorts;   // P1-C：本端下一次映射的端口预测（cone 为空）
                // B1：探测出 preserving 后再启动分档保活（cone 全池 / 对称 socket0 单目标）
                var natKeepalive = Task.Run(() => KeepNatMappingsAsync(pool, _api.ApiHost, preserving, kaCts.Token), CancellationToken.None);
                // 等映射结果（≤5s， discovery 4s 自限；未完成按 0 上报，不拖信令）
                await Task.WhenAny(mapTask, Task.Delay(5000, CancellationToken.None)).ConfigureAwait(false);
                upnpPort = mapTask.IsCompleted ? mapTask.Result : 0;
                var peerSymmetric = string.Equals(joiner.Nat, "symmetric", StringComparison.OrdinalIgnoreCase);


                // B4/B5：不再「双侧对称且不可预测就放弃」——按 cone 处理并照常打洞，对称组合改用最大喷洒打满窗口
                LogLine($"[P2P] 为 {email} 开直连路（映射 {mapped}，本端 {natSelf}，socket ×{pool.Count}"
                    + (predPorts.Count > 0 ? $"，预测窗口 ×{predPorts.Count}" : "")
                    + (upnpPort > 0 ? $"，外部映射 {upnpPort}" : "") + $"），定向上报…");

                IPEndPoint? joinerEp = null;
                var peerPunchAt = 0.0;
                var lastSrvNow = 0.0;
                var lastRttMs = 0.0;   // B7：最近一次信令往返 RTT（punch_at 半程补偿）
                var secret = "";   // v50 M6：双方 ≥50 时云端下发，HMAC 化握手 magic
                for (var i = 0; i < 6 && !ct.IsCancellationRequested; i++)
                {
                    var sigSw = Stopwatch.StartNew();
                    var signal = await _api.P2pSignalAsync(_token, _code, "host",
                        new P2pCandidates(v6, mapped, natSelf, ResolvePort(), lan, punchPort, predPorts, upnpPort,
                            pool.Select(s => ((IPEndPoint)s.LocalEndPoint!).Port).ToList(), probe.MapsStrings, probe.Delta),
                        ct, forPeer: email).ConfigureAwait(false);
                    sigSw.Stop();
                    lastRttMs = sigSw.ElapsedMilliseconds;
                    if (signal.Secret.Length > 0) secret = signal.Secret;
                    lastSrvNow = signal.SrvNow;
                    var p = signal.Peer;
                    if (p is not null)
                    {
                        // 加入端已走 v6 直连：免打洞，本路关闭
                        if (p.Nat == "direct-v6" || string.IsNullOrEmpty(p.V4))
                        {
                            LogLine($"[P2P] {email} 已走其他线路，本路关闭");
                            Report("closed");
                            return;
                        }
                        if (IPEndPoint.TryParse(p.V4, out joinerEp))
                        {
                            peerPunchAt = p.PunchAt;
                            break;
                        }
                    }
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }
                if (joinerEp is null)
                {
                    LogLine($"[P2P] {email} 候选不可用（走中转）");
                    _failed[email] = DateTime.UtcNow;
                    Report("no_peer");
                    return;
                }

                // P0-E LAN 优先：对加入者的私网候选发 magic 探测（同网段即 100% 直连；AP 隔离时 1.5s 内失败回落）
                var magic = RoomMagic(_code, secret);
                if (joiner.PunchPort > 0)
                {
                    foreach (var ip in joiner.V4Lan ?? (IReadOnlyList<string>)Array.Empty<string>())
                    {
                        if (!IPAddress.TryParse(ip, out var lanIp)) continue;
                        var lanEp = new IPEndPoint(lanIp, joiner.PunchPort);
                        punchSw.Start();
                        var lanObserved = await Task.Run(() => HolePunch(udp, new[] { lanEp }, magic + "|H|",
                            magic + "|", 1500, ct), ct).ConfigureAwait(false);
                        if (lanObserved is not null)
                        {
                            LogLine($"[P2P] {email} 同网段直连成功（对端 {lanObserved}），等待游戏流量…");
                            Report("lan");
                            await ServeKcpAsync(udp, lanObserved, email, ct).ConfigureAwait(false);
                            return;
                        }
                    }
                }

                // 公网打洞（v49.4 池化引擎）：P1-F 同步起打 + 每 socket 映射/预测窗口/邻域批量齐发
                // B7：punch_at 半 RTT 补偿（srv_now 采样点早于收到响应 RTT/2）
                if (await WaitForRendezvousAsync(lastSrvNow, peerPunchAt, lastRttMs, ct).ConfigureAwait(false))
                    LogLine($"[P2P] {email} 已同步起打");
                // B5 窗口按组合分档 / B2 喷洒预算：cone×cone 12s 不喷洒；含对称 10s×256；双对称 10s×512
                var dualSymmetric = !preserving && peerSymmetric;
                var anySym = !preserving || peerSymmetric;
                var punchTimeout = anySym ? 10000 : 12000;
                var targetBudget = dualSymmetric ? SprayTargetsDualSym : anySym ? SprayTargetsSym : 0;
                var targets = BuildPunchTargets(joiner, joinerEp, anySym ? 12 : 8,
                    targetBudget > 0 ? targetBudget : 72);
                LogLine($"[P2P] {email} 开始打洞（{pool.Count} 路并发 × 目标 {targets.Count}"
                    + (targetBudget > 0 ? $"（喷洒预算 {targetBudget}）" : "")
                    + $"（含 {joinerEp}），{punchTimeout / 1000}s 窗口）…");
                // B3：任一侧有 UPnP/PCP 外部映射即可作「对端可直达」的首选直连目标（已进目标组第一层）
                if (joiner.UpnpPort > 0 || upnpPort > 0)
                    LogLine($"[P2P] {email} UPnP 直连通道：对端映射 {(joiner.UpnpPort > 0 ? joiner.UpnpPort.ToString() : "无")}"
                        + $"，本端映射 {(upnpPort > 0 ? upnpPort.ToString() : "无")}（单侧即可直达）");
                punchSw.Start();
                // v51 观测回灌（两阶段）：同加入端——涉对称组合先跑短观测窗（confirmOnly 只观测不提交），
                // 回灌对端真实端点后再做「观测端点置顶」的定向补打；cone×cone 保持单阶段快路径。
                var observed = new List<IPEndPoint>();
                if (anySym)
                {
                    await PunchPoolAsync(pool, targets, magic + "|H|", magic + "|", ObserveWindowMs, ct,
                        0, LotterySpan, observed, confirmOnly: true).ConfigureAwait(false);
                    if (observed.Count > 0)
                    {
                        LogLine($"[P2P] {email} 打洞观测：收到加入端 magic 来自 {observed[0]}（回灌信令）…");
                        try
                        {
                            var sig = await _api.P2pSignalAsync(_token, _code, "host",
                                new P2pCandidates(v6, mapped, natSelf, ResolvePort(), lan, punchPort, predPorts,
                                    upnpPort, pool.Select(s => ((IPEndPoint)s.LocalEndPoint!).Port).ToList(),
                                    probe?.MapsStrings, probe?.Delta ?? 0, Observed: observed[0].ToString()),
                                ct, forPeer: email).ConfigureAwait(false);
                            var p2 = sig.Peer;
                            if (p2 is not null && IPEndPoint.TryParse(p2.V4, out var ep2))
                            {
                                joiner = p2;      // 刷新加入端候选（含其回灌后的映射）
                                joinerEp = ep2;
                            }
                            if (p2 is { Observed.Length: > 0 })
                                LogLine($"[P2P] {email} 加入端回灌观测：本端该方向真实映射为 {p2.Observed}");
                        }
                        catch { /* 回灌失败不致命，按原目标继续补打 */ }
                        targets = BuildPunchTargets(joiner, joinerEp, anySym ? 12 : 8,
                            targetBudget > 0 ? targetBudget : 72, observed);
                        P2pTrace.Log($"[P2P] {email} 定向补打目标组（{targets.Count}）：[{string.Join(" | ", targets)}]");
                    }
                }
                var win = await PunchPoolAsync(pool, targets, magic + "|H|",
                    magic + "|", punchTimeout, ct, targetBudget, LotterySpan).ConfigureAwait(false);
                if (win is null)
                {
                    LogLine($"[P2P] {email} 打洞未成功（走中转）");
                    _failed[email] = DateTime.UtcNow;
                    Report("timeout");
                    return;
                }
                LogLine($"[P2P] {email} 打洞成功（对端 {win.Observed}），等待游戏流量…");
                Report("udp");
                await ServeKcpAsync(win.Socket, win.Observed, email, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogLine($"[P2P] {email} 直连路异常：" + ex.Message);
                _failed[email] = DateTime.UtcNow;
                Report("error");
            }
            finally
            {
                foreach (var s in pool)
                {
                    try { s.Dispose(); } catch { }
                }
                // v51：路结束即释放该路 CTS（「会话结束」标记只 Cancel 不 Dispose，统一在此收口）
                if (_active.TryRemove(email, out var rc))
                {
                    try { rc.Dispose(); } catch { }
                }
            }
        }

        /// <summary>KCP 会话：收到加入端首包（MC 客户端流量）后懒连接本地 MC 服务器并双向泵；
        /// 等待会话结束（断开/超时/取消）后返回，socket 由路线 finally 统一释放。
        /// P2-G2：会话期间每 15s 向对端发裸 magic keep-alive（保持 host 侧 NAT 映射）。</summary>
        private async Task ServeKcpAsync(Socket udp, IPEndPoint observed, string email, CancellationToken ct)
        {
            TcpClient? mc = null;
            using var kaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var kaTask = Task.Run(async () =>
            {
                var magic = Encoding.ASCII.GetBytes(MagicPrefix + "KA");
                while (!kaCts.IsCancellationRequested)
                {
                    try { udp.SendTo(magic, observed); }
                    catch { break; }
                    // 只发不收：接收循环归 KCP transport 独占；对端 KA 包由 KcpSharp 按会话 ID 静默丢弃
                    try { await Task.Delay(15000, kaCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }, CancellationToken.None);
            try
            {
                // 清掉打洞阶段的残留 magic 包，再把 socket 交给 KCP
                var junk = new byte[128];
                var any = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                while (udp.Poll(0, SelectMode.SelectRead)) udp.ReceiveFrom(junk, ref any);

                using var transport = KcpSocketTransport.CreateConversation(udp, observed, ConvId, GameKcpOptions());
                transport.Start();
                var conv = transport.Connection;
                var buf = new byte[65536];
                while (!ct.IsCancellationRequested)
                {
                    var r = await conv.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (r.TransportClosed) { LogLine($"[P2P] {email} 对端关闭隧道"); break; }
                    if (mc is null)
                    {
                        mc = new TcpClient();
                        await mc.ConnectAsync(IPAddress.Loopback, ResolvePort(), ct).ConfigureAwait(false);
                        LogLine($"[P2P] {email} 游戏流量接入本地 MC 服务器（端口 {ResolvePort()}）");
                        _ = PumpTcpToKcp(mc, conv, ct);
                    }
                    await mc.GetStream().WriteAsync(buf.AsMemory(0, r.BytesReceived), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogLine($"[P2P] {email} KCP 会话结束：" + ex.Message);
            }
            finally
            {
                mc?.Dispose();
                LogLine($"[P2P] {email} 隧道会话关闭");
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _v6Fwd?.Dispose(); } catch { }
            try { _udp?.Dispose(); } catch { }
            _cts.Dispose();
        }
    }

    /// <summary>C-a（安全备选方案）进程内 IPv6 转发器：MC 默认只绑 IPv4（server-ip 为空 → 0.0.0.0），
    /// 对端无法用 v6 直连。此处不动 MC 的绑定（避免 IPV6_V6ONLY 把 127.0.0.1 / 局域网桥接一起打断），
    /// 由启动器在 [::]:port 起一个 v6-only 监听（DualMode=false，与 0.0.0.0 不冲突），
    /// 把 v6 入站流量转发到 127.0.0.1:port。这样 v6 直连可用，IPv4 路径完全不受影响。</summary>
    private sealed class V6Forwarder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _targetPort;
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; }

        public V6Forwarder(int port, int targetPort)
        {
            _targetPort = targetPort;
            _listener = new TcpListener(IPAddress.IPv6Any, port);
            _listener.Server.DualMode = false;   // v6-only：与 MC 的 IPv4 绑定共存，不抢 127.0.0.1
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient inbound;
                try { inbound = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { break; }
                _ = Task.Run(() => PumpAsync(inbound));
            }
        }

        private async Task PumpAsync(TcpClient inbound)
        {
            TcpClient? local = null;
            try
            {
                local = new TcpClient();
                await local.ConnectAsync(IPAddress.Loopback, _targetPort, _cts.Token).ConfigureAwait(false);
                var a = inbound.GetStream();
                var b = local.GetStream();
                var up = a.CopyToAsync(b, _cts.Token);
                var down = b.CopyToAsync(a, _cts.Token);
                await Task.WhenAny(up, down).ConfigureAwait(false);
            }
            catch { /* 单连接失败不影响转发器 */ }
            finally
            {
                try { inbound.Dispose(); } catch { }
                try { local?.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }

    // ---------------------------------------------------------------- 打洞与泵（共享实现）

    /// <summary>池化 NAT 探测结果：Mapped=socket0 公网映射，Preserving=cone 判定，
    /// Samples=按发送顺序（≈NAT 分配顺序）的全部样本，Maps=每 socket 最后一个观察映射，
    /// Delta=端口递增步长（0=学不到），PredPorts=对称 NAT 的下一映射预测窗（兼容字段，cone 空）。</summary>
    private sealed record NatProbe(string Mapped, bool Preserving, List<string> Samples,
        List<IPEndPoint> Maps, List<string> MapsStrings, int Delta, List<int> PredPorts);

    private static string PortOf(string s) => s.Split(':')[^1];

    /// <summary>
    /// v49.4 池化 NAT 探测：socket 池逐个向云机 STUN-lite（8802/8803）采样（每 socket 得到
    /// 独立映射——cone 侧即 12 个对端可直接命中的稳定端口），socket0 额外探 2 个公共 STUN
    /// （RFC 5389）做端口保持性判定；全部样本按发送顺序（≈NAT 分配顺序）返回，供步长学习。
    /// 总耗时固定 ≤2.5s。判定：socket0 全部目标样本端口一致 + 其余每 socket 双目标一致 → cone；
    /// 任一 socket 对不同目标出现不同端口 → 对称（EDM）。
    /// </summary>
    private static NatProbe ProbeNatPool(IReadOnlyList<Socket> sockets, string apiHost, CancellationToken ct)
    {
        var samples = new List<string>();

        var liteTargets = new List<IPEndPoint>();
        foreach (var port in StunPorts)
        {
            try { liteTargets.Add(new IPEndPoint(IPAddress.Parse(apiHost), port)); } catch { /* 地址异常跳过 */ }
        }

        // 公共 STUN 目标（DNS 解析失败静默跳过，降级为纯云机采样）
        var stunTargets = new List<IPEndPoint>();
        var txids = new List<byte[]>();
        if (UsePublicStun)
        {
            foreach (var (host, port) in new[] { ("stun.chat.bilibili.com", 3478), ("stun.cloudflare.com", 3478) })
            {
                try
                {
                    var ip = Dns.GetHostAddresses(host)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (ip is null) continue;
                    stunTargets.Add(new IPEndPoint(ip, port));
                    var txid = new byte[12];
                    Random.Shared.NextBytes(txid);
                    txids.Add(txid);
                }
                catch { /* 不可达跳过 */ }
            }
        }

        var nSockets = sockets.Count;
        var baseSock = sockets[0];
        // 每 (socket, 云机目标) 的观察样本；publics 只由 socket0 探测
        var cloud = new string?[nSockets][];
        for (var i = 0; i < nSockets; i++) cloud[i] = new string?[liteTargets.Count];
        var pubs = new string?[stunTargets.Count];

        // 发送顺序 = NAT 分配顺序的学习基础：先公共 STUN（socket0），再逐 socket 云机对
        for (var i = 0; i < stunTargets.Count; i++)
        {
            var req = new byte[20];
            req[0] = 0x00; req[1] = 0x01;
            req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;
            Array.Copy(txids[i], 0, req, 8, 12);
            try { baseSock.SendTo(req, stunTargets[i]); } catch { }
        }
        var probeReq = Encoding.ASCII.GetBytes("MCSTUN1");
        for (var si = 0; si < nSockets; si++)
        {
            for (var ti = 0; ti < liteTargets.Count; ti++)
            {
                try { sockets[si].SendTo(probeReq, liteTargets[ti]); } catch { /* 发送失败按该目标缺席 */ }
            }
        }

        // 统一收包循环：按 (接收 socket, 来源端点) 解复用，全部样本到位或 2.5s 截止
        var expected = nSockets * liteTargets.Count + stunTargets.Count;
        var got = 0;
        var buf = new byte[512];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2500 && got < expected && !ct.IsCancellationRequested)
        {
            for (var si = 0; si < nSockets; si++)
            {
                var s = sockets[si];
                while (s.Poll(0, SelectMode.SelectRead))
                {
                    var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = s.ReceiveFrom(buf, ref from); }
                    catch (SocketException) { break; }
                    if (from is not IPEndPoint ep) continue;
                    var text = Encoding.ASCII.GetString(buf, 0, n);
                    if (text.StartsWith("MCSTUNR", StringComparison.Ordinal))
                    {
                        var ti = liteTargets.FindIndex(t => t.Equals(ep));
                        if (ti >= 0 && cloud[si][ti] is null)
                        {
                            cloud[si][ti] = text["MCSTUNR".Length..];
                            got++;
                        }
                        continue;
                    }
                    if (si == 0)
                    {
                        var idx = stunTargets.FindIndex(t => t.Equals(ep));
                        if (idx >= 0 && pubs[idx] is null && n >= 20 && buf[0] == 0x01 && buf[1] == 0x01
                            && buf[8] == txids[idx][0] && buf[9] == txids[idx][1])
                        {
                            var mappedAddr = ParseStunMapped(buf, n);
                            if (mappedAddr is not null)
                            {
                                pubs[idx] = mappedAddr;
                                got++;
                            }
                        }
                    }
                }
            }
            if (got < expected) Thread.Sleep(25);
        }

        // 顺序样本（学习分配序列）：公共（socket0）在前，随后逐 socket 云机对
        foreach (var p in pubs) if (p is not null) samples.Add(p);
        for (var si = 0; si < nSockets; si++)
            for (var ti = 0; ti < liteTargets.Count; ti++)
                if (cloud[si][ti] is not null) samples.Add(cloud[si][ti]!);

        // cone 判定：socket0 全部样本端口一致（公共+云机 ≥3 目标为确定性判定），
        // 且其余每 socket 的云机对一致；样本不足时乐观保留 cone（打洞超时兜底）
        static bool SamePorts(IEnumerable<string> ss)
        {
            var ports = ss.Select(PortOf).ToList();
            return ports.Count == 0 || ports.All(p => p == ports[0]);
        }
        var baseAll = pubs.Where(p => p is not null).Select(p => p!)
            .Concat(cloud[0].Where(p => p is not null).Select(p => p!)).ToList();
        var preserving = SamePorts(baseAll);
        for (var si = 1; si < nSockets && preserving; si++)
            preserving = SamePorts(cloud[si].Where(p => p is not null).Select(p => p!).ToList());

        // 每 socket 映射 = 其最后一个云机样本（cone=稳定端口；对称=最近分配）
        var maps = new List<IPEndPoint>();
        var mapsStr = new List<string>();
        for (var si = 0; si < nSockets; si++)
        {
            var last = cloud[si].LastOrDefault(p => p is not null);
            if (last is null) continue;
            if (IPEndPoint.TryParse(last, out var ep))
            {
                maps.Add(ep);
                mapsStr.Add(last);
            }
        }

        // 步长学习（P1-C 增强）+ B1.3 预测原点：改为只用「socket0 的样本序列」——
        // 保活只续 socket0 的映射、不推进其余 socket 的分配计数器，故 socket0 的最近样本才与开窗时刻对齐
        // （原实现取全体样本末位，混入了其他 socket 的端口，预测原点偏移）
        var sock0Ports = new List<int>();
        foreach (var p in pubs)
            if (p is not null && int.TryParse(PortOf(p), out var pp0)) sock0Ports.Add(pp0);
        foreach (var c in cloud[0])
            if (c is not null && int.TryParse(PortOf(c), out var pc)) sock0Ports.Add(pc);

        var delta = 0;
        var deltas = new List<int>();
        for (var i = 1; i < sock0Ports.Count; i++)
        {
            var d = sock0Ports[i] - sock0Ports[i - 1];
            if (d > 0 && d <= 32) deltas.Add(d);
        }
        if (deltas.Count > 0) delta = deltas.GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key;

        // 预测窗口（兼容字段，仅对称 NAT）：socket0 末样本 + delta·k ±1（k=1..8，最多 24 个；服务端 D1 已放宽至保留 24）
        var predPorts = new List<int>();
        if (!preserving && delta > 0 && sock0Ports.Count > 0)
        {
            var last = sock0Ports[^1];
            for (var k = 1; k <= 8; k++)
            {
                for (var w = -1; w <= 1; w++)
                {
                    var p = last + delta * k + w;
                    if (p > 0 && p <= 65535 && !predPorts.Contains(p)) predPorts.Add(p);
                }
            }
        }

        // B4 样本门槛：样本 < 4 不足以判定「对称」（丢包会把 cone 误判成对称，掐死本可打通的对），
        // 按 cone 处理并清空步长/预测，让打洞照常进行
        if (samples.Count < 4)
        {
            preserving = true;
            delta = 0;
            predPorts.Clear();
        }

        var mapped = cloud[0].FirstOrDefault(p => p is not null) ?? pubs.FirstOrDefault(p => p is not null) ?? $"{apiHost}:0";
        P2pTrace.Log($"NAT 探测完成：样本×{samples.Count}/期望×{expected}（socket×{nSockets}，公共 STUN×{stunTargets.Count}）"
            + $"→ {(preserving ? "cone" : "symmetric")}，delta {delta}"
            + (samples.Count == 0 ? " ← 全部无应答，NAT 判定与端口预测不可信" : ""));
        return new NatProbe(mapped!, preserving, samples, maps, mapsStr, delta, predPorts);
    }

    /// <summary>B1 NAT 映射保活（与端口预测解耦）：间隔默认 15s（原 2.5s 比 RFC 4787 要求激进约 50 倍，
    /// 且持续推进 NAT 分配计数器、与端口预测互相打架），打洞窗口开启前由调用方取消
    /// （窗口内本就在主动发包，无需保活，且要让预测样本不被中途推进）。
    /// 分档：cone 全池续命（每路稳定映射都要保）；对称仅 socket0 + 单一目标——复用同一映射，
    /// 不推进其余 socket 的分配序列，避免污染端口预测。
    /// 国内 CGN 的 UDP 映射超时实测 15–30s（多层 NAT 取最短层），不加保活时窗口开打时映射已过期重建，
    /// 双方朝死端口互打，是 cone×cone 成功率仅 14.8% 的最可信解释。RFC 4787 REQ-6：出向包刷新映射计时。
    /// ct 取消 / 90s 兜底 / socket 释放（SendTo 异常）均自然退出，不影响打洞主流程。</summary>
    private static async Task KeepNatMappingsAsync(IReadOnlyList<Socket> sockets, string apiHost,
        bool preserving, CancellationToken ct)
    {
        var req = Encoding.ASCII.GetBytes("MCSTUN1");
        var targets = new List<IPEndPoint>();
        try
        {
            if (preserving)
                foreach (var port in StunPorts)
                    targets.Add(new IPEndPoint(IPAddress.Parse(apiHost), port));
            else if (StunPorts.Length > 0)
                targets.Add(new IPEndPoint(IPAddress.Parse(apiHost), StunPorts[0]));
        }
        catch { return; }
        if (targets.Count == 0 || sockets.Count == 0) return;
        IReadOnlyList<Socket> keep = preserving || sockets.Count <= 1 ? sockets : new[] { sockets[0] };
        var deadline = Environment.TickCount64 + 90_000;
        try
        {
            while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                foreach (var s in keep)
                {
                    if (s is null) continue;
                    foreach (var t in targets)
                    {
                        try { s.SendTo(req, t); }
                        catch (ObjectDisposedException) { return; }
                        catch (SocketException) { }
                    }
                }
                await Task.Delay(NatKeepAliveMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* 保活异常不影响打洞主流程 */ }
    }

    /// <summary>解析 STUN Binding Success 中的 XOR-MAPPED-ADDRESS(0x0020) 或 MAPPED-ADDRESS(0x0001)。</summary>
    private static string? ParseStunMapped(byte[] buf, int n)
    {
        int off = 20;
        while (off + 4 <= n)
        {
            int type = (buf[off] << 8) | buf[off + 1];
            int len = (buf[off + 2] << 8) | buf[off + 3];
            if (type is 0x0020 or 0x0001 && len >= 8 && off + 4 + len <= n)
            {
                int p = off + 4;
                int rawPort = (buf[p + 2] << 8) | buf[p + 3];
                var addrBytes = new byte[4];
                Array.Copy(buf, p + 4, addrBytes, 0, 4);
                if (type == 0x0020)
                {
                    rawPort ^= 0x2112; // magic cookie 高 16 位参与端口异或
                    addrBytes[0] ^= 0x21; addrBytes[1] ^= 0x12;
                    addrBytes[2] ^= 0xA4; addrBytes[3] ^= 0x42;
                }
                return $"{new IPAddress(addrBytes)}:{rawPort}";
            }
            off += 4 + ((len + 3) & ~3);
        }
        return null;
    }

    /// <summary>
    /// P1-F 同步起打：按服务器锚定的 punch_at（服务器时钟）等待到同一绝对时刻。
    /// 时钟校准：offset = 本机时钟 − 服务器时钟（取自同一响应的 srv_now，精度 ±RTT/2 足够）。
    /// B7：srv_now 是服务器在「收到请求后、发出响应前」采样的，客户端收到时已过 RTT/2，
    /// 因此等待量再减 rttMs/2，把双方首包到达对端 NAT 的时刻对齐到同一窗口（DCUtR 的关键手段）。
    /// 等待上限 5s：偏差过大或锚点已过期时放弃同步，退化为原来的自然重叠（双方长窗口兜底）。
    /// 返回是否执行了同步等待。
    /// </summary>
    private static async Task<bool> WaitForRendezvousAsync(double srvNow, double punchAt, double rttMs,
        CancellationToken ct)
    {
        if (srvNow <= 0 || punchAt <= 0) return false;
        var nowLocal = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var offset = nowLocal - srvNow;
        var rttHalf = rttMs > 0 && rttMs < 4000 ? rttMs / 2000.0 : 0;   // 半 RTT（秒）；异常值不补偿
        var waitMs = (int)((punchAt + offset - nowLocal - rttHalf) * 1000);
        if (waitMs <= 0 || waitMs > 5000) return false;
        await Task.Delay(waitMs, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 双向打洞：持续向目标组发 magic 并监听；收到对端包即认为洞已开，返回观察到的对端真实地址。
    /// P1-C：对端为对称 NAT 时 targets 含其预测端口窗口（每个窗口轮发包，任何一路命中即成功）。
    /// </summary>
    private static IPEndPoint? HolePunch(Socket udp, IReadOnlyList<IPEndPoint> targets, string magic,
        string validPrefix, int timeoutMs, CancellationToken ct)
    {
        var payloadRaw = Encoding.ASCII.GetBytes(magic + Guid.NewGuid().ToString("N")[..8]);
        // v49：补齐到 64B——部分 CGNAT/防火墙对超小 UDP 包做 QoS 丢弃
        var payload = payloadRaw.Length >= 64 ? payloadRaw : PadTo(payloadRaw, 64);
        var echo = Encoding.ASCII.GetBytes(magic.Replace("|J|", "|E|").Replace("|H|", "|E|"));
        var buf = new byte[128];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            foreach (var t in targets)
            {
                try { udp.SendTo(payload, t); } catch (SocketException) { /* 单目标失败不致命，继续 */ }
            }
            // v49：100ms 轮询（原 300ms）——同窗口发包量 ×3，窗口内命中概率提升
            if (udp.Poll(100_000, SelectMode.SelectRead))
            {
                var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = udp.ReceiveFrom(buf, ref from); }
                catch (SocketException) { continue; }
                var text = Encoding.ASCII.GetString(buf, 0, n);
                if (text.StartsWith(validPrefix, StringComparison.Ordinal))
                {
                    if (!text.Contains("|E|", StringComparison.Ordinal))
                    {
                        try { udp.SendTo(echo, from); } catch { /* 回声失败不致命 */ }
                    }
                    return (IPEndPoint)from;
                }
            }
        }
        return null;
    }

    /// <summary>v49.4 打洞目标组（B2 分级预算）：对端每 socket 映射（cone=稳定直达，最优先）→ 外部映射
    /// （UPnP/PMP/PCP）→ 主映射 → 对端自报预测窗（以上为「必进」高可信层）→ 主映射邻域 ±spread
    /// （CGNAT 半对称主因，第二优先、保证不被 delta 窗挤掉）→ 由对端 delta 推导的扩展窗
    /// （k=1..20 ±2，用剩余预算填充）。全部限定对端公网 IP，按端口去重，封顶 budget 个；
    /// 超出部分留给 PunchPool 的持续彩票口按窗口预算补充。
    /// v51 观测回灌：observed（本端打洞中直接观测到的对端真实端点）作为最高优先目标层——
    /// 观测端点是对端在本方向的真实映射，命中即直连，优先于一切猜测（maps/预测/邻域）。</summary>
    private static List<IPEndPoint> BuildPunchTargets(P2pPeer peer, IPEndPoint primary, int spread, int budget,
        IReadOnlyList<IPEndPoint>? observed = null)
    {
        if (budget < 8) budget = 8;
        var list = new List<IPEndPoint>();
        var seen = new HashSet<int>();
        void Add(IPEndPoint ep)
        {
            if (list.Count >= budget) return;
            if (ep.Port is > 0 and < 65536 && seen.Add(ep.Port)) list.Add(ep);
        }

        // 第零层（最高优先，v51 观测回灌）：本端直接观测到的对端真实端点——对端在本方向的
        // 实际映射，是「学习」而非「猜测」，命中即直连。限定同公网 IP（观测端点必来自对端）。
        foreach (var o in observed ?? (IReadOnlyList<IPEndPoint>)Array.Empty<IPEndPoint>())
        {
            if (o.Address.Equals(primary.Address)) Add(o);
        }
        // 第一层（必进）：对端稳定/已知映射——命中即直连，不受预算裁剪
        foreach (var m in peer.Maps ?? (IReadOnlyList<string>)Array.Empty<string>())
        {
            if (IPEndPoint.TryParse(m, out var ep) && ep.Address.Equals(primary.Address))
                Add(ep);
        }
        if (peer.UpnpPort > 0) Add(new IPEndPoint(primary.Address, peer.UpnpPort));
        Add(primary);
        foreach (var p in peer.PredPorts ?? (IReadOnlyList<int>)Array.Empty<int>())
            Add(new IPEndPoint(primary.Address, p));
        // 第二层（保证）：邻域扫描——主映射 ±spread。实测 CGNAT「半对称」：对多数目标复用映射端口，
        // 对新目标（对端）分配邻近端口；两侧均判 cone 却打洞超时的主因，必须优先于 delta 窗保留
        if (spread > 0)
        {
            for (var d = 1; d <= spread; d++)
            {
                Add(new IPEndPoint(primary.Address, primary.Port - d));
                Add(new IPEndPoint(primary.Address, primary.Port + d));
            }
        }
        // 第三层（填充）：对端最新映射 + delta·k（k=1..20）±2——对端打洞首轮回的每个新目标端口
        // 都会按分配序列落在此区间内，扫描即可命中其任意 socket 的早期映射
        if (peer.Delta > 0 && (peer.Maps?.Count ?? 0) > 0
            && IPEndPoint.TryParse(peer.Maps![^1], out var lastMap))
        {
            for (var k = 1; k <= 20; k++)
            {
                for (var w = -2; w <= 2; w++)
                {
                    var p = lastMap.Port + peer.Delta * k + w;
                    if (p > 0 && p < 65536) Add(new IPEndPoint(primary.Address, p));
                }
            }
        }
        return list;
    }

    private static byte[] PadTo(byte[] src, int len)
    {
        var dst = new byte[len];
        Array.Copy(src, dst, src.Length);
        return dst;
    }

    /// <summary>释放池中除 keep 外的全部 socket（keep 交由 KcpUpstream/调用方接管）。</summary>
    private static void DisposeExcept(IEnumerable<Socket> pool, Socket? keep)
    {
        foreach (var s in pool)
        {
            if (ReferenceEquals(s, keep)) continue;
            try { s.Dispose(); } catch { }
        }
    }

    /// <summary>池化打洞胜者：命中的本地 socket + 观察到的对端真实地址。</summary>
    private sealed record PunchWin(Socket Socket, IPEndPoint Observed);

    /// <summary>
    /// v49.4 池化打洞引擎（B2 增强）：socket 池全部并发向目标组发 magic 并监听，任一 socket 先收到
    /// 对端包即胜（立即返回，不再像 v49 双 socket 那样等两路全部跑完）。
    /// 发包节奏：前 3 轮每 70ms 对「高可信结构化目标」全量突发（起打对齐窗口内的首包命中率），
    /// 其后每 180ms 每 socket 轮转 16 目标子集（限制稳态 pps，防 CGNAT 泛洪检测）。
    /// B2：budget&gt;targets.Count 时启用「持续彩票口」——每约 1.5s 重抽一批随机端口（不与既有重复），
    /// 累计覆盖端口数随窗口增长直至 budget（对称 NAT 的生日悖论补救）；span 为随机半径，不足 budget
    /// 时自动放宽以保证预算放得下。彩票口始终按分片轮转，不参与首轮全量突发，避免瞬时泛洪。
    /// 收到合法 magic 即回 echo（对端同刻确认），返回胜者。旧版 HolePunch 保留给 LAN 直连用。
    /// v51 观测回灌：observedSink 非空时把每次收到的对端真实源端点记入（去重）；confirmOnly=true 时
    /// 只观测不提交（记录 + 回 echo 后继续喷洒），供调用方完成回灌信令后再定向补打。
    /// </summary>
    private static async Task<PunchWin?> PunchPoolAsync(
        IReadOnlyList<Socket> sockets, IReadOnlyList<IPEndPoint> targets, string magic,
        string validPrefix, int timeoutMs, CancellationToken ct, int budget = 0, int span = 160,
        IList<IPEndPoint>? observedSink = null, bool confirmOnly = false)
    {
        return await Task.Run(() => PunchPoolCore(sockets, targets, magic, validPrefix, timeoutMs, ct, budget, span,
            observedSink, confirmOnly), ct).ConfigureAwait(false);
    }

    private static PunchWin? PunchPoolCore(
        IReadOnlyList<Socket> sockets, IReadOnlyList<IPEndPoint> targets, string magic,
        string validPrefix, int timeoutMs, CancellationToken ct, int budget, int span,
        IList<IPEndPoint>? observedSink = null, bool confirmOnly = false)
    {
        if (targets.Count == 0) return null;
        var payloadRaw = Encoding.ASCII.GetBytes(magic + Guid.NewGuid().ToString("N")[..8]);
        // v49：补齐到 64B——部分 CGNAT/防火墙对超小 UDP 包做 QoS 丢弃（B8）
        var payload = payloadRaw.Length >= 64 ? payloadRaw : PadTo(payloadRaw, 64);
        var echo = Encoding.ASCII.GetBytes(magic.Replace("|J|", "|E|").Replace("|H|", "|E|"));
        var buf = new byte[160];
        var address = targets[0].Address;
        var basePort = targets[0].Port;
        var rnd = new Random();
        // B2 持续彩票口：累计覆盖到 budget（budget<=targets.Count 时不喷洒，如 cone×cone）
        var lottery = new List<IPEndPoint>();
        var seen = new HashSet<int>(targets.Select(t => t.Port));
        var sprayEnabled = budget > targets.Count;
        var effSpan = sprayEnabled ? Math.Max(span, budget) : span;   // 半径不足则放宽，保证预算放得下
        const int refreshMs = 1500;
        var nextRefreshAt = 0L;
        var sw = Stopwatch.StartNew();
        var round = 0;
        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            // 持续重抽：按「窗口内均匀铺满预算」定批量，并受 PunchPps 限速（防泛洪检测）
            if (sprayEnabled && sw.ElapsedMilliseconds >= nextRefreshAt)
            {
                var remaining = budget - targets.Count - lottery.Count;
                var perWindow = (int)Math.Ceiling(remaining * (double)refreshMs / Math.Max(1, timeoutMs));
                var cap = Math.Max(1, PunchPps * refreshMs / 1000);
                var add = Math.Min(Math.Max(perWindow, 1), cap);
                var added = 0;
                for (var tries = 0; added < add && tries < add * 30; tries++)
                {
                    var p = basePort + rnd.Next(-effSpan, effSpan + 1);
                    if (p is > 1024 and < 65535 && seen.Add(p))
                    {
                        lottery.Add(new IPEndPoint(address, p));
                        added++;
                    }
                }
                if (added > 0)
                    P2pTrace.Log($"彩票口扩窗 +{added} → 累计 {targets.Count + lottery.Count}/{budget}（±{effSpan}，第 {round} 轮）");
                nextRefreshAt = sw.ElapsedMilliseconds + refreshMs;
            }
            var count = targets.Count + lottery.Count;
            var chunks = Math.Max(1, (count + 15) / 16);
            for (var si = 0; si < sockets.Count; si++)
            {
                var s = sockets[si];
                if (round < 3)
                {
                    // 首 3 轮：结构化目标全量突发（首包命中率）
                    foreach (var t in targets) SendTo(s, payload, t);
                    // 彩票口按分片轮转铺开（不参与全量突发，避免瞬时泛洪）
                    if (lottery.Count > 0)
                    {
                        var lchunks = Math.Max(1, (lottery.Count + 15) / 16);
                        var lfrom = ((round + si) % lchunks) * 16;
                        var lto = Math.Min(lottery.Count, lfrom + 16);
                        for (var j = lfrom; j < lto; j++) SendTo(s, payload, lottery[j]);
                    }
                }
                else
                {
                    // 每 socket 轮转 16 目标分片：稳态 pps = sockets×16/180ms
                    var chunk = (round + si) % chunks;
                    var from = chunk * 16;
                    var to = Math.Min(count, from + 16);
                    for (var j = from; j < to; j++)
                        SendTo(s, payload, j < targets.Count ? targets[j] : lottery[j - targets.Count]);
                }
            }
            round++;
            var roundMs = round <= 3 ? 70 : 180;
            var phaseEnd = sw.ElapsedMilliseconds + roundMs;
            while (sw.ElapsedMilliseconds < phaseEnd && sw.ElapsedMilliseconds < timeoutMs
                   && !ct.IsCancellationRequested)
            {
                for (var si = 0; si < sockets.Count; si++)
                {
                    var s = sockets[si];
                    while (s.Poll(0, SelectMode.SelectRead))
                    {
                        var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                        int n;
                        try { n = s.ReceiveFrom(buf, ref from); }
                        catch (SocketException) { break; }
                        var text = Encoding.ASCII.GetString(buf, 0, n);
                        if (!text.StartsWith(validPrefix, StringComparison.Ordinal)) continue;
                        var src = (IPEndPoint)from;
                        // v51 观测回灌：记录对端真实源端点（去重），供回灌信令与定向补打使用
                        if (observedSink is not null && !observedSink.Any(o => o.Port == src.Port))
                            observedSink.Add(src);
                        if (!text.Contains("|E|", StringComparison.Ordinal))
                        {
                            try { s.SendTo(echo, src); } catch { /* 回声失败不致命 */ }
                        }
                        // confirmOnly：仅观测不提交（继续喷洒以收集更多端点），由调用方回灌后再补打
                        if (confirmOnly) continue;
                        return new PunchWin(s, src);
                    }
                }
                if (sw.ElapsedMilliseconds < phaseEnd - 8) Thread.Sleep(8);
            }
        }
        return null;

        static void SendTo(Socket s, byte[] payload, IPEndPoint t)
        {
            // 单目标失败不致命；socket 已被外部释放（ObjectDisposedException）时同样静默，
            // 避免打洞循环被异常中断（取消路径上池 socket 可能先于本循环释放）
            try { s.SendTo(payload, t); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>IPv6 直连测试：逐个候选 TCP 连接（C 收敛为单候选 2s、整体 ≤6s），成功返回已连接的 TcpClient。
    /// v46.2：超时/失败只记日志并试下一候选——原实现超时的 OperationCanceledException 会
    /// 外溢中断整个加入流程（连中转回退都到不了），顺手修复。</summary>
    private static async Task<TcpClient?> TryV6DirectAsync(IReadOnlyList<string> v6List, int port, CancellationToken ct)
    {
        // C 超时收敛：原每候选硬超时 5s，3 个候选白烧 15s；改为单候选 2s、整体不超过 6s
        const int PerCandidateMs = 2000;
        const int TotalBudgetMs = 6000;
        var sw = Stopwatch.StartNew();
        foreach (var v6 in v6List)
        {
            if (sw.ElapsedMilliseconds >= TotalBudgetMs) break;
            if (!IPAddress.TryParse(v6, out var ip))
            {
                P2pTrace.Log($"v6 候选无效已跳过：{v6}");
                continue;
            }
            if (IPAddress.IsLoopback(ip))   // C-b 兜底：回环候选（::1）绝不可用
            {
                P2pTrace.Log($"v6 候选为回环地址已跳过：[{v6}]");
                continue;
            }
            TcpClient? client = null;
            var ok = false;
            try
            {
                client = new TcpClient(AddressFamily.InterNetworkV6);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(PerCandidateMs);
                await client.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
                P2pTrace.Log($"v6 候选连接成功：[{v6}]:{port}");
                ok = true;
                return client;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                P2pTrace.Log($"v6 候选超时（{PerCandidateMs / 1000}s 无响应，可能被防火墙拦截或非全球地址）：[{v6}]:{port}");
            }
            catch (Exception e)
            {
                P2pTrace.Log($"v6 候选连接失败：[{v6}]:{port} · {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                // 失败/超时的候选：显式释放，避免逐个候选累积未关闭的 socket（成功时已随返回值移交调用方）
                if (!ok) { try { client?.Dispose(); } catch { } }
            }
        }
        return null;
    }

    private static async Task PumpTcpToKcp(TcpClient mc, KcpConversation conv, CancellationToken ct)
    {
        var buf = new byte[65536];
        var stream = mc.GetStream();
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await stream.ReadAsync(buf, ct).ConfigureAwait(false); }
            catch { break; }
            if (n == 0) break;
            try { if (!await conv.SendAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false)) break; }
            catch { break; }
        }
    }

    private static async Task PumpKcpToTcp(KcpConversation conv, TcpClient mc, CancellationToken ct)
    {
        var buf = new byte[65536];
        var stream = mc.GetStream();
        while (!ct.IsCancellationRequested)
        {
            KcpConversationReceiveResult r;
            try { r = await conv.ReceiveAsync(buf, ct).ConfigureAwait(false); }
            catch { break; }
            if (r.TransportClosed) break;
            // MC 断开后流被释放属正常收尾；异常逃逸会变成未观测任务异常弹窗（好友端实测踩坑）
            try { await stream.WriteAsync(buf.AsMemory(0, r.BytesReceived), ct).ConfigureAwait(false); }
            catch { break; }
        }
    }

    private static async Task CopyStreamToStream(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        var buf = new byte[65536];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await from.ReadAsync(buf, ct).ConfigureAwait(false); }
            catch { break; }
            if (n == 0) break;
            await to.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
        }
    }

    /// <summary>收集本机 Up 网卡的私网 IPv4（10 / 172.16-31 / 192.168 段，排除回环），最多 4 个。
    /// 用途：同网段好友直连候选（P0-E）；AP 隔离环境下探测失败自然回落公网打洞。</summary>
    private static List<string> CollectV4Lan()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = a.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ip.GetAddressBytes();
                    var isLan = b.Length == 4 && (
                        b[0] == 10 ||
                        (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                        (b[0] == 192 && b[1] == 168));
                    if (isLan) list.Add(ip.ToString());
                }
            }
        }
        catch { /* 网卡枚举失败按无 LAN 处理 */ }
        return list.Distinct().Take(4).ToList();
    }

    /// <summary>收集本机全局 IPv6 地址（排除回环/链路本地/站点本地/ULA/多播），最多 4 个。</summary>
    private static List<string> CollectV6()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = a.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetworkV6) continue;
                    // C-b：必须显式排除回环 ::1——其首字节 0x00 不匹配任何前缀，IsIPv6LinkLocal 对它也为 false，
                    // 原过滤会把 ::1 混进候选池，对端连接必然被拒（日志 5 处 [::1] 实证）
                    if (IPAddress.IsLoopback(ip)) continue;
                    if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Teredo || ip.IsIPv6UniqueLocal) continue;
                    var bytes = ip.GetAddressBytes();
                    if (bytes.Length != 16 || bytes[0] == 0xfc || bytes[0] == 0xfd || bytes[0] == 0xff) continue;
                    list.Add(ip.ToString());
                }
            }
        }
        catch { /* 网卡枚举失败按无 v6 处理 */ }
        return list.Distinct().Take(4).ToList();
    }

    /// <summary>房主侧防火墙放行（java.exe 入站，供 IPv6 直连与打洞回包；非管理员时静默失败）。</summary>
    public static void EnsureFirewallRule(string javaExe)
    {
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"MCStudio P2P\"");
            RunNetsh($"advfirewall firewall add rule name=\"MCStudio P2P\" dir=in action=allow program=\"{javaExe}\" enable=yes profile=any");
            // v49：打洞收包方是启动器进程自身（punch socket），PCL exe 也要 UDP 入站放行——
            // 多 exe 部署下旧规则绑的是别的 exe 路径，按名重建绑定当前进程
            var self = Environment.ProcessPath ?? "";
            if (self.Length > 0)
            {
                RunNetsh($"advfirewall firewall delete rule name=\"MCStudio P2P Self\"");
                RunNetsh($"advfirewall firewall add rule name=\"MCStudio P2P Self\" dir=in action=allow " +
                         $"program=\"{self}\" protocol=udp enable=yes profile=any");
            }
        }
        catch { /* 非管理员：回退链兜底 */ }
    }

    private static void RunNetsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        p?.WaitForExit(5000);
    }
}
