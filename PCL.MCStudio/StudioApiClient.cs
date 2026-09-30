using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.MCStudio;

// ---------------------------------------------------------------- 数据模型

/// <summary>房间模组条目（Url 为可选的公开直链——Modrinth mrpack 导入时携带，加入端优先按直链下载）。</summary>
public sealed record ModEntry(string Project, string? Version, string? Filename, string? Url = null);

/// <summary>allocate 结果：v50 起携带房间级临时凭证——FrpsToken 连 frps 用（S2，取代随包静态 token），
/// LeaseToken 释放租约用（S1，匿名开房也只放行凭证持有者）。旧服务端两者为空。</summary>
public sealed record AllocateResult(string Room, int RemotePort, string Address, string? RoomCode,
    int? LeaseHours, string FrpsToken = "", string LeaseToken = "");

/// <summary>虚拟局域网房间描述（v48）：mode=lan 时房主 MC「对局域网开放」捕获的世界端口与名称。</summary>
public sealed record LanInfo(int Port, string Motd);

public sealed record RoomInfo(string Code, string Address, int Port, string Mc, string Loader,
    IReadOnlyList<ModEntry> Mods, int ExpiresIn, ClientPackInfo? ClientPack = null,
    string Mode = "server", LanInfo? Lan = null, RoomPacks? Packs = null);

// ---------------------------------------------------------------- P2P 直连信令

/// <summary>P2P 候选上报内容：v6 全局地址列表 + v4 STUN 映射（空=不可用）+ NAT 类型 + 房主本地 MC 端口。</summary>
/// <summary>本端候选：V6 全局地址、V4 公网映射、NAT 类型、V4Lan 私网地址（同网段直连）、
/// PunchPort 本端打洞 socket 池首口（LAN 候选的实际监听口）。
/// PredPorts 为 P1-C 端口递增预测：对称 NAT 下预测下一次映射的端口窗口（cone 为空）。
/// UpnpPort 为 P1-D 主动端口映射的外部端口（0=路由器不支持或未开启）。
/// v49.4 池化字段：PunchPorts=池内全部 socket 本地端口，Maps=每 socket 的公网映射（ip:port），
/// Delta=端口递增步长（0=学不到）；纯转发字段，旧服务端/旧对端不解析（向后兼容）。
/// Ver=本端协议版本（v50 起上报；云端双方均 ≥50 才下发房间密钥，旧端回落静态 magic）。</summary>
public sealed record P2pCandidates(IReadOnlyList<string> V6, string V4, string Nat, int Port,
    IReadOnlyList<string>? V4Lan = null, int PunchPort = 0, IReadOnlyList<int>? PredPorts = null,
    int UpnpPort = 0, IReadOnlyList<int>? PunchPorts = null, IReadOnlyList<string>? Maps = null,
    int Delta = 0, int Ver = P2pProtocol.Version);

/// <summary>对方候选（服务器转发的 host/joiner 候选，SrcV4 为服务器观察到的源地址）。
/// Targeted=True 表示这是 host 为当前加入者单独开路的定向候选（多人 P2P）。
/// PunchAt 为服务器锚定的同步起打时刻（服务器时钟，0=旧服务端无此字段）。
/// PredPorts 为对方的端口递增预测窗口（P1-C；空/缺省=旧端或 cone，忽略即可）。
/// UpnpPort 为对方的主动端口映射外部端口（P1-D；0=无映射，可直连入站）。
/// v49.4 池化字段：PunchPorts=对端 socket 池本地端口，Maps=对端每 socket 公网映射，Delta=端口步长。</summary>
public sealed record P2pPeer(string Email, IReadOnlyList<string> V6, string V4, string Nat, int Port,
    string SrcV4, bool Targeted = false, IReadOnlyList<string>? V4Lan = null, int PunchPort = 0,
    double PunchAt = 0, IReadOnlyList<int>? PredPorts = null, int UpnpPort = 0,
    IReadOnlyList<int>? PunchPorts = null, IReadOnlyList<string>? Maps = null, int Delta = 0);

