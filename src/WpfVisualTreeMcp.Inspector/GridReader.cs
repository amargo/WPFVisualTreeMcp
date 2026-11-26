using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;

namespace WpfVisualTreeMcp.Inspector;

/// <summary>
/// Reads a page of DataGrid rows as text. Values come from the data items through the
/// column bindings, so virtualized rows are read without scrolling or realizing containers.
/// Runs on the WPF Dispatcher and never changes selection, scroll position or data.
/// </summary>
internal static class GridReader
{
    internal const int MaxCellLength = 2000;
    /// <summary>Row searches stay below the Inspector's 10-second UI operation timeout.</summary>
    internal static TimeSpan SearchBudget = TimeSpan.FromSeconds(6);

    internal static GridReadResult Read(DependencyObject target, ReadGridRequest request, TreeWalker walker)
    {
        request.Validate();
        var owner = ResolveGrid(target);
        if (owner is ListView list) return ReadList(list, (GridView)list.View, request, walker);
        var grid = (DataGrid)owner;
        var columns = ResolveColumns(grid, request.Columns);

        // The new-item placeholder sits at either end of the view and is not a data row.
        var items = grid.Items;
        var first = items.Count > 0 && items[0] == CollectionView.NewItemPlaceholder ? 1 : 0;
        var last = items.Count > first && items[items.Count - 1] == CollectionView.NewItemPlaceholder ? 1 : 0;
        var rowCount = items.Count - first - last;

        int? currentRow = null;
        if (grid.CurrentItem != null && grid.CurrentItem != CollectionView.NewItemPlaceholder)
        {
            var index = items.IndexOf(grid.CurrentItem);
            if (index >= 0) currentRow = index;
        }

        var result = new GridReadResult
        {
            GridHandle = walker.GetOrCreateHandle(grid),
            RowCount = rowCount,
            Offset = request.Offset,
            Columns = columns.Select(c => new GridReadColumn
            {
                Index = c.Index, Header = HeaderText(c.Column.Header), HeaderToolTip = HeaderToolTip(c.Column.Header),
                BindingPath = BindingPath(c.Column), Converter = Converter(c.Column), ValueSource = c.Source,
                SortMemberPath = c.Source == "sortMemberPath" ? c.Column.SortMemberPath : null
            }).ToList(),
            SelectedIndices = ControlOperations.SelectedIndices(grid),
            CurrentRowIndex = currentRow
        };

        // One detached probe evaluates bindings exactly as a text cell would, including
        // converters and StringFormat in the grid's language.
        var probe = new TextBlock { Language = grid.Language };
        var unrealizedVisualCells = false;
        var end = (int)Math.Min((long)request.Offset + request.Limit, rowCount);
        for (var row = request.Offset; row < end; row++)
        {
            var viewIndex = row + first;
            var item = items[viewIndex];
            Dictionary<DataGridColumn, DataGridCell>? realized = null;
            var cells = new List<string?>(columns.Count);
            foreach (var column in columns)
            {
                cells.Add(Truncate(CellValue(grid, column, probe, item, viewIndex, ref realized, ref unrealizedVisualCells)));
            }
            result.Rows.Add(new GridReadRow { Index = viewIndex, Cells = cells });
        }
        probe.DataContext = null;

        result.Returned = result.Rows.Count;
        result.HasMore = end < rowCount;
        if (unrealizedVisualCells)
            result.Note = "Columns with valueSource 'visual' have no data binding; their cells are null for rows that are not currently scrolled into view.";
        return result;
    }

    private static string? CellValue(DataGrid grid, ReadColumn column, TextBlock probe, object item, int viewIndex,
        ref Dictionary<DataGridColumn, DataGridCell>? realized, ref bool unrealized)
    {
        if (column.Binding != null)
            return column.Column is DataGridComboBoxColumn combo
                ? ComboText(probe, item, combo, column.Binding)
                : Evaluate(probe, item, column.Binding);
        if (column.Source == "sortMemberPath") return ReadMember(item, column.Column.SortMemberPath);
        realized ??= RealizedCells(grid, viewIndex);
        // A realized cell always has a text (possibly empty); null is reserved for rows that are not on screen.
        if (realized.TryGetValue(column.Column, out var cell))
            return ControlOperations.CellText(cell) ?? TreeWalker.GetDeepText(cell) ?? string.Empty;
        unrealized = true;
        return null;
    }

    /// <summary>
    /// The text one cell shows for a data item, as <see cref="Read"/> returns it: evaluated on the
    /// item when the column has a data binding, otherwise read from the cell visual.
    /// </summary>
    internal static string? ItemCellText(DataGrid grid, DataGridColumn column, object item, DataGridCell cell)
    {
        var binding = DataBinding(column);
        if (binding == null) return ControlOperations.CellText(cell) ?? TreeWalker.GetDeepText(cell);
        var probe = new TextBlock { Language = grid.Language };
        try { return column is DataGridComboBoxColumn combo ? ComboText(probe, item, combo, binding) : Evaluate(probe, item, binding); }
        finally { probe.DataContext = null; }
    }

