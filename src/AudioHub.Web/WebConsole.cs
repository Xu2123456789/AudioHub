using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AudioHub.Hosting;

namespace AudioHub.Web;

/// <summary>
/// Serves the control API and a small console page over HTTP, so a phone or tablet on
/// the same network can adjust the mix.
/// </summary>
/// <remarks>
/// <para>
/// Built on <see cref="HttpListener"/> rather than ASP.NET Core on purpose. The console
/// is six endpoints and one static file; taking on a web framework would add a shared
/// framework that has to be installed separately, which the tray app cannot assume.
/// </para>
/// <para>
/// HTTP.SYS needs a URL reservation for a wildcard prefix, which normally means running
/// once as administrator. Without it the console still binds to localhost, so it works
/// out of the box; <see cref="IsLanVisible"/> reports whether phones can reach it, and
/// <see cref="LanReservationCommand"/> gives the command that enables that.
/// </para>
/// </remarks>
public sealed class WebConsole : IAsyncDisposable
{
    private readonly IAudioHubHost _host;
    // Relaxed escaping keeps device names containing Chinese or other non-ASCII
    // characters readable in the payload instead of escaping them all.
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    /// <summary>Creates a console bound to an existing host.</summary>
    public WebConsole(IAudioHubHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>Port the console listens on, or null when not running.</summary>
    public int? Port { get; private set; }

    /// <summary>Whether the console is reachable from other machines on the network.</summary>
    public bool IsLanVisible { get; private set; }

    /// <summary>The one-time administrator command that makes the console reachable from a phone.</summary>
    public string? LanReservationCommand => IsLanVisible || Port is null
        ? null
        : $"netsh http add urlacl url=http://+:{Port}/ user=everyone";

    /// <summary>Starts listening.</summary>
    public Task StartAsync(int port = 5055)
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        HttpListener listener = new();
        bool lanVisible;

        try
        {
            listener.Prefixes.Add($"http://+:{port}/");
            listener.Start();
            lanVisible = true;
        }
        catch (Exception)
        {
            // No wildcard reservation. Fall back to loopback only, which always works.
            listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            lanVisible = false;
        }

        _listener = listener;
        Port = port;
        IsLanVisible = lanVisible;

        _cancellation = new CancellationTokenSource();
        _loop = Task.Run(() => ListenAsync(listener, _cancellation.Token));

        return Task.CompletedTask;
    }

    /// <summary>Stops listening.</summary>
    public async Task StopAsync()
    {
        _cancellation?.Cancel();

        if (_listener is { } listener)
        {
            _listener = null;
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch (ObjectDisposedException)
            {
                // Already gone.
            }
        }

        if (_loop is { } loop)
        {
            _loop = null;
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        Port = null;
        IsLanVisible = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task ListenAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            // Each request is handled on its own task; a slow phone must not stall the rest.
            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;

            switch (path)
            {
                case "/api/state" when method == "GET":
                    await ReplyJsonAsync(context, 200, GetState()).ConfigureAwait(false);
                    return;

                case "/api/gain" when method == "POST":
                    await HandleGainAsync(context).ConfigureAwait(false);
                    return;

                case "/api/mute" when method == "POST":
                    await HandleMuteAsync(context).ConfigureAwait(false);
                    return;

                case "/api/device" when method == "POST":
                {
                    DeviceRequest? request = await ReadJsonAsync<DeviceRequest>(context).ConfigureAwait(false);
                    await _host.SetOutputDeviceAsync(request?.Id).ConfigureAwait(false);
                    await ReplyJsonAsync(context, 200, new { ok = true }).ConfigureAwait(false);
                    return;
                }

                case "/api/start" when method == "POST":
                    await _host.StartAsync().ConfigureAwait(false);
                    await ReplyJsonAsync(context, 200, new { ok = true }).ConfigureAwait(false);
                    return;

                case "/api/stop" when method == "POST":
                    await _host.StopAsync().ConfigureAwait(false);
                    await ReplyJsonAsync(context, 200, new { ok = true }).ConfigureAwait(false);
                    return;

                case "/" or "/index.html" when method == "GET":
                    await ReplyFileAsync(context).ConfigureAwait(false);
                    return;

                default:
                    await ReplyJsonAsync(context, 404, new { error = "not found" }).ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            await ReplyJsonAsync(context, 500, new { error = ex.Message }).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task HandleGainAsync(HttpListenerContext context)
    {
        GainRequest? request = await ReadJsonAsync<GainRequest>(context).ConfigureAwait(false);

        if (request is null || string.IsNullOrWhiteSpace(request.Id))
        {
            await ReplyJsonAsync(context, 400, new { error = "id is required" }).ConfigureAwait(false);
            return;
        }

        if (request.GainDb is < -60f or > 20f)
        {
            await ReplyJsonAsync(context, 400, new { error = "gainDb must be between -60 and 20" }).ConfigureAwait(false);
            return;
        }

        if (!_host.Mixer.ChannelIds.Contains(request.Id, StringComparer.Ordinal))
        {
            await ReplyJsonAsync(context, 404, new { error = $"no channel '{request.Id}'" }).ConfigureAwait(false);
            return;
        }

        _host.Mixer.SetGainDb(request.Id, request.GainDb);
        await ReplyJsonAsync(context, 200, new { ok = true }).ConfigureAwait(false);
    }

    private async Task HandleMuteAsync(HttpListenerContext context)
    {
        MuteRequest? request = await ReadJsonAsync<MuteRequest>(context).ConfigureAwait(false);

        if (request is null || !_host.Mixer.ChannelIds.Contains(request.Id, StringComparer.Ordinal))
        {
            await ReplyJsonAsync(context, 404, new { error = "unknown channel" }).ConfigureAwait(false);
            return;
        }

        _host.Mixer.SetMuted(request.Id, request.Muted);
        await ReplyJsonAsync(context, 200, new { ok = true }).ConfigureAwait(false);
    }

    private object GetState()
    {
        _host.RefreshOutputDevices();

        return new
        {
            running = _host.IsRunning,
            sampleRate = _host.Mixer.Format.SampleRate,
            channels = _host.Mixer.ChannelIds.Select(id => new
            {
                id,
                gainDb = Math.Round(_host.Mixer.GetGainDb(id), 1),
                muted = _host.Mixer.GetMuted(id),
                level = Math.Round(_host.Mixer.GetLevel(id), 4),
            }).ToArray(),
            devices = _host.OutputDevices.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                isDefault = d.IsDefault,
            }).ToArray(),
            outputDeviceId = _host.OutputDeviceId,
            lanVisible = IsLanVisible,
        };
    }

    private async Task ReplyFileAsync(HttpListenerContext context)
    {
        string filePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        if (!File.Exists(filePath))
        {
            await ReplyJsonAsync(context, 404, new { error = "console page not deployed" }).ConfigureAwait(false);
            return;
        }

        byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private async Task<T?> ReadJsonAsync<T>(HttpListenerContext context)
    {
        using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
        string body = await reader.ReadToEndAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, _json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private async Task ReplyJsonAsync(HttpListenerContext context, int statusCode, object payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _json);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>Body of POST /api/gain.</summary>
    public sealed record GainRequest(string Id, float GainDb);

    /// <summary>Body of POST /api/mute.</summary>
    public sealed record MuteRequest(string Id, bool Muted);

    /// <summary>Body of POST /api/device.</summary>
    public sealed record DeviceRequest(string? Id);
}
