using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioHub.Core;
using AudioHub.Core.Connection;
using AudioHub.Core.Discovery;
using AudioHub.Hosting;
using AudioHub.Platform.Windows;
using QRCoder;

// UseWPF and UseWindowsForms both contribute implicit global usings and clash on
// several type names, so anything ambiguous gets an explicit alias.
using ProgressBar = System.Windows.Controls.ProgressBar;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace AudioHub.App;

/// <summary>Settings window: pick the output device and set per-channel gain.</summary>
public partial class MainWindow : Window
{
    private readonly IAudioHubHost _host;
    private readonly int _webPort;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, ProgressBar> _meters = new(StringComparer.Ordinal);

    /// <summary>
    /// How recently an unencrypted packet must have been refused for the count to still be worth
    /// reporting. Past this window the refusals are history, not a diagnosis.
    /// </summary>
    private static readonly TimeSpan RejectionHintWindow = TimeSpan.FromSeconds(10);

    /// <summary>When the graph last started running, so "no packets yet" can say for how long.</summary>
    private DateTimeOffset? _runningSince;

    /// <summary>When the output device name was last refreshed, to throttle device re-enumeration.</summary>
    private DateTimeOffset _lastDeviceRefresh;

    /// <summary>Remembers whether the QR code includes the pairing key. Default is on.</summary>
    private readonly UiSettings _settings = UiSettings.Load();

    /// <summary>
    /// Set while the code expands or collapses the panel itself, so that "user touched it" is
    /// not recorded for something the user did not do. Without it the 15-second auto-expand
    /// would be indistinguishable from the user opening it, and auto-collapse would never run.
    /// </summary>
    private bool _suppressPanelEvents;

    /// <summary>Set while the code sets the security dropdown itself, so the handler ignores it.</summary>
    private bool _suppressSecurityEvent;

    /// <summary>True once the user opened or closed the panel by hand; stops all automation.</summary>
    private bool _userTouchedPanel;

    /// <summary>True while the panel is expanded because nothing has arrived yet.</summary>
    private bool _autoExpanded;

    /// <summary>When the first packet arrived, so the panel can retract a few seconds later.</summary>
    private DateTimeOffset? _firstPacketAt;

    /// <summary>When the transient "copied" hint should disappear.</summary>
    private DateTimeOffset _copyHintUntil;

    private string _recommendedAddress = string.Empty;
    private string _pairingKey = string.Empty;
    private bool _keyVisible;
    private bool _qrVisible;

    /// <summary>Last payload drawn into the QR image, so an unchanged tick can skip the redraw.</summary>
    private string? _lastQrPayload;

    public MainWindow(IAudioHubHost host, int webPort, string? lanReservationCommand = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
        _webPort = webPort;

        InitializeComponent();
        UrlText.Text = lanReservationCommand is null
            ? $"网页控制台（局域网可用）：{DescribeAddresses()}"
            : $"网页控制台（仅本机）：{DescribeAddresses()}\n让手机也能访问，请以管理员身份执行一次：\n{lanReservationCommand}";

        RefreshDevices();
        RebuildChannelRows();

        // Set the remembered choices without treating them as user actions.
        _suppressPanelEvents = true;
        QrIncludeKeyCheck.IsChecked = _settings.QrIncludesPairingKey;

        // 二维码默认就摆出来，面板是否展开也按上次的选择来。
        // 两件事都必须在这里做：_qrVisible 要在 RefreshConnectionInfo 之前定下来，
        // 否则第一次刷新会按"不显示"跳过生成，用户看到的是一块空白。
        _qrVisible = _settings.QrVisible;
        ApplyQrVisibility();
        ManualExpander.IsExpanded = _settings.ManualPanelExpanded;

        _suppressPanelEvents = false;

        // 接收加密策略：以保存的值为准，不当作一次用户操作。
        _suppressSecurityEvent = true;
        SecurityModeCombo.SelectedIndex =
            SecurityIndexFor(SecurityModeNames.Parse(_settings.AdvertisedSecurity));
        _suppressSecurityEvent = false;

        RefreshConnectionInfo();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();

        UpdateStatus();
    }

