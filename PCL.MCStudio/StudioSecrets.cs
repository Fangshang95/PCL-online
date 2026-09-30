using System.Reflection;

namespace PCL.MCStudio;

/// <summary>
/// v50.9.6 构建期内嵌配置读取器。真实 api.json / host.json 由打包机在
/// StudioSecretsDir（默认 D:\PClonline\secrets\）提供，csproj 以 Exists 条件内嵌进
/// 程序集——源码树与公开仓库不含任何真实地址/凭证（设计原则"IP 不进源码"不变，
/// 只是构建产物自包含）。运行时兜底链 = 磁盘文件（可手改覆盖）→ 内嵌资源 → 占位默认。
/// </summary>
public static class StudioSecrets
{
    /// <summary>读取内嵌的密钥配置文本；不存在返回 null。</summary>
    public static string? EmbeddedJson(string fileName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) return null;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch { return null; }
    }
}
