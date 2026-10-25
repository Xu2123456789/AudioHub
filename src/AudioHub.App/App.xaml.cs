using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using AudioHub.Hosting;
using AudioHub.Web;
using Forms = System.Windows.Forms;

// UseWPF and UseWindowsForms both contribute implicit global usings, and the two
// frameworks share type names. Alias what we actually mean.
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Color = System.Drawing.Color;
using FontStyle = System.Drawing.FontStyle;
using SmoothingMode = System.Drawing.Drawing2D.SmoothingMode;

namespace AudioHub.App;

/// <summary>
/// Tray-resident host process. Starts the audio graph and the web console, and stays
/// out of the way until the user opens the window.
/// </summary>
public partial class App : Application
{
    private const int WebPort = 5055;

    private WindowsAudioHubHost? _host;
    private WebConsole? _web;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private Bitmap? _iconBitmap;

    /// <summary>The running graph, exposed to the settings window.</summary>
    public IAudioHubHost? Host => _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // VBAN on, local capture off. Both halves of that are deliberate.
            //
            // Local capture has to stay off while the headset is the Windows default playback
            // device: tapping the endpoint we render to feeds the mixer its own output back,
            // which is the comb-filtered noise and pumping level this app shipped with. The
            // PC's own audio still reaches the headset, because Windows mixes it there itself.
            //
            // That leaves the phone as the only thing AudioHub actually contributes, so the
            // transport has to be on for the tray app to do anything at all. It used to default
            // to None, which made the tray app a graph with no sources in it.
            _host = new WindowsAudioHubHost(AudioHubOptions.Default with { Transport = TransportKind.Vban });
            await _host.StartAsync().ConfigureAwait(true);

            _web = new WebConsole(_host);
            await _web.StartAsync(WebPort).ConfigureAwait(true);

            BuildTray();

            // Show the window on launch as well as keeping the tray icon. A tray-only
            // startup gives no feedback that anything happened, which is confusing the
            // first time. Closing the window leaves the app running in the tray.
            ShowWindow();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"AudioHub 启动失败：{ex.Message}",
                "AudioHub",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_web is not null)
        {
            await _web.DisposeAsync().ConfigureAwait(false);
        }

        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _iconBitmap?.Dispose();

        base.OnExit(e);
    }

    private void BuildTray()
    {
        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("打开 AudioHub…", null, (_, _) => ShowWindow());
        menu.Items.Add("刷新设备列表", null, (_, _) => _host?.RefreshOutputDevices());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "AudioHub — 多音源混音",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        if (_host is null)
        {
            return;
        }

        if (_window is null)
        {
            _window = new MainWindow(_host, WebPort, _web?.LanReservationCommand);
            _window.Closed += (_, _) => _window = null;
        }

        _window.Show();
        _window.Activate();
    }

    /// <summary>
    /// 托盘图标：与安卓启动器、exe 图标同一套——蓝底 + 白色耳机。
    ///
    /// 这里只画耳机、不画那三条汇聚线，是因为 Windows 托盘实际按 16px 显示，
    /// "三条线 + 耳机"在 16px 下会糊成一团（见 tools/make_icons.py 生成的尺寸对比）。
    /// 小尺寸只留最容易辨认的耳机，48px 以上用完整图形，是图标设计的常规减法。
    /// </summary>
    private Icon CreateTrayIcon()
    {
        // The bitmap must outlive the icon: Icon.FromHandle does not copy the pixels.
        _iconBitmap = new Bitmap(32, 32);

        using (Graphics graphics = Graphics.FromImage(_iconBitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath background = RoundedRect(new RectangleF(0, 0, 32, 32), 7f))
            using (SolidBrush fill = new(Color.FromArgb(0x25, 0x63, 0xEB)))
            {
                graphics.FillPath(fill, background);
            }

            // 头梁：圆心 (16,17.19)、半径 5.04 的上半圆
            using (Pen band = new(Color.White, 2.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                graphics.DrawArc(band, 10.96f, 12.15f, 10.08f, 10.08f, 180f, 180f);
            }

            // 耳罩比头梁粗一点，16px 下才分得清头梁和两侧
            using (Pen cup = new(Color.White, 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                graphics.DrawLine(cup, 10.96f, 17.48f, 10.96f, 20.15f);
                graphics.DrawLine(cup, 21.04f, 17.48f, 21.04f, 20.15f);
            }
        }

        return Icon.FromHandle(_iconBitmap.GetHicon());
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        GraphicsPath path = new();
        float d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
