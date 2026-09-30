using System.IO;
using System.Text.Json.Nodes;

namespace PCL.MCStudio;

/// <summary>一个已保存的服务器档案：关服后可随时按原配置重开（同一 slug 复用同一端口与服务端目录）。</summary>
public sealed record ServerProfile(string Slug, string Display, string Mc, string Loader,
    long CreatedAt, long LastOpenedAt, string LastCode,
    ClientPackInfo? ClientPack = null)
{
    /// <summary>列表展示文本：显示名 + 版本/加载器。</summary>
    public string ListItem => $"{Display}（{Mc} · {Loader}）";
}

/// <summary>
/// 本地服务器档案列表：存 {exe}\mcstudio\servers.json（UTF-8，无 BOM 也可，自产自销）。
/// 设计要点：
///  - slug 是云端 allocate 的房间名：同 slug 租约存活期内复用同一端口/房间码，
///    服务端目录（ServerRoot/{slug}）也随 slug 隔离——重开零重装；
///  - 一次只能开一个服（IsHosting 单例不变），档案只是"存着以后开"；
///  - 1 号档案固定 host-{前缀}，与历史行为无缝衔接（老用户的服务端目录继续复用）。
/// </summary>
public static class ServerListStore
{
    /// <summary>
    /// 档案存放位置。v50.10 起应用层在 app\ 子目录里，但服务器档案属于用户数据：
    /// 老位置（启动器根目录，即工作目录）已有档案时继续沿用，避免升级后档案"消失"。
    /// </summary>
    private static readonly string StorePath = ResolveStorePath();

    private static string ResolveStorePath()
    {
        const string rel = "mcstudio";
        const string file = "servers.json";
        var exeSide = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rel, file);
        var cwdSide = Path.Combine(Environment.CurrentDirectory, rel, file);
        if (File.Exists(cwdSide)) return cwdSide;
        return Directory.Exists(Path.GetDirectoryName(cwdSide)!) ? cwdSide : exeSide;
    }

    private static readonly object Lock = new();

    public static List<ServerProfile> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new List<ServerProfile>();
            var node = JsonNode.Parse(File.ReadAllText(StorePath));
            if (node is not JsonArray arr) return new List<ServerProfile>();
            var list = new List<ServerProfile>();
            foreach (var item in arr.OfType<JsonObject>())
            {
                var slug = item["slug"]?.GetValue<string>() ?? "";
                var mc = item["mc"]?.GetValue<string>() ?? "";
                if (slug == "" || mc == "") continue;
                list.Add(new ServerProfile(
                    slug,
                    item["display"]?.GetValue<string>() ?? slug,
                    mc,
                    item["loader"]?.GetValue<string>() ?? "vanilla",
                    item["created_at"]?.GetValue<long>() ?? 0,
                    item["last_opened_at"]?.GetValue<long>() ?? 0,
                    item["last_code"]?.GetValue<string>() ?? "",
                    ClientPackInfo.FromJson(item["client_pack"])));
            }
            // 最近开过的排前面
            return list.OrderByDescending(p => p.LastOpenedAt).ThenBy(p => p.CreatedAt).ToList();
        }
        catch
        {
            return new List<ServerProfile>();
        }
    }

    /// <summary>写入或更新档案（按 slug 匹配；display 相同视为更新而非新建）。</summary>
    public static void Upsert(ServerProfile profile)
    {
        lock (Lock)
        {
            var list = Load();
            list.RemoveAll(p => p.Slug == profile.Slug);
            list.Add(profile);
            Save(list);
        }
    }

    public static ServerProfile? Find(string slug) =>
        Load().FirstOrDefault(p => p.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>新档案的房间名：1 号 = host-{前缀}（兼容老目录/老租约），之后 host-{前缀}{2}、{3}…</summary>
    public static string NextSlug(string baseSlug)
    {
        var list = Load();
        if (!list.Any(p => p.Slug.Equals(baseSlug, StringComparison.OrdinalIgnoreCase)))
            return baseSlug;
        for (var n = 2; n < 100; n++)
        {
            var candidate = $"{baseSlug}{n}";
            if (!list.Any(p => p.Slug.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return $"{baseSlug}-{DateTime.Now:HHmmss}";
    }

    /// <summary>显示名防重：同名追加序号（"1.20.1 Forge"、"1.20.1 Forge 2"…）。</summary>
    public static string NextDisplay(string baseDisplay)
    {
        var list = Load();
        if (!list.Any(p => p.Display == baseDisplay)) return baseDisplay;
        for (var n = 2; n < 100; n++)
        {
            var candidate = $"{baseDisplay} {n}";
            if (!list.Any(p => p.Display == candidate)) return candidate;
        }
        return $"{baseDisplay}-{DateTime.Now:HHmmss}";
    }

    private static void Save(List<ServerProfile> list)
    {
        try
        {
            var arr = new JsonArray();
            foreach (var p in list)
            {
                var rec = new JsonObject
                {
                    ["slug"] = p.Slug,
                    ["display"] = p.Display,
                    ["mc"] = p.Mc,
                    ["loader"] = p.Loader,
                    ["created_at"] = p.CreatedAt,
                    ["last_opened_at"] = p.LastOpenedAt,
                    ["last_code"] = p.LastCode,
                };
                if (p.ClientPack is not null) rec["client_pack"] = p.ClientPack.ToJson();
                arr.Add(rec);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, arr.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch
        {
            // 存档失败不阻塞开房（下次成功再写）
        }
    }
}
