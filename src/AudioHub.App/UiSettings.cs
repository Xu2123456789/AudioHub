using System.IO;
using System.Text.Json;

namespace AudioHub.App;

/// <summary>
/// The handful of interface choices worth remembering between runs.
/// </summary>
/// <remarks>
/// Deliberately a separate file from <c>identity.json</c>: losing this one costs a checkbox
/// position, while losing the identity would cost every pairing the phone has made. Keeping
/// them apart means a corrupt UI preference cannot take the identity with it.
/// </remarks>
internal sealed class UiSettings
{
    /// <summary>
    /// Whether the QR code carries the pairing key. On by default: the common case is a first
    /// pairing, and one scan beats "scan, then type 25 characters". Users who would rather not
    /// put the key on screen can turn it off.
    /// </summary>
    public bool QrIncludesPairingKey { get; set; } = true;

    /// <summary>
    /// 展开「手动连接」时是否直接把二维码摆出来。默认是。
    /// </summary>
    /// <remarks>
    /// 二维码是给手机扫的那张"纸"，需要它的人每次都需要它。把它藏在"显示二维码"按钮后面，
    /// 等于每次连接都要求人多点一下 —— 而折叠本身是他可以选的，不是我们该替他决定的。
    /// 想让它收起来的人按一次"隐藏二维码"，这个选择会被记住。
    /// </remarks>
    public bool QrVisible { get; set; } = true;

    /// <summary>
    /// 「手动连接」这块面板是否保持展开。默认收起，但记住用户自己的选择。
    /// </summary>
    /// <remarks>
    /// 第一次打开窗口不该先看到一堆 IP、端口、指纹；可一旦他为了配对把这块展开过，
    /// 每次都重新收起就是在跟人作对。
    /// </remarks>
    public bool ManualPanelExpanded { get; set; }

    /// <summary>
    /// 接收加密策略：None / Optional / Required，对应 <c>AudioHub.Core.Discovery.SecurityMode</c>。
    /// 默认 Optional：加密和明文都收，这样没配对的旧手机仍能连，配对过的则会自动加密。
    /// 取值在下次启动创建 host 时生效。
    /// </summary>
    public string AdvertisedSecurity { get; set; } = "Optional";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioHub",
        "ui.json");

    /// <summary>
    /// Reads the settings, falling back to defaults for a missing, unreadable or corrupt file.
    /// A preference file must never be the reason the app fails to open.
    /// </summary>
    public static UiSettings Load()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                return new UiSettings();
            }

            return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path)) ?? new UiSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UiSettings();
        }
    }

    /// <summary>Writes the settings. Failure is ignored: it costs one checkbox position.</summary>
    public void Save()
    {
        try
        {
            string path = FilePath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing here is worth interrupting the user for.
        }
    }
}