/// <summary>候选交换结果：Peer 为对方最新候选（无=对方尚未上报，调用方可轮询），
/// Peers 为 role=host 时的全部加入者候选（多人发现），YourV4 为服务器看到的本端公网 v4，
/// Feat 为云端灰度特性开关（未知特性忽略，向后兼容），SrvNow 为响应时刻的服务器时钟（0=旧服务端）。
/// Secret 为房间级打洞密钥（v50 M6：双方版本均 ≥50 时由云端下发，用于 HMAC 化 KCP 握手 magic；
/// 空=任一端为旧版本，双方回落静态 magic）。</summary>
public sealed record P2pSignal(P2pPeer? Peer, string YourV4, IReadOnlyList<P2pPeer>? Peers = null,
    IReadOnlyDictionary<string, bool>? Feat = null, double SrvNow = 0, string Secret = "");

/// <summary>P2P 协议版本（v50）：随候选上报，云端据此协商房间密钥下发（双方 ≥50 才启用 HMAC magic）。</summary>
public static class P2pProtocol
{
    public const int Version = 50;
}

public sealed record FrpcState(bool Running, int? Pid, string ConfigPath);

/// <summary>房间中转用量（云端从 frps 流量计数聚合；BytesIn=云端发给玩家，BytesOut=玩家发给云端）。</summary>
public sealed record RoomUsage(long BytesIn, long BytesOut, long TotalBytes, int CurConns,
    bool Online, long? UpdatedAt);

/// <summary>平台 API 配置（云端 tunnel-api 地址与共享密钥，从本地 JSON 文件读取，不硬编码）。</summary>
public sealed record StudioConfig(string BaseUrl, string Key)
{
    /// <summary>从 JSON（{"url": "...", "key": "..."}）读取配置。</summary>
    public static StudioConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>v50.9.6：支持从内嵌配置文本解析（磁盘 api.json 缺失时的自包含兜底）。</summary>
    public static StudioConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var url = doc.RootElement.GetProperty("url").GetString()
            ?? throw new InvalidDataException("配置缺少 url 字段");
        var key = doc.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null;
        return new StudioConfig(url.TrimEnd('/'), key ?? "");
    }
}

// ---------------------------------------------------------------- API 客户端

/// <summary>云端 tunnel-api 客户端：端口租约分配、房间注册/解析/释放。</summary>
public sealed class StudioApiClient : IDisposable
{
    private readonly HttpClient _http;

    public string BaseUrl { get; }

    /// <summary>API 主机（STUN-lite 与打洞目标同主机）。</summary>
    public string ApiHost => _apiHost ??= new Uri(BaseUrl).Host;
    private string? _apiHost;

    public StudioApiClient(StudioConfig config)
    {
        BaseUrl = config.BaseUrl;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/") , Timeout = TimeSpan.FromSeconds(15)};
        if (!string.IsNullOrEmpty(config.Key))
            _http.DefaultRequestHeaders.Add("X-Auth-Token", config.Key);
    }

    /// <summary>分配端口（幂等：同房间返回同端口），返回远程端口与地址。</summary>
    public async Task<AllocateResult> AllocateAsync(string room, CancellationToken ct = default)
    {
        using var doc = await PostJsonAsync($"/v1/allocate", new { room }, ct).ConfigureAwait(false);
        return new AllocateResult(
            doc.RootElement.GetProperty("room").GetString() ?? room,
            doc.RootElement.GetProperty("remote_port").GetInt32(),
            doc.RootElement.GetProperty("address").GetString() ?? "",
            doc.RootElement.TryGetProperty("room_code", out var c) ? c.GetString() : null,
            doc.RootElement.TryGetProperty("lease_hours", out var h) ? h.GetInt32() : null,
            doc.RootElement.TryGetProperty("frps_token", out var ft) ? ft.GetString() ?? "" : "",
            doc.RootElement.TryGetProperty("lease_token", out var lt) ? lt.GetString() ?? "" : "");
    }

