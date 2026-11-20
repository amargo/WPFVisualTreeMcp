using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FluentAssertions;
using WpfVisualTreeMcp.Inspector;
using Xunit;

namespace WpfVisualTreeMcp.IntegrationTests;

public class DiagnosticReporterTests
{
    [Fact]
    public async Task Reports_current_binding_errors_and_state_without_calling_disabled_elements_defects()
    {
        await OnSta(() =>
        {
            var panel = new StackPanel();
            var broken = new TextBox { Name = "BrokenText", IsEnabled = false };
            broken.SetBinding(TextBox.TextProperty, new Binding("MissingProperty") { Source = new object() });
            panel.Children.Add(broken);
            panel.Children.Add(new Button { Visibility = Visibility.Collapsed });
            panel.Measure(new Size(300, 300));
            panel.Arrange(new Rect(0, 0, 300, 300));
            var walker = new TreeWalker();
            var report = DiagnosticReporter.Capture(new[] { panel }, walker, new BindingAnalyzer(), null, 500, 25, 100);
            report.BindingsChecked.Should().BeGreaterThan(0);
            report.DisabledElements.Should().BeGreaterThan(0);
            report.InvisibleElements.Should().BeGreaterThan(0);
            report.Findings.Should().Contain(f => f.Code == "binding_error" && f.Name == "BrokenText" && f.Property == "Text");
            report.Findings.First(f => f.Name == "BrokenText").Message.Should().Contain("Path: 'MissingProperty'").And.Contain("source: Object");
            report.Findings.Should().NotContain(f => f.Code == "disabled" || f.Code == "invisible");
            var finding = report.Findings.First(f => f.Name == "BrokenText");
            walker.ResolveHandle(finding.ElementHandle).Should().BeSameAs(broken);
            broken.IsEnabled.Should().BeFalse();
        });
    }

    [Fact]
    public async Task Merges_repeated_binding_trace_errors_and_counts_occurrences()
    {
        await OnSta(() =>
        {
            var analyzer = new BindingAnalyzer();
            analyzer.StartCapturingErrors();
            try
            {
                var panel = new StackPanel();
                for (var i = 0; i < 6; i++)
                {
                    // One problem repeated per row, as a grid column with a wrong path would be.
                    var text = new TextBlock { DataContext = new object() };
                    text.SetBinding(TextBlock.TextProperty, new Binding(i == 5 ? "OtherMissing" : "MissingProperty"));
                    panel.Children.Add(text);
                }
                panel.Measure(new Size(300, 300));
                panel.Arrange(new Rect(0, 0, 300, 300));

                var report = DiagnosticReporter.Capture(new[] { panel }, new TreeWalker(), analyzer, null, 500, 25, 100);

                report.CapturedBindingErrorCount.Should().Be(6);
                report.RecentBindingErrors.Should().HaveCount(2);
                report.RecentBindingErrors.Should().Contain(e => e.Occurrences == 5 && e.FirstSeen != null && e.Message.Contains("MissingProperty"));
                report.RecentBindingErrors.Should().Contain(e => e.Occurrences == 1 && e.FirstSeen == null && e.Message.Contains("OtherMissing"));
                report.Coverage.Should().Contain("not counted as findings");
            }
            finally { analyzer.StopCapturingErrors(); }
        });
    }

    [Fact]
    public async Task Bounds_scan_and_findings_and_labels_incomplete_coverage()
    {
        await OnSta(() =>
        {
            var panel = new StackPanel();
            for (var i = 0; i < 20; i++)
            {
                var box = new TextBox();
                box.SetBinding(TextBox.TextProperty, new Binding("Missing") { Source = new object() });
                panel.Children.Add(box);
            }
            var report = DiagnosticReporter.Capture(new[] { panel }, new TreeWalker(), new BindingAnalyzer(), "root", 4, 25, 1);
            report.ScannedElements.Should().Be(4);
            report.Findings.Should().HaveCount(1);
            report.FindingCount.Should().BeGreaterThan(1);
            report.TruncationReasons.Should().Contain(new[] { "max_elements", "max_findings" });
            report.Scope.Should().Be("subtree");
            report.BindingErrorScope.Should().Be("process_since_inspector_attach");
        });
    }

    internal static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
