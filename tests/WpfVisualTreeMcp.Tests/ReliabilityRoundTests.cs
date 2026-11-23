using FluentAssertions;
using ModelContextProtocol;
using Moq;
using WpfVisualTreeMcp.Server;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;
using Xunit;

namespace WpfVisualTreeMcp.Tests;

public class ReliabilityRoundTests
{
    [Fact]
    public void CompatibilityNote_NamesTheStaleHalfAndHowToRefreshIt()
    {
        InspectorFeatures.CompatibilityNote(InspectorFeatures.Level).Should().BeNull();
        // Builds that predate the handshake report level 0.
        InspectorFeatures.CompatibilityNote(0).Should().Contain("Inspector loaded in the attached app is older").And.Contain("restart the app");
        InspectorFeatures.CompatibilityNote(InspectorFeatures.Level + 1).Should().Contain("This server is older").And.Contain("reconnect");
    }

    private static GridReadResult Grid(int rows, params string[] cells) => new()
    {
        RowCount = rows, Returned = cells.Length,
        Rows = cells.Select((cell, index) => new GridReadRow { Index = index, Cells = new List<string?> { cell, null } }).ToList()
    };

    private static Exception Error(string message) => new McpException(message);

    private static WpfWindowInfo Window(string handle, string title, bool visible = true) => new() { Handle = handle, Title = title, IsVisible = visible };

    private static Mock<WpfVisualTreeMcp.Automation.Infrastructure.IIpcBridge> WindowBridge(params List<WpfWindowInfo>[] listings)
    {
        var bridge = new Mock<WpfVisualTreeMcp.Automation.Infrastructure.IIpcBridge>();
        var call = 0;
        bridge.Setup(x => x.WindowAsync(It.Is<WindowRequest>(r => r.Action == "list")))
            .ReturnsAsync(() => new WindowResponse { Windows = listings[Math.Min(call++, listings.Length - 1)] });
        bridge.Setup(x => x.WindowAsync(It.Is<WindowRequest>(r => r.Action != "list"))).ReturnsAsync(new WindowResponse());
        return bridge;
    }

    [Fact]
    public async Task Window_wait_returns_once_a_visible_window_matches_and_times_out_with_the_open_windows()
    {
        var login = new List<WpfWindowInfo> { Window("elem_1", "Login") };
        var main = new List<WpfWindowInfo> { Window("elem_1", "Login", visible: false), Window("elem_2", "Main Window") };

        var waited = System.Text.Json.JsonSerializer.Serialize(await AppWindows.WindowAsync(WindowBridge(login, login, main).Object, "wait", null, "main", null, 5000, Error, pollMs: 1));
        waited.Should().Contain("\"Handle\":\"elem_2\"").And.Contain("\"matches\":1");

        var closed = System.Text.Json.JsonSerializer.Serialize(await AppWindows.WindowAsync(WindowBridge(login, main).Object, "wait-closed", null, "login", null, 5000, Error, pollMs: 1));
        closed.Should().Contain("\"closed\":true");

        var never = () => AppWindows.WindowAsync(WindowBridge(login).Object, "wait", null, "main", null, 0, Error, pollMs: 1);
        (await never.Should().ThrowAsync<McpException>()).WithMessage("*No visible window matched*elem_1 'Login'*");
    }

    [Fact]
    public async Task Window_actions_need_exactly_one_window_and_never_guess()
    {
        var two = new List<WpfWindowInfo> { Window("elem_1", "Invoice 1"), Window("elem_2", "Invoice 2") };
        var bridge = WindowBridge(two);

        var ambiguous = () => AppWindows.WindowAsync(bridge.Object, "close", null, "invoice", null, 0, Error);
        (await ambiguous.Should().ThrowAsync<McpException>()).WithMessage("2 windows match*");
        var untargeted = () => AppWindows.WindowAsync(bridge.Object, "close", null, null, null, 0, Error);
        (await untargeted.Should().ThrowAsync<McpException>()).WithMessage("*needs a window*");
        var unknown = () => AppWindows.WindowAsync(bridge.Object, "destroy", null, "invoice", null, 0, Error);
        (await unknown.Should().ThrowAsync<McpException>()).WithMessage("Unknown action*");
        bridge.Verify(x => x.WindowAsync(It.Is<WindowRequest>(r => r.Action != "list")), Times.Never);

        var done = System.Text.Json.JsonSerializer.Serialize(await AppWindows.WindowAsync(bridge.Object, "close", null, "invoice 2", null, 0, Error));
        done.Should().Contain("close requested for").And.Contain("Invoice 2");
        bridge.Verify(x => x.WindowAsync(It.Is<WindowRequest>(r => r.Action == "close" && r.ElementHandle == "elem_2")), Times.Once);
    }

