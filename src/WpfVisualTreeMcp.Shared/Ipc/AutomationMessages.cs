using WpfVisualTreeMcp.Shared.Models;

namespace WpfVisualTreeMcp.Shared.Ipc;

/// <summary>Bounded, data-only operations on standard WPF controls.</summary>
public sealed class ControlOperationRequest : IpcRequest
{
    public override string RequestType => "ControlOperation";
    public string ElementHandle { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string? Value { get; set; }
    public bool? Checked { get; set; }
    public List<ItemLocator> Items { get; set; } = new();
    public string SelectionMode { get; set; } = "replace";
    public ItemLocator? Row { get; set; }
    public ColumnLocator? Column { get; set; }
    public List<ItemLocator> Path { get; set; } = new();

    public bool Mutates => Operation is not ("grid_info" or "grid_read");
    public void Validate()
    {
        if (Operation is not ("check" or "expand" or "collapse" or "select" or "grid_info" or "grid_read"
            or "grid_edit" or "grid_cell" or "tree_expand" or "tree_collapse" or "tree_select" or "tree_node"))
            throw new ArgumentException("Unsupported WPF control operation: " + Operation);
        if (Items == null || Items.Count > 1000 || Path == null || Path.Count > 64 || Value?.Length > 65536)
            throw new ArgumentException("Control operation exceeds its item, path or value limit.");
        if (SelectionMode is not ("replace" or "add" or "remove")) throw new ArgumentException("Invalid selection mode.");
        foreach (var item in Items) (item ?? throw new ArgumentException("Null item locator.")).Validate();
        foreach (var item in Path) (item ?? throw new ArgumentException("Null path locator.")).Validate();
        Row?.Validate(); Column?.Validate();
        if (Operation is "grid_read" or "grid_edit" or "grid_cell" && (Row == null || Column == null))
            throw new ArgumentException("A grid cell requires row and column locators.");
        if (Operation == "grid_edit" && Value == null) throw new ArgumentException("Cell text is required.");
        if (Operation.StartsWith("tree_", StringComparison.Ordinal) && Path.Count == 0)
            throw new ArgumentException("A tree node requires a nonempty path.");
    }
}

public sealed class ControlOperationResponse : IpcResponse
{
    /// <summary>Only reported before the requested non-idempotent operation has been dispatched.</summary>
    public bool NotReady { get; set; }
    public string? Reason { get; set; }
    public ControlResult Result { get; set; } = new();
}

/// <summary>Read-only page of DataGrid rows as text, evaluated from the data items so virtualized rows need no scrolling.</summary>
public sealed class ReadGridRequest : IpcRequest
{
    public const int MaxLimit = 500;
    public override string RequestType => "ReadGrid";
    public string ElementHandle { get; set; } = string.Empty;
    public int Offset { get; set; }
    public int Limit { get; set; } = 50;
    /// <summary>Optional exact headers or binding paths; empty returns every visible column in display order.</summary>
    public List<string> Columns { get; set; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ElementHandle)) throw new ArgumentException("A grid read requires an element handle.");
        if (Offset < 0 || Limit < 1 || Limit > MaxLimit)
            throw new ArgumentException($"Grid read requires offset >= 0 and limit 1-{MaxLimit}.");
        if (Columns == null || Columns.Count > 200 || Columns.Any(c => string.IsNullOrEmpty(c) || c.Length > 2048))
            throw new ArgumentException("Grid read accepts at most 200 nonempty column names.");
    }
}

public sealed class ReadGridResponse : IpcResponse
{
    public GridReadResult? Result { get; set; }
}

public sealed class WindowRequest : IpcRequest
{
    public override string RequestType => "Window";
    public string Action { get; set; } = "list";
    public string? ElementHandle { get; set; }
}

public sealed class WindowResponse : IpcResponse
{
    public List<WpfWindowInfo> Windows { get; set; } = new();
}

public sealed class SubscribePropertyRequest : IpcRequest
{
    public override string RequestType => "SubscribeProperty";
    public string WatchId { get; set; } = Guid.NewGuid().ToString("N");
    public string ElementHandle { get; set; } = string.Empty;
    public string PropertyName { get; set; } = string.Empty;
}

public sealed class UnwatchPropertyRequest : IpcRequest
{
    public override string RequestType => "UnwatchProperty";
    public string WatchId { get; set; } = string.Empty;
}

public sealed class UnwatchPropertyResponse : IpcResponse { }

/// <summary>One frame on the dedicated property subscription connection.</summary>
public sealed class PropertyStreamFrame
{
    public PropertyChangedNotification? Change { get; set; }
    public string? Error { get; set; }
}