    /// <summary>注册房间元信息（幂等：返回同一房间码），朋友端据此解析。
    /// ownerToken 为开房人的玩家登录令牌，服务端校验后绑定开房人（供好友查看）。
    /// clientPack 为客户端整合包描述（v44，云端白名单+截断；LocalPath 不上云）。
    /// lan 为虚拟局域网描述（v48，mode=lan；重复注册携带新端口即热切换）。
    /// hb=true（v50.6.1）注册为心跳房间：云端 TTL 收紧为 180s，须 HeartbeatAsync 每 55s 续期；
    /// 退出即销毁语义由「主动 release + 心跳停 3 分钟自动回收」共同保证；旧服务端忽略该字段。</summary>
    /// packs 为 v50.8 包身份清单（整合包 + 资源包/光影包/数据包，典型 &lt;1KB）：
    /// 玩家端据此用 PCL 原生管线装同款包；云端白名单截断存储，旧服务端忽略该字段。
    public async Task<string> RegisterRoomAsync(string room, string mc, string loader,
        IEnumerable<ModEntry> mods, string? ownerToken = null, CancellationToken ct = default,
        ClientPackInfo? clientPack = null, LanInfo? lan = null, bool hb = false,
        RoomPacks? packs = null)
    {
        using var doc = await PostJsonAsync("/v1/rooms",
            new
            {
                room,
                mc,
                loader,
                mods = mods.Select(m => new { project = m.Project, version = m.Version, filename = m.Filename }),
                owner_token = ownerToken ?? "",
                clientpack = clientPack?.ToJson(),
                packs = packs?.ToJson(),
                lan = lan is null ? null : new { port = lan.Port, motd = lan.Motd },
                hb = hb ? (bool?)true : null,
            },
            ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }

    /// <summary>v50.6.1 房主心跳：续期 hb 房间（同房间码），房间已被云端回收时返回 null。
    /// bearer 为开房人玩家令牌（服务端仅放行房主本人）。网络错误抛出，由调用方决定重试。</summary>
    public async Task<string?> HeartbeatAsync(string room, string bearer, CancellationToken ct = default)
    {
        try
        {
            using var doc = await PostJsonAsync("/v1/rooms/heartbeat",
                new { room }, bearer, ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : room;
        }
        catch (StudioApiException ex) when (ex.StatusCode == 404)
        {
            return null;    // 房间已被回收：调用方应停止心跳（下次开联机自动开新房间）
        }
    }

    /// <summary>按房间码解析房间（公开接口，朋友端无需密钥）。</summary>
    public async Task<RoomInfo> ResolveAsync(string code, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"/v1/rooms/{Uri.EscapeDataString(code.ToUpperInvariant())}", ct).ConfigureAwait(false);
        var mods = new List<ModEntry>();
        if (doc.RootElement.TryGetProperty("mods", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var m in arr.EnumerateArray())
            {
                var p = m.TryGetProperty("project", out var pj) ? pj.GetString() : null;
                if (string.IsNullOrEmpty(p)) continue;
                mods.Add(new ModEntry(p!,
                    m.TryGetProperty("version", out var v) ? v.GetString() : null,
                    m.TryGetProperty("filename", out var f) ? f.GetString() : null));
            }
        JsonNode? clientpack = null;
        if (doc.RootElement.TryGetProperty("clientpack", out var cp) && cp.ValueKind == JsonValueKind.Object)
            clientpack = JsonNode.Parse(cp.GetRawText());
        // v50.8：包身份（老服务端/未升级房间无该字段 → null，朋友端回退清单流程）
        JsonNode? packs = null;
        if (doc.RootElement.TryGetProperty("packs", out var pk) && pk.ValueKind == JsonValueKind.Object)
            packs = JsonNode.Parse(pk.GetRawText());
        // v48：lan 描述（老服务端无该字段时缺省 server 模式）
        var mode = doc.RootElement.TryGetProperty("mode", out var md) ? md.GetString() ?? "server" : "server";
        LanInfo? lan = null;
        if (doc.RootElement.TryGetProperty("lan_port", out var lp) && lp.ValueKind == JsonValueKind.Number)
        {
            var lanPort = lp.GetInt32();
            if (lanPort > 0)
                lan = new LanInfo(lanPort,
                    doc.RootElement.TryGetProperty("motd", out var mo) ? mo.GetString() ?? "" : "");
        }
        return new RoomInfo(
            doc.RootElement.GetProperty("code").GetString() ?? code,
            doc.RootElement.GetProperty("address").GetString() ?? "",
            doc.RootElement.GetProperty("port").GetInt32(),
            doc.RootElement.GetProperty("mc").GetString() ?? "",
            doc.RootElement.GetProperty("loader").GetString() ?? "",
            mods,
            doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 0,
            ClientPackInfo.FromJson(clientpack),
            mode, lan, RoomPacks.FromJson(packs));
    }

    /// <summary>释放房间（删除租约与房间码）。leaseToken 为 allocate 下发的归属凭证
    /// （v50 S1：无凭证时云端只放行房主 Bearer 或管理密钥；旧服务端忽略该字段）。
    /// bearer（v50.6.1）为房主玩家令牌——释放走房主鉴权路径（客户端注销场景使用）。</summary>
    public async Task ReleaseAsync(string room, string? leaseToken = null, CancellationToken ct = default,
        string? bearer = null)
        => await PostJsonAsync("/v1/release", new { room, lease_token = leaseToken ?? "" }, bearer, ct)
            .ConfigureAwait(false);

    /// <summary>当前租约列表（管理用）。</summary>
    public async Task<JsonElement> RoomsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("/v1/rooms", ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    // ---------------------------------------------------------------- 玩家账号

    /// <summary>请求邮箱验证码。dev 模式下服务端会直接回传验证码（Code 非空）。</summary>
    public async Task<SendCodeResult> SendCodeAsync(string email, CancellationToken ct = default)
    {
        using var doc = await PostJsonAsync("/v1/auth/sendcode", new { email }, null, ct).ConfigureAwait(false);
        var r = doc.RootElement;
        return new SendCodeResult(
            r.TryGetProperty("dev", out var d) && d.GetBoolean(),
            r.TryGetProperty("code", out var c) ? c.GetString() : null,
            r.TryGetProperty("expire_minutes", out var e) ? e.GetInt32() : 10);
    }

    /// <summary>邮箱注册：验证码校验通过后建号并返回登录令牌。</summary>
    public async Task<UserIdentity> RegisterAsync(string email, string code, string password,
        string? nickname = null, CancellationToken ct = default)
    {
        using var doc = await PostJsonAsync("/v1/auth/register",
            new { email, code, password, nickname = nickname ?? "" }, null, ct).ConfigureAwait(false);
        var token = doc.RootElement.GetProperty("token").GetString() ?? "";
        var nick = ReadNestedString(doc.RootElement, "user", "nickname");
        return new UserIdentity(email, string.IsNullOrEmpty(nick) ? email : nick, token);
    }

    /// <summary>邮箱 + 密码登录，返回登录令牌。</summary>
    public async Task<UserIdentity> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        using var doc = await PostJsonAsync("/v1/auth/login",
            new { email, password }, null, ct).ConfigureAwait(false);
        var token = doc.RootElement.GetProperty("token").GetString() ?? "";
        var nick = ReadNestedString(doc.RootElement, "user", "nickname");
        return new UserIdentity(email, string.IsNullOrEmpty(nick) ? email : nick, token);
    }

    /// <summary>校验令牌是否仍然有效；令牌过期或已失效返回 null。</summary>
    public async Task<UserIdentity?> GetMeAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync("/v1/me", token, ct).ConfigureAwait(false);
            var u = doc.RootElement.GetProperty("user");
            return new UserIdentity(
                u.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                u.TryGetProperty("nickname", out var n) ? n.GetString() ?? "" : "",
                token);
        }
        catch (StudioApiException ex) when (ex.StatusCode is 401 or 403)
        {
            return null;
        }
    }

