using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AzIPTV;

/// <summary>
/// ViewModel wrapping a <see cref="Channel"/>.
/// Logo images are fetched asynchronously (throttled to 4 concurrent requests)
/// and assigned via INotifyPropertyChanged so the ListBox updates live.
/// </summary>
public sealed class ChannelVm : INotifyPropertyChanged
{
    // Shared across all instances ─ 4 concurrent logo fetches max, 5s timeout.
    private static readonly HttpClient LogoHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly SemaphoreSlim LogoThrottle = new(4, 4);

    private IImage? _logo;

    public Channel Channel { get; }
    public string  Name    => Channel.Name;
    public string  Group   => Channel.Group;
    public string  Url     => Channel.Url;

    public IImage? Logo
    {
        get => _logo;
        private set
        {
            _logo = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Logo)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChannelVm(Channel channel)
    {
        Channel = channel;
        if (!string.IsNullOrEmpty(channel.LogoUrl))
            _ = LoadLogoAsync(channel.LogoUrl);
    }

    private async Task LoadLogoAsync(string url)
    {
        await LogoThrottle.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var data = await LogoHttp.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);

            // Ignore suspiciously large blobs (likely not a thumbnail).
            if (data.Length > 512 * 1024) return;

            using var ms = new MemoryStream(data);
            var bmp = new Bitmap(ms);
            await Dispatcher.UIThread.InvokeAsync(() => Logo = bmp);
        }
        catch { /* logo stays null — placeholder icon is shown */ }
        finally { LogoThrottle.Release(); }
    }
}