    [Fact]
    public async Task Native_dialog_waits_for_a_dialog_that_opens_after_asynchronous_work()
    {
        NativeDialogInfo Box(long handle, string title) => new() { Handle = handle, Title = title, Buttons = { new NativeDialogButton { Text = "OK", IsEnabled = true } } };
        var bridge = new Mock<WpfVisualTreeMcp.Automation.Infrastructure.IIpcBridge>();
        var lists = new Queue<List<NativeDialogInfo>>(new[] { new List<NativeDialogInfo>(), new List<NativeDialogInfo>(), new List<NativeDialogInfo> { Box(7, "Error") } });
        bridge.Setup(x => x.NativeDialogAsync(null, null)).ReturnsAsync(() => new NativeDialogResponse { Dialogs = lists.Count > 1 ? lists.Dequeue() : lists.Peek() });

        var listed = System.Text.Json.JsonSerializer.Serialize(await WpfTools.NativeDialogAsync(bridge.Object, null, null, 5000, pollMs: 1));
        listed.Should().Contain("\"Title\":\"Error\"").And.Contain("waited_ms");

        // After an answer, the answered dialog itself is not a follow-up; a new one is.
        var follow = new Queue<List<NativeDialogInfo>>(new[] { new List<NativeDialogInfo> { Box(1, "Save?") }, new List<NativeDialogInfo>(), new List<NativeDialogInfo> { Box(2, "Error") } });
        var answering = new Mock<WpfVisualTreeMcp.Automation.Infrastructure.IIpcBridge>();
        answering.Setup(x => x.NativeDialogAsync(null, null)).ReturnsAsync(() => new NativeDialogResponse { Dialogs = follow.Count > 1 ? follow.Dequeue() : follow.Peek() });
        answering.Setup(x => x.NativeDialogAsync("OK", null)).ReturnsAsync(new NativeDialogResponse { Detail = "pressed 'OK' on 'Save?'; the dialog closed", Dialogs = { Box(1, "Save?") } });
        var answered = System.Text.Json.JsonSerializer.Serialize(await WpfTools.NativeDialogAsync(answering.Object, "OK", null, 5000, pollMs: 1));
        answered.Should().Contain("\"Title\":\"Error\"").And.Contain("the dialog closed");

        // Nothing appears: returns after the wait with an empty list instead of failing.
        var none = new Mock<WpfVisualTreeMcp.Automation.Infrastructure.IIpcBridge>();
        none.Setup(x => x.NativeDialogAsync(null, null)).ReturnsAsync(new NativeDialogResponse());
        System.Text.Json.JsonSerializer.Serialize(await WpfTools.NativeDialogAsync(none.Object, null, null, 50, pollMs: 1)).Should().Contain("\"dialogs\":[]");
    }

    [Fact]
    public async Task Launch_rejects_anything_that_is_not_an_existing_exe()
    {
        var processes = new Mock<WpfVisualTreeMcp.Automation.Infrastructure.IProcessManager>();
        var bridge = Mock.Of<WpfVisualTreeMcp.Automation.Infrastructure.IIpcBridge>();
        var missing = () => AppWindows.LaunchAsync(processes.Object, bridge, @"C:\no\such\app.exe", null, null, 1000, true, Error);
        (await missing.Should().ThrowAsync<McpException>()).WithMessage("*not an existing .exe*");
        var document = () => AppWindows.LaunchAsync(processes.Object, bridge, typeof(ReliabilityRoundTests).Assembly.Location, null, null, 1000, true, Error);
        (await document.Should().ThrowAsync<McpException>()).WithMessage("*not an existing .exe*");
        processes.VerifyNoOtherCalls();
    }

    [Fact]
    public void Launch_runs_only_dotnet_applications_and_never_a_shell()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        AppWindows.LaunchRefusal(Path.Combine(system, "cmd.exe")).Should().Contain("Windows component");
        AppWindows.LaunchRefusal(Path.Combine(system, "notepad.exe")).Should().Contain("Windows component");

        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "launch-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var managed = typeof(ReliabilityRoundTests).Assembly.Location;
            string Copy(string from, string name) { var to = Path.Combine(dir, name); File.Copy(from, to); return to; }

