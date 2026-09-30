using PCL.Core.App;
using PCL.MCStudio;

namespace PCL;

/// <summary>
/// v50.8 房主端包身份采集：把「房主在用什么包」压成 &lt;1KB 的 RoomPacks 随房间上云，
/// 玩家端据此用 PCL 原生下载/安装管线装同款包（房主端零文件上传、云端零转发）。
///
/// 整合包：读实例标记。PCL 安装整合包时**已经**写入了 ModpackSource / ModpackId /
/// ModpackVersion（见 ModModpack.cs:617/859），身份从未丢失——本类只负责把它读出来，
/// 不改动安装器。
/// 资源包/光影包/数据包：走 sha1 反查（PCL.MCStudio.HostPackScanner），因此对外部导入、
/// 手动拷入、别处下载的文件同样有效——比在下载页埋点覆盖面更广，且零额外埋点代码。
/// </summary>
public static class HostPackCollector
{
    /// <summary>读取实例的整合包身份；无标记（手动建的实例/外部导入）返回 null。</summary>
    public static PackIdentity? ModpackOf(string? instancePath)
    {
        if (string.IsNullOrEmpty(instancePath)) return null;
        try
        {
            var pid = States.Instance.ModpackId[instancePath];
            if (string.IsNullOrEmpty(pid)) return null;      // 没有项目 ID = 没有身份
            var src = States.Instance.ModpackSource[instancePath];
            var vid = States.Instance.ModpackVersion[instancePath];
            var name = instancePath!.TrimEnd('\\', '/');
            var i = name.LastIndexOfAny(['\\', '/']);
            if (i >= 0) name = name[(i + 1)..];
            return new PackIdentity(PackIdentity.KindModpack,
                string.Equals(src, "CurseForge", System.StringComparison.OrdinalIgnoreCase)
                    ? "curseforge" : "modrinth",
                pid!, vid ?? "", name);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>采集整房包身份：整合包（实例标记）+ 资源类（sha1 反查）。
    /// 网络失败不影响开房——未识别的散件仍交由 v50.7 清单兜底。</summary>
    public static async Task<RoomPacks> CollectAsync(string? instancePath,
        CancellationToken ct = default)
    {
        var modpack = ModpackOf(instancePath);
        var extras = new List<PackIdentity>();
        try
        {
            var (found, _) = await HostPackScanner.ScanExtrasAsync(instancePath, ct).ConfigureAwait(false);
            extras = found;
        }
        catch
        {
            // 反查失败（离线/限流）不应阻断开房
        }
        return new RoomPacks(modpack, extras);
    }

    /// <summary>采集并给出可直接上云的结果：没有任何包身份时返回 null（不占云端字段）。</summary>
    public static async Task<RoomPacks?> CollectOrNullAsync(string? instancePath,
        CancellationToken ct = default)
    {
        var packs = await CollectAsync(instancePath, ct).ConfigureAwait(false);
        return packs.Count > 0 ? packs : null;
    }
}
