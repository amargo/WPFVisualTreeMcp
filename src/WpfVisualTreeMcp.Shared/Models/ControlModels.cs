namespace WpfVisualTreeMcp.Shared.Models;

/// <summary>Exactly one index, exact text, or property/value key. Indices follow the current items view.</summary>
public sealed class ItemLocator
{
    public int? Index { get; set; }
    public string? Text { get; set; }
    public string? KeyProperty { get; set; }
    public string? KeyValue { get; set; }

    public static ItemLocator At(int index) => new() { Index = index };
    public static ItemLocator ByText(string text) => new() { Text = text };
    public static ItemLocator ByKey(string property, string? value) => new() { KeyProperty = property, KeyValue = value };
    public void Validate()
    {
        if ((Index.HasValue ? 1 : 0) + (Text != null ? 1 : 0) + (KeyProperty != null ? 1 : 0) != 1 || Index < 0)
            throw new ArgumentException("An item requires exactly one nonnegative index, exact text or property key.");
        if (KeyProperty != null && (string.IsNullOrWhiteSpace(KeyProperty) || KeyProperty.Length > 256))
            throw new ArgumentException("A property key must contain 1-256 nonblank characters.");
        if (Text?.Length > 2048 || KeyValue?.Length > 2048 || KeyValue != null && KeyProperty == null)
            throw new ArgumentException("Invalid or oversized item locator.");
    }
}

public sealed class ColumnLocator
{
    /// <summary>Index in DataGrid.Columns, independent of display reordering.</summary>
    public int? Index { get; set; }
    public string? Header { get; set; }
    public string? BindingPath { get; set; }
    public static ColumnLocator At(int index) => new() { Index = index };
    public static ColumnLocator ByHeader(string header) => new() { Header = header };
    public static ColumnLocator ByBinding(string path) => new() { BindingPath = path };
    public void Validate()
    {
        if ((Index.HasValue ? 1 : 0) + (Header != null ? 1 : 0) + (BindingPath != null ? 1 : 0) != 1 || Index < 0
            || Header?.Length > 2048 || BindingPath?.Length > 256 || BindingPath != null && string.IsNullOrWhiteSpace(BindingPath))
            throw new ArgumentException("A column requires exactly one nonnegative index, exact header or binding path.");
    }
}

public sealed class ControlResult
{
    public bool Changed { get; set; }
    public string? Value { get; set; }
    public string? ElementHandle { get; set; }
    public int? RowIndex { get; set; }
    public int? ColumnIndex { get; set; }
    public int RowCount { get; set; }
    public List<GridColumnInfo> Columns { get; set; } = new();
    public List<int> SelectedIndices { get; set; } = new();
}

public sealed class GridColumnInfo
{
    public int Index { get; set; }
    public int DisplayIndex { get; set; }
    public string? Header { get; set; }
    public string? BindingPath { get; set; }
    public bool IsReadOnly { get; set; }
}

/// <summary>One page of DataGrid rows. Row indices follow the current items view (sorting/filtering applied).</summary>
public sealed class GridReadResult
{
    /// <summary>Handle of the DataGrid that was read (differs from the request when a container was passed).</summary>
    public string GridHandle { get; set; } = string.Empty;
    public int RowCount { get; set; }
    public int Offset { get; set; }
    public int Returned { get; set; }
    public bool HasMore { get; set; }
    public List<GridReadColumn> Columns { get; set; } = new();
    public List<GridReadRow> Rows { get; set; } = new();
    public List<int> SelectedIndices { get; set; } = new();
    public int? CurrentRowIndex { get; set; }
    public string? Note { get; set; }
}

public sealed class GridReadColumn
{
    /// <summary>Index in DataGrid.Columns, independent of display reordering.</summary>
    public int Index { get; set; }
    public string? Header { get; set; }
    /// <summary>Binding path; "." when the binding targets the whole data item, child paths joined for multi-bindings.</summary>
    public string? BindingPath { get; set; }
    /// <summary>The member read for valueSource "sortMemberPath" columns.</summary>
    public string? SortMemberPath { get; set; }
    /// <summary>Type name of the binding's value converter, when it has one.</summary>
    public string? Converter { get; set; }
    /// <summary>"binding" (evaluated on the data item), "sortMemberPath" or "visual" (text of realized cells only).</summary>
    public string ValueSource { get; set; } = "binding";
}

public sealed class GridReadRow
{
    public int Index { get; set; }
    /// <summary>One entry per returned column, in the same order; null when the value is unavailable.</summary>
    public List<string?> Cells { get; set; } = new();
}

public sealed class WpfWindowInfo
{
    public string Handle { get; set; } = string.Empty;
    public string? AutomationId { get; set; }
    public string? Name { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? OwnerHandle { get; set; }
    public bool IsMain { get; set; }
    public bool IsActive { get; set; }
    public bool IsVisible { get; set; }
    public bool IsEnabled { get; set; }
    public string State { get; set; } = string.Empty;
}
