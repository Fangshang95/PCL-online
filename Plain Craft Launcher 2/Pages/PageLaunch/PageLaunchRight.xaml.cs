using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI;
using PCL.MCStudio;

namespace PCL;

/// <summary>
/// 主页右栏（微信式布局）：启动游戏与版本选择 + 加入好友房间 + 一键开房。
/// 原 PCL 新闻/自定义主页内容已按产品方向移除。
/// </summary>
public partial class PageLaunchRight : IRefreshable
{
    /// <summary>当前选中的服务器档案（null = 新服务器）。</summary>
    private ServerProfile? _serverProfile;

    public PageLaunchRight()
    {
        InitializeComponent();
        Loaded += (_, _) => Init();
        ModInstanceList.mcInstanceListLoader.LoadingStateChanged += (_, _) =>
            ModBase.RunInUi(RefreshLanInstances);
        ComboHostServer.SelectionChanged += (_, _) => ApplyServerSelection();
        ComboHostClientPack.SelectionChanged += ApplyClientPackSelection;
    }

    private void Init()
    {
        PanBack.ScrollToHome();
        PanScroll = PanBack; // 不知道为啥不能在 XAML 设置
        RefreshServerCombo();
        InitClientPackCombo();
        UpdateServerPackStatus();
        RefreshLanInstances();
        SelectMode(_mode);   // v49：恢复本会话上次选择的场景（默认「加入好友的房间」）
        if (_serverProfile is null)
            StudioSession.ApplyImportSuggestionToUi(ComboHostMc, ComboHostLoader);
    }

    // ---------------------------------------------------------------- 场景选择（v49 UX）

    /// <summary>当前场景：0=加入好友的房间 1=和朋友玩我的存档 2=开设专用服务器。</summary>
    private int _mode = -1;
    private static int _lastMode;   // 会话内记忆（页面重建后恢复）

    private void SelectMode(int mode)
    {
        if (mode < 0 || mode > 2) mode = 0;
        _mode = mode;
        _lastMode = mode;
        PanModeJoin.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanModeLan.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanModeHost.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        BtnModeJoin.ColorType = mode == 0 ? MyButton.ColorState.Highlight : MyButton.ColorState.Normal;
        BtnModeLan.ColorType = mode == 1 ? MyButton.ColorState.Highlight : MyButton.ColorState.Normal;
        BtnModeHost.ColorType = mode == 2 ? MyButton.ColorState.Highlight : MyButton.ColorState.Normal;
    }

    private void BtnModeJoin_Click(object sender, MouseButtonEventArgs e) => SelectMode(0);
    private void BtnModeLan_Click(object sender, MouseButtonEventArgs e) => SelectMode(1);
    private void BtnModeHost_Click(object sender, MouseButtonEventArgs e) => SelectMode(2);

    // ---------------------------------------------------------------- 虚拟局域网联机（v48）

    private List<McInstance> _lanInstances = [];

    /// <summary>LAN 实例下拉懒加载（加载器未就绪时强制重扫，随 LoadingStateChanged 刷新）。</summary>
    private void RefreshLanInstances()
    {
        if (!IsLoaded) return;
        var loader = ModInstanceList.mcInstanceListLoader;
        if (loader.State == ModBase.LoadState.Loading) return;   // 就绪后事件会再触发刷新
        if (loader.State == ModBase.LoadState.Waiting && !string.IsNullOrEmpty(ModFolder.mcFolderSelected))
        {
            ModLoader.LoaderFolderRun(loader, ModFolder.mcFolderSelected,
                ModLoader.LoaderFolderRunType.ForceRun, 1, "versions\\", true);
            return;
        }
        try
        {
            _lanInstances = ModInstanceList.mcInstanceList.Values.SelectMany(v => v)
                .Where(i => i is not null).ToList();
        }
        catch { _lanInstances = []; }
        ComboLanInstance.SelectionChanged -= ComboLanInstance_SelectionChanged;
        ComboLanInstance.Items.Clear();
        foreach (var inst in _lanInstances)
            ComboLanInstance.Items.Add(inst.Name);
        var selected = ModInstanceList.McMcInstanceSelected;
        if (selected is not null)
        {
            var idx = _lanInstances.FindIndex(i => i.PathInstance == selected.PathInstance);
            if (idx >= 0) ComboLanInstance.SelectedIndex = idx;
        }
        else if (ComboLanInstance.Items.Count > 0)
            ComboLanInstance.SelectedIndex = 0;
        ComboLanInstance.SelectionChanged += ComboLanInstance_SelectionChanged;
    }

