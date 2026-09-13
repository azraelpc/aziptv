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
    private static readonly IReadOnlyList<ChannelVm> EmptyChannels = Array.Empty<ChannelVm>();
    private List<Channel> _allChannels = new();
    private Dictionary<string, List<Channel>> _groupedChannels = new();
    private Dictionary<string, int> _playCounts = new();
    private List<Channel> _favouriteChannels = new();
    private bool _isLoading;

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
        ChannelList.ItemsSource  = EmptyChannels;

        // Tunnel phase fires before the TextBox handles the key itself, so
        // PageUp/PageDown (which the TextBox consumes in the bubble phase) are
        // intercepted here and used to navigate the channel list instead.
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Intercept group-combo keys before the ComboBox opens its dropdown.
        GroupCombo.AddHandler(KeyDownEvent, OnGroupKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // DropDownClosed fires AFTER Avalonia has committed the selected item,
        // so SelectedItem is always the confirmed value at that point.
        GroupCombo.DropDownClosed += OnGroupDropDownClosed;
        UpdateChannelListPlaceholder();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void LoadChannels(ParseResult result, Dictionary<string, int>? playCounts = null)
    {
        _allChannels     = result.Channels;
        _groupedChannels = result.Groups;
        _playCounts      = playCounts ?? new Dictionary<string, int>();
        _isLoading       = false;
        _vmCache.Clear();
        _favouriteChannels = BuildFavourites();

        _updatingGroups = true;
        try { RebuildGroupCombo(); }
        finally { _updatingGroups = false; }

        ApplyFilter();
    }

    public void SetLoadingState(bool isLoading, string loadingText = "Loading channels...")
    {
        _isLoading = isLoading;
        ChannelListPlaceholder.Text = loadingText;
        if (isLoading && _allChannels.Count == 0)
            ChannelList.ItemsSource = EmptyChannels;
        UpdateChannelListPlaceholder();
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

        IReadOnlyList<Channel> source = group switch
        {
            "FAVOURITES (MOST VIEWED)" => _favouriteChannels,
            "ALL CHANNELS" => _allChannels,
            _              => _groupedChannels.TryGetValue(group, out var g) ? g : Array.Empty<Channel>()
        };

        IReadOnlyList<Channel> filtered;
        if (!string.IsNullOrEmpty(search))
            filtered = source.Where(c =>
                c.Url == ClearFavsUrl ||
                c.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        else
            filtered = source;

        ChannelList.ItemsSource = new LazyChannelVmList(filtered, GetOrCreateVm);
        UpdateChannelListPlaceholder();
    }

    private void UpdateChannelListPlaceholder()
    {
        var count = (ChannelList.ItemsSource as IList)?.Count ?? 0;
        ChannelListPlaceholder.IsVisible = _isLoading && count == 0;
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

    /// <summary>
    /// Indexed VM adapter that avoids creating ChannelVm objects for the whole list up front.
    /// </summary>
    private sealed class LazyChannelVmList : IList
    {
        private readonly IReadOnlyList<Channel> _channels;
        private readonly Func<Channel, ChannelVm> _factory;

        public LazyChannelVmList(IReadOnlyList<Channel> channels, Func<Channel, ChannelVm> factory)
        {
            _channels = channels;
            _factory = factory;
        }

        public int Count => _channels.Count;
        public bool IsReadOnly => true;
        public bool IsFixedSize => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;

        public object? this[int index]
        {
            get => _factory(_channels[index]);
            set => throw new NotSupportedException();
        }

        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();

        public bool Contains(object? value)
        {
            if (value is not ChannelVm vm) return false;
            return _channels.Any(c => c.Url == vm.Url);
        }

        public int IndexOf(object? value)
        {
            if (value is not ChannelVm vm) return -1;
            for (int i = 0; i < _channels.Count; i++)
            {
                if (_channels[i].Url == vm.Url)
                    return i;
            }
            return -1;
        }

        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();

        public void CopyTo(Array array, int index)
        {
            if (array is null) throw new ArgumentNullException(nameof(array));
            for (int i = 0; i < _channels.Count; i++)
                array.SetValue(_factory(_channels[i]), index + i);
        }

        public IEnumerator GetEnumerator()
        {
            for (int i = 0; i < _channels.Count; i++)
                yield return _factory(_channels[i]);
        }
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