    /// <summary>A column named as '#index' (its position in Columns), for grids whose headers repeat.</summary>
    private static int? IndexName(string name, int count) =>
        name.Length > 1 && name[0] == '#' && int.TryParse(name.Substring(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && index < count
            ? index : null;

    private static string ColumnError(string name, List<(int Index, string? Header, string? Path)> all, List<int> matched) =>
        $"Column '{name}' matches {matched.Count} columns; exactly one is required. Available: "
        + string.Join(", ", all.Select(c => "'" + (c.Header ?? c.Path ?? "#" + c.Index) + "'"))
        + (matched.Count > 1
            ? ". The matching columns are " + string.Join(", ", matched.Select(i => $"'#{i}'" + (all[i].Path != null ? $" (binding path '{all[i].Path}')" : "")))
              + ": pass the binding path or the '#index' form instead."
            : ". A column can also be named by binding path or as '#index'.");

    /// <summary>The caption of a column header, without the tooltip an element header may carry.</summary>
    internal static string? HeaderText(object? header) =>
        header is DependencyObject element ? TreeWalker.GetSearchableText(element, includeToolTip: false) : ControlOperations.Format(header);

    private static string? HeaderToolTip(object? header) =>
        header is FrameworkElement { ToolTip: var tip } && tip != null
            ? tip is DependencyObject content ? TreeWalker.GetSearchableText(content) : ControlOperations.Format(tip)
            : null;

    internal static bool HeaderMatches(object? header, string name) =>
        string.Equals(HeaderText(header), name, StringComparison.Ordinal) || string.Equals(ControlOperations.Display(header), name, StringComparison.Ordinal);

    private static List<string?> RowCells(DataGrid grid, List<ReadColumn> columns, TextBlock probe, int viewIndex)
    {
        Dictionary<DataGridColumn, DataGridCell>? realized = null;
        var unrealized = false;
        var item = grid.Items[viewIndex];
        var cells = columns.Select(c => CellValue(grid, c, probe, item, viewIndex, ref realized, ref unrealized)).ToList();
        probe.DataContext = null;
        return cells;
    }

    private static string JoinCells(IEnumerable<string?> cells)
    {
        var text = string.Join(" | ", cells.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()));
        return text.Length > 200 ? text.Substring(0, 200) + "…" : text;
    }

    /// <summary>The visible cell texts of one row, as wpf_read_grid returns them, joined for display.</summary>
    internal static string DescribeRow(DataGrid grid, int viewIndex) =>
        JoinCells(RowCells(grid, ResolveColumns(grid, new List<string>()), new TextBlock { Language = grid.Language }, viewIndex));

    /// <summary>
    /// First row with a visible cell containing <paramref name="text"/> (case-insensitive), or -1.
    /// Uses the same cell texts as <see cref="Read"/>, so virtualized rows are searched too.
    /// </summary>
    internal static int FindRow(DataGrid grid, string text, out string rowText)
    {
        var columns = ResolveColumns(grid, new List<string>());
        var probe = new TextBlock { Language = grid.Language };
        var clock = Stopwatch.StartNew();
        var items = grid.Items;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] == CollectionView.NewItemPlaceholder) continue;
            if (clock.Elapsed > SearchBudget)
                throw new InvalidOperationException(
                    $"Searched {i} of {items.Count} rows without finding '{text}' before the time limit. " +
                    "Page the grid with wpf_read_grid (optionally with columns) and select by index.");
            var cells = RowCells(grid, columns, probe, i);
            if (!cells.Any(c => c != null && c.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            rowText = JoinCells(cells);
            return i;
        }
        rowText = string.Empty;
        return -1;
    }

    private static bool IsGrid(DependencyObject element) => element is DataGrid || element is ListView { View: GridView };

    /// <summary>The DataGrid, or ListView with a GridView, that the target is, is inside of, or solely contains.</summary>
    private static ItemsControl ResolveGrid(DependencyObject target)
    {
        // A row, cell or cell content handle reads its owning grid.
        for (var parent = target; parent != null;
             parent = parent is Visual ? VisualTreeHelper.GetParent(parent) : LogicalTreeHelper.GetParent(parent))
            if (IsGrid(parent)) return (ItemsControl)parent;
        var grids = ControlOperations.Descendants(target).Where(IsGrid).Take(2).ToArray();
        if (grids.Length == 1) return (ItemsControl)grids[0];
        throw new ArgumentException(grids.Length == 0
            ? $"Expected a DataGrid or a ListView with a GridView; {target.GetType().Name} is not one and contains none. Find the grid with type_name='=DataGrid' or '=ListView'. Vendor grids are not supported."
            : $"{target.GetType().Name} contains more than one grid. Pass the handle of the grid to read (find with type_name='=DataGrid' or '=ListView').");
    }