    private void ComboLanInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 仅记录选择（ModInstanceList.McMcInstanceSelected 不动，避免干扰左栏启动）
    }

    private McInstance? SelectedLanInstance()
    {
        var idx = ComboLanInstance.SelectedIndex;
        return idx >= 0 && idx < _lanInstances.Count ? _lanInstances[idx] : null;
    }

    // MyButton.Click 委托是 (object, MouseButtonEventArgs)（铁律 1）
    private async void BtnLanHost_Click(object sender, MouseButtonEventArgs e)
    {
        var inst = SelectedLanInstance();
        if (inst is null)
        {
            HintService.Hint("请先在「游戏实例」下拉中选择要联机的本机实例", HintType.Error);
            return;
        }
        try
        {
            BtnLanHost.IsEnabled = false;
            TxtLanStatus.Visibility = Visibility.Visible;
            TxtLanStatus.Text = "正在启动游戏并开始侦测局域网世界…";
            await StudioSession.LanLaunchAsync(inst, new Progress<string>(s =>
            {
                TxtLanStatus.Text = s;
                // 桥接就绪后展开「复制房间码 / 结束联机」，并自动复制邀请（省一步）
                if (s.StartsWith("✅"))
                {
                    PanLanActions.Visibility = Visibility.Visible;
                    if (CopyLanInvite())
                        HintService.Hint("邀请信息已自动复制，直接粘贴发给好友即可", HintType.Success);
                }
                // v48.2：世界关闭（游戏退出/端口静默）→ 保持「结束联机」可用（会话/隧道仍在）
                if (s.StartsWith("⚠ 世界已关闭"))
                    PanLanActions.Visibility = Visibility.Visible;
            }));
            // v48.3：启动动作到此已完成（后续状态走事件上报），主按钮立即可点——
            // 用户随时可换实例重新开始（LanLaunchAsync 幂等清理旧会话）
            BtnLanHost.IsEnabled = true;
            PanLanActions.Visibility = StudioSession.LanRoomCode is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            HintService.Hint("虚拟局域网启动失败：" + ex.Message, HintType.Error);
            TxtLanStatus.Text = "❌ " + ex.Message;
            BtnLanHost.IsEnabled = true;
        }
    }

    // MyTextButton.Click 委托是 (object, EventArgs)（铁律 1）
    private static string BuildInviteText(string code) =>
        "我在 PCLonline 开了一个联机房间！\n" +
        $"房间码：{code}\n" +
        "在 PCLonline 主页选「加入好友的房间」，输入这个码就能一起玩。";

    private bool CopyLanInvite()
    {
        var code = StudioSession.LanRoomCode;
        if (string.IsNullOrEmpty(code)) return false;
        try
        {
            System.Windows.Clipboard.SetText(BuildInviteText(code));
            return true;
        }
        catch (Exception ex)
        {
            HintService.Hint("复制失败：" + ex.Message, HintType.Error);
            return false;
        }
    }

    private void BtnLanCopy_Click(object sender, EventArgs e)
    {
        var code = StudioSession.LanRoomCode;
        if (string.IsNullOrEmpty(code))
        {
            HintService.Hint("尚未生成房间码：请在游戏内打开存档 → 按 ESC →「对局域网开放」→ 点开始联机，回到启动器约 3 秒内自动生成", HintType.Warning);
            return;
        }
        if (CopyLanInvite())
            HintService.Hint("邀请信息已复制：直接粘贴发给好友即可", HintType.Success);
    }

    private async void BtnLanStop_Click(object sender, EventArgs e)
    {
        try
        {
            BtnLanStop.IsEnabled = false;
            await StudioSession.StopLanAsync();
            TxtLanStatus.Text = "已结束虚拟局域网联机。可重新点「启动游戏并开始联机」";
            PanLanActions.Visibility = Visibility.Collapsed;
        }
        finally
        {
            BtnLanStop.IsEnabled = true;
            BtnLanHost.IsEnabled = true;
        }
    }

    // MyTextButton.Click 委托是 (object, EventArgs)（铁律 1）
    private async void BtnLanCleanup_Click(object sender, EventArgs e)
    {
        try
        {
            BtnLanCleanup.IsEnabled = false;
            TxtLanStatus.Visibility = Visibility.Visible;
            TxtLanStatus.Text = "正在扫描历史装配实例…";
            var (names, dirs, bytes) = await Task.Run(StudioSession.ScanLegacyPackInstances);
            if (names.Count == 0)
            {
                TxtLanStatus.Text = "没有可清理的历史装配实例（当前已是干净状态）";
                return;
            }
            var sizeText = bytes >= 1073741824 ? $"{bytes / 1073741824.0:F1} GB" : $"{bytes / 1048576.0:F0} MB";
            var body = $"将永久删除 {names.Count} 个历史装配实例（共约 {sizeText}）：\n  " +
                       string.Join("\n  ", names.Take(10)) +
                       (names.Count > 10 ? $"\n  …共 {names.Count} 个" : "") +
                       "\n\n这些是旧版本按房间码创建的实例，已无法被新流程复用。" +
                       "\n当前使用的 mcstudio-pack-* 实例不受影响。确定删除吗？";
            var choice = ModMain.MyMsgBox(body, "清理历史装配实例", "删除", "取消");
            if (choice != 1)
            {
                TxtLanStatus.Text = "已取消清理";
                return;
            }
            var (deleted, failed) = await StudioSession.DeleteInstancesAsync(dirs);
            RefreshLanInstances();
            TxtLanStatus.Text = failed.Count > 0
                ? $"已删除 {deleted} 个实例；{failed.Count} 个因正在运行无法删除：{string.Join("、", failed.Take(3))}"
                : $"已删除 {deleted} 个历史装配实例，释放约 {sizeText} 空间";
            HintService.Hint(TxtLanStatus.Text, failed.Count > 0 ? HintType.Warning : HintType.Success);
        }
        catch (Exception ex)
        {
            HintService.Hint("清理失败：" + ex.Message, HintType.Error);
            TxtLanStatus.Text = "❌ 清理失败：" + ex.Message;
        }
        finally
        {
            BtnLanCleanup.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- 服务端整合包（v43/v45：选择即登记，开房自动展开）

    /// <summary>选择服务端整合包 zip：登记后开房时整包展开为服务器，版本/加载器自动跟随。</summary>
    private async void BtnServerPackPick_Click(object sender, EventArgs e)  // MyTextButton.Click 委托是 (object, EventArgs)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择服务端整合包（内含已装好的 Forge/模组/配置的 zip）",
            Filter = "整合包 (*.zip;*.mrpack)|*.zip;*.mrpack|全部文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        BtnServerPackPick.IsEnabled = false;
        TxtServerPackStatus.Text = "正在识别服务端包…";
        try
        {
            var result = await ModpackImporter.ImportAsync([dialog.FileName],
                new Progress<string>(m => TxtServerPackStatus.Text = m));
            UpdateServerPackStatus();
            HintService.Hint(result.Detail, HintType.Info);
            // 版本/加载器跟随服务端包（档案锁定时提示）
            if (result.SuggestedMc is { Length: > 0 } mc && result.SuggestedLoader is { Length: > 0 } ld)
            {
                if (_serverProfile is not null && (_serverProfile.Mc != mc ||
                    !_serverProfile.Loader.Equals(ld, StringComparison.OrdinalIgnoreCase)))
                    HintService.Hint($"服务端包版本（{mc} {ld}）与当前档案（{_serverProfile.Mc} {_serverProfile.Loader}）不符，建议在「服务器」选新服务器", HintType.Warning);
                else
                {
                    SetHostCombo(ComboHostMc, mc);
                    SetHostCombo(ComboHostLoader, CapitalizeLoader(ld));
                }
            }
            UpdateClientPackStatus();
        }
        catch (Exception ex)
        {
            TxtServerPackStatus.Text = "服务端包识别失败：" + ex.Message;
        }
        finally
        {
            BtnServerPackPick.IsEnabled = true;
        }
    }

    private void UpdateServerPackStatus()
    {
        if (TxtServerPackStatus is null) return;
        if (ModpackImporter.GetServerPackImport() is not { } sp)
        {
            TxtServerPackStatus.Text = "未选择（不选则按下方版本/加载器开原版或需安装的 Forge 服）";
            return;
        }
        var cp = ModpackImporter.GetClientPackImport();
        TxtServerPackStatus.Text = $"{Path.GetFileName(sp.ZipPath)} · MC {sp.Mc} {sp.Loader}" +
            (cp is not null ? $" · 客户端包已配对：{cp.Name}" : "");
    }

    // ---------------------------------------------------------------- 客户端整合包（v44）

    /// <summary>本地 zip 选择结果（含 LocalPath）。</summary>
    private ClientPackInfo? _clientPackLocal;

    /// <summary>手填的公开下载链接。</summary>
    private string? _clientPackUrl;

    private void InitClientPackCombo()
    {
        ComboHostClientPack.SelectionChanged -= ApplyClientPackSelection;
        try
        {
            ComboHostClientPack.Items.Clear();
            ComboHostClientPack.Items.Add("与服务端包配对");
            ComboHostClientPack.Items.Add("选择本地 zip…");
            ComboHostClientPack.Items.Add("填写下载链接…");
            ComboHostClientPack.Items.Add("不提供");
            ComboHostClientPack.SelectedIndex = 0;
        }
        finally
        {
            ComboHostClientPack.SelectionChanged += ApplyClientPackSelection;
        }
        UpdateClientPackStatus();
    }


    /// <summary>链接后台预检（v46）：可达性/大小回填；疑似网页时警告并可取消。</summary>
    private async void ValidateJoinLinkAsync(ClientPackInfo cpInfo)
    {
        try
        {
            var (size, warning) = await StudioSession.ValidateClientPackUrlAsync(cpInfo.Url!).ConfigureAwait(true);
            if (_clientPackUrl != cpInfo.Url) return; // 用户已改链接
            ModpackImporter.SetClientPackImport(cpInfo with { Size = size });
            UpdateClientPackStatus();
            if (warning is not null)
            {
                var confirm = ModBase.RunInUiWait(() =>
                    ModMain.MyMsgBox(warning + "\n\n仍要使用该链接吗？", isWarn: true) == 1);
                if (!confirm)
                {
                    _clientPackUrl = null;
                    ModpackImporter.SetClientPackImport(null);
                    ComboHostClientPack.SelectedIndex = 0;
                    HintService.Hint("已取消该链接", HintType.Info);
                }
            }
        }
        catch (Exception ex)
        {
            HintService.Hint("链接预检失败（不影响使用）：" + ex.Message, HintType.Warning);
        }
    }

    private void ApplyClientPackSelection(object sender, SelectionChangedEventArgs e)
    {
        switch (ComboHostClientPack.SelectedIndex)
        {
            case 1: // 选择本地 zip
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择客户端整合包（朋友加入时将自动下载安装的版本）",
                    Filter = "整合包 (*.zip;*.mrpack)|*.zip;*.mrpack|全部文件 (*.*)|*.*",
                };
                if (dialog.ShowDialog() != true) { ComboHostClientPack.SelectedIndex = 0; return; }
                _clientPackLocal = new ClientPackInfo(Path.GetFileName(dialog.FileName),
                    ClientPackInfo.SourceLocal, null, new FileInfo(dialog.FileName).Length,
                    null, "", "", dialog.FileName);
                _clientPackUrl = null;
                ModpackImporter.SetClientPackImport(_clientPackLocal);
                break;
            }
            case 2: // 填写下载链接
            {
                var url = ModMain.MyMsgBoxInput("客户端整合包下载链接",
                    "填写公开可下载的整合包直链（朋友加入时将从此链接自动下载）：",
                    _clientPackUrl ?? "");
                if (string.IsNullOrWhiteSpace(url)) { ComboHostClientPack.SelectedIndex = 0; return; }
                _clientPackUrl = url.Trim();
                _clientPackLocal = null;
                var cpInfo = new ClientPackInfo(
                    Uri.TryCreate(_clientPackUrl, UriKind.Absolute, out var pu) && pu.Segments.Length > 0
                        ? Uri.UnescapeDataString(pu.Segments[^1]) : "客户端整合包",
                    ClientPackInfo.SourceUrl, _clientPackUrl, 0, null, "", "");
                ModpackImporter.SetClientPackImport(cpInfo);
                ValidateJoinLinkAsync(cpInfo); // v46 后台预检：大小回填 + 疑似网页警告
                break;
            }
        }
        UpdateClientPackStatus();
    }

    private void UpdateClientPackStatus()
    {
        if (TxtClientPackStatus is null) return;
        string text;
        switch (ComboHostClientPack.SelectedIndex)
        {
            case 1 when _clientPackLocal is not null:
                text = $"{_clientPackLocal.Name} · {_clientPackLocal.Size / 1048576.0:F0}MB · 仅本机可用（朋友加入需另填下载链接，否则会被拦截）";
                break;
            case 2 when !string.IsNullOrWhiteSpace(_clientPackUrl):
                text = $"朋友将从链接下载：{_clientPackUrl}";
                break;
            case 3:
                text = "未提供客户端包：朋友将无法自动加入该房间";
                break;
            default:
            {
                var imp = ModpackImporter.GetClientPackImport();
                text = imp is null
                    ? "未配对客户端包（可先导入服务端包+客户端包自动配对）；不提供时朋友无法加入"
                    : $"已配对：{imp.Name} · {imp.Size / 1048576.0:F0}MB" +
                      (imp.HasDownload ? " · 朋友将从链接下载" : " · 仅本机可用（朋友需可下载链接）");
                break;
            }
        }
        TxtClientPackStatus.Text = text;
        TxtClientPackStatus.Visibility = Visibility.Visible;
    }

    /// <summary>开房时解析客户端包（v44）；不提供返回 null（严格模式下朋友会被拦截）。</summary>
    private ClientPackInfo? ResolveClientPack(string mc, string loader)
    {
        return ComboHostClientPack.SelectedIndex switch
        {
            1 when _clientPackLocal is not null => _clientPackLocal with { Mc = mc, Loader = loader },
            2 when !string.IsNullOrWhiteSpace(_clientPackUrl) => new ClientPackInfo(
                Uri.TryCreate(_clientPackUrl, UriKind.Absolute, out var u) && u.Segments.Length > 0
                    ? Uri.UnescapeDataString(u.Segments[^1])
                    : "客户端整合包",
                ClientPackInfo.SourceUrl, _clientPackUrl, 0, null, mc, loader),
            _ => ModpackImporter.GetClientPackImport() is { } imp
                ? imp with { Mc = mc, Loader = loader }
                : null,
        };
    }

    // ---------------------------------------------------------------- 本地服务器档案（存档重开）

    /// <summary>刷新服务器下拉：已存档案在前 + 末尾"＋ 新服务器"；默认选中最近开过的档案。</summary>
    private void RefreshServerCombo()
    {
        var profiles = ServerListStore.Load();
        ComboHostServer.Items.Clear();
        foreach (var p in profiles) ComboHostServer.Items.Add(p.ListItem);
        ComboHostServer.Items.Add("＋ 新服务器");
        ComboHostServer.SelectedIndex = profiles.Count > 0 ? 0 : profiles.Count;
    }

    /// <summary>下拉选择 → 应用档案（回填并锁定版本/加载器）或切回新服务器（解锁）。</summary>
    private void ApplyServerSelection()
    {
        var profiles = ServerListStore.Load();
        var idx = ComboHostServer.SelectedIndex;
        _serverProfile = idx >= 0 && idx < profiles.Count ? profiles[idx] : null;
        if (_serverProfile is not null)
        {
            SetHostCombo(ComboHostMc, _serverProfile.Mc);
            SetHostCombo(ComboHostLoader, CapitalizeLoader(_serverProfile.Loader));
            ComboHostMc.IsEnabled = false;
            ComboHostLoader.IsEnabled = false;
            RestoreClientPack(_serverProfile.ClientPack); // v44：档案记住的客户端包
        }
        else
        {
            ComboHostMc.IsEnabled = true;
            ComboHostLoader.IsEnabled = true;
        }
    }

    /// <summary>档案回填客户端包：链接包→链接模式，本地包→本地模式（文件已不在则回落"与服务端包配对"）。</summary>
    private void RestoreClientPack(ClientPackInfo? cp)
    {
        ComboHostClientPack.SelectionChanged -= ApplyClientPackSelection;
        try
        {
            if (cp is null) ComboHostClientPack.SelectedIndex = 0;
            else if (cp.HasDownload)
            {
                _clientPackUrl = cp.Url;
                _clientPackLocal = null;
                ComboHostClientPack.SelectedIndex = 2;
            }
            else if (cp.LocalPath is { Length: > 0 } && File.Exists(cp.LocalPath))
            {
                _clientPackLocal = cp;
                _clientPackUrl = null;
                ComboHostClientPack.SelectedIndex = 1;
            }
            else ComboHostClientPack.SelectedIndex = 0;
        }
        finally
        {
            ComboHostClientPack.SelectionChanged += ApplyClientPackSelection;
        }
        UpdateClientPackStatus();
    }

    private static void SetHostCombo(PCL.MyComboBox combo, string value)
    {
        if (combo.Items.Contains(value))
        {
            combo.SelectedItem = value;
            return;
        }
        // 版本不在列表里（如整合包的小众版本）：按数字顺序插入
        var insertAt = combo.Items.Count;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is string s && Version.TryParse(s, out var a)
                && Version.TryParse(value, out var b) && a > b)
            {
                insertAt = i;
                break;
            }
        }
        combo.Items.Insert(insertAt, value);
        combo.SelectedItem = value;
    }

    private static string CapitalizeLoader(string loader) => loader.ToLowerInvariant() switch
    {
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "vanilla" => "Vanilla",
        _ => "Fabric",
    };

    /// <summary>开房成功后保存档案；房间码与上次不同时提醒（租约过期后重开会换码）。v44 起随档案持久化客户端包。</summary>
    private void SaveServerProfile(string mc, string loader, string slug, string code, ClientPackInfo? clientPack = null)
    {
        try
        {
            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            var oldCode = _serverProfile?.LastCode;
            if (_serverProfile is null)
            {
                var display = ServerListStore.NextDisplay($"{mc} {CapitalizeLoader(loader)}");
                _serverProfile = new ServerProfile(slug, display, mc, loader, now, now, code, clientPack);
            }
            else
            {
                _serverProfile = _serverProfile with
                {
                    Mc = mc, Loader = loader, LastOpenedAt = now, LastCode = code,
                    ClientPack = clientPack ?? _serverProfile.ClientPack,
                };
            }
            ServerListStore.Upsert(_serverProfile);
            RefreshServerCombo();
            if (oldCode is { Length: > 0 } oc && oc != code)
                HintService.Hint($"房间码已变更：{oc} → {code}（租约过期后重开会换新码），请通知朋友用新房间码加入",
                    HintType.Warning);
        }
        catch
        {
            // 档案保存失败不影响开房
        }
    }

    /// <summary>外部“刷新主页”事件入口（v47.1：启动游戏卡已移除，仅保留接口实现）。</summary>
    public void Refresh()
    {
    }

    // ---------------------------------------------------------------- 加入好友房间

    // MyTextButton.Click 委托是 (object, EventArgs)（铁律 1）
    private void BtnJoinLocalPack_Click(object sender, EventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择该服务器的客户端整合包（加入时免下载）",
            Filter = "整合包 (*.zip;*.mrpack)|*.zip;*.mrpack|全部文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var meta = StudioSession.SetJoinPickedLocalPack(dialog.FileName);
            var sizeMb = double.Parse(meta.Split('|')[0]) / 1048576.0;
            TxtJoinLocalPack.Text = $"已选：{Path.GetFileName(dialog.FileName)}（{sizeMb:F0}MB）——加入时优先使用，不再下载";
            HintService.Hint("已导入本地整合包，加入该服务器时将优先使用", HintType.Success);
        }
        catch (Exception ex)
        {
            HintService.Hint("导入失败：" + ex.Message, HintType.Error);
        }
    }


    private async void BtnJoin_Click(object sender, MouseButtonEventArgs e)
    {
        var code = TxtCode.Text.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code))
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.EmptyCode"), HintType.Error);
            return;
        }
        if (StudioSession.TryGetApi() is null)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NoConfig"), HintType.Error);
            return;
        }
        StudioSession.EnsureFirewallUdpRule();
        try
        {
            BtnJoin.IsEnabled = false;
            TxtResult.Visibility = Visibility.Collapsed;
            ShowJoinProgress(new AssembleProgress(AssembleStep.Resolving, ""));

            var p2pMode = ComboConnectMode.SelectedIndex switch
            {
                1 => P2pMode.DirectOnly,
                2 => P2pMode.RelayOnly,
                _ => P2pMode.Auto,
            };
            var result = await StudioSession.JoinAsync(code, new Progress<AssembleProgress>(ShowJoinProgress), p2pMode);
            TxtJoinProgress.Text = string.Format(
                Lang.Text("Tools.Studio.Join.AssembleDone"),
                string.IsNullOrEmpty(result.PackName) ? result.InstanceId : result.PackName, result.Mods, result.NewLibraries);
            if (result.Missing.Count > 0)
            {
                TxtResult.Text = "以下模组未能自动下载，进入服务器前请向房主索取并放入实例 mods 文件夹：\n  " +
                                 string.Join("\n  ", result.Missing.Take(10)) +
                                 (result.Missing.Count > 10 ? $"\n  …共 {result.Missing.Count} 个" : "");
                TxtResult.Visibility = Visibility.Visible;
                HintService.Hint($"有 {result.Missing.Count} 个模组需手动补齐", HintType.Warning);
                // 缺模组不自动启动（进去必掉线）：只刷新版本列表，用户补齐后手动启动
                ModBase.RunInNewThread(() =>
                {
                    // 必须显式传入 MC 文件夹：WaitForExit 不带 input 会以 null 重启加载器，
                    // 导致 InitMcInstanceList 里 Path.Combine(null, "versions") 抛 ArgumentNullException（v31 实测踩坑）
                    ModInstanceList.mcInstanceListLoader.WaitForExit(
                        StudioSession.GetMcFolder(), isForceRestart: true);
                }, "StudioRefreshVersions");
                return;
            }

            // 自动进入游戏：刷新版本列表选中该实例并直接启动（PCL 会先自动下载缺失的客户端文件，
            // 用户无需再手动点一次「下载游戏」）；v44 整包路径已在 JoinAsync 内启动，跳过防双启动
            HintService.Hint(Lang.Text("Tools.Studio.Join.Launching"), HintType.Info);
            if (!result.Reused)
                StudioSession.RefreshVersionsAndLaunch(result.InstanceId);
        }
        catch (StudioApiException)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NotFound"), HintType.Error);
            TxtResult.Text = Lang.Text("Tools.Studio.Hint.NotFoundDetail");
            TxtResult.Visibility = Visibility.Visible;
            TxtJoinProgress.Visibility = Visibility.Collapsed;
        }
        catch (NotSupportedException)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.JoinUnsupported"), HintType.Error);
            ShowJoinProgress(new AssembleProgress(AssembleStep.Done,
                Lang.Text("Tools.Studio.Join.AssembleFailed")));
        }
        catch (Exception ex)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.JoinAssembleFailed") + "：" + ex.Message, HintType.Error);
            ShowJoinProgress(new AssembleProgress(AssembleStep.Done,
                Lang.Text("Tools.Studio.Join.AssembleFailed")));
        }
        finally
        {
            BtnJoin.IsEnabled = true;
        }
    }

    private void ShowJoinProgress(AssembleProgress p)
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
        TxtJoinProgress.Text = string.IsNullOrEmpty(p.Detail) ? stepText : $"{stepText} · {p.Detail}";
        TxtJoinProgress.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- 一键开房


    private async void BtnHostStart_Click(object sender, MouseButtonEventArgs e)
    {
        if (StudioSession.TryGetApi() is null)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NoConfig"), HintType.Error);
            return;
        }
        StudioSession.EnsureFirewallUdpRule();
        // v50.9.7：管理员静默配规则；非管理员弹一次提示并可 UAC 自提权重启
        StudioFirewall.EnsureAdminInteractive("一键开房");
        var env = HostEnvironment.Load();
        StudioSession.EnsureFrpcExtracted();   // v50.9.5：frpc.exe 缺失自愈
        if (!File.Exists(env.FrpcExe))
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.FrpcMissing"), HintType.Error);
            return;
        }
        if (string.IsNullOrEmpty(env.Token))
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.TokenMissing"), HintType.Error);
            return;
        }
        var mc = ComboHostMc.SelectedItem as string ?? StudioSession.AutoHostLabel;
        var loader = (ComboHostLoader.SelectedItem as string ?? StudioSession.AutoHostLabel).ToLowerInvariant();
        var isAutoMc = mc == StudioSession.AutoHostLabel;
        var isAutoLoader = loader == StudioSession.AutoHostLabel;

        // 已选服务器档案：版本/加载器锁定为档案值（服务端目录是按版本装好的，换版本=新档）
        if (_serverProfile is not null)
        {
            mc = _serverProfile.Mc;
            loader = _serverProfile.Loader.ToLowerInvariant();
        }
        else if (isAutoMc || isAutoLoader)
        {
        // "自动"选项（v39）：导入整合包 manifest → 散装模组 jar 元数据 → 当前实例 → 兜底 Forge 1.21.1
        var (autoMc, autoLoader, autoSource) = StudioSession.ResolveAutoHostOptions();
        if (isAutoMc) mc = autoMc;
        if (isAutoLoader) loader = autoLoader;
        HintService.Hint($"已自动识别开房配置：MC {mc} / {CapitalizeLoader(loader)}（{autoSource}）", HintType.Info);
        // 写回下拉框让用户看到实际值（"自动"被具体值替换）
        SetHostCombo(ComboHostMc, mc);
        var loaderText = CapitalizeLoader(loader);
        if (!ComboHostLoader.Items.Contains(loaderText)) ComboHostLoader.Items.Add(loaderText);
        ComboHostLoader.SelectedItem = loaderText;
        }
        else
        {

        // 导入整合包记录的建议版本优先（导入池里的模组版本与实例/下拉框都可能不符）；Quilt 不支持托管，跳过
        var (impMc, impLoader) = ModpackImporter.GetImportSuggestion();
        if (impMc.Length > 0 && impLoader != "quilt" && (impMc != mc || (impLoader.Length > 0 && impLoader != loader)))
        {
            HintService.Hint($"已按导入的整合包版本开房：MC {impMc}" +
                             (impLoader.Length > 0 ? $" / {impLoader}" : ""), HintType.Info);
            mc = impMc;
            if (impLoader.Length > 0) loader = impLoader;
        }
        }

        // v50.3：mc 确定后再按兼容区间选 Java（1.20.1+forge → JDK 17~21，自动避开过新的 JDK 25/26）
        // v50.4：所有机型通用——本机没有兼容 Java 时自动下载 Mojang 官方 runtime
        TxtHostStatus.Text = "正在准备 Java 运行环境…";
        TxtHostStatus.Visibility = Visibility.Visible;
        var javaExe = await StudioSession.ResolveOrInstallJavaAsync(mc,
            msg => { TxtHostStatus.Text = msg; TxtHostStatus.Visibility = Visibility.Visible; });
        if (string.IsNullOrEmpty(javaExe) || !File.Exists(javaExe))
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.JavaMissing") +
                "（MC " + mc + " 需要 Java 17~21；自动下载失败，请手动安装后重试：https://adoptium.net）",
                HintType.Error);
            TxtHostStatus.Text = "❌ 未获取到兼容的 Java（17~21）。请检查网络后重试，或手动安装：https://adoptium.net";
            return;
        }

        // 重开已存档案 → 用档案的 slug（租约存活期内复用同端口/同房间码，服务端目录零重装）；
        // 新服务器 → 取下一个可用 slug 并建档
        var roomSlug = _serverProfile?.Slug ?? ServerListStore.NextSlug(StudioSession.HostSlug());
        var serverDir = Path.Combine(env.ServerRoot, roomSlug);
        // v44 客户端整合包：随房间注册上云，朋友加入时整包下载安装
        var clientPack = ResolveClientPack(mc, loader);
        // v50.8 包身份：整合包 + 资源包/光影包/数据包（<1KB）。玩家端据此用 PCL 原生管线装同款，
        // 房主端零文件上传。不 ConfigureAwait(false)——后续要回 UI 线程更新控件
        var packs = await HostPackCollector.CollectOrNullAsync(
            ModInstanceList.McMcInstanceSelected?.PathInstance);

        try
        {
            BtnHostStart.IsEnabled = false;
            BtnHostStop.IsEnabled = false;
            TxtHostStatus.Text = Lang.Text("Tools.Studio.Host.StatusStarting");
            TxtHostStatus.Visibility = Visibility.Visible;
            TxtHostProgress.Visibility = Visibility.Collapsed;

            var progress = new Progress<HostProgress>(ShowHostProgress);
            var room = await StudioSession.HostStartAsync(new HostOptions
            {
                Mc = mc,
                Loader = loader,
                ServerDir = serverDir,
                JavaExe = javaExe,
                RoomName = roomSlug,
                SkipSeed = _serverProfile is not null,   // 重开档案：跳过暂存区播种，防误灌其他整合包
                ClientPack = clientPack,                 // v44 客户端整合包随房间上云
                ClientInstanceDir = ModInstanceList.McMcInstanceSelected?.PathInstance,   // v50.7 全类型清单来源
                Packs = packs,                           // v50.8 包身份（玩家端原生装同款）
            }, progress);
            // 只显示房间码：连接地址/端口不再对外暴露，玩家只能走启动器一键加入（P2P 优先）
            TxtHostStatus.Text = string.Format(
                Lang.Text("Tools.Studio.Host.StatusRunning"), room.Code) +
                StudioSession.LanHint(room.LocalPort) +
                (clientPack is null
                    ? "\n⚠ 未提供客户端整合包：朋友将无法自动加入（可重开时在「客户端包」指定）"
                    : $"\n客户端包：{clientPack.Name}（{(clientPack.HasDownload ? "朋友可自动下载" : "仅本机可用")}）");
            BtnHostStop.IsEnabled = true;
            BtnHostJoin.IsEnabled = true;   // 房间运行中即可一键加入（本机 127.0.0.1 直连）
            HintService.Hint(string.Format(Lang.Text("Tools.Studio.Hint.HostStarted"), room.Code),
                HintType.Success);
            SaveServerProfile(mc, loader, roomSlug, room.Code, clientPack);
        }
        catch (Exception ex)
        {
            TxtHostStatus.Text = Lang.Text("Tools.Studio.Host.StatusFailed");
            // v45.2：服务端崩溃类错误自带【诊断】，不再套"检查网络/frpc"的误导前缀
            var isServerCrash = ex.Message.Contains("服务端启动过程中退出") || ex.Message.Contains("【诊断】");
            HintService.Hint(isServerCrash ? ex.Message
                : Lang.Text("Tools.Studio.Hint.HostFailed") + "：" + ex.Message, HintType.Error);
        }
        finally
        {
            // 房间运行中保持禁用（防重复开房）；失败/关房后恢复
            BtnHostStart.IsEnabled = !StudioSession.IsHosting;
        }
    }

    /// <summary>房主一键加入：用本机 127.0.0.1 直连地址装配同款客户端并直接启动（零延迟、不占中转）。</summary>
    private async void BtnHostJoin_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            BtnHostJoin.IsEnabled = false;
            ShowJoinProgress(new AssembleProgress(AssembleStep.Resolving, ""));

            // v45 服务端包房间：服务器就在本机，直接 127.0.0.1 直连——
            // 不走云端解析、不走 P2P 信令、不走回环转发（那些是给朋友远程加入用的）。
            // 客户端包优先用导入时配对的本地 zip（房主零下载）。
            if (ModpackImporter.GetServerPackImport() is { })
            {
                var cp = ModpackImporter.GetClientPackImport();
                if (cp is null)
                {
                    HintService.Hint("未配置客户端整合包：请先在「客户端包」选择本地 zip 或填写下载链接", HintType.Error);
                    return;
                }
                var spInfo = StudioSession.HostLocalRoomInfo();
                await StudioSession.JoinLocalClientPackAsync(spInfo,
                    cp with { Mc = spInfo.Mc, Loader = spInfo.Loader },
                    new Progress<AssembleProgress>(ShowJoinProgress));
                return;
            }

            var info = StudioSession.HostLocalRoomInfo();

            // v36 复用快捷路径：本机已有同版本纯原版实例 → 免装配（不下载客户端/依赖库）直接进房
            var reuse = await StudioSession.TryFindReusableInstanceAsync(info).ConfigureAwait(true);
            if (reuse is not null)
            {
                TxtJoinProgress.Text = string.Format(Lang.Text("Tools.Studio.Join.ReuseHit"), info.Mc, reuse.Name);
                var launchOk = ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions
                {
                    ServerIp = info.Address,
                    instance = reuse,
                });
                if (launchOk)
                {
                    ModMain.frmMain.PageChange(new FormMain.PageStackData { page = FormMain.PageType.Launch });
                    HintService.Hint(string.Format(Lang.Text("Tools.Studio.Join.ReuseHit"), info.Mc, reuse.Name),
                        HintType.Info);
                }
                else
                {
                    HintService.Hint(Lang.Text("Minecraft.Launch.Error.LaunchFailed"), HintType.Error);
                }
                return;
            }

            // v37 房主快速通道：模组优先从本机服务端目录复制（免网络下载），缺的仍走镜像/Modrinth
            var hostModsDir = StudioSession.TryGetHostLocalModsDir(info.Code);
            var result = await new RoomAssembler().AssembleAsync(info, StudioSession.GetMcFolder(),
                new Progress<AssembleProgress>(ShowJoinProgress), localModsDir: hostModsDir);
            TxtJoinProgress.Text = string.Format(
                Lang.Text("Tools.Studio.Join.AssembleDone"),
                string.IsNullOrEmpty(result.PackName) ? result.InstanceId : result.PackName, result.Mods, result.NewLibraries);
            HintService.Hint(Lang.Text("Tools.Studio.Join.Launching"), HintType.Info);
            StudioSession.RefreshVersionsAndLaunch(result.InstanceId);
        }
        catch (NotSupportedException)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.JoinUnsupported"), HintType.Error);
        }
        catch (Exception ex)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.JoinAssembleFailed") + "：" + ex.Message, HintType.Error);
        }
        finally
        {
            BtnHostJoin.IsEnabled = StudioSession.IsHosting;
        }
    }

    private async void BtnHostStop_Click(object sender, MouseButtonEventArgs e)
    {
        if (!StudioSession.IsHosting) return;
        try
        {
            BtnHostStop.IsEnabled = false;
            TxtHostStatus.Text = Lang.Text("Tools.Studio.Host.StatusStopping");
            var progress = new Progress<HostProgress>(ShowHostProgress);
            await StudioSession.HostStopAsync(progress);
            TxtHostStatus.Text = Lang.Text("Tools.Studio.Host.StatusStopped");
            HintService.Hint(Lang.Text("Tools.Studio.Hint.HostStopped"), HintType.Info);
        }
        catch (Exception ex)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.HostStopFailed") + "：" + ex.Message, HintType.Error);
        }
        finally
        {
            BtnHostStart.IsEnabled = true;
            BtnHostStop.IsEnabled = false;
            BtnHostJoin.IsEnabled = false;
        }
    }

    private void ShowHostProgress(HostProgress p)
    {
        var stepText = p.Step switch
        {
            HostStep.Preparing => Lang.Text("Tools.Studio.Host.StepPreparing"),
            HostStep.ServerJar => Lang.Text("Tools.Studio.Host.StepServerJar"),
            HostStep.Starting => Lang.Text("Tools.Studio.Host.StepStarting"),
            HostStep.WaitingDone => Lang.Text("Tools.Studio.Host.StepWaitingDone"),
            HostStep.Tunnel => Lang.Text("Tools.Studio.Host.StepTunnel"),
            HostStep.Register => Lang.Text("Tools.Studio.Host.StepRegister"),
            HostStep.Running => Lang.Text("Tools.Studio.Host.StepRunning"),
            _ => Lang.Text("Tools.Studio.Host.StepStopping"),
        };
        TxtHostProgress.Text = string.IsNullOrEmpty(p.Detail) ? stepText : $"{stepText} · {p.Detail}";
        TxtHostProgress.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- 启动提示语

    /// <summary>启动进度页的随机提示语（外部 hints.txt 优先，回落嵌入式资源）。</summary>
    public static string GetRandomHint(bool enableLengthLimit = false, bool raw = false)
    {
        string[]? lines = null;

        // 外部文件
        var externalPath = Path.Combine(ModBase.exePath, "PCL", "hints.txt");
        if (File.Exists(externalPath))
        {
            try
            {
                lines = File.ReadAllLines(externalPath)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Trim())
                    .ToArray();
            }
            catch
            {
                ModBase.Log(
                    Lang.Text("Launch.Homepage.Error.ExternalFile", externalPath),
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Launch.Homepage.Error.ExternalFile", externalPath));
            }
        }

        // 嵌入式资源
        if (lines is null || lines.Length == 0)
        {
            var langCode = LocalizationService.CurrentLanguage.Code;
            lines = _LoadEmbeddedHints(langCode)
                ?? _LoadEmbeddedHints(LocalizationService.DefaultLanguageCode);
        }
        if (lines is null || lines.Length == 0) return "";

        // 长度限制
        if (enableLengthLimit)
        {
            var shortLines = lines.Where(l => l.Length < 50).ToArray();
            if (shortLines.Length > 0) lines = shortLines;
        }

        // 随机返回
        var hint = lines[Random.Shared.Next(lines.Length)];
        return raw ? hint : hint.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    private static string[]? _LoadEmbeddedHints(string langCode)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/PClonine;component/Resources/hints/{langCode}.txt", UriKind.Absolute);
            using var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .ToArray();
        }
        catch
        {
            return null;
        }
    }
}