    /// <summary>添加好友（按邮箱）。对方不存在返回 404，已是好友返回 409。</summary>
    public async Task FriendAddAsync(string token, string email, CancellationToken ct = default)
    {
        using var doc = await PostJsonAsync("/v1/friends/add", new { email }, token, ct).ConfigureAwait(false);
    }

    /// <summary>删除好友（按邮箱）。</summary>
    public async Task FriendRemoveAsync(string token, string email, CancellationToken ct = default)
    {
        using var doc = await PostJsonAsync("/v1/friends/remove", new { email }, token, ct).ConfigureAwait(false);
    }

    /// <summary>好友列表（含在线状态，在线的排前面）。</summary>
    public async Task<IReadOnlyList<FriendEntry>> FriendListAsync(string token, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("/v1/friends/list", token, ct).ConfigureAwait(false);
        var list = new List<FriendEntry>();
        if (doc.RootElement.TryGetProperty("friends", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var f in arr.EnumerateArray())
                list.Add(new FriendEntry(
                    f.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                    f.TryGetProperty("nickname", out var n) ? n.GetString() ?? "" : "",
                    f.TryGetProperty("friends_since", out var s) ? s.GetInt64() : 0,
                    f.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.True));
        return list;
    }

    /// <summary>上报加入房间（服务端记录 membership，供好友查看所在房间）。</summary>
    public async Task JoinRoomAsync(string token, string roomCode, CancellationToken ct = default)
    {
        using var _ = await PostJsonAsync("/v1/rooms/join", new { room_code = roomCode }, token, ct).ConfigureAwait(false);
    }

    /// <summary>查询房间中转用量（房主 Bearer；云端从 frps 流量聚合，限速提示与商业用量依据）。</summary>
    public async Task<RoomUsage> UsageAsync(string token, string roomCode, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            $"/v1/usage?code={Uri.EscapeDataString(roomCode.ToUpperInvariant())}", token, ct).ConfigureAwait(false);
        var r = doc.RootElement;
        return new RoomUsage(
            r.TryGetProperty("bytes_in", out var bi) ? bi.GetInt64() : 0,
            r.TryGetProperty("bytes_out", out var bo) ? bo.GetInt64() : 0,
            r.TryGetProperty("total_bytes", out var tb) ? tb.GetInt64() : 0,
            r.TryGetProperty("cur_conns", out var cc) ? cc.GetInt32() : 0,
            r.TryGetProperty("online", out var on) && on.ValueKind == JsonValueKind.True,
            r.TryGetProperty("updated_at", out var ua) && ua.ValueKind == JsonValueKind.Number
                ? (long?)ua.GetInt64() : null);
    }