    /// <summary>ListView + GridView rows: DisplayMemberBinding columns from the data items, template columns from realized rows.</summary>
    private static GridReadResult ReadList(ListView list, GridView view, ReadGridRequest request, TreeWalker walker)
    {
        var all = view.Columns.Select((column, index) =>
        {
            var binding = column.DisplayMemberBinding != null && !NeedsVisualTree(column.DisplayMemberBinding) ? column.DisplayMemberBinding : null;
            return (Column: column, Index: index, Binding: binding, Header: HeaderText(column.Header), Path: PathOf(column.DisplayMemberBinding));
        }).ToList();
        var columns = all;
        if (request.Columns.Count > 0)
        {
            columns = new();
            foreach (var name in request.Columns)
            {
                var matches = all.Where(c => HeaderMatches(c.Column.Header, name) || string.Equals(c.Path, name, StringComparison.Ordinal)).ToArray();
                if (matches.Length == 0 && IndexName(name, all.Count) is int position) matches = new[] { all[position] };
                if (matches.Length != 1)
                    throw new ArgumentException(ColumnError(name, all.Select(c => (c.Index, c.Header, c.Path)).ToList(), matches.Select(c => c.Index).ToList()));
                columns.Add(matches[0]);
            }
        }

        var items = list.Items;
        var result = new GridReadResult
        {
            GridHandle = walker.GetOrCreateHandle(list), RowCount = items.Count, Offset = request.Offset,
            Columns = columns.Select(c => new GridReadColumn
            {
                Index = c.Index, Header = c.Header, HeaderToolTip = HeaderToolTip(c.Column.Header), BindingPath = c.Path,
                Converter = c.Column.DisplayMemberBinding switch
                {
                    Binding b => b.Converter?.GetType().Name, MultiBinding m => m.Converter?.GetType().Name, _ => null
                },
                ValueSource = c.Binding != null ? "binding" : "visual"
            }).ToList(),
            SelectedIndices = ControlOperations.SelectedIndices(list)
        };

        var probe = new TextBlock { Language = list.Language };
        var unrealized = false;
        var end = (int)Math.Min((long)request.Offset + request.Limit, items.Count);
        for (var row = request.Offset; row < end; row++)
        {
            List<DependencyObject>? cells = null;
            var texts = new List<string?>(columns.Count);
            foreach (var column in columns)
            {
                string? text = null;
                if (column.Binding != null) text = Evaluate(probe, items[row], column.Binding);
                else
                {
                    // A GridViewRowPresenter holds one child per column, in column order.
                    cells ??= list.ItemContainerGenerator.ContainerFromIndex(row) is DependencyObject container
                        && ControlOperations.Descendants(container).OfType<GridViewRowPresenter>().FirstOrDefault() is { } presenter
                        ? Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(presenter)).Select(i => VisualTreeHelper.GetChild(presenter, i)).ToList()
                        : new List<DependencyObject>();
                    if (column.Index < cells.Count) text = TreeWalker.GetSearchableText(cells[column.Index]) ?? TreeWalker.GetDeepText(cells[column.Index]) ?? string.Empty;
                    else unrealized = true;
                }
                texts.Add(Truncate(text));
            }
            result.Rows.Add(new GridReadRow { Index = row, Cells = texts });
        }
        probe.DataContext = null;

