using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FluentAssertions;
using WpfVisualTreeMcp.Inspector;
using Xunit;
using TreeWalker = WpfVisualTreeMcp.Inspector.TreeWalker;

namespace WpfVisualTreeMcp.IntegrationTests;

public class AutomationIdTests
{
    private static Button Button(string id, Visibility visibility = Visibility.Visible)
    {
        var button = new Button { Name = "DifferentName", Content = "Different text", Visibility = visibility };
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }

    [Fact]
    public Task Resolves_only_the_exact_case_sensitive_id_including_the_root() => DiagnosticReporterTests.OnSta(() =>
    {
        var root = new StackPanel();
        var target = Button("Save.Order");
        root.Children.Add(Button("save.order"));
        root.Children.Add(Button("Save.Order.Copy"));
        root.Children.Add(target);
        AutomationIdResolver.Resolve("Save.Order", new[] { root }).Should().BeSameAs(target);
        AutomationIdResolver.Resolve("Save.Order", new[] { target, target }).Should().BeSameAs(target);
        var missing = () => AutomationIdResolver.Resolve("Save", new[] { root });
        missing.Should().Throw<InvalidOperationException>().WithMessage("*not found*");
    });

    [Fact]
    public Task Rejects_duplicates_even_if_hidden_and_supports_a_scoped_search() => DiagnosticReporterTests.OnSta(() =>
    {
        var left = new StackPanel();
        var right = new StackPanel();
        var target = Button("save");
        left.Children.Add(target);
        right.Children.Add(Button("save", Visibility.Collapsed));
        var ambiguous = () => AutomationIdResolver.Resolve("save", new[] { left, right });
        ambiguous.Should().Throw<InvalidOperationException>().WithMessage("*Ambiguous*root_handle*");
        AutomationIdResolver.Resolve("save", new[] { left }).Should().BeSameAs(target);
    });

    [Fact]
    public Task Falls_back_to_x_name_like_ui_automation_and_prefers_an_explicit_id() => DiagnosticReporterTests.OnSta(() =>
    {
        var root = new StackPanel();
        var named = new DataGrid { Name = "MainDataGrid" };
        root.Children.Add(named);
        root.Children.Add(new TextBox { Name = "maindatagrid" });
        AutomationIdResolver.Resolve("MainDataGrid", new[] { root }).Should().BeSameAs(named);

        // A second element with only the same name is ambiguous, and the error names the candidates.
        var hidden = new DataGrid { Name = "MainDataGrid", Visibility = Visibility.Collapsed };
        root.Children.Add(hidden);
        var ambiguous = () => AutomationIdResolver.ResolveOrDefault("MainDataGrid", new[] { root }, e => ReferenceEquals(e, hidden) ? "elem_hidden" : "elem_shown");
        ambiguous.Should().Throw<InvalidOperationException>()
            .WithMessage("*Ambiguous*2 elements match by x:Name*DataGrid elem_shown (not visible); DataGrid elem_hidden (not visible)*root_handle*");

        // An explicit AutomationId wins over name-only matches.
        var explicitId = Button("MainDataGrid");
        root.Children.Add(explicitId);
        AutomationIdResolver.Resolve("MainDataGrid", new[] { root }).Should().BeSameAs(explicitId);

        // An element with an explicit id is not reachable by its name.
        var byName = () => AutomationIdResolver.Resolve("DifferentName", new[] { root });
        byName.Should().Throw<InvalidOperationException>().WithMessage("*not found*x:Name*");
    });

    [Fact]
    public Task Names_inside_templates_are_not_ids_and_find_uses_the_same_fallback() => DiagnosticReporterTests.OnSta(() =>
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock)) { Name = "Inner" };
        var list = new ListBox { Name = "Items", ItemsSource = new[] { "a", "b" }, ItemTemplate = new DataTemplate { VisualTree = factory } };
        var window = new Window { Content = list, Width = 200, Height = 200 }; window.Show(); window.UpdateLayout();
        try
        {
            var walker = new TreeWalker();
            int Matches(string id) => System.Text.RegularExpressions.Regex.Matches(
                walker.FindElements(window, new FindCriteria { AutomationId = id }, 100), "\"handle\"").Count;

            Matches("Items").Should().Be(1);
            Matches("Inner").Should().Be(0, "template-internal names repeat per row");
            AutomationIdResolver.ResolveOrDefault("Inner", new[] { (DependencyObject)window }).Should().BeNull();
            AutomationIdResolver.Resolve("Items", new[] { (DependencyObject)window }).Should().BeSameAs(list);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Searches_popup_children_and_deduplicates_overlapping_roots() => DiagnosticReporterTests.OnSta(() =>
    {
        var target = Button("popup.save");
        var popup = new Popup { Child = target };
        AutomationIdResolver.Resolve("popup.save", new DependencyObject[] { popup, target }).Should().BeSameAs(target);
    });

    [Fact]
    public Task Does_not_act_on_an_incomplete_search() => DiagnosticReporterTests.OnSta(() =>
    {
        var roots = Enumerable.Range(0, 10001).Select(_ => (DependencyObject)new Button()).ToArray();
        AutomationProperties.SetAutomationId(roots[0], "save");
        var overBudget = () => AutomationIdResolver.Resolve("save", roots);
        overBudget.Should().Throw<InvalidOperationException>().WithMessage("*uniqueness is unknown*");
    });

    [Fact]
    public Task Query_filters_combine_id_with_other_criteria_and_wait_can_find_it() => DiagnosticReporterTests.OnSta(() =>
    {
        var root = new StackPanel();
        var target = Button("save");
        root.Children.Add(target);
        root.Children.Add(Button("save.copy"));
        root.Children.Add(Button("SAVE"));
        var walker = new TreeWalker();
        var criteria = new FindCriteria { AutomationId = "save", TypeName = "Button", Text = "Different" };
        using var matches = JsonDocument.Parse(walker.FindElements(root, criteria));
        matches.RootElement.GetProperty("elements").GetArrayLength().Should().Be(1);
        walker.FindFirstMatch(criteria, root)!.Value.Handle.Should().Be(walker.GetOrCreateHandle(target));
        criteria.TypeName = "TextBox";
        using var mismatch = JsonDocument.Parse(walker.FindElementsDeep(root, criteria));
        mismatch.RootElement.GetProperty("elements").GetArrayLength().Should().Be(0);
    });
}
