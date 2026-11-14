using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Moq;
using WpfVisualTreeMcp.Automation.Infrastructure;
using WpfVisualTreeMcp.Server;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;
using Xunit;

namespace WpfVisualTreeMcp.Tests;

public class GridReadAndScreenshotDefaultsTests
{
    private readonly Mock<IIpcBridge> _bridge = new(MockBehavior.Strict);
    private WpfTools Tools => new(new Mock<IProcessManager>().Object, _bridge.Object);

    [Fact]
    public async Task WpfReadGrid_ResolvesAutomationIdAndPassesPagingAndColumns()
    {
        var expected = new GridReadResult { RowCount = 3, Returned = 1 };
        var columns = new[] { "Name" };
        _bridge.Setup(x => x.ResolveElementHandleAsync(null, "InvoiceGrid", "elem_root")).ReturnsAsync("elem_grid");
        _bridge.Setup(x => x.ReadGridAsync("elem_grid", 100, 25, columns)).ReturnsAsync(expected);

        var result = await Tools.WpfReadGrid(automation_id: "InvoiceGrid", root_handle: "elem_root", offset: 100, limit: 25, columns: columns);

        result.Should().BeSameAs(expected);
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(0, 0)]
    [InlineData(0, 501)]
    public async Task WpfReadGrid_RejectsInvalidPagingBeforeAnyIpc(int offset, int limit)
    {
        await Assert.ThrowsAsync<McpException>(() => Tools.WpfReadGrid("elem_grid", offset: offset, limit: limit));
    }

    [Fact]
    public void ReadGridRequest_RoundTripsDerivedPropertiesAndValidates()
    {
        var json = IpcSerializer.SerializeRequest(new ReadGridRequest { ElementHandle = "elem_1", Offset = 7, Limit = 9, Columns = new() { "A" } });
        var (type, data) = IpcSerializer.DeserializeRequest(json)!.Value;
        var back = IpcSerializer.DeserializeRequestData<ReadGridRequest>(data)!;

        type.Should().Be("ReadGrid");
        (back.ElementHandle, back.Offset, back.Limit).Should().Be(("elem_1", 7, 9));
        back.Columns.Should().Equal("A");
        new Action(() => new ReadGridRequest { ElementHandle = "elem_1", Limit = 501 }.Validate()).Should().Throw<ArgumentException>();
        new Action(() => new ReadGridRequest { Limit = 1 }.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task WpfFindElements_ShortensPathsUnlessTheFullPathIsRequested()
    {
        const string path = "Window[Main] > Grid > DockPanel > DataGrid[Orders] > Border > TextBlock";
        _bridge.Setup(x => x.FindElementsAsync(null, "TextBlock", null, null, null, false, 50, null))
            .ReturnsAsync(() => new FindElementsResult { Count = 1, Elements = { new FoundElement { Handle = "h", Path = path } } });

        var compact = (FindElementsResult)await Tools.WpfFindElements(type_name: "TextBlock");
        compact.Elements[0].Path.Should().Be("… > DataGrid[Orders] > Border > TextBlock");

        var full = (FindElementsResult)await Tools.WpfFindElements(type_name: "TextBlock", full_path: true);
        full.Elements[0].Path.Should().Be(path);

        WpfTools.ShortenPath("Window > Button").Should().Be("Window > Button", "short paths are left alone");
    }

    [Fact]
    public async Task WpfCaptureScreenshot_DefaultsToCompactBoundsAndReportsPayloadSize()
    {
        _bridge.Setup(x => x.CaptureScreenshotAsync(null, 1280, 800, "auto", false))
            .ReturnsAsync(new ScreenshotResult { ImageBase64 = new string('A', 4096), Width = 1280, Height = 720 });

        var result = await Tools.WpfCaptureScreenshot();

        result.Content.OfType<ImageContentBlock>().Single().MimeType.Should().Be("image/png");
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("1280x720px").And.Contain("3 KB PNG");
    }
}