        result.Returned = result.Rows.Count;
        result.HasMore = end < items.Count;
        if (unrealized)
            result.Note = "Columns with valueSource 'visual' have no data binding; their cells are null for rows that are not currently scrolled into view.";
        return result;
    }

    private sealed class ReadColumn
    {
        public DataGridColumn Column = null!;
        public int Index;
        public BindingBase? Binding;
        public string Source = "visual";
    }

    private static List<ReadColumn> ResolveColumns(DataGrid grid, List<string> names)
    {
        var all = grid.Columns.Select((column, index) =>
        {
            var binding = DataBinding(column);
            return new ReadColumn
            {
                Column = column, Index = index, Binding = binding,
                Source = binding != null ? "binding" : !string.IsNullOrEmpty(column.SortMemberPath) ? "sortMemberPath" : "visual"
            };
        }).ToList();
        if (names.Count == 0)
            return all.Where(c => c.Column.Visibility == Visibility.Visible).OrderBy(c => c.Column.DisplayIndex).ToList();

        var selected = new List<ReadColumn>();
        foreach (var name in names)
        {
            var matches = all.Where(c => HeaderMatches(c.Column.Header, name)
                || string.Equals(BindingPath(c.Column), name, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0 && IndexName(name, all.Count) is int position) matches = new[] { all[position] };
            if (matches.Length != 1)
                throw new ArgumentException(ColumnError(name,
                    all.Select(c => (c.Index, HeaderText(c.Column.Header), BindingPath(c.Column))).ToList(), matches.Select(c => c.Index).ToList()));
            selected.Add(matches[0]);
        }
        return selected;
    }

    /// <summary>The binding a cell displays, when it can be evaluated on the data item alone.</summary>
    private static BindingBase? DataBinding(DataGridColumn column)
    {
        var binding = (column as DataGridBoundColumn)?.Binding ?? column.ClipboardContentBinding;
        return binding != null && !NeedsVisualTree(binding) ? binding : null;
    }

    private static bool NeedsVisualTree(BindingBase binding) => binding switch
    {
        Binding b => b.ElementName != null || b.RelativeSource != null && b.RelativeSource.Mode != RelativeSourceMode.Self,
        MultiBinding m => m.Bindings.Any(NeedsVisualTree),
        PriorityBinding p => p.Bindings.Any(NeedsVisualTree),
        _ => false
    };

    private static string? BindingPath(DataGridColumn column) =>
        PathOf((column as DataGridBoundColumn)?.Binding ?? column.ClipboardContentBinding);

    // "." marks a binding to the whole data item (usually shaped by a converter), so a
    // column reported as valueSource 'binding' always says what it binds to.
    private static string? PathOf(BindingBase? binding) => binding switch
    {
        Binding b => string.IsNullOrEmpty(b.Path?.Path) ? "." : b.Path!.Path,
        MultiBinding m => string.Join("+", m.Bindings.Select(PathOf)),
        PriorityBinding p => string.Join("|", p.Bindings.Select(PathOf)),
        _ => null
    };

    private static string? Converter(DataGridColumn column) =>
        ((column as DataGridBoundColumn)?.Binding ?? column.ClipboardContentBinding) switch
        {
            Binding b => b.Converter?.GetType().Name,
            MultiBinding m => m.Converter?.GetType().Name,
            _ => null
        };

    private static string? Evaluate(TextBlock probe, object item, BindingBase binding)
    {
        probe.DataContext = item;
        BindingOperations.SetBinding(probe, TextBlock.TextProperty, binding);
        try { return probe.Text; }
        finally { BindingOperations.ClearBinding(probe, TextBlock.TextProperty); }
    }

    /// <summary>
    /// The text a combo cell shows: the selected item (looked up by SelectedValuePath in the
    /// column's ItemsSource when the cell binds a key) through DisplayMemberPath. Falls back to
    /// the bound value when the items are supplied by a style and cannot be read from the column.
    /// </summary>
    private static string? ComboText(TextBlock probe, object item, DataGridComboBoxColumn combo, BindingBase binding)
    {
        probe.DataContext = item;
        BindingOperations.SetBinding(probe, FrameworkElement.TagProperty, binding);
        object? value;
        try { value = probe.Tag; }
        finally { BindingOperations.ClearBinding(probe, FrameworkElement.TagProperty); }
        if (value == null) return string.Empty;

        var selected = value;
        if (ReferenceEquals(binding, combo.SelectedValueBinding))
        {
            selected = null;
            if (combo.ItemsSource != null)
                foreach (var candidate in combo.ItemsSource)
                {
                    var key = string.IsNullOrEmpty(combo.SelectedValuePath) ? candidate : ReadOrNull(candidate, combo.SelectedValuePath);
                    if (Equals(key, value)) { selected = candidate; break; }
                }
            if (selected == null) return ControlOperations.Format(value);
        }
        return ControlOperations.Format(string.IsNullOrEmpty(combo.DisplayMemberPath) || ReferenceEquals(binding, combo.TextBinding)
            ? selected : ReadOrNull(selected, combo.DisplayMemberPath));
    }

    private static object? ReadOrNull(object? item, string path)
    {
        try { return ControlOperations.ReadPath(item, path); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    private static string? ReadMember(object item, string path)
    {
        // Reflection instead of a guessed Binding: a wrong path must not add binding errors to the app's trace.
        try { return ControlOperations.Format(ControlOperations.ReadPath(item, path)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    private static Dictionary<DataGridColumn, DataGridCell> RealizedCells(DataGrid grid, int viewIndex)
    {
        var cells = new Dictionary<DataGridColumn, DataGridCell>();
        if (grid.ItemContainerGenerator.ContainerFromIndex(viewIndex) is DataGridRow row)
            foreach (var cell in ControlOperations.Descendants(row).OfType<DataGridCell>())
                if (cell.Column != null) cells[cell.Column] = cell;
        return cells;
    }

    private static string? Truncate(string? text) =>
        text != null && text.Length > MaxCellLength ? text.Substring(0, MaxCellLength) + "…" : text;
}
