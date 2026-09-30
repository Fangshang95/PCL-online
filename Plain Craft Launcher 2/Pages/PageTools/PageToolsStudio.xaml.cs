using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.Core.UI;
using PCL.MCStudio;

namespace PCL;

public partial class PageToolsStudio
{
    /// <summary>当前登录身份；null 表示游客态（游客也能加入房间，只是好友列表不保存）。</summary>
    private UserIdentity? _me;

    /// <summary>开房会话（含服务端进程与隧道）；null 表示未开房。</summary>
    private HostRoomManager? _host;
    private StudioApiClient? _hostApi;

    private readonly DispatcherTimer _cooldownTimer = new() { Interval = System.TimeSpan.FromSeconds(1) };
    private int _cooldownLeft;

    public PageToolsStudio()
    {
        InitializeComponent();
        _cooldownTimer.Tick += CooldownTimer_Tick;
        Loaded += PageToolsStudio_Loaded;
        ComboHostServer.SelectionChanged += (_, _) => ApplyServerSelection();
        ComboHostClientPack.SelectionChanged += ApplyClientPackSelection;
        // 跟随全局会话状态：登录/登出/令牌失效时同步本页显示（本页为缓存单例）
        StudioSession.SessionChanged += OnStudioSessionChanged;
    }

    private void OnStudioSessionChanged()
    {
        ModBase.RunInUi(() =>
        {
            _me = StudioSession.Me;
            RefreshAccount();
        });
    }