    private void RefreshDevices()
    {
        _host.RefreshOutputDevices();

        // 固定用系统默认输出设备（不提供切换入口），这里只显示它的名字作说明。
        AudioEndpointInfo? device = _host.OutputDevices.FirstOrDefault(d => d.IsDefault);
        DeviceNameText.Text = device is null
            ? "系统默认输出设备（未检测到播放设备）"
            : $"当前输出：{device.Name}（跟随系统默认设备）";
    }

    private void RebuildChannelRows()
    {
        ChannelPanel.Children.Clear();
        _meters.Clear();

        foreach (string channelId in _host.Mixer.ChannelIds)
        {
            Grid grid = new() { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

            TextBlock name = new() { Text = channelId, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 0);

            ProgressBar meter = new()
            {
                Height = 6,
                Maximum = 0.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75)),
            };
            Grid.SetColumn(meter, 1);

            Slider gain = new()
            {
                Minimum = -60,
                Maximum = 20,
                Value = _host.Mixer.GetGainDb(channelId),
                VerticalAlignment = VerticalAlignment.Center,
                TickFrequency = 6,
                IsSnapToTickEnabled = false,
            };
            Grid.SetColumn(gain, 2);

            TextBlock gainText = new()
            {
                Text = $"{_host.Mixer.GetGainDb(channelId):0.0} dB",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(gainText, 3);

            Button mute = new() { Content = _host.Mixer.GetMuted(channelId) ? "取消静音" : "静音", Padding = new Thickness(4, 2, 4, 2) };
            Grid.SetColumn(mute, 4);

            string capturedId = channelId;
            gain.ValueChanged += (_, _) =>
            {
                _host.Mixer.SetGainDb(capturedId, (float)gain.Value);
                gainText.Text = $"{gain.Value:0.0} dB";
            };

            mute.Click += (_, _) =>
            {
                bool next = !_host.Mixer.GetMuted(capturedId);
                _host.Mixer.SetMuted(capturedId, next);
                mute.Content = next ? "取消静音" : "静音";
            };

            grid.Children.Add(name);
            grid.Children.Add(meter);
            grid.Children.Add(gain);
            grid.Children.Add(gainText);
            grid.Children.Add(mute);
            ChannelPanel.Children.Add(grid);

            _meters[capturedId] = meter;
        }
    }

    private void UpdateStatus()
    {
        // 设备名跟随系统默认设备：插拔耳机 / 切换蓝牙后，2 秒内反映出来。
        if (DateTimeOffset.UtcNow - _lastDeviceRefresh > TimeSpan.FromSeconds(2))
        {
            _lastDeviceRefresh = DateTimeOffset.UtcNow;
            RefreshDevices();
            // Switching Wi-Fi or plugging in USB tethering changes the address the phone needs,
            // so the manual panel has to follow it without the user reopening the window.
            RefreshConnectionInfo();
        }

        bool running = _host.IsRunning;
        _runningSince = running ? _runningSince ?? DateTimeOffset.UtcNow : null;

        StatusText.Text = running
            ? $"运行中 · {_host.Mixer.Format.SampleRate} Hz · {_host.Mixer.Format.Channels} 声道 · {_host.Mixer.ChannelIds.Count} 路音源"
            : "已停止";

        RefreshSecurityNote();

        // Shown rather than left implicit, because a limit nobody can see is a limit nobody
        // knows they have. Most people run the tray, not the console, so putting this only on
        // the CLI would leave the common case uninformed.
        float ceilingDb = _host.Mixer.OutputCeilingDb;
        CeilingText.Text = ceilingDb >= 0f
            ? "输出上限：0 dB（无保护）—— 任何能发到 6980 端口的设备都能把耳机推到满刻度。"
            : $"输出上限：{ceilingDb:0.#} dBFS（任何输入都不会超过这个值）";
        CeilingText.Foreground = new SolidColorBrush(ceilingDb >= 0f
            ? Color.FromRgb(0xA3, 0x2D, 0x2D)
            : Color.FromRgb(0x80, 0x80, 0x80));

        (string text, bool nothingArrived) = DescribeNetwork();
        NetworkText.Text = text;
        NetworkText.Foreground = new SolidColorBrush(nothingArrived
            ? Color.FromRgb(0xA3, 0x2D, 0x2D)
            : Color.FromRgb(0x80, 0x80, 0x80));

        foreach (KeyValuePair<string, ProgressBar> pair in _meters)
        {
            pair.Value.Value = _host.Mixer.GetLevel(pair.Key);
        }

        UpdateManualPanelAutomation(running);

        if (CopyHint.Text.Length > 0 && DateTimeOffset.UtcNow > _copyHintUntil)
        {
            CopyHint.Text = string.Empty;
        }
    }

