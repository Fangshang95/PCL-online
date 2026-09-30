using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.MCStudio;

/// <summary>
/// v50.8 包身份：房主「在用什么包」的最小描述（每项约 100–130 字节）。
/// 玩家端凭 src+pid+vid 用 PCL 原生下载/安装管线装同款包，房主端不上传任何文件本体，
/// 云端只存这几十字节 —— 对比 v50.7 逐文件清单（42–129 KB/房）降 50–100 倍。
/// </summary>
/// <param name="Kind">modpack / resourcepack / shaderpack / datapack</param>
/// <param name="Src">modrinth / curseforge</param>
/// <param name="Pid">项目 ID（Modrinth 为 project_id，CurseForge 为数字 ID）</param>
/// <param name="Vid">版本 ID（Modrinth 为 version id，CurseForge 为 file id）</param>
/// <param name="Name">显示名（整合包名 / 资源包名）</param>
/// <param name="Fn">实例内文件名（资源类用于判断玩家端是否已存在）</param>
public sealed record PackIdentity(string Kind, string Src, string Pid, string Vid,
    string Name = "", string Fn = "")
{
    public const string KindModpack = "modpack";
    public const string KindResource = "resourcepack";
    public const string KindShader = "shaderpack";
    public const string KindData = "datapack";

    public JsonObject ToJson() => new()
    {
        ["k"] = Kind,
        ["s"] = Src,
        ["p"] = Pid,
        ["v"] = Vid,
        ["n"] = Name,
        ["f"] = Fn,
    };

    public static PackIdentity? FromJson(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        var pid = o["p"]?.GetValue<string>() ?? "";
        if (pid.Length == 0) return null;    // 无项目 ID 即无身份，玩家端无从下载
        return new PackIdentity(
            o["k"]?.GetValue<string>() ?? "",
            o["s"]?.GetValue<string>() ?? "modrinth",
            pid,
            o["v"]?.GetValue<string>() ?? "",
            o["n"]?.GetValue<string>() ?? "",
            o["f"]?.GetValue<string>() ?? "");
    }
}

/// <summary>
/// 房间包清单（v50.8）：一个整合包 + 若干资源包/光影包/数据包。整房典型 &lt; 1 KB。
/// </summary>
public sealed record RoomPacks(PackIdentity? Modpack, IReadOnlyList<PackIdentity> Extras)
{
    public JsonObject ToJson()
    {
        var extras = new JsonArray();
        foreach (var e in Extras) extras.Add(e.ToJson());
        return new JsonObject
        {
            ["modpack"] = Modpack?.ToJson(),
            ["extras"] = extras,
        };
    }

    public static RoomPacks? FromJson(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        var mp = PackIdentity.FromJson(o["modpack"]);
        var extras = new List<PackIdentity>();
        if (o["extras"] is JsonArray arr)
            foreach (var e in arr)
                if (PackIdentity.FromJson(e) is { } p) extras.Add(p);
        return mp is null && extras.Count == 0 ? null : new RoomPacks(mp, extras);
    }

    public int Count => (Modpack is null ? 0 : 1) + Extras.Count;
}

/// <summary>
/// 房主端采集器（v50.8）：只读身份、不读文件内容。
/// 整合包走实例标记（PCL 安装时已写 ModpackSource/ModpackId/ModpackVersion，无需改动 ModModpack）；
/// 资源包/光影包/数据包走 sha1 反查（复用 v50.7 的 FindModFilesByHashAsync，单次 ≤512 个），
/// 因此外部导入、手动拷贝的文件同样能识别 —— 比在下载页埋点覆盖面更广。
/// </summary>
public static class HostPackScanner
{
    /// <summary>实例内资源类目录 → 包类型（与 v50.7 清单 path 白名单一致）。</summary>
    private static readonly (string Dir, string Kind)[] ExtraDirs =
    [
        ("resourcepacks", PackIdentity.KindResource),
        ("shaderpacks", PackIdentity.KindShader),
        ("datapacks", PackIdentity.KindData),
    ];

    /// <summary>扫描实例目录里的资源包/光影包/数据包，sha1 反查得身份。
    /// 未命中（CurseForge 独占 / 自制 / 他人修改过）的文件计入 Skipped —— 交由 L3 清单兜底。</summary>
    public static async Task<(List<PackIdentity> Packs, int Skipped)> ScanExtrasAsync(
        string? instanceDir, CancellationToken ct = default)
    {
        var packs = new List<PackIdentity>();
        var skipped = 0;
        if (string.IsNullOrEmpty(instanceDir) || !Directory.Exists(instanceDir)) return (packs, skipped);

        // 1) 收集候选文件（只取 zip —— 资源包/光影包/数据包均为 zip）
        var candidates = new List<(string Full, string Fn, string Kind, string Sha1)>();
        foreach (var (dir, kind) in ExtraDirs)
        {
            var path = Path.Combine(instanceDir, dir);
            if (!Directory.Exists(path)) continue;
            foreach (var full in Directory.EnumerateFiles(path, "*.zip"))
            {
                ct.ThrowIfCancellationRequested();
                var sha1 = ClientPackInfo.Sha1OfFile(full);
                if (sha1 is null) { skipped++; continue; }
                candidates.Add((full, Path.GetFileName(full), kind, sha1));
            }
        }
        if (candidates.Count == 0) return (packs, skipped);

        // 2) 一次批量反查（Chunk 512 内为单次 API 调用）
        var found = await McSources.FindModFilesByHashAsync(
            candidates.Select(c => c.Sha1), ct).ConfigureAwait(false);

        foreach (var c in candidates)
        {
            if (!found.TryGetValue(c.Sha1, out var f) || f.ProjectId.Length == 0) { skipped++; continue; }
            packs.Add(new PackIdentity(c.Kind, "modrinth", f.ProjectId, f.VersionId,
                Path.GetFileNameWithoutExtension(c.Fn), c.Fn));
        }
        return (packs, skipped);
    }
}
