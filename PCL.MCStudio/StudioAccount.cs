using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PCL.MCStudio;

/// <summary>已登录的玩家身份（邮箱 + 昵称 + 登录令牌）。</summary>
public sealed record UserIdentity(string Email, string Nickname, string Token);

/// <summary>好友条目（Online 由服务端按 last_seen 判定，120 秒内活跃视为在线）。</summary>
public sealed record FriendEntry(string Email, string Nickname, long FriendsSince, bool Online = false);

/// <summary>好友相关房间简报（好友页展示用）。HasClientPack 缺省 true = 老服务端/老房间不误标。</summary>
public sealed record RoomBrief(string Code, string Room, string Mc, string Loader, string Address, int ExpiresIn,
    bool HasClientPack = true);

/// <summary>发送验证码的结果。服务端为 dev 模式时会直接回传验证码，便于本地联调。</summary>
public sealed record SendCodeResult(bool Dev, string? Code, int ExpireMinutes);

/// <summary>服务端返回非 2xx 时抛出，携带 HTTP 状态码与服务端错误信息。</summary>
public class StudioApiException : Exception
{
    public int StatusCode { get; }

    public StudioApiException(int statusCode, string message) : base(message)
        => StatusCode = statusCode;
}

/// <summary>
/// 本地账号存储：把登录令牌落盘，重启启动器后仍保持登录。
/// 只保存邮箱、昵称与令牌，不保存密码。
/// v50 H1：整包 JSON 经 DPAPI（CurrentUser）加密后落盘（{"v":2,"enc":…}），
/// 同机其他程序/用户读不到明文令牌；v1 明文文件读取时自动迁移加密。
/// </summary>
public static class StudioAccount
{
    /// <summary>账号文件路径：优先复用已存在的文件，否则放在启动器目录的 mcstudio 下。</summary>
    public static string FilePath
    {
        get
        {
            string[] roots = { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory };
            foreach (var root in roots)
            {
                var existing = Path.Combine(root, "mcstudio", "account.json");
                if (File.Exists(existing)) return existing;
            }
            var dir = Path.Combine(roots[0], "mcstudio");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "account.json");
        }
    }

    /// <summary>读取本地登录态；文件不存在、损坏或解密失败（如换了 Windows 用户）返回 null（按游客处理）。
    /// 读到 v1 明文格式时顺带回写为加密格式完成迁移。</summary>
    public static UserIdentity? Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            UserIdentity? identity = null;
            if (root.TryGetProperty("enc", out var enc) && enc.GetString() is { Length: > 0 } encText)
            {
                var plain = ProtectedData.Unprotect(Convert.FromBase64String(encText), null,
                    DataProtectionScope.CurrentUser);
                using var inner = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(plain));
                identity = ParseIdentity(inner.RootElement);
            }
            else
            {
                identity = ParseIdentity(root);   // v1 明文（旧版遗留）
                if (identity is not null) Save(identity);   // 自动迁移为加密格式
            }
            return identity;
        }
        catch
        {
            return null;
        }
    }

    private static UserIdentity? ParseIdentity(JsonElement root)
    {
        var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
        var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token)) return null;
        var nickname = root.TryGetProperty("nickname", out var n) ? n.GetString() : null;
        return new UserIdentity(email!, string.IsNullOrEmpty(nickname) ? email! : nickname!, token!);
    }

    /// <summary>保存登录态（覆盖写；DPAPI CurrentUser 加密）。</summary>
    public static void Save(UserIdentity identity)
    {
        var json = JsonSerializer.Serialize(new
        {
            email = identity.Email,
            nickname = identity.Nickname,
            token = identity.Token,
        });
        var enc = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(json), null,
            DataProtectionScope.CurrentUser);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new { v = 2, enc = Convert.ToBase64String(enc) }));
    }

    /// <summary>退出登录：删除本地登录态文件。</summary>
    public static void Clear()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删除失败不影响内存中的退出流程
        }
    }
}