            AppWindows.LaunchRefusal(Copy(managed, "Kur.Main.exe")).Should().BeNull("a .NET Framework app is a managed exe");
            AppWindows.LaunchRefusal(Copy(managed, "pwsh.exe")).Should().Contain("shell");
            AppWindows.LaunchRefusal(Copy(Path.Combine(system, "cmd.exe"), "tool.exe")).Should().Contain("not a .NET application");

            // .NET 5+: native apphost next to the managed dll and its runtimeconfig.
            var host = Copy(Path.Combine(system, "cmd.exe"), "App.exe");
            AppWindows.LaunchRefusal(host).Should().Contain("not a .NET application");
            Copy(managed, "App.dll");
            File.WriteAllText(Path.Combine(dir, "App.runtimeconfig.json"), "{}");
            AppWindows.LaunchRefusal(host).Should().BeNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ReadGridWhen_ReturnsImmediatelyWithoutConditions()
    {
        var calls = 0;
        var grid = await WpfTools.ReadGridWhenAsync(() => { calls++; return Task.FromResult(Grid(0)); }, 0, null, 5000, Error);
        grid.RowCount.Should().Be(0);
        grid.WaitedMs.Should().BeNull("nothing was waited for");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ReadGridWhen_RetriesUntilTheGridExistsAndHasTheRowsAndText()
    {
        var calls = 0;
        var grid = await WpfTools.ReadGridWhenAsync(() =>
        {
            calls++;
            return calls switch
            {
                1 => throw new McpException("AutomationId 'Years' was not found in the requested scope."),
                2 => Task.FromResult(Grid(0)),
                3 => Task.FromResult(Grid(1, "2025")),
                _ => Task.FromResult(Grid(2, "2025", "Year 2026"))
            };
        }, 2, "year 2026", 5000, Error);

        calls.Should().Be(4);
        grid.RowCount.Should().Be(2);
        grid.WaitedMs.Should().BeGreaterThanOrEqualTo(3 * WpfTools.GridWaitPollMs - 50);
    }

    [Fact]
    public async Task ReadGridWhen_TimesOutWithTheLastObservedState()
    {
        var empty = () => WpfTools.ReadGridWhenAsync(() => Task.FromResult(Grid(1, "a")), 3, null, 250, Error);
        (await empty.Should().ThrowAsync<McpException>()).WithMessage("*250 ms*at least 3 row(s)*The grid has 1 row(s).*");

        var missing = () => WpfTools.ReadGridWhenAsync(() => throw new McpException("not found"), 1, null, 250, Error);
        (await missing.Should().ThrowAsync<McpException>()).WithMessage("*could not be read: not found*");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var wrongColumn = () => WpfTools.ReadGridWhenAsync(
            () => throw new McpException("Column 'Nope' matches 0 columns; exactly one is required. Available: 'Id', 'Name'"), 1, null, 20000, Error);
        (await wrongColumn.Should().ThrowAsync<McpException>()).WithMessage("*Available: 'Id', 'Name'*");
        clock.ElapsedMilliseconds.Should().BeLessThan(5000, "a wrong column name fails at once instead of waiting out the timeout");

        var invalid = () => WpfTools.ReadGridWhenAsync(() => Task.FromResult(Grid(0)), 1, null, 60000, Error);
        (await invalid.Should().ThrowAsync<McpException>()).WithMessage("*timeout_ms 0-25000*");
    }

    [Fact]
    public void MergeBindingErrors_CountsIdenticalErrorsIgnoringInstanceHashes()
    {
        var start = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        BindingError Row(int hash, int minute) => new()
        {
            ElementType = "DataGridCell", Property = "IsReadOnly", BindingPath = "IsReadOnly", ErrorType = "SourceNotFound",
            Message = $"Cannot find source; target element is 'DataGridCell' (Name=''); HashCode={hash}", Timestamp = start.AddMinutes(minute)
        };
        var result = new BindingErrorsResult
        {
            Count = 4,
            Errors =
            {
                Row(111, 0), Row(-222, 5), Row(333, 2),
                new BindingError { ElementType = "TabControl", Property = "IsEnabled", BindingPath = "EnabledTabControl", ErrorType = "PathError", Message = "Property not found", Timestamp = start.AddMinutes(9) }
            }
        };

        var merged = WpfTools.MergeBindingErrors(result);

        merged.Count.Should().Be(4, "count stays the raw total");
        merged.Errors.Should().HaveCount(2);
        merged.Errors[0].ElementType.Should().Be("TabControl", "newest first");
        merged.Errors[0].Occurrences.Should().Be(1);
        merged.Errors[0].FirstSeen.Should().BeNull();
        merged.Errors[1].Occurrences.Should().Be(3);
        merged.Errors[1].Timestamp.Should().Be(start.AddMinutes(5));
        merged.Errors[1].FirstSeen.Should().Be(start);
        merged.Errors[1].Message.Should().EndWith("HashCode");
    }

    [Fact]
    public void TreeOutline_PrintsOneIndentedLinePerElement()
    {
        var tree = new VisualTreeResult
        {
            TotalElements = 3, MaxDepthReached = true,
            Root = new VisualTreeNode
            {
                Handle = "elem_00000001", TypeName = "System.Windows.Window", Name = "Main",
                Children =
                {
                    new VisualTreeNode
                    {
                        Handle = "elem_00000002", TypeName = "System.Windows.Controls.Grid",
                        Children = { new VisualTreeNode { Handle = "elem_00000003", TypeName = "Demo.Controls.Pager`1[[System.Int32, mscorlib]]", Name = "Pages" } }
                    }
                }
            }
        };

        WpfTools.TreeOutline(tree).Should().Be(
            "Window [Main] elem_00000001\n Grid elem_00000002\n  Pager`1[[System.Int32, mscorlib]] [Pages] elem_00000003\n"
            + "totalElements: 3; max_depth reached, deeper elements omitted");
    }

    [Fact]
    public async Task EditGridCell_ResolvesTheGridAndColumnThenCommitsOneEdit()
    {
        var bridge = new Moq.Mock<WpfVisualTreeMcp.Automation.Infrastructure.IIpcBridge>();
        bridge.Setup(x => x.ReadGridAsync("panel", 3, 1, Moq.It.Is<IReadOnlyList<string>>(c => c.Single() == "Quantity")))
            .ReturnsAsync(new GridReadResult
            {
                GridHandle = "grid", RowCount = 10, Returned = 1,
                Columns = { new GridReadColumn { Index = 4, Header = "Quantity" } },
                Rows = { new GridReadRow { Index = 3, Cells = new List<string?> { "7" } } }
            });
        ControlOperationRequest? sent = null;
        bridge.Setup(x => x.ControlOperationAsync(Moq.It.IsAny<ControlOperationRequest>()))
            .Callback<ControlOperationRequest>(request => sent = request)
            .ReturnsAsync(new ControlOperationResponse { Result = new ControlResult { Changed = true, Value = "12", ColumnIndex = 4, ElementHandle = "cell" } });

        var result = await WpfTools.EditGridCellAsync(bridge.Object, "panel", 3, "Quantity", "12", Error);

        sent!.ElementHandle.Should().Be("grid", "a container handle is replaced by the grid it holds");
        sent.Operation.Should().Be("grid_edit");
        sent.Value.Should().Be("12");
        sent.Row!.Index.Should().Be(3);
        sent.Column!.Index.Should().Be(4);
        System.Text.Json.JsonSerializer.Serialize(result).Should().Contain("\"changed\":true").And.Contain("\"previous\":\"7\"").And.Contain("\"value\":\"12\"");
    }

    [Fact]
    public async Task NativeDialog_ListsWithoutAnAnswerAndReportsTheResultWithOne()
    {
        var bridge = new Moq.Mock<WpfVisualTreeMcp.Automation.Infrastructure.IIpcBridge>();
        var open = new NativeDialogInfo { Handle = 42, Title = "Confirm", Text = "Delete?", Buttons = { new NativeDialogButton { Id = 6, Text = "Yes", IsEnabled = true } } };
        bridge.Setup(x => x.NativeDialogAsync(null, null)).ReturnsAsync(new WpfVisualTreeMcp.Shared.Ipc.NativeDialogResponse { Dialogs = { open } });
        bridge.Setup(x => x.NativeDialogAsync("Yes", 42)).ReturnsAsync(new WpfVisualTreeMcp.Shared.Ipc.NativeDialogResponse { Detail = "pressed 'Yes' on 'Confirm'; the dialog closed" });
        var tools = new WpfTools(Moq.Mock.Of<WpfVisualTreeMcp.Automation.Infrastructure.IProcessManager>(), bridge.Object);

        var listed = System.Text.Json.JsonSerializer.Serialize(await tools.WpfNativeDialog());
        listed.Should().Contain("\"Title\":\"Confirm\"").And.Contain("\"Text\":\"Yes\"").And.NotContain("result");

        var answered = System.Text.Json.JsonSerializer.Serialize(await tools.WpfNativeDialog("Yes", 42));
        answered.Should().Contain("the dialog closed").And.Contain("\"dialogs\":[]");

        var blank = () => tools.WpfNativeDialog(" ");
        await blank.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task EditGridCell_ReportsAChangeTheApplicationAppliedAfterTheCommit()
    {
        var bridge = new Moq.Mock<WpfVisualTreeMcp.Automation.Infrastructure.IIpcBridge>();
        GridReadResult Row(string? cell) => new()
        {
            GridHandle = "grid", RowCount = 10, Returned = 1, Columns = { new GridReadColumn { Index = 4, Header = "Paid" } },
            Rows = { new GridReadRow { Index = 1, Cells = new List<string?> { cell } } }
        };
        bridge.Setup(x => x.ReadGridAsync("grid", 1, 1, Moq.It.Is<IReadOnlyList<string>>(c => c.Single() == "Paid"))).ReturnsAsync(Row(""));
        bridge.Setup(x => x.ReadGridAsync("grid", 1, 1, Moq.It.Is<IReadOnlyList<string>>(c => c.Single() == "#4"))).ReturnsAsync(Row("26 400,00"));
        bridge.Setup(x => x.ControlOperationAsync(Moq.It.IsAny<ControlOperationRequest>()))
            .ReturnsAsync(new ControlOperationResponse { Result = new ControlResult { Changed = false, Value = "", ColumnIndex = 4 } });

        var result = await WpfTools.EditGridCellAsync(bridge.Object, "grid", 1, "Paid", "26400", Error);

        System.Text.Json.JsonSerializer.Serialize(result).Should().Contain("\"changed\":true").And.Contain("\"previous\":\"\"").And.Contain("26 400,00");
    }

    [Fact]
    public async Task EditGridCell_FailsWithoutEditingWhenTheRowIsMissingOrNotReady()
    {
        var bridge = new Moq.Mock<WpfVisualTreeMcp.Automation.Infrastructure.IIpcBridge>();
        bridge.Setup(x => x.ReadGridAsync("grid", 99, 1, Moq.It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(new GridReadResult { GridHandle = "grid", RowCount = 10, Returned = 0 });
        var missing = () => WpfTools.EditGridCellAsync(bridge.Object, "grid", 99, "Quantity", "1", Error);
        (await missing.Should().ThrowAsync<McpException>()).WithMessage("*Row 99 does not exist*10 row(s)*");
        bridge.Verify(x => x.ControlOperationAsync(Moq.It.IsAny<ControlOperationRequest>()), Moq.Times.Never);

        bridge.Setup(x => x.ReadGridAsync("grid", 2, 1, Moq.It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(new GridReadResult
            {
                GridHandle = "grid", RowCount = 10, Returned = 1, Columns = { new GridReadColumn { Index = 0 } },
                Rows = { new GridReadRow { Index = 2, Cells = new List<string?> { "a" } } }
            });
        bridge.Setup(x => x.ControlOperationAsync(Moq.It.IsAny<ControlOperationRequest>()))
            .ReturnsAsync(new ControlOperationResponse { NotReady = true, Reason = "Virtualized grid row has not been realized yet." });
        var notReady = () => WpfTools.EditGridCellAsync(bridge.Object, "grid", 2, "Quantity", "1", Error);
        (await notReady.Should().ThrowAsync<McpException>()).WithMessage("*not ready*Nothing was changed*");
    }

    [Fact]
    public void WithStateFilters_AddsReservedKeysWithoutChangingTheCallersFilter()
    {
        WpfTools.WithStateFilters(null, false, false).Should().BeNull();
        var original = new Dictionary<string, string> { ["IsEnabled"] = "True" };

        var filter = WpfTools.WithStateFilters(original, onScreen: true, hasText: true)!;

        filter.Should().Contain("IsEnabled", "True").And.ContainKey(FindFilters.OnScreen).And.ContainKey(FindFilters.HasText);
        original.Should().HaveCount(1);
    }
}