    /// <summary>外部入口（主页好友栏"前往登录"）：同步会话状态，确保登录表单可见并聚焦邮箱框。</summary>
    public void FocusLogin()
    {
        _me = StudioSession.Me ?? StudioAccount.Load();
        RefreshAccount();
        if (PanAccountLogin.Visibility == Visibility.Visible)
            Dispatcher.BeginInvoke(() => TxtLoginEmail.Focus(),
                System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PageToolsStudio_Loaded(object sender, RoutedEventArgs e)
    {
        _me = StudioAccount.Load();
        RefreshAccount();
        if (_me is not null) _ = ValidateAndLoadFriendsAsync();
        RefreshServerCombo();
        InitClientPackCombo();
        UpdateServerPackStatus();
        // 导入池里有整合包建议版本时，开房下拉框同步自动选中；服务器档案锁定时跳过
        if (_serverProfile is null)
            StudioSession.ApplyImportSuggestionToUi(ComboHostMc, ComboHostLoader);
    }

    // ---------------------------------------------------------------- 服务端整合包（v43/v45）

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
    }


    /// <summary>链接后台预检（v46）：可达性/大小回填；疑似网页时警告并可取消。</summary>
    private async void ValidateJoinLinkAsync(ClientPackInfo cpInfo)
    {
        try
        {
            var (size, warning) = await StudioSession.ValidateClientPackUrlAsync(cpInfo.Url!).ConfigureAwait(true);
            if (_clientPackUrl != cpInfo.Url) return; // 用户已改链接
            ModpackImporter.SetClientPackImport(cpInfo with { Size = size });
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
            case 1:
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
            case 2:
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

    /// <summary>当前选中的服务器档案（null = 新服务器）。</summary>
    private ServerProfile? _serverProfile;

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
    }

    private static void SetHostCombo(PCL.MyComboBox combo, string value)
    {
        if (combo.Items.Contains(value))
        {
            combo.SelectedItem = value;
            return;
        }
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

    // ---------------------------------------------------------------- 账号

    private void RefreshAccount()
    {
        var loggedIn = _me is not null;
        Show(PanAccountUser, loggedIn);
        Show(PanAccountLogin, !loggedIn);
        if (!loggedIn) Show(PanAccountRegister, false);

        if (loggedIn)
        {
            TxtAccountName.Text = _me!.Nickname;
            TxtAccountEmail.Text = _me!.Email;
        }
        else
        {
            TxtFriendsEmpty.Text = Lang.Text("Tools.Studio.Friends.NeedLogin");
            TxtFriendsEmpty.Visibility = Visibility.Visible;
            PanFriends.Children.Clear();
        }
        BtnFriendAdd.IsEnabled = loggedIn;
    }

    /// <summary>启动时校验本地令牌是否还有效；失效则清掉登录态。</summary>
    private async Task ValidateAndLoadFriendsAsync()
    {
        using var api = TryGetApi();
        if (api is null)
        {
            TxtFriendsEmpty.Text = Lang.Text("Tools.Studio.Hint.NoConfig");
            TxtFriendsEmpty.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            var fresh = await api.GetMeAsync(_me!.Token);
            if (fresh is null)
            {
                StudioAccount.Clear();
                _me = null;
                RefreshAccount();
                StudioSession.ReloadLocal();
                HintService.Hint(Lang.Text("Tools.Studio.Hint.SessionExpired"), HintType.Warning);
                return;
            }
            _me = fresh;
            StudioAccount.Save(fresh);
            RefreshAccount();
            await LoadFriendsAsync(api);
        }
        catch
        {
            // 网络不可用时保留本地登录态，仅提示
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NetworkError"), HintType.Warning);
        }
    }

    private async void BtnLogin_Click(object sender, MouseButtonEventArgs e)
    {
        var email = TxtLoginEmail.Text.Trim()
            .Replace('＠', '@')
            .Replace('。', '.')
            .ToLowerInvariant();
        var password = TxtLoginPassword.Password;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.LoginRequired"), HintType.Error);
            return;
        }
        // 邮箱格式预检：避免把服务端 400 原文抛给用户（常见错误：只输用户名没输 @域名）
        if (!System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.LoginFormat"), HintType.Error);
            return;
        }
        using var api = TryGetApi();
        if (api is null)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NoConfig"), HintType.Error);
            return;
        }
        try
        {
            BtnLogin.IsEnabled = false;
            var identity = await api.LoginAsync(email, password);
            StudioAccount.Save(identity);
            _me = identity;
            TxtLoginPassword.Password = "";
            RefreshAccount();
            StudioSession.ReloadLocal();
            _ = StudioSession.RefreshFriendsAsync();
            HintService.Hint(Lang.Text("Tools.Studio.Hint.LoginOk"), HintType.Success);
            await LoadFriendsAsync(api);
        }
        catch (StudioApiException ex)
        {
            HintService.Hint(ex.StatusCode switch
            {
                400 => Lang.Text("Tools.Studio.Hint.LoginFormat"),
                401 => Lang.Text("Tools.Studio.Hint.LoginBad"),
                429 => Lang.Text("Tools.Studio.Hint.TooMany"),
                _ => Lang.Text("Tools.Studio.Hint.LoginFailed") + "：" + ex.Message,
            }, HintType.Error);
        }
        catch
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NetworkError"), HintType.Error);
        }
        finally
        {
            BtnLogin.IsEnabled = true;
        }
    }

    private void BtnShowRegister_Click(object sender, System.EventArgs e)
    {
        Show(PanAccountLogin, false);
        Show(PanAccountRegister, true);
    }

    private void BtnShowLogin_Click(object sender, System.EventArgs e)
    {
        Show(PanAccountRegister, false);
        Show(PanAccountLogin, true);
    }