    // The tray has no console behind it, so this window is the only place a connection problem
    // can surface. Before this existed the tray showed only channel meters, and a phone that
    // never reached the PC looked exactly like a phone that had not started yet — the two need
    // opposite actions, so leaving them indistinguishable was the actual bug.
    private (string Text, bool NothingArrived) DescribeNetwork()
    {
        if (_host.TransportStats is not { } stats)
        {
            return ("网络：未启用音源传输。", false);
        }

        // Whether this is happening *now*, not whether it ever happened. The count is cumulative
        // and never falls, so a phone that was refused once and then paired would otherwise keep
        // producing the warning below for the rest of the session — and then bury the next real
        // problem behind a message the user has already fixed.
        bool rejectionsRecent = stats.LastRejectedUnencryptedUtc is { } rejectedAt &&
            DateTimeOffset.UtcNow - rejectedAt <= RejectionHintWindow;

        // A phone without the key sends fine; every packet is refused here. Counting those is the
        // difference between "your firewall is blocking it" and "this phone has no key yet", and
        // the two need opposite actions. Without this branch the zero-accepted path below tells
        // the user to go and check the network, which is the wrong thing to do about a key.
        if (stats.PacketsAccepted == 0 && rejectionsRecent)
        {
            return ($"网络：手机正在发，但被拒了 {stats.PacketsRejectedUnencrypted} 个包 —— "
                    + "这台电脑设的是「仅加密」，而这台手机还没配密钥。\n"
                    + "  在手机上填一次配对密钥就好：电脑这扇窗口「手动连接 → 配对密钥 → 显示」，"
                    + "或跑 dist\\show-key.bat。\n"
                    + "  （不想用加密，就把上面「接收加密」改成「关闭加密」。）", true);
        }

        if (stats.PacketsAccepted > 0)
        {
            string crypto = _host.SecurityMode == SecurityMode.None
                ? " · 加密已关闭"
                : $" · 解密 {_host.PacketsDecrypted ?? 0} 个";

            // Two phones, one paired and one not, is a real setup: the paired one plays while the
            // unpaired one is silently dropped. Say so rather than letting one work and the other
            // vanish for no stated reason — but only while it is still true.
            string strays = rejectionsRecent
                ? $" · 另有 {stats.PacketsRejectedUnencrypted} 个未加密包被拒（有设备还没配密钥）"
                : string.Empty;

            return ($"网络：已收到 {stats.PacketsAccepted} 个包 · 拒绝 {stats.PacketsRejected}"
                    + crypto + $" · 丢失 {stats.FramesLost} 帧" + strays, false);
        }

        int waited = _runningSince is { } since
            ? (int)(DateTimeOffset.UtcNow - since).TotalSeconds
            : 0;

        if (waited < 5)
        {
            return ("网络：正在等待手机发来第一个包…", false);
        }

        StringBuilder text = new();
        text.AppendLine($"网络：还没收到任何包（已等 {waited} 秒），通常是这三件事之一：");
        text.AppendLine("  1  这个网络不允许设备互相访问（校园网 / 公共 WiFi 常见，叫 AP 隔离）");
        text.AppendLine("  2  手机和电脑不在同一个网段（比如一个连无线、一个插有线）");
        text.AppendLine("  3  电脑防火墙没放行 —— 跑一次 dist\\allow-firewall.bat");
        text.AppendLine($"  手机上要填（挑同网段的那个）：{string.Join("  ", LocalIPv4Addresses())}");
        text.Append("  拿不准就用手机热点或 USB 网络共享：那是你自己的私有链路，不受环境影响。");
        return (text.ToString(), true);
    }

    // ------------------------------------------------------------ 手动连接（折叠面板）