    /// <summary>查询好友所开与所在的房间（需好友关系）。</summary>
    public async Task<(IReadOnlyList<RoomBrief> Hosted, IReadOnlyList<RoomBrief> Joined)> FriendRoomsAsync(
        string token, string email, CancellationToken ct = default)
    {
        using var doc = await PostJsonAsync("/v1/friends/rooms", new { email }, token, ct).ConfigureAwait(false);
        return (ReadBriefList(doc.RootElement, "hosted"), ReadBriefList(doc.RootElement, "joined"));
    }

    /// <summary>P2P 候选上报 + 对方候选拉取（信令一次完成；peer 为空表示对方尚未上报，调用方可轮询）。
    /// host 为特定加入者上报定向候选时传 forPeer（多人 P2P：每个加入者一路独立会话）。</summary>
    public async Task<P2pSignal> P2pSignalAsync(string token, string roomCode, string role,
        P2pCandidates candidates, CancellationToken ct = default, string? forPeer = null)
    {
        using var doc = await PostJsonAsync("/v1/rooms/p2p",
            new
            {
                room_code = roomCode,
                role,
                @for = forPeer,
                candidates = new
                {
                    v6 = candidates.V6,
                    v4 = candidates.V4,
                    nat = candidates.Nat,
                    port = candidates.Port,
                    v4lan = candidates.V4Lan ?? (IReadOnlyList<string>)Array.Empty<string>(),
                    punch_port = candidates.PunchPort,
                    pred_ports = candidates.PredPorts ?? (IReadOnlyList<int>)Array.Empty<int>(),
                    upnp_port = candidates.UpnpPort,
                    punch_ports = candidates.PunchPorts ?? (IReadOnlyList<int>)Array.Empty<int>(),
                    maps = candidates.Maps ?? (IReadOnlyList<string>)Array.Empty<string>(),
                    delta = candidates.Delta,
                    v = candidates.Ver,
                },
            }, token, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        P2pPeer? peer = null;
        if (root.TryGetProperty("peer", out var pe) && pe.ValueKind == JsonValueKind.Object)
            peer = ParseP2pPeer(pe);
        var peers = new List<P2pPeer>();
        if (root.TryGetProperty("joiners", out var js) && js.ValueKind == JsonValueKind.Array)
            foreach (var j in js.EnumerateArray())
            {
                if (j.ValueKind == JsonValueKind.Object && ParseP2pPeer(j) is { } p) peers.Add(p);
            }
        var yourV4 = root.TryGetProperty("your_v4", out var yv) ? yv.GetString() ?? "" : "";
        Dictionary<string, bool>? feat = null;
        if (root.TryGetProperty("feat", out var ft) && ft.ValueKind == JsonValueKind.Object)
        {
            feat = new Dictionary<string, bool>();
            foreach (var kv in ft.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.True || kv.Value.ValueKind == JsonValueKind.False)
                    feat[kv.Name] = kv.Value.GetBoolean();
        }
        var srvNow = root.TryGetProperty("srv_now", out var sn) && sn.ValueKind == JsonValueKind.Number
            ? sn.GetDouble() : 0;
        var secret = root.TryGetProperty("secret", out var sc) ? sc.GetString() ?? "" : "";
        return new P2pSignal(peer, yourV4, peers, feat, srvNow, secret);
    }

    /// <summary>P2P 打洞结果埋点（每次连接终点一条；服务端按 NAT 组合聚合成功率）。
    /// 后台尽力上报，失败静默（不阻塞连接流程）。</summary>
    public async Task P2pMetricAsync(string token, object payload, CancellationToken ct = default)
    {
        using var _ = await PostJsonAsync("/v1/metrics/p2p", payload, token, ct).ConfigureAwait(false);
    }

    /// <summary>登出全部设备（v50 M3：作废云端所有会话；本机令牌由调用方清除）。</summary>
    public async Task LogoutAllAsync(string token, CancellationToken ct = default)
    {
        using var _ = await PostJsonAsync("/v1/auth/logout-all", new { }, token, ct).ConfigureAwait(false);
    }

    /// <summary>注销账号（v50 M2：密码确认；云端清除 users/sessions/friends/名下房间全链数据）。</summary>
    public async Task DeleteAccountAsync(string token, string password, CancellationToken ct = default)
    {
        using var _ = await PostJsonAsync("/v1/account/delete", new { password }, token, ct).ConfigureAwait(false);
    }

    private static P2pPeer? ParseP2pPeer(JsonElement pe)
    {
        var v6 = new List<string>();
        if (pe.TryGetProperty("v6", out var v6arr) && v6arr.ValueKind == JsonValueKind.Array)
            foreach (var a in v6arr.EnumerateArray())
                if (a.GetString() is { Length: > 0 } s) v6.Add(s);
        var v4lan = new List<string>();
        if (pe.TryGetProperty("v4lan", out var lanArr) && lanArr.ValueKind == JsonValueKind.Array)
            foreach (var a in lanArr.EnumerateArray())
                if (a.GetString() is { Length: > 0 } s) v4lan.Add(s);
        var pred = new List<int>();
        if (pe.TryGetProperty("pred_ports", out var ppArr) && ppArr.ValueKind == JsonValueKind.Array)
            foreach (var a in ppArr.EnumerateArray())
                if (a.ValueKind == JsonValueKind.Number)
                {
                    var v = a.GetInt32();
                    if (v > 0 && v <= 65535) pred.Add(v);
                }
        var punchPorts = new List<int>();
        if (pe.TryGetProperty("punch_ports", out var ppsArr) && ppsArr.ValueKind == JsonValueKind.Array)
            foreach (var a in ppsArr.EnumerateArray())
                if (a.ValueKind == JsonValueKind.Number)
                {
                    var v = a.GetInt32();
                    if (v > 0 && v <= 65535) punchPorts.Add(v);
                }
        var maps = new List<string>();
        if (pe.TryGetProperty("maps", out var mArr) && mArr.ValueKind == JsonValueKind.Array)
            foreach (var a in mArr.EnumerateArray())
                if (a.GetString() is { Length: > 0 } s) maps.Add(s);
        var delta = pe.TryGetProperty("delta", out var dl) && dl.ValueKind == JsonValueKind.Number
            ? dl.GetInt32() : 0;
        return new P2pPeer(
            pe.TryGetProperty("email", out var em) ? em.GetString() ?? "" : "",
            v6,
            pe.TryGetProperty("v4", out var v4) ? v4.GetString() ?? "" : "",
            pe.TryGetProperty("nat", out var nt) ? nt.GetString() ?? "unknown" : "unknown",
            pe.TryGetProperty("port", out var po) && po.ValueKind == JsonValueKind.Number ? po.GetInt32() : 0,
            pe.TryGetProperty("src_v4", out var sv) ? sv.GetString() ?? "" : "",
            pe.TryGetProperty("targeted", out var tg) && tg.ValueKind == JsonValueKind.True,
            v4lan,
            pe.TryGetProperty("punch_port", out var pp) && pp.ValueKind == JsonValueKind.Number ? pp.GetInt32() : 0,
            pe.TryGetProperty("punch_at", out var pa) && pa.ValueKind == JsonValueKind.Number ? pa.GetDouble() : 0,
            pred,
            pe.TryGetProperty("upnp_port", out var up) && up.ValueKind == JsonValueKind.Number ? up.GetInt32() : 0,
            punchPorts,
            maps,
            delta);
    }

    private static IReadOnlyList<RoomBrief> ReadBriefList(JsonElement root, string prop)
    {
        var list = new List<RoomBrief>();
        if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var r in arr.EnumerateArray())
                list.Add(new RoomBrief(
                    r.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "",
                    r.TryGetProperty("room", out var rm) ? rm.GetString() ?? "" : "",
                    r.TryGetProperty("mc", out var mc) ? mc.GetString() ?? "" : "",
                    r.TryGetProperty("loader", out var ld) ? ld.GetString() ?? "" : "",
                    r.TryGetProperty("address", out var ad) ? ad.GetString() ?? "" : "",
                    r.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 0,
                    !r.TryGetProperty("has_clientpack", out var hcp) || hcp.ValueKind != JsonValueKind.False));
        return list;
    }

