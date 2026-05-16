using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AzIPTV;

public partial class SidePanel : UserControl
{
    private List<Channel> _allChannels = new();
    private Dictionary<string, List<Channel>> _groupedChannels = new();

    // Cache VMs so logos don't reload on every keystroke filter.
    private readonly Dictionary<string, ChannelVm> _vmCache = new();

    public event Action<Channel>? ChannelSelected;
    public event Action? PanelCloseRequested;

    public SidePanel()
    {
        InitializeComponent();
        GroupCombo.ItemsSource   = new[] { "ALL CHANNELS" };
        GroupCombo.SelectedIndex = 0;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void LoadChannels(ParseResult result)
    {
        _allChannels    = result.Channels;
        _groupedChannels = result.Groups;
        _vmCache.Clear();

        var groupNames = result.Groups.Keys
            .OrderBy(g => g, StringComparer.CurrentCulture)
            .ToList();

        groupNames.Insert(0, "ALL CHANNELS");
        GroupCombo.ItemsSource   = groupNames;
        GroupCombo.SelectedIndex = 0;

        ApplyFilter();
    }

    public void FocusSearch() => SearchBox.Focus();

    // ── Filtering ─────────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        var search = (SearchBox.Text ?? string.Empty).Trim();
        var group  = GroupCombo.SelectedItem as string ?? "ALL CHANNELS";

        IEnumerable<Channel> source = group == "ALL CHANNELS"
            ? _allChannels
            : _groupedChannels.TryGetValue(group, out var g) ? g : Array.Empty<Channel>();

        if (!string.IsNullOrEmpty(search))
            source = source.Where(c =>
                c.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        ChannelList.ItemsSource = source.Select(GetOrCreateVm).ToList();
    }

    private ChannelVm GetOrCreateVm(Channel c)
    {
        if (!_vmCache.TryGetValue(c.Url, out var vm))
        {
            vm = new ChannelVm(c);
            _vmCache[c.Url] = vm;
        }
        return vm;
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnGroupChanged(object? sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void OnChannelDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ChannelList.SelectedItem is ChannelVm vm)
            ChannelSelected?.Invoke(vm.Channel);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (ChannelList.SelectedItem is ChannelVm vm)
                    ChannelSelected?.Invoke(vm.Channel);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(-10);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(10);
                e.Handled = true;
                break;
            case Key.Escape:
                PanelCloseRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        int count = (ChannelList.ItemsSource as IList)?.Count ?? 0;
        if (count == 0) return;

        int current = ChannelList.SelectedIndex < 0 ? 0 : ChannelList.SelectedIndex;
        int next    = Math.Clamp(current + delta, 0, count - 1);
        ChannelList.SelectedIndex = next;

        if (ChannelList.SelectedItem is not null)
            ChannelList.ScrollIntoView(ChannelList.SelectedItem);
    }
}
