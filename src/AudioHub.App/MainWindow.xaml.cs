using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioHub.Core;
using AudioHub.Hosting;
using AudioHub.Platform.Windows;

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

    /// <summary>When the graph last started running, so "no packets yet" can say for how long.</summary>
    private DateTimeOffset? _runningSince;

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

        DeviceBox.SelectionChanged -= DeviceBox_SelectionChanged;
        DeviceBox.ItemsSource = _host.OutputDevices;
        DeviceBox.DisplayMemberPath = nameof(AudioEndpointInfo.Name);
        DeviceBox.SelectedValuePath = nameof(AudioEndpointInfo.Id);
        DeviceBox.SelectedValue = _host.OutputDeviceId
            ?? _host.OutputDevices.FirstOrDefault(d => d.IsDefault)?.Id;
        DeviceBox.SelectionChanged += DeviceBox_SelectionChanged;
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
        bool running = _host.IsRunning;
        _runningSince = running ? _runningSince ?? DateTimeOffset.UtcNow : null;

        StatusText.Text = running
            ? $"运行中 · {_host.Mixer.Format.SampleRate} Hz · {_host.Mixer.Format.Channels} 声道 · {_host.Mixer.ChannelIds.Count} 路音源"
            : "已停止";

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

        if (stats.PacketsAccepted > 0)
        {
            return ($"网络：已收到 {stats.PacketsAccepted} 个包 · 拒绝 {stats.PacketsRejected}"
                    + $" · 丢失 {stats.FramesLost} 帧", false);
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

    private async void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceBox.SelectedValue is not string deviceId)
        {
            return;
        }

        await _host.SetOutputDeviceAsync(deviceId).ConfigureAwait(true);
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
