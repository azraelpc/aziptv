using System;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, Task<IImage?>> LogoTasks = new(StringComparer.Ordinal);

    private IImage? _logo;
    private int _logoRequested;

    public Channel Channel { get; }
    public string  Name    => Channel.Name;
    public string  Group   => Channel.Group;
    public string  Url     => Channel.Url;

    public IImage? Logo
    {
        get
        {
            if (_logo is null
                && _logoRequested == 0
                && !string.IsNullOrEmpty(Channel.LogoUrl)
                && Interlocked.CompareExchange(ref _logoRequested, 1, 0) == 0)
            {
                _ = LoadLogoAsync(Channel.LogoUrl);
            }
            return _logo;
        }
        private set
        {
            _logo = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Logo)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowLogoPlaceholder)));
        }
    }

    public bool ShowLogoPlaceholder => Logo is null;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChannelVm(Channel channel)
    {
        Channel = channel;
    }

    public static Task<IImage?> LoadLogoImageAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Task.FromResult<IImage?>(null);
        return LogoTasks.GetOrAdd(url, DownloadLogoAsync);
    }

    private async Task LoadLogoAsync(string url)
    {
        var logo = await LoadLogoImageAsync(url).ConfigureAwait(false);
        if (logo is not null)
            await Dispatcher.UIThread.InvokeAsync(() => Logo = logo);
    }

    private static async Task<IImage?> DownloadLogoAsync(string url)
    {
        await LogoThrottle.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var data = await LogoHttp.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);

            if (data.Length > 512 * 1024) return null;

            using var ms = new MemoryStream(data);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
        finally
        {
            LogoThrottle.Release();
        }
    }
}