    private async void BtnSendCode_Click(object sender, MouseButtonEventArgs e)
    {
        var email = TxtRegEmail.Text.Trim();
        if (string.IsNullOrEmpty(email))
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.EmailRequired"), HintType.Error);
            return;
        }
        using var api = TryGetApi();
        if (api is null)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NoConfig"), HintType.Error);
            return;
        }
        try
        {
            BtnSendCode.IsEnabled = false;
            var result = await api.SendCodeAsync(email);
            if (result.Dev && !string.IsNullOrEmpty(result.Code))
            {
                TxtRegCode.Text = result.Code;
                HintService.Hint(Lang.Text("Tools.Studio.Hint.CodeDev"), HintType.Info);
            }
            else
            {
                HintService.Hint(string.Format(Lang.Text("Tools.Studio.Hint.CodeSent"), result.ExpireMinutes),
                    HintType.Success);
            }
            StartCooldown(60);
        }
        catch (StudioApiException ex)
        {
            HintService.Hint(ex.StatusCode switch
            {
                409 => Lang.Text("Tools.Studio.Hint.AlreadyRegistered"),
                429 => Lang.Text("Tools.Studio.Hint.TooMany"),
                400 => Lang.Text("Tools.Studio.Hint.EmailRejected"),
                503 => Lang.Text("Tools.Studio.Hint.MailDown"),
                _ => Lang.Text("Tools.Studio.Hint.CodeFailed") + "：" + ex.Message,
            }, HintType.Error);
            BtnSendCode.IsEnabled = true;
        }
        catch
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NetworkError"), HintType.Error);
            BtnSendCode.IsEnabled = true;
        }
    }

    private async void BtnRegister_Click(object sender, MouseButtonEventArgs e)
    {
        var email = TxtRegEmail.Text.Trim();
        var code = TxtRegCode.Text.Trim();
        var nickname = TxtRegNickname.Text.Trim();
        var password = TxtRegPassword.Password;
        if (string.IsNullOrEmpty(email))
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.EmailRequired"), HintType.Error);
            return;
        }
        if (code.Length != 6)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.CodeRequired"), HintType.Error);
            return;
        }
        if (password.Length < 8)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.PasswordShort"), HintType.Error);
            return;
        }
        using var api = TryGetApi();
        if (api is null)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NoConfig"), HintType.Error);
            return;
        }
        try
        {
            BtnRegister.IsEnabled = false;
            var identity = await api.RegisterAsync(email, code, password,
                string.IsNullOrEmpty(nickname) ? null : nickname);
            StudioAccount.Save(identity);
            _me = identity;
            TxtRegPassword.Password = "";
            TxtRegCode.Text = "";
            RefreshAccount();
            StudioSession.ReloadLocal();
            _ = StudioSession.RefreshFriendsAsync();
            HintService.Hint(Lang.Text("Tools.Studio.Hint.RegisterOk"), HintType.Success);
            await LoadFriendsAsync(api);
        }
        catch (StudioApiException ex)
        {
            HintService.Hint(ex.StatusCode switch
            {
                409 => Lang.Text("Tools.Studio.Hint.AlreadyRegistered"),
                429 => Lang.Text("Tools.Studio.Hint.TooMany"),
                400 => Lang.Text("Tools.Studio.Hint.RegisterRejected") + "：" + ex.Message,
                _ => Lang.Text("Tools.Studio.Hint.RegisterFailed") + "：" + ex.Message,
            }, HintType.Error);
        }
        catch
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NetworkError"), HintType.Error);
        }
        finally
        {
            BtnRegister.IsEnabled = true;
        }
    }

    private void BtnLogout_Click(object sender, MouseButtonEventArgs e)
    {
        StudioAccount.Clear();
        _me = null;
        TxtLoginEmail.Text = "";
        TxtLoginPassword.Password = "";
        RefreshAccount();
        StudioSession.ReloadLocal();
        HintService.Hint(Lang.Text("Tools.Studio.Hint.LogoutOk"), HintType.Info);
    }

    private void StartCooldown(int seconds)
    {
        _cooldownLeft = seconds;
        BtnSendCode.IsEnabled = false;
        BtnSendCode.Text = string.Format(Lang.Text("Tools.Studio.Account.SendCodeCooldown"), _cooldownLeft);
        _cooldownTimer.Start();
    }

    private void CooldownTimer_Tick(object? sender, System.EventArgs e)
    {
        _cooldownLeft--;
        if (_cooldownLeft > 0)
        {
            BtnSendCode.Text = string.Format(Lang.Text("Tools.Studio.Account.SendCodeCooldown"), _cooldownLeft);
            return;
        }
        _cooldownTimer.Stop();
        BtnSendCode.Text = Lang.Text("Tools.Studio.Account.SendCode");
        BtnSendCode.IsEnabled = true;
    }

    // ---------------------------------------------------------------- 好友

    private async Task LoadFriendsAsync(StudioApiClient api)
    {
        PanFriends.Children.Clear();
        TxtFriendsEmpty.Text = Lang.Text("Tools.Studio.Friends.Loading");
        TxtFriendsEmpty.Visibility = Visibility.Visible;
        try
        {
            var friends = await api.FriendListAsync(_me!.Token);
            PanFriends.Children.Clear();
            if (friends.Count == 0)
            {
                TxtFriendsEmpty.Text = Lang.Text("Tools.Studio.Friends.Empty");
                TxtFriendsEmpty.Visibility = Visibility.Visible;
                return;
            }
            TxtFriendsEmpty.Visibility = Visibility.Collapsed;
            foreach (var friend in friends) PanFriends.Children.Add(BuildFriendRow(api, friend));
        }
        catch (StudioApiException ex) when (ex.StatusCode is 401 or 403)
        {
            StudioAccount.Clear();
            _me = null;
            RefreshAccount();
            StudioSession.ReloadLocal();
            HintService.Hint(Lang.Text("Tools.Studio.Hint.SessionExpired"), HintType.Warning);
        }
        catch
        {
            TxtFriendsEmpty.Text = Lang.Text("Tools.Studio.Hint.NetworkError");
            TxtFriendsEmpty.Visibility = Visibility.Visible;
        }
    }

    private UIElement BuildFriendRow(StudioApiClient api, FriendEntry friend)
    {
        var wrap = new StackPanel();
        var expanded = false;   // 房间面板展开状态（每行独立）

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = string.IsNullOrEmpty(friend.Nickname)
            ? friend.Email
            : $"{friend.Nickname}（{friend.Email}）";
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)FindResource("BasedOnTextBlock"),
        };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        // 「房间」按钮：展开该好友正在运行的房间，无需手动输房间码（一键加入）
        var roomsBtn = new MyTextButton
        {
            Text = Lang.Text("Tools.Studio.Friends.ViewRooms"),
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panRooms = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(14, 0, 0, 6),
        };
        roomsBtn.Click += async (_, _) =>
        {
            if (expanded)
            {
                panRooms.Children.Clear();
                panRooms.Visibility = Visibility.Collapsed;
                expanded = false;
                return;
            }
            roomsBtn.IsEnabled = false;
            try
            {
                var (hosted, joined) = await StudioSession.GetFriendRoomsAsync(friend.Email);
                panRooms.Children.Clear();
                // v50.9.5：开的房间与在的房间都列出并标注（同 StudioSession.BuildFriendRoomRow joined 参数）
                if (hosted.Count == 0 && joined.Count == 0)
                {
                    panRooms.Children.Add(new TextBlock
                    {
                        Text = Lang.Text("Tools.Studio.Friends.NoRooms"),
                        Style = (Style)FindResource("BasedOnTextBlock"),
                        FontSize = 12,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                else
                {
                    foreach (var r in hosted) panRooms.Children.Add(BuildFriendRoomRow(r));
                    foreach (var r in joined) panRooms.Children.Add(BuildFriendRoomRow(r, joined: true));
                }
                panRooms.Visibility = Visibility.Visible;
                expanded = true;
            }
            catch (InvalidOperationException)
            {
                HintService.Hint(Lang.Text("Tools.Studio.Hint.NeedLogin"), HintType.Error);
            }
            catch (StudioApiException ex) when (ex.StatusCode == 403)
            {
                HintService.Hint(Lang.Text("Tools.Studio.Friends.NoRooms"), HintType.Info);
            }
            catch (Exception ex)
            {
                HintService.Hint(Lang.Text("Tools.Studio.Hint.NetworkError") + "：" + ex.Message, HintType.Error);
            }
            finally
            {
                roomsBtn.IsEnabled = true;
            }
        };
        Grid.SetColumn(roomsBtn, 1);
        grid.Children.Add(roomsBtn);

        var remove = new MyTextButton
        {
            Text = Lang.Text("Tools.Studio.Friends.Remove"),
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += async (_, _) => await RemoveFriendAsync(api, friend.Email);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);

        wrap.Children.Add(grid);
        wrap.Children.Add(panRooms);
        return wrap;
    }

    /// <summary>好友房间行：房间码 + 版本信息 + 一键加入（共享实现，见 StudioSession.BuildFriendRoomRow）。</summary>
    private UIElement BuildFriendRoomRow(RoomBrief room, bool joined = false)
        => StudioSession.BuildFriendRoomRow(room, joined);

    /// <summary>一键加入好友房间：共享入口（见 StudioSession.JoinRoomWithFeedbackAsync），本页附带详细装配进度显示。</summary>
    private async Task JoinFriendRoomAsync(string code, MyTextButton btn)
    {
        if (StudioSession.Me is null)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NeedLogin"), HintType.Error);
            return;
        }
        ShowJoinProgress(new AssembleProgress(AssembleStep.Resolving, ""));
        var result = await StudioSession.JoinRoomWithFeedbackAsync(code,
            new Progress<AssembleProgress>(ShowJoinProgress), btn);
        if (result is not null && result.Missing.Count == 0)
            TxtJoinProgress.Text = string.Format(
                Lang.Text("Tools.Studio.Join.AssembleDone"),
                string.IsNullOrEmpty(result.PackName) ? result.InstanceId : result.PackName, result.Mods, result.NewLibraries);
    }

    private async void BtnFriendAdd_Click(object sender, MouseButtonEventArgs e)
    {
        if (_me is null)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NeedLogin"), HintType.Error);
            return;
        }
        var email = TxtFriendEmail.Text.Trim();
        if (string.IsNullOrEmpty(email))
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.EmailRequired"), HintType.Error);
            return;
        }
        using var api = TryGetApi();
        if (api is null)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NoConfig"), HintType.Error);
            return;
        }
        try
        {
            BtnFriendAdd.IsEnabled = false;
            await api.FriendAddAsync(_me.Token, email);
            TxtFriendEmail.Text = "";
            HintService.Hint(Lang.Text("Tools.Studio.Hint.FriendAdded"), HintType.Success);
            await LoadFriendsAsync(api);
            _ = StudioSession.RefreshFriendsAsync();
        }
        catch (StudioApiException ex)
        {
            HintService.Hint(ex.StatusCode switch
            {
                404 => Lang.Text("Tools.Studio.Hint.FriendMissing"),
                409 => Lang.Text("Tools.Studio.Hint.FriendExists"),
                429 => Lang.Text("Tools.Studio.Hint.TooMany"),
                _ => Lang.Text("Tools.Studio.Hint.FriendAddFailed") + "：" + ex.Message,
            }, HintType.Error);
        }
        catch
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NetworkError"), HintType.Error);
        }
        finally
        {
            BtnFriendAdd.IsEnabled = true;
        }
    }

    private async Task RemoveFriendAsync(StudioApiClient api, string email)
    {
        if (_me is null) return;
        try
        {
            await api.FriendRemoveAsync(_me.Token, email);
            HintService.Hint(Lang.Text("Tools.Studio.Hint.FriendRemoved"), HintType.Info);
            await LoadFriendsAsync(api);
            _ = StudioSession.RefreshFriendsAsync();
        }
        catch (StudioApiException ex)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.FriendRemoveFailed") + "：" + ex.Message,
                HintType.Error);
        }
    }

    // ---------------------------------------------------------------- 加入房间

    /// <summary>
    /// 读取平台配置（云端 tunnel-api 地址与密钥）。
    /// 查找顺序：启动器目录 mcstudio/api.json → 当前目录 mcstudio/api.json →
    /// v50.9.6 构建期内嵌配置（全新电脑/只换 exe 零配置可用）。
    /// </summary>
    private static StudioApiClient? TryGetApi()
    {
        string[] candidates =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcstudio", "api.json"),
            Path.Combine(Environment.CurrentDirectory, "mcstudio", "api.json"),
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                return new StudioApiClient(StudioConfig.Load(path));
            }
            catch
            {
                // 配置文件损坏则继续找下一个
            }
        }
        // v50.9.6：磁盘 api.json 缺失/损坏 → 内嵌配置兜底
        try
        {
            var embedded = StudioSecrets.EmbeddedJson("api.json");
            if (embedded is not null) return new StudioApiClient(StudioConfig.Parse(embedded));
        }
        catch { /* 内嵌缺失：保持 null（联机入口会提示） */ }
        return null;
    }

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
        using var api = TryGetApi();
        if (api is null)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.NoConfig"), HintType.Error);
            return;
        }
        try
        {
            BtnJoin.IsEnabled = false;
            var info = await api.ResolveAsync(code);
            TxtResult.Text = string.Format(
                Lang.Text("Tools.Studio.Join.Found"),
                info.Code, info.Loader, info.Mc, info.Address, info.Mods.Count, info.ExpiresIn / 60);
            TxtResult.Visibility = Visibility.Visible;
            ShowJoinProgress(new AssembleProgress(AssembleStep.Resolving, ""));

            // v36 复用快捷路径：vanilla 房 + 本机已有同版本原版实例 → 免装配直接进房。
            // 该入口无回环包装：地址非 127.0.0.1（公网中转）时不复用（防绕过启动器直连公网），回落装配路径（装配会包回环）
            if (info.Address.StartsWith("127.0.0.1", StringComparison.Ordinal))
            {
                var reuse = await StudioSession.TryFindReusableInstanceAsync(info).ConfigureAwait(true);
                if (reuse is not null)
                {
                    TxtJoinProgress.Text = string.Format(
                        Lang.Text("Tools.Studio.Join.ReuseHit"), info.Mc, reuse.Name);
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
            }

            // 自动装配：合成版本 json（烤入直连参数）→ 下载客户端与依赖 → 同步模组
            // v37 房主快速通道：输入的是本机正在开的房间码 → 模组优先从服务端目录本地复制
            var progress = new Progress<AssembleProgress>(ShowJoinProgress);
            var hostModsDir = StudioSession.TryGetHostLocalModsDir(info.Code);
            var result = await new RoomAssembler().AssembleAsync(info, GetMcFolder(), progress,
                localModsDir: hostModsDir);
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
                return;
            }
            // 自动进入游戏：刷新版本列表选中该实例并直接启动（缺的客户端文件由 PCL 自动下载）
            HintService.Hint(Lang.Text("Tools.Studio.Join.Launching"), HintType.Info);
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

    /// <summary>装配目标 .minecraft：当前选中的游戏文件夹，缺省回落启动器目录。</summary>
    private static string GetMcFolder()
    {
        var selected = ModFolder.mcFolderSelected;
        if (!string.IsNullOrEmpty(selected) && Directory.Exists(selected)) return selected;
        var fallback = Path.Combine(ModBase.exePath, ".minecraft");
        Directory.CreateDirectory(Path.Combine(fallback, "versions"));
        return fallback;
    }

    // ---------------------------------------------------------------- 一键开房

    private async void BtnHostStart_Click(object sender, MouseButtonEventArgs e)
    {
        _hostApi = TryGetApi();
        if (_hostApi is null)
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
        var javaExe = ResolveJavaExe(env);
        if (!File.Exists(javaExe))
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.JavaMissing"), HintType.Error);
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
        SetHostCombo(ComboHostMc, mc);
        var loaderText = CapitalizeLoader(loader);
        if (!ComboHostLoader.Items.Contains(loaderText)) ComboHostLoader.Items.Add(loaderText);
        ComboHostLoader.SelectedItem = loaderText;
        }
        else
        {
        // 导入整合包记录的建议版本优先（导入池里的模组版本与下拉框默认值可能不符）；Quilt 不支持托管，跳过
        var (impMc, impLoader) = ModpackImporter.GetImportSuggestion();
        if (impMc.Length > 0 && impLoader != "quilt" && (impMc != mc || (impLoader.Length > 0 && impLoader != loader)))
        {
            HintService.Hint($"已按导入的整合包版本开房：MC {impMc}" +
                             (impLoader.Length > 0 ? $" / {impLoader}" : ""), HintType.Info);
            mc = impMc;
            if (impLoader.Length > 0) loader = impLoader;
        }
        }

        // 重开档案 → 档案 slug（复用端口/房间码/服务端目录）；新服务器 → 下一个可用 slug
        var roomSlug = _serverProfile?.Slug ?? ServerListStore.NextSlug(HostSlug());
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

            _host = new HostRoomManager(_hostApi, env.FrpcExe, env.FrpsHost, env.FrpsPort, env.Token);
            var progress = new Progress<HostProgress>(ShowHostProgress);
            var room = await _host.StartAsync(new HostOptions
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
            HintService.Hint(string.Format(Lang.Text("Tools.Studio.Hint.HostStarted"), room.Code),
                HintType.Success);
            SaveServerProfile(mc, loader, roomSlug, room.Code, clientPack);
        }
        catch (Exception ex)
        {
            CleanupHost();
            TxtHostStatus.Text = Lang.Text("Tools.Studio.Host.StatusFailed");
            // v45.2：服务端崩溃类错误自带【诊断】，不再套"检查网络/frpc"的误导前缀
            var isServerCrash = ex.Message.Contains("服务端启动过程中退出") || ex.Message.Contains("【诊断】");
            HintService.Hint(isServerCrash ? ex.Message
                : Lang.Text("Tools.Studio.Hint.HostFailed") + "：" + ex.Message, HintType.Error);
        }
        finally
        {
            // 房间运行中保持禁用（防重复开房）；失败/关房后恢复
            BtnHostStart.IsEnabled = _host is null;
        }
    }

    private async void BtnHostStop_Click(object sender, MouseButtonEventArgs e)
    {
        if (_host is null) return;
        try
        {
            BtnHostStop.IsEnabled = false;
            TxtHostStatus.Text = Lang.Text("Tools.Studio.Host.StatusStopping");
            var progress = new Progress<HostProgress>(ShowHostProgress);
            await _host.StopAsync(progress);
            TxtHostStatus.Text = Lang.Text("Tools.Studio.Host.StatusStopped");
            HintService.Hint(Lang.Text("Tools.Studio.Hint.HostStopped"), HintType.Info);
        }
        catch (Exception ex)
        {
            HintService.Hint(Lang.Text("Tools.Studio.Hint.HostStopFailed") + "：" + ex.Message, HintType.Error);
        }
        finally
        {
            CleanupHost();
            BtnHostStart.IsEnabled = true;
            BtnHostStop.IsEnabled = false;
        }
    }

    private void CleanupHost()
    {
        _host?.Dispose();
        _host = null;
        _hostApi?.Dispose();
        _hostApi = null;
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

    /// <summary>开房房间名：host-{登录邮箱前缀}，未登录回落 host-{机器名}（云端房间名空间全局唯一）。</summary>
    private string HostSlug()
    {
        var seed = _me is not null ? _me.Email.Split('@')[0] : Environment.MachineName;
        var sb = new System.Text.StringBuilder("host-");
        foreach (var ch in seed.ToLowerInvariant())
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
        var name = sb.ToString();
        return name.Length > 20 ? name[..20] : name;
    }

    /// <summary>Java 解析：host.json 环境路径 → PCL 扫描到的 Java 21+ → PATH。</summary>
    private static string ResolveJavaExe(HostEnvironment env)
    {
        if (File.Exists(env.JavaExe)) return env.JavaExe;
        try
        {
            var java = JavaService.JavaManager.GetSortedJavaList()
                .Where(j => j.IsEnabled)
                .Select(j => j.Installation)
                .FirstOrDefault(i => i.MajorVersion >= 21 && i.IsStillAvailable);
            if (java is not null) return java.JavaExePath;
        }
        catch
        {
            // Java 管理器未就绪，回落环境配置
        }
        return env.JavaExe;
    }

    private static void Show(UIElement element, bool visible)
        => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