    /// <summary>
    /// Fills the manual-connection panel. Everything shown here already exists on the host:
    /// the identity, the pairing key and the transport counters. Nothing new had to be plumbed
    /// through, which is why this panel cannot drift away from what the phone actually needs.
    /// </summary>
    private void RefreshConnectionInfo()
    {
        List<(string Address, bool Recommended)> addresses = LocalIPv4WithRole();

        _recommendedAddress = addresses.Where(a => a.Recommended).Select(a => a.Address).FirstOrDefault()
            ?? addresses.Select(a => a.Address).FirstOrDefault()
            ?? string.Empty;

        AddressText.Text = addresses.Count == 0
            ? "未检测到局域网地址"
            : string.Join("   ", addresses.Select(a => a.Recommended ? a.Address + "（推荐）" : a.Address));

        // The tray always uses the default port, and a short code does not carry a port,
        // so these two numbers are fixed rather than configurable here.
        PortsText.Text = "6980 音频 · 6979 发现";

        _pairingKey = _host.PairingKey ?? string.Empty;
        KeyText.Text = _pairingKey.Length == 0
            ? "未启用加密（明文接收）"
            : _keyVisible ? _pairingKey : new string('•', 12);
        RevealKeyButton.Content = _keyVisible ? "隐藏" : "显示";
        RevealKeyButton.IsEnabled = _pairingKey.Length > 0;
        CopyKeyButton.IsEnabled = _pairingKey.Length > 0;

        FingerprintText.Text = _host.Identity.Fingerprint;

        if (ShortCode.TryEncode(_recommendedAddress, out string code))
        {
            ShortCodeText.Text = code;
            ShortCodeHeader.Text = code;
            ShortCodeNote.Text = "手机输入这 6 位";
        }
        else
        {
            ShortCodeText.Text = "—";
            ShortCodeHeader.Text = string.Empty;
            ShortCodeNote.Text = addresses.Count == 0 ? "没有可用地址" : "此网段不支持短码，请用二维码";
        }

        if (_qrVisible)
        {
            // 这条路径每 2 秒跑一次（跟 IP / 默认设备），所以必须判重。
            RegenerateQrIfChanged();
        }
    }

    /// <summary>
    /// Builds the QR payload and renders it. The key is included only when the user left the
    /// switch on; with it off the code still gets the phone to the right address, but a first
    /// pairing will ask for the key.
    /// </summary>
    private void RegenerateQr()
    {
        if (_recommendedAddress.Length == 0)
        {
            QrImage.Source = null;
            QrNote.Text = "没有可用地址，二维码生成不了。";
            _lastQrPayload = null;
            return;
        }

        string payload = BuildQrPayload();
        ApplyQr(payload);
        _lastQrPayload = payload;
    }

    /// <summary>
    /// Same as <see cref="RegenerateQr"/> but skips the work when the payload has not changed.
    /// </summary>
    /// <remarks>
    /// 这张二维码现在默认是展开的，而托盘每 2 秒刷新一次连接信息（为了跟住 IP 和默认设备）。
    /// 不判重就等于**每 2 秒编码并重画一张二维码**，白烧 UI 线程 —— 而它的内容
    /// 只在地址、密钥、加密模式、机器名变化时才会变。
    /// </remarks>
    private void RegenerateQrIfChanged()
    {
        string payload = BuildQrPayload();
        if (payload == _lastQrPayload)
        {
            return;
        }

        ApplyQr(payload);
        _lastQrPayload = payload;
    }

    /// <summary>
    /// What the QR actually encodes. Kept in one place so "did it change" and "draw it"
    /// can never disagree about what the code contains.
    /// </summary>
    private string BuildQrPayload()
    {
        bool includeKey = QrIncludeKeyCheck.IsChecked == true && _pairingKey.Length > 0;

        // The mode goes in so a scan does not have to guess it. Without this the phone fell back
        // to "encryption optional" and kept sealing packets at a hub that had encryption off,
        // where nothing can read them — silent on both ends.
        return ConnectionQr.Build(new ConnectionQrPayload(
            _recommendedAddress,
            ShortCode.DefaultPort,
            includeKey ? _pairingKey : null,
            _host.Identity.HubId,
            _host.Identity.Fingerprint,
            Environment.MachineName,
            _host.SecurityMode));
    }

    private void ApplyQr(string payload)
    {
        bool includeKey = QrIncludeKeyCheck.IsChecked == true && _pairingKey.Length > 0;

        QrImage.Source = RenderQr(payload);
        QrNote.Text = includeKey
            ? "二维码含配对密钥：手机扫一次即可完成配对。不要把它留在公共可见的地方。"
            : "二维码只含地址：手机首次配对仍需输入密钥。";
    }

