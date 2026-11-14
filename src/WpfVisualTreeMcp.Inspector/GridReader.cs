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
        var grid = ResolveGrid(target);
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
                Index = c.Index, Header = ControlOperations.Display(c.Column.Header),
                BindingPath = BindingPath(c.Column), Converter = Converter(c.Column), ValueSource = c.Source
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
        if (column.Binding != null) return Evaluate(probe, item, column.Binding);
        if (column.Source == "sortMemberPath") return ReadMember(item, column.Column.SortMemberPath);
        realized ??= RealizedCells(grid, viewIndex);
        if (realized.TryGetValue(column.Column, out var cell)) return ControlOperations.CellText(cell);
        unrealized = true;
        return null;
    }

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

    private static DataGrid ResolveGrid(DependencyObject target)
    {
        // A row, cell or cell content handle reads its owning grid.
        for (var parent = target; parent != null;
             parent = parent is Visual ? VisualTreeHelper.GetParent(parent) : LogicalTreeHelper.GetParent(parent))
            if (parent is DataGrid owner) return owner;
        var grids = ControlOperations.Descendants(target).OfType<DataGrid>().Take(2).ToArray();
        if (grids.Length == 1) return grids[0];
        throw new ArgumentException(grids.Length == 0
            ? $"Expected a DataGrid; {target.GetType().Name} is not one and contains none. Find the grid with type_name='=DataGrid'. Only the standard WPF DataGrid is supported."
            : $"{target.GetType().Name} contains more than one DataGrid. Pass the handle of the grid to read (find with type_name='=DataGrid').");
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
            var matches = all.Where(c => string.Equals(ControlOperations.Display(c.Column.Header), name, StringComparison.Ordinal)
                || string.Equals(BindingPath(c.Column), name, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new ArgumentException($"Column '{name}' matches {matches.Length} columns; exactly one is required. Available: "
                    + string.Join(", ", all.Select(c => "'" + (ControlOperations.Display(c.Column.Header) ?? BindingPath(c.Column) ?? "#" + c.Index) + "'")));
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
