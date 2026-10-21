using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OverInk.Editor;

/// <summary>The library bar's wheel scrolling and the full-library grid (browse, search, move to front).</summary>
public partial class EditorWindow
{
    /// <summary>How many of the newest captures the library bar shows; the rest live in the full-library grid.</summary>
    private const int LibraryBarLimit = 20;

    private bool _gridOpen;
    private readonly List<LibraryGridEntry> _gridEntries = new();
    private List<LibraryGridEntry> _gridShown = new();

    // ----- Library bar -----

    /// <summary>The trailing "All captures" card, which lives in the library list's template.</summary>
    private Button? AllCapturesCard
    {
        get
        {
            LibraryList.ApplyTemplate();
            return LibraryList.Template?.FindName("AllCapturesButton", LibraryList) as Button;
        }
    }

    /// <summary>Turns the mouse wheel into sideways scrolling, since the bar only ever scrolls horizontally.</summary>
    private void LibraryList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindDescendant<ScrollViewer>(LibraryList) is not { ScrollableWidth: > 0 } scroll)
        {
            return;
        }

        // One notch (120) moves about a card and a half.
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta * 1.6);
        e.Handled = true;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    // ----- Reordering the bar by dragging -----

    /// <summary>How far past the bar's top or bottom edge a drag goes before it turns into a drag out to other apps.</summary>
    private const double ReorderLeaveMargin = 10;

    private bool _reordering;
    private CaptureTrayEntry? _reorderEntry;
    private int _reorderIndex = -1;
    private DispatcherTimer? _reorderScrollTimer;

    private FrameworkElement? CardFor(CaptureTrayEntry entry) =>
        LibraryList.ItemContainerGenerator.ContainerFromItem(entry) as FrameworkElement;

    private void BeginReorder(CaptureTrayEntry entry)
    {
        _reordering = true;
        _reorderEntry = entry;
        _reorderIndex = -1;
        LibraryList.CaptureMouse();
        if (CardFor(entry) is { } card)
        {
            card.Opacity = 0.4;
        }

        if (_reorderScrollTimer is null)
        {
            _reorderScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _reorderScrollTimer.Tick += (_, _) => ReorderAutoScroll();
        }

        _reorderScrollTimer.Start();
    }

    /// <summary>Works out which gap the card would drop into and shows the marker there.</summary>
    private void UpdateReorder(Point pos)
    {
        if (_reorderEntry is null || _trayEntries.Count == 0)
        {
            return;
        }

        // Gap = number of cards whose middle is left of the pointer.
        var index = 0;
        for (var i = 0; i < _trayEntries.Count; i++)
        {
            if (CardFor(_trayEntries[i]) is not { } card)
            {
                continue;
            }

            var middle = card.TranslatePoint(new Point(card.ActualWidth / 2, 0), LibraryList).X;
            if (pos.X > middle)
            {
                index = i + 1;
            }
        }

        _reorderIndex = index;

        var from = _trayEntries.IndexOf(_reorderEntry);
        if (index == from || index == from + 1)
        {
            // Dropping here leaves the order as it is.
            ReorderIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var edgeCard = CardFor(_trayEntries[Math.Min(index, _trayEntries.Count - 1)]);
        if (edgeCard is null)
        {
            return;
        }

        var left = edgeCard.TranslatePoint(new Point(0, 0), ReorderLayer).X;
        var x = index < _trayEntries.Count ? left - 4 : left + edgeCard.ActualWidth + 4;
        Canvas.SetLeft(ReorderIndicator, x - ReorderIndicator.Width / 2);
        Canvas.SetTop(ReorderIndicator, 2);
        ReorderIndicator.Height = Math.Max(0, ReorderLayer.ActualHeight - 8);
        ReorderIndicator.Visibility = Visibility.Visible;
    }

    /// <summary>Scrolls the bar while the dragged card is held near its left or right edge.</summary>
    private void ReorderAutoScroll()
    {
        if (!_reordering || FindDescendant<ScrollViewer>(LibraryList) is not { } scroll)
        {
            return;
        }

        const double edge = 56;
        var pos = Mouse.GetPosition(LibraryList);
        var speed = 0.0;
        if (pos.X < edge)
        {
            speed = -Math.Min(28, (edge - pos.X) * 0.6 + 2);
        }
        else if (pos.X > LibraryList.ActualWidth - edge)
        {
            speed = Math.Min(28, (pos.X - (LibraryList.ActualWidth - edge)) * 0.6 + 2);
        }

        if (speed == 0)
        {
            return;
        }

        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + speed);
        scroll.UpdateLayout();
        UpdateReorder(pos);
    }

    private void FinishReorder(bool commit)
    {
        var entry = _reorderEntry;
        var index = _reorderIndex;

        _reordering = false;
        _reorderEntry = null;
        _reorderIndex = -1;
        _reorderScrollTimer?.Stop();
        ReorderIndicator.Visibility = Visibility.Collapsed;
        if (entry is not null && CardFor(entry) is { } card)
        {
            card.Opacity = 1;
        }

        if (LibraryList.IsMouseCaptured)
        {
            LibraryList.ReleaseMouseCapture();
        }

        if (!commit || entry is null || index < 0)
        {
            return;
        }

        var order = _trayEntries.ToList();
        var from = order.IndexOf(entry);
        if (from < 0)
        {
            return;
        }

        order.RemoveAt(from);
        order.Insert(Math.Clamp(index > from ? index - 1 : index, 0, order.Count), entry);
        if (order.SequenceEqual(_trayEntries))
        {
            return;
        }

        _library.SetFrontOrder(order.Select(e => e.Item).ToList());
        RefreshTray(keepSelection: true, preserveScroll: true);
    }
    // ----- Full-library grid -----

    private void AllCaptures_Click(object sender, RoutedEventArgs e) => OpenLibraryGrid();

    private void GridBack_Click(object sender, RoutedEventArgs e) => CloseLibraryGrid();

    private void OpenLibraryGrid()
    {
        if (_gridOpen)
        {
            return;
        }

        // The canvas stays loaded underneath; save so the grid never hides unsaved work.
        TrySaveCurrent(background: true);
        CancelShapeDrag();

        _gridOpen = true;
        GridSearchBox.Text = string.Empty;
        GridSelectToggle.IsChecked = false;
        LibraryGridView.Visibility = Visibility.Visible;
        RebuildGrid(_library.Enumerate());
    }

    private void CloseLibraryGrid()
    {
        if (!_gridOpen)
        {
            return;
        }

        _gridOpen = false;
        LibraryGridView.Visibility = Visibility.Collapsed;
        GridSearchBox.Text = string.Empty;
        GridSelectToggle.IsChecked = false;
        GridItems.ItemsSource = null;
        _gridEntries.Clear();
        _gridShown = new List<LibraryGridEntry>();
    }

    /// <summary>Syncs the grid with the library, keeping existing cards (their ticks and thumbnails) where possible.</summary>
    private void RebuildGrid(IReadOnlyList<CaptureItem> all)
    {
        var existing = _gridEntries.ToDictionary(e => e.Item.FlattenedPath, StringComparer.OrdinalIgnoreCase);
        var selecting = GridSelectToggle.IsChecked == true;
        _gridEntries.Clear();
        foreach (var item in all)
        {
            if (existing.TryGetValue(item.FlattenedPath, out var entry))
            {
                entry.Update(item);
            }
            else
            {
                entry = new LibraryGridEntry(item);
            }

            entry.ShowCheck = selecting;
            _gridEntries.Add(entry);
        }

        ApplyGridFilter();
    }

    private void ApplyGridFilter()
    {
        var terms = GridSearchBox.Text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToArray();

        _gridShown = terms.Length == 0
            ? _gridEntries.ToList()
            : _gridEntries.Where(entry => terms.All(entry.SearchText.Contains)).ToList();

        GridItems.ItemsSource = _gridShown;
        GridCountLabel.Text = terms.Length == 0 || _gridShown.Count == _gridEntries.Count
            ? _gridEntries.Count.ToString()
            : $"{_gridShown.Count} of {_gridEntries.Count}";
        GridEmptyLabel.Text = _gridEntries.Count == 0
            ? "Nothing here yet — screenshots you take with OverInk appear in your library."
            : "No captures match your search.";
        GridEmptyLabel.Visibility = _gridShown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateGridSelectionUi();

        // Cards exist only after layout; then decode whatever is on (or near) screen.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, LoadVisibleGridThumbnails);
    }

    /// <summary>Starts thumbnails for cards in view plus one screen's margin, so a big library opens instantly.</summary>
    private void LoadVisibleGridThumbnails()
    {
        if (!_gridOpen || GridScroll.ViewportHeight <= 0)
        {
            return;
        }

        var margin = GridScroll.ViewportHeight;
        foreach (var entry in _gridShown)
        {
            if (GridItems.ItemContainerGenerator.ContainerFromItem(entry) is not FrameworkElement card || !card.IsVisible)
            {
                continue;
            }

            var top = card.TranslatePoint(new Point(0, 0), GridScroll).Y;
            if (top + card.ActualHeight >= -margin && top <= GridScroll.ViewportHeight + margin)
            {
                entry.EnsureThumbnail();
            }
        }
    }

    private void GridScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => LoadVisibleGridThumbnails();

    private void GridScroll_SizeChanged(object sender, SizeChangedEventArgs e) => LoadVisibleGridThumbnails();

    private void GridSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_gridOpen)
        {
            ApplyGridFilter();
        }
    }

    private void GridSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // First Esc clears the search, the next one leaves the grid.
            if (GridSearchBox.Text.Length > 0)
            {
                GridSearchBox.Text = string.Empty;
            }
            else
            {
                CloseLibraryGrid();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _gridShown.Count == 1 && GridSelectToggle.IsChecked != true)
        {
            OpenFromGrid(_gridShown[0].Item);
            e.Handled = true;
        }
    }

    private void HandleGridKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.FocusedElement is not TextBox)
        {
            CloseLibraryGrid();
            e.Handled = true;
        }
        else if (key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            GridSearchBox.Focus();
            GridSearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void GridSelectToggle_Changed(object sender, RoutedEventArgs e)
    {
        var selecting = GridSelectToggle.IsChecked == true;
        foreach (var entry in _gridEntries)
        {
            entry.ShowCheck = selecting;
            if (!selecting)
            {
                entry.IsChecked = false;
            }
        }

        UpdateGridSelectionUi();
    }

    private void UpdateGridSelectionUi()
    {
        var selecting = GridSelectToggle.IsChecked == true;
        var picked = _gridEntries.Count(entry => entry.IsChecked);
        GridMoveButton.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
        GridMoveButton.IsEnabled = picked > 0;
        GridMoveButton.Content = picked > 0 ? $"Move to front ({picked})" : "Move to front";
    }

    private void GridCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LibraryGridEntry entry })
        {
            return;
        }

        var selecting = GridSelectToggle.IsChecked == true;
        if (!selecting && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Ctrl+click is the shortcut for starting a selection.
            GridSelectToggle.IsChecked = true;
            selecting = true;
        }

        if (selecting)
        {
            entry.IsChecked = !entry.IsChecked;
            UpdateGridSelectionUi();
            return;
        }

        OpenFromGrid(entry.Item);
    }

    private void GridCard_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LibraryGridEntry entry })
        {
            return;
        }

        e.Handled = true;
        ShowTrayContextMenu(entry.Item);
    }

    private void OpenFromGrid(CaptureItem item)
    {
        CloseLibraryGrid();
        if (_currentItem is not null &&
            string.Equals(_currentItem.FlattenedPath, item.FlattenedPath, StringComparison.OrdinalIgnoreCase))
        {
            // Already the open capture: just go back to it, keeping its undo history.
            return;
        }

        // Picking an older capture from the full library brings it back to the front of the bar, and it stays there.
        _library.MoveToFront(new[] { item });
        OpenItem(item);
    }

    /// <summary>Opens a capture from the right-click menu; from the grid that also moves it to the front, like a click.</summary>
    private void OpenFromContextMenu(CaptureItem item)
    {
        if (_gridOpen)
        {
            OpenFromGrid(item);
        }
        else
        {
            OpenItem(item);
        }
    }

    private void GridMoveToFront_Click(object sender, RoutedEventArgs e)
    {
        var picked = _gridEntries.Where(entry => entry.IsChecked).Select(entry => entry.Item).ToList();
        if (picked.Count == 0)
        {
            return;
        }

        _library.MoveToFront(picked);
        CloseLibraryGrid();

        // Open the one that is now frontmost so the move is visible in both the bar and the canvas.
        var ids = new HashSet<string>(picked.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
        if (_library.Enumerate().FirstOrDefault(i => ids.Contains(i.Id)) is { } first)
        {
            OpenItem(first);
        }

        ShowStatus(picked.Count == 1 ? "Moved to the front" : $"Moved {picked.Count} to the front");
    }
}