    /// <summary>Encodes the payload with medium error correction and returns a frozen image.</summary>
    private static BitmapImage RenderQr(string text)
    {
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        PngByteQRCode png = new(data);
        byte[] bytes = png.GetGraphic(6);

        BitmapImage image = new();
        using MemoryStream stream = new(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ManualExpander_Expanded(object sender, RoutedEventArgs e)
    {
        // 自动化展开（"开始收包但一直没声音"时替用户打开这块）不算用户的选择，
        // 所以不能在这里写进设置 —— 那会让人下次启动莫名看到一整面板技术细节。
        if (_suppressPanelEvents)
        {
            return;
        }

        _userTouchedPanel = true;
        RememberPanelState(true);
        RefreshConnectionInfo();
    }

    private void ManualExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (_suppressPanelEvents)
        {
            return;
        }

        _userTouchedPanel = true;
        RememberPanelState(false);
    }

    private void RememberPanelState(bool expanded)
    {
        _settings.ManualPanelExpanded = expanded;
        _settings.Save();
    }

    private void RevealKey_Click(object sender, RoutedEventArgs e)
    {
        _keyVisible = !_keyVisible;
        RefreshConnectionInfo();
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(_recommendedAddress, "地址");

    private void CopyKey_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(_pairingKey, "配对密钥");

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        string text =
            "AudioHub 连接信息" + Environment.NewLine
            + "地址：" + _recommendedAddress + Environment.NewLine
            + "端口：6980" + Environment.NewLine
            + "配对密钥：" + (_pairingKey.Length == 0 ? "未启用加密" : _pairingKey) + Environment.NewLine
            + "身份指纹：" + _host.Identity.Fingerprint;

        CopyToClipboard(text, "连接信息");
    }

    private void CopyToClipboard(string text, string what)
    {
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            CopyHint.Text = $"已复制{what}";
            _copyHintUntil = DateTimeOffset.UtcNow.AddSeconds(2);
        }
        catch (ExternalException)
        {
            // Another process holds the clipboard. Saying so beats a silent no-op.
            CopyHint.Text = "复制失败：剪贴板正被其他程序占用";
            _copyHintUntil = DateTimeOffset.UtcNow.AddSeconds(3);
        }
    }

    private void ToggleQr_Click(object sender, RoutedEventArgs e)
    {
        _qrVisible = !_qrVisible;
        ApplyQrVisibility();

        // 记住这个选择。用户明确要求过"折叠与否由我决定"，那就不能下次启动又替他展开。
        _settings.QrVisible = _qrVisible;
        _settings.Save();

        if (_qrVisible)
        {
            RegenerateQr();
        }
        else
        {
            QrNote.Text = string.Empty;
        }
    }

    /// <summary>
    /// 把 <see cref="_qrVisible"/> 落到控件上。构造时和点击时走同一段，
    /// 免得两处各写一份"可见性 + 按钮文字"而慢慢对不上。
    /// </summary>
    private void ApplyQrVisibility()
    {
        QrImage.Visibility = _qrVisible ? Visibility.Visible : Visibility.Collapsed;
        ShowQrButton.Content = _qrVisible ? "隐藏二维码" : "显示二维码";
    }

