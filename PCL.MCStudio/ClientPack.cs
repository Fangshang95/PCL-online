using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PCL.MCStudio;

/// <summary>
/// 客户端整合包描述（v44）：房间携带的"朋友加入所需的客户端包"。
/// Source=local 时 Url 为空（仅本机可用，朋友端会被严格模式拦下并提示房主填链接）；
/// Source=url 时朋友端走下载。LocalPath 仅本机缓存用，不上云。
/// </summary>
public sealed record ClientPackInfo(
    string Name, string Source, string? Url, long Size, string? Sha1,
    string Mc, string Loader, string? LocalPath = null, string? Index = null)
{
    public const string SourceLocal = "local";
    public const string SourceUrl = "url";

    public bool HasDownload => Source == SourceUrl && !string.IsNullOrEmpty(Url);

    /// <summary>
    /// 整合包身份键（v46）：缓存文件名、.mcstudio-pack 标记、实例名三处共用——
    /// 同包跨房间/跨房间码复用同一份安装。Sha1（房主端已补）→ Url 哈希 → Name+Size 兜底，
    /// 双端 cp 同源于云端 JSON，键必一致。
    /// </summary>
    public string PackKey()
    {
        if (!string.IsNullOrEmpty(Sha1)) return "s:" + Sha1;
        if (!string.IsNullOrEmpty(Url)) return "u:" + Sha1Hex(Url);
        return "n:" + Sha1Hex(Name) + "-" + Size;
    }

    /// <summary>上云/缓存键用的精简节点（不含 LocalPath）。</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["name"] = Name, ["source"] = Source, ["size"] = Size,
            ["mc"] = Mc, ["loader"] = Loader,
        };
        if (!string.IsNullOrEmpty(Url)) o["url"] = Url;
        if (!string.IsNullOrEmpty(Sha1)) o["sha1"] = Sha1;
        if (!string.IsNullOrEmpty(Index)) o["index"] = Index;   // v50.6.8：mrpack 精确清单（≤16KB 文本）
        return o;
    }

    public static ClientPackInfo? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var name = o["name"]?.GetValue<string>() ?? "";
        var url = o["url"]?.GetValue<string>();
        var index = o["index"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(url)
            && string.IsNullOrWhiteSpace(index)) return null;
        return new ClientPackInfo(
            name,
            o["source"]?.GetValue<string>() ?? (string.IsNullOrEmpty(url) ? SourceLocal : SourceUrl),
            string.IsNullOrEmpty(url) ? null : url,
            o["size"]?.GetValue<long>() ?? 0,
            o["sha1"]?.GetValue<string>(),
            o["mc"]?.GetValue<string>() ?? "",
            o["loader"]?.GetValue<string>() ?? "",
            Index: string.IsNullOrEmpty(index) ? null : index);
    }

    /// <summary>本地缓存路径：{clientpacks}\{PackKey 前 16}.zip（与 marker/实例名同键，v46）。</summary>
    public string CachePath()
    {
        var key = PackKey().Replace(":", "");
        return Path.Combine(ClientPackRoot(), key[..Math.Min(16, key.Length)] + ".zip");
    }

    public static string ClientPackRoot()
        => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcstudio", "clientpacks");

    /// <summary>计算文件 sha1（小写十六进制）；失败返回 null。</summary>
    public static string? Sha1OfFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    public static string Sha1Hex(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
    }
}
