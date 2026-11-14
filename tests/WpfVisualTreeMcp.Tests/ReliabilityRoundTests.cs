using FluentAssertions;
using ModelContextProtocol;
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
    public void WithStateFilters_AddsReservedKeysWithoutChangingTheCallersFilter()
    {
        WpfTools.WithStateFilters(null, false, false).Should().BeNull();
        var original = new Dictionary<string, string> { ["IsEnabled"] = "True" };

        var filter = WpfTools.WithStateFilters(original, onScreen: true, hasText: true)!;

        filter.Should().Contain("IsEnabled", "True").And.ContainKey(FindFilters.OnScreen).And.ContainKey(FindFilters.HasText);
        original.Should().HaveCount(1);
    }
}