    private void QrIncludeKey_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPanelEvents)
        {
            return;
        }

        _settings.QrIncludesPairingKey = QrIncludeKeyCheck.IsChecked == true;
        _settings.Save();

        if (_qrVisible)
        {
            RegenerateQr();
        }
    }

    private void SecurityMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSecurityEvent)
        {
            return;
        }

        if (SecurityModeCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        // Normalise through the one parser so the settings file can never drift to a
        // spelling that the loader would read as a different mode.
        SecurityMode mode = SecurityModeNames.Parse(item.Tag as string);
        _settings.AdvertisedSecurity = SecurityModeNames.ToName(mode);
        _settings.Save();

        // Apply to the running hub right away. Saving alone used to leave the window showing the
        // new value while the hub kept enforcing the old one — a disagreement that is silent from
        // both sides, and exactly how a phone ends up sealing packets at a hub with no envelope.
        _host.SetSecurityMode(mode);

        // The key goes away when encryption is off, and the QR carries the mode, so both have to
        // be rebuilt rather than waiting for the next 250 ms tick.
        RefreshConnectionInfo();
        RefreshSecurityNote();
    }

    /// <summary>
    /// Says whether the dropdown matches what the running host is actually doing. A saved
    /// setting that still needs a restart must not look like it is already in force.
    /// </summary>
    private void RefreshSecurityNote()
    {
        if (SecurityModeCombo.SelectedItem is not ComboBoxItem item)
        {
            SecurityModeNote.Text = string.Empty;
            return;
        }

        SecurityMode saved = SecurityModeNames.Parse(item.Tag as string);
        if (saved == _host.SecurityMode)
        {
            SecurityModeNote.Text = "当前生效";
            SecurityModeNote.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75));
        }
        else
        {
            // Should not happen any more: the change is applied to the running host immediately.
            // If it ever does, saying "restart later" would hide a real failure, so say what it is.
            SecurityModeNote.Text = "设置未能生效，请重新选择";
            SecurityModeNote.Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0x2D, 0x2D));
        }
    }

    /// <summary>
    /// ComboBox order is 自动 / 仅加密 / 关闭加密, so the index mapping lives here and nowhere
    /// else — the XAML must list the items in that order.
    /// </summary>
    private static int SecurityIndexFor(SecurityMode mode) => mode switch
    {
        SecurityMode.Required => 1,
        SecurityMode.None => 2,
        _ => 0,
    };

    /// <summary>
    /// Expands the panel after 15 seconds without a single packet, and retracts it three seconds
    /// after the first one arrives. Both are skipped once the user has touched the panel: an
    /// expander that closes itself under the user's hands is worse than no automation at all.
    /// </summary>
    private void UpdateManualPanelAutomation(bool running)
    {
        if (_userTouchedPanel)
        {
            return;
        }

        bool receiving = _host.TransportStats is { PacketsAccepted: > 0 };

        if (receiving)
        {
            _firstPacketAt ??= DateTimeOffset.UtcNow;

            if (_autoExpanded && DateTimeOffset.UtcNow - _firstPacketAt > TimeSpan.FromSeconds(3))
            {
                SetPanelExpanded(false);
                _autoExpanded = false;
            }

            return;
        }

        _firstPacketAt = null;

        if (!running || _autoExpanded)
        {
            return;
        }

        int waited = _runningSince is { } since
            ? (int)(DateTimeOffset.UtcNow - since).TotalSeconds
            : 0;

        if (waited >= 15)
        {
            SetPanelExpanded(true);
            _autoExpanded = true;
        }
    }

    private void SetPanelExpanded(bool expanded)
    {
        _suppressPanelEvents = true;
        ManualExpander.IsExpanded = expanded;
        _suppressPanelEvents = false;
    }

    /// <summary>
    /// Local IPv4 addresses, with exactly one marked as recommended: the first one on an
    /// interface that is up and has a default gateway. Virtual adapters and Bluetooth PAN
    /// usually have no gateway, so they end up below the line rather than leading the list.
    /// </summary>
    private static List<(string Address, bool Recommended)> LocalIPv4WithRole()
    {
        List<(string Address, bool HasGateway)> found = new();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            bool hasGateway = nic.GetIPProperties().GatewayAddresses
                .Any(g => g.Address is not null && !g.Address.Equals(System.Net.IPAddress.Any));

            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    continue;
                }

                string text = address.Address.ToString();

                // APIPA means the interface never got a lease, so nothing can reach it.
                if (text.StartsWith("169.254.", StringComparison.Ordinal))
                {
                    continue;
                }

                found.Add((text, hasGateway));
            }
        }

        int recommended = found.FindIndex(a => a.HasGateway);
        if (recommended < 0 && found.Count > 0)
        {
            recommended = 0;
        }

        return found
            .Select((a, index) => (a.Address, index == recommended))
            .ToList();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        await _host.StartAsync().ConfigureAwait(true);
        UpdateStatus();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await _host.StopAsync().ConfigureAwait(true);
        UpdateStatus();
    }

    private static IEnumerable<string> LocalIPv4Addresses()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    yield return address.Address.ToString();
                }
            }
        }
    }

    private string DescribeAddresses()
    {
        List<string> addresses = ["http://localhost:" + _webPort];

        foreach (string address in LocalIPv4Addresses())
        {
            addresses.Add($"http://{address}:" + _webPort);
        }

        return string.Join("  ·  ", addresses.Distinct());
    }
}
