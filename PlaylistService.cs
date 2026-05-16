using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace AzIPTV;

/// <summary>A named URL entry in the user's playlist history.</summary>
public sealed record UrlHistoryEntry(string Url, string Name);

public static class PlaylistService
{
    // Single shared HttpClient — avoids socket exhaustion on repeated calls.
    private static readonly HttpClient Http;

    static PlaylistService()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("VLC");
    }

    private static string IniPath =>
        Path.Combine(AppContext.BaseDirectory, "user.ini");

    // ── Load ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Buffers the HTTP response into RAM, then parses on a background thread.
    /// No ConfigureAwait(false) — continuations return to the caller's SynchronizationContext.
    /// </summary>
    public static async Task<ParseResult> LoadFromUrlAsync(string url)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var ms = new System.IO.MemoryStream();
        using (var src = await response.Content.ReadAsStreamAsync())
            await src.CopyToAsync(ms);
        ms.Position = 0;
        return await M3uParser.ParseAsync(ms); // Task.Run inside; continuation on UI thread
    }

    public static async Task<ParseResult> LoadFromFileAsync(string path)
    {
        using var stream = File.OpenRead(path);
        return await M3uParser.ParseAsync(stream); // Task.Run inside; continuation on UI thread
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    public static (string? playlistUrl, string? playlistFile, string? lastStreamUrl) LoadSettings()
    {
        if (!File.Exists(IniPath)) return (null, null, null);

        string? pu = null, pf = null, ls = null;
        foreach (var line in File.ReadLines(IniPath))
        {
            if      (TryGet(line, "PlaylistUrl=",   out var v)) pu = Decode(v);
            else if (TryGet(line, "PlaylistFile=",  out v))     pf = Decode(v);
            else if (TryGet(line, "LastStreamUrl=", out v))     ls = Decode(v);
        }
        return (pu, pf, ls);
    }

    /// <summary>Returns the saved theme name ("Dark" or "Light"). Defaults to "Dark".</summary>
    public static string LoadTheme()
    {
        if (!File.Exists(IniPath)) return "Dark";
        foreach (var line in File.ReadLines(IniPath))
            if (TryGet(line, "Theme=", out var v) && v is "Dark" or "Light")
                return v!;
        return "Dark";
    }

    public static void SaveTheme(string theme)
    {
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, LoadUrlHistory(), theme);
    }

    public static void SaveSettings(string? playlistUrl, string? playlistFile,
                                    string? lastStreamUrl = null)
    {
        var (eu, ef, el) = LoadSettings();
        WriteIni(playlistUrl  ?? eu ?? string.Empty,
                 playlistFile ?? ef ?? string.Empty,
                 lastStreamUrl ?? el ?? string.Empty,
                 LoadUrlHistory(), LoadTheme());
    }

    public static void SaveLastStreamUrl(string url)
    {
        var (pu, pf, _) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, url, LoadUrlHistory(), LoadTheme());
    }

    private static void WriteIni(string playlistUrl, string playlistFile,
                                  string lastStreamUrl, List<UrlHistoryEntry> history,
                                  string theme = "Dark")
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[AzIPTV]");
        sb.AppendLine($"Theme={theme}");
        sb.AppendLine($"PlaylistUrl={Encode(playlistUrl)}");
        sb.AppendLine($"PlaylistFile={Encode(playlistFile)}");
        sb.AppendLine($"LastStreamUrl={Encode(lastStreamUrl)}");
        for (int i = 0; i < history.Count; i++)
        {
            sb.AppendLine($"UrlHistoryUrl{i}={Encode(history[i].Url)}");
            sb.AppendLine($"UrlHistoryName{i}={Encode(history[i].Name)}");
        }
        File.WriteAllText(IniPath, sb.ToString());
    }

    // ── URL history ───────────────────────────────────────────────────────────

    /// <summary>Fixed playlists always shown at the top of the dialog (not persisted).</summary>
    public static readonly UrlHistoryEntry[] FixedPlaylists =
    {
        new("https://iptv-org.github.io/iptv/index.m3u",         "Public 1"),
        new("https://iptv-org.github.io/iptv/languages/spa.m3u", "Public ES"),
    };

    /// <summary>Maintained for call sites that only need the first fixed URL.</summary>
    public static string FixedPlaylistUrl => FixedPlaylists[0].Url;

    private const int MaxUrlHistory = 20;

    public static List<UrlHistoryEntry> LoadUrlHistory()
    {
        var urls  = new SortedDictionary<int, string>();
        var names = new SortedDictionary<int, string>();
        if (!File.Exists(IniPath)) return new List<UrlHistoryEntry>();

        foreach (var rawLine in File.ReadLines(IniPath))
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith("UrlHistoryUrl", StringComparison.Ordinal))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (!int.TryParse(line.AsSpan(13, eq - 13), out int n)) continue;
                if (Decode(line[(eq + 1)..]) is string u && !string.IsNullOrWhiteSpace(u))
                    urls[n] = u;
            }
            else if (line.StartsWith("UrlHistoryName", StringComparison.Ordinal))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (!int.TryParse(line.AsSpan(14, eq - 14), out int n)) continue;
                if (Decode(line[(eq + 1)..]) is string nm)
                    names[n] = nm;
            }
            // Legacy plain UrlHistory{n}= entries (URL only, name inferred).
            else if (line.StartsWith("UrlHistory", StringComparison.Ordinal))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (!int.TryParse(line.AsSpan(10, eq - 10), out int n)) continue;
                if (Decode(line[(eq + 1)..]) is string u && !string.IsNullOrWhiteSpace(u))
                    urls[n] = u;
            }
        }

        int idx = 0;
        var result = new List<UrlHistoryEntry>(urls.Count);
        foreach (var kvp in urls)
        {
            var nm = names.TryGetValue(kvp.Key, out var s) ? s : $"List #{idx + 1}";
            result.Add(new UrlHistoryEntry(kvp.Value, nm));
            idx++;
        }
        return result;
    }

    public static void AddUrlToHistory(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (FixedPlaylists.Any(f => string.Equals(f.Url, url.Trim(), StringComparison.OrdinalIgnoreCase)))
            return;
        var history = LoadUrlHistory();
        history.RemoveAll(e => string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase));
        var defaultName = $"List #{history.Count + 1}";
        history.Insert(0, new UrlHistoryEntry(url, defaultName));
        if (history.Count > MaxUrlHistory)
            history.RemoveRange(MaxUrlHistory, history.Count - MaxUrlHistory);
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, history, LoadTheme());
    }

    public static void RemoveUrlFromHistory(string url)
    {
        var history = LoadUrlHistory();
        history.RemoveAll(e => string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase));
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, history, LoadTheme());
    }

    public static void UpdateUrlName(string url, string name)
    {
        var history = LoadUrlHistory();
        for (int i = 0; i < history.Count; i++)
        {
            if (string.Equals(history[i].Url, url, StringComparison.OrdinalIgnoreCase))
            {
                history[i] = history[i] with { Name = name };
                break;
            }
        }
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, history, LoadTheme());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Encode(string value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

    private static string? Decode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch { return null; } // ignore corrupt/legacy plain-text entries
    }

    private static bool TryGet(string line, string key, out string? value)
    {
        var span = line.AsSpan().TrimStart();
        if (!span.StartsWith(key.AsSpan(), StringComparison.Ordinal))
        {
            value = null;
            return false;
        }
        var v = span[key.Length..].Trim();
        value = v.IsEmpty ? null : v.ToString();
        return true;
    }
}
