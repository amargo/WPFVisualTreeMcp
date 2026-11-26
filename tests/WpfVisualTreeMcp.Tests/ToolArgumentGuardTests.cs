using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpfVisualTreeMcp.Server;
using Xunit;

namespace WpfVisualTreeMcp.Tests;

public class ToolArgumentGuardTests
{
    private static Tool Schema(string method) =>
        McpServerTool.Create(typeof(WpfTools).GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!,
            (Func<RequestContext<CallToolRequestParams>, object>)(_ => null!)).ProtocolTool;

    private static Dictionary<string, JsonElement> Arguments(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    [Fact]
    public void Known_parameters_pass()
    {
        ToolArgumentGuard.Check(Schema(nameof(WpfTools.WpfFindElements)), Arguments(new { type_name = "Button", text = "OK" })).Should().BeNull();
        ToolArgumentGuard.Check(Schema(nameof(WpfTools.WpfFindElements)), null).Should().BeNull();
    }

    [Fact]
    public void Unknown_parameters_are_rejected_with_the_meant_one()
    {
        // The cases that silently widened a search or captured another window.
        ToolArgumentGuard.Check(Schema(nameof(WpfTools.WpfFindElements)), Arguments(new { type = "Button" }))
            .Should().Contain("'type' (did you mean 'type_name'?)").And.Contain("nothing was done").And.Contain("Valid parameters:");
        ToolArgumentGuard.Check(Schema(nameof(WpfTools.WpfCaptureScreenshot)), Arguments(new { window_handle = "elem_1" }))
            .Should().Contain("did you mean 'element_handle'");
        ToolArgumentGuard.Check(Schema(nameof(WpfTools.WpfNativeDialog)), Arguments(new { action = "click", button_text = "OK" }))
            .Should().Contain("Unknown parameters 'action', 'button_text' (did you mean 'answer'?)");
        ToolArgumentGuard.Check(Schema(nameof(WpfTools.WpfFindElements)), Arguments(new { elementName = "x" }))
            .Should().Contain("did you mean 'element_name'");
        ToolArgumentGuard.Check(Schema(nameof(WpfTools.WpfFindElements)), Arguments(new { automation_idd = "x" }))
            .Should().Contain("did you mean 'automation_id'");
    }

    [Fact]
    public void Wait_results_say_when_the_matched_state_may_be_the_previous_one()
    {
        var stale = new WpfVisualTreeMcp.Shared.Models.WaitForResult { Matched = true, AlreadyMet = true };
        WpfTools.WaitNote(stale, afterChange: false).Should().Contain("previous state").And.Contain("after_change=true");
        WpfTools.WaitNote(new() { Matched = true }, afterChange: false).Should().BeNull();
        WpfTools.WaitNote(new() { Matched = true, AlreadyMet = true, Changed = true }, afterChange: true).Should().BeNull();
        WpfTools.WaitNote(new() { Matched = false, AlreadyMet = true }, afterChange: true).Should().Contain("never changed");
    }

    [Fact]
    public void Cli_options_are_checked_against_the_command_help()
    {
        WpfVisualTreeMcp.Server.Cli.CliRunner.UnknownOptionError("find", new[] { "pid", "type", "root", "compact" }).Should().BeNull();
        WpfVisualTreeMcp.Server.Cli.CliRunner.UnknownOptionError("find", new[] { "pid", "type-name" })
            .Should().Contain("--type-name (did you mean --type?)").And.Contain("See 'find --help'");
        WpfVisualTreeMcp.Server.Cli.CliRunner.UnknownOptionError("dialog", new[] { "pid", "button" })
            .Should().Contain("did you mean --answer");
        WpfVisualTreeMcp.Server.Cli.CliRunner.UnknownOptionError("screenshot", new[] { "pid", "automation-id", "root", "out" }).Should().BeNull();
        WpfVisualTreeMcp.Server.Cli.CliRunner.UnknownOptionError("update", new[] { "anything" }).Should().BeNull("update validates its own options");
    }
}
