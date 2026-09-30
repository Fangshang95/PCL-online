using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PCL.MCStudio;

/// <summary>
/// 模组端侧检测（v42）：判断 jar 是否为纯客户端模组——装进专用服务端必崩（如 ETF 实测）或纯无用。
/// 信号优先级：文件名特征库 &gt; Forge mods.toml 的 side=CLIENT &gt; Fabric fabric.mod.json 的 environment=client。
/// 检测失败一律放行（宁可多装，不可误杀双端必需模组）；服务端仍崩时由崩溃诊断兜底点名。
/// </summary>
public static class ModSideDetector
{
    /// <summary>高置信度纯客户端模组文件名特征（渲染/UI/地图/皮肤类，服务端缺失无任何影响）。
    /// 匹配时把文件名压平（去 -_ 空格转小写）后 Contains，避免分隔符变体漏网。</summary>
    private static readonly (string Key, string Label)[] KnownClientMods =
    {
        ("entitytexturefeatures", "ETF 实体材质特效"),
        ("skinlayers", "3D 皮肤层"),
        ("oculus", "光影加载器 Oculus"),
        ("sodium", "渲染优化 Sodium 系"),
        ("embeddium", "渲染优化 Embeddium"),
        ("rubidium", "渲染优化 Rubidium"),
        ("xenon", "渲染优化 Xenon"),
        ("nvidium", "渲染优化 Nvidium"),
        ("xaerosminimap", "Xaero 小地图"),
        ("xaerosworldmap", "Xaero 世界地图"),
        ("journeymap", "JourneyMap 地图"),
        ("voxelmap", "VoxelMap 地图"),
        ("firstpersonmodel", "第一人称模型"),
        ("notenoughanimations", "更多动画"),
        ("betterclouds", "更好云层"),
        ("custommainmenu", "自定义主菜单"),
        ("fancymenu", "FancyMenu 菜单"),
        ("drippyloadingscreen", "加载屏美化"),
        ("konkrete", "菜单 UI 库 Konkrete"),
    };

    /// <summary>检测结果：Skip=true 表示不该装进服务端；Reason 供进度显示（如"纯客户端模组（3D 皮肤层）"）。</summary>
    public static (bool Skip, string Reason) Inspect(string jarPath, string roomLoader)
    {
        var name = Path.GetFileName(jarPath);
        try
        {
            // 0) 文件名特征（最快，两种加载器通用；压平比较）
            var flat = name.ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
            foreach (var (key, label) in KnownClientMods)
                if (flat.Contains(key))
                    return (true, $"纯客户端模组（{label}）");

            using var zip = ZipFile.OpenRead(jarPath);

            // 1) Forge/NeoForge：META-INF/mods.toml 的 side 字段（全部条目都 CLIENT 才判客户端；
            //    实测 ETF 7.0.6 声明 side=CLIENT 且其双向 mixin 在专用服务端必崩）
            var toml = ReadEntry(zip, "META-INF/mods.toml") ?? ReadEntry(zip, "META-INF/neoforge.mods.toml");
            if (toml is not null)
            {
                var sides = Regex.Matches(toml, @"(?m)^\s*side\s*=\s*""(\w+)""")
                    .Select(m => m.Groups[1].Value.ToUpperInvariant()).ToList();
                if (sides.Count > 0 && sides.All(s => s == "CLIENT"))
                    return (true, "Forge 声明仅客户端（side=CLIENT）");
            }

            // 2) Fabric：fabric.mod.json 的 environment 字段（"client"=仅客户端；"*"/"server" 放行）。
            //    Forge 房同样检查：经 Sinytra Connector 混跑的 Fabric 声明客户端模组同样不该进服务端
            var fmj = ReadEntry(zip, "fabric.mod.json");
            if (fmj is not null)
            {
                var env = Regex.Match(fmj, @"""environment""\s*:\s*""(\w+)""");
                if (env.Success && env.Groups[1].Value == "client")
                    return (true, "Fabric 声明仅客户端");
            }
        }
        catch
        {
            // jar 损坏/读不出：放行，服务端启动报错时由崩溃诊断兜底点名
        }
        return (false, "");
    }

    private static string? ReadEntry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e is null) return null;
        using var s = e.Open();
        using var sr = new StreamReader(s, Encoding.UTF8);
        return sr.ReadToEnd();
    }
}