    private static string? ReadNestedString(JsonElement root, string obj, string prop)
        => root.TryGetProperty(obj, out var o) && o.TryGetProperty(prop, out var p) ? p.GetString() : null;

    // ---------------------------------------------------------------- HTTP 基础

    private Task<JsonDocument> PostJsonAsync(string path, object body, CancellationToken ct)
        => PostJsonAsync(path, body, null, ct);

    private async Task<JsonDocument> PostJsonAsync(string path, object body, string? bearer, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(body);
        return await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(bearer))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return req;
        }, ct).ConfigureAwait(false);
    }

    private Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
        => GetJsonAsync(path, null, ct);

    private async Task<JsonDocument> GetJsonAsync(string path, string? bearer, CancellationToken ct)
    {
        return await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, path);
            if (!string.IsNullOrEmpty(bearer))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return req;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>统一发送并校验响应：非 2xx 抛 StudioApiException（带服务端 error 文案）。
    /// 移动网络对云机 HTTP 瞬断实测高发（同一桥接流程内前一请求成功、下一请求连接层失败，
    /// 20260908 20:21 开房即此挂法），网络层错误自动重试 2 次（1s/2s 退避）；用户取消不重试。</summary>
    private async Task<JsonDocument> SendAsync(Func<HttpRequestMessage> reqFactory, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpRequestMessage? req = null;
            try
            {
                req = reqFactory();
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                }
                catch (JsonException)
                {
                    throw new StudioApiException((int)resp.StatusCode,
                        text.Length > 200 ? text[..200] : text);
                }
                if (!resp.IsSuccessStatusCode)
                {
                    var msg = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
                    doc.Dispose();
                    throw new StudioApiException((int)resp.StatusCode,
                        string.IsNullOrWhiteSpace(msg) ? $"HTTP {(int)resp.StatusCode}" : msg!);
                }
                return doc;
            }
            // HttpRequestMessage 不能复用：重试必须经 factory 重建请求
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested
                                       && (ex is HttpRequestException || ex is TaskCanceledException))
            {
                await Task.Delay(attempt * 1000, ct).ConfigureAwait(false);
            }
            finally
            {
                req?.Dispose();
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
