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
    private Dictionary<string, int> _playCounts = new();
    private List<Channel> _favouriteChannels = new();

    /// <summary>Sentinel URL used by the "Clear Favourites" pseudo-channel.</summary>
    public const string ClearFavsUrl = "__CLEAR_FAVOURITES__";

    public event Action<Channel>? ChannelSelected;
    public event Action?          PanelCloseRequested;
    public event Action?          ClearFavouritesRequested;
    public event Action<string>?  GroupSelectionChanged;

    // Cache VMs so logos don't reload on every keystroke filter.
    private readonly Dictionary<string, ChannelVm> _vmCache = new();

    // True while the group dropdown was opened by keyboard (Up-arrow from channel list).
    // Lets DropDownClosed know it should return focus to the channel list.
    private bool _groupDropDownOpenedByKeyboard;
    // True while we're rebuilding the group combo — suppresses OnGroupChanged → ApplyFilter.
    private bool _updatingGroups;

    public SidePanel()
    {
        InitializeComponent();
        GroupCombo.ItemsSource   = new[] { "ALL CHANNELS" };
        GroupCombo.SelectedIndex = 0;

        // Tunnel phase fires before the TextBox handles the key itself, so
        // PageUp/PageDown (which the TextBox consumes in the bubble phase) are
        // intercepted here and used to navigate the channel list instead.
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Intercept group-combo keys before the ComboBox opens its dropdown.
        GroupCombo.AddHandler(KeyDownEvent, OnGroupKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // DropDownClosed fires AFTER Avalonia has committed the selected item,
        // so SelectedItem is always the confirmed value at that point.
        GroupCombo.DropDownClosed += OnGroupDropDownClosed;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void LoadChannels(ParseResult result, Dictionary<string, int>? playCounts = null)
    {
        _allChannels     = result.Channels;
        _groupedChannels = result.Groups;
        _playCounts      = playCounts ?? new Dictionary<string, int>();
        _vmCache.Clear();
        _favouriteChannels = BuildFavourites();

        _updatingGroups = true;
        try { RebuildGroupCombo(); }
        finally { _updatingGroups = false; }

        ApplyFilter();
    }

    public void UpdatePlayCounts(Dictionary<string, int> playCounts)
    {
        _playCounts = playCounts;
        _favouriteChannels = BuildFavourites();

        var current = GroupCombo.SelectedItem as string;
        _updatingGroups = true;
        try
        {
            RebuildGroupCombo();
            if (current is not null &&
                (GroupCombo.ItemsSource as IEnumerable<string>)?.Contains(current) == true)
                GroupCombo.SelectedItem = current;
        }
        finally { _updatingGroups = false; }

        ApplyFilter();
    }

    private List<Channel> BuildFavourites()
    {
        if (_playCounts.Count == 0) return new List<Channel>();
        var favs = _allChannels
            .Select(c => (channel: c, id: PlaylistService.GetChannelId(c.Url)))
            .Where(x => _playCounts.TryGetValue(x.id, out var cnt) && cnt > 0)
            .OrderByDescending(x => _playCounts.GetValueOrDefault(x.id))
            .Select(x => x.channel)
            .ToList();
        favs.Add(new Channel("✕  Clear Favourites", ClearFavsUrl, string.Empty, string.Empty));
        return favs;
    }

    private void InvokeChannel(ChannelVm? vm)
    {
        if (vm is null) return;
        if (vm.Url == ClearFavsUrl)
            ClearFavouritesRequested?.Invoke();
        else
            ChannelSelected?.Invoke(vm.Channel);
    }

    private void RebuildGroupCombo()
    {
        var groupNames = _groupedChannels.Keys
            .OrderBy(g => g, StringComparer.CurrentCulture)
            .ToList();
        groupNames.Insert(0, "ALL CHANNELS");
        if (_favouriteChannels.Count > 0)
            groupNames.Insert(0, "FAVOURITES (MOST VIEWED)");
        GroupCombo.ItemsSource   = groupNames;
        GroupCombo.SelectedIndex = 0;
    }

    /// <summary>Tries to select <paramref name="groupName"/> in the group combo.
    /// Falls back silently if the group does not exist in the current playlist.</summary>
    public void TrySelectGroup(string groupName)
    {
        if (string.IsNullOrEmpty(groupName)) return;
        var items = GroupCombo.ItemsSource as IEnumerable<string>;
        if (items is null) return;
        var match = items.FirstOrDefault(g => g == groupName);
        if (match is null) return;
        _updatingGroups = true;
        try { GroupCombo.SelectedItem = match; }
        finally { _updatingGroups = false; }
        ApplyFilter();
    }

    public void FocusSearch() => SearchBox.Focus();

    // ── Filtering ─────────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        var search = (SearchBox.Text ?? string.Empty).Trim();
        var group  = GroupCombo.SelectedItem as string ?? "ALL CHANNELS";

        IEnumerable<Channel> source = group switch
        {
            "FAVOURITES (MOST VIEWED)" => _favouriteChannels,
            "ALL CHANNELS" => _allChannels,
            _              => _groupedChannels.TryGetValue(group, out var g) ? g : Array.Empty<Channel>()
        };

        if (!string.IsNullOrEmpty(search))
            source = source.Where(c =>
                c.Url == ClearFavsUrl ||
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

    private void OnGroupChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingGroups) return;
        ApplyFilter();
        if (GroupCombo.SelectedItem is string group)
            GroupSelectionChanged?.Invoke(group);
    }

    private void OnChannelDoubleTapped(object? sender, TappedEventArgs e)
    {
        InvokeChannel(ChannelList.SelectedItem as ChannelVm);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                InvokeChannel(ChannelList.SelectedItem as ChannelVm);
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

    private void OnGroupKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                // Cancel: clear flag so DropDownClosed won't jump to the list.
                _groupDropDownOpenedByKeyboard = false;
                GroupCombo.IsDropDownOpen = false;
                SearchBox.Focus();
                e.Handled = true;
                break;
            // Enter / Up / Down: let the ComboBox handle natively so the
            // highlighted item is properly committed before we act on it.
        }
    }

    private void OnGroupDropDownClosed(object? sender, EventArgs e)
    {
        if (!_groupDropDownOpenedByKeyboard) return;
        _groupDropDownOpenedByKeyboard = false;
        ReturnToList();
    }

    /// <summary>Moves focus back to the search box and highlights the first channel.</summary>
    private void ReturnToList()
    {
        SearchBox.Focus();
        ChannelList.SelectedIndex = 0;
        if (ChannelList.SelectedItem is not null)
            ChannelList.ScrollIntoView(ChannelList.SelectedItem);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(10);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(-10);
                e.Handled = true;
                break;
            case Key.Enter:
                // Load selected item, or the first item if nothing is selected yet.
                if (ChannelList.SelectedItem is ChannelVm selected)
                {
                    ChannelSelected?.Invoke(selected.Channel);
                }
                else
                {
                    ChannelList.SelectedIndex = 0;
                    if (ChannelList.SelectedItem is ChannelVm first)
                        ChannelSelected?.Invoke(first.Channel);
                }
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        int count = (ChannelList.ItemsSource as IList)?.Count ?? 0;

        // Pressing Up when already on the first item — redirect focus to group picker.
        if (delta < 0 && ChannelList.SelectedIndex == 0)
        {
            _groupDropDownOpenedByKeyboard = true;
            GroupCombo.Focus();
            GroupCombo.IsDropDownOpen = true;
            return;
        }

        if (count == 0) return;

        int next;
        if (ChannelList.SelectedIndex < 0)
            next = delta > 0 ? 0 : count - 1;  // no selection: Down→first, Up→last
        else
            next = Math.Clamp(ChannelList.SelectedIndex + delta, 0, count - 1);

        ChannelList.SelectedIndex = next;

        if (ChannelList.SelectedItem is not null)
            ChannelList.ScrollIntoView(ChannelList.SelectedItem);
    }
}
