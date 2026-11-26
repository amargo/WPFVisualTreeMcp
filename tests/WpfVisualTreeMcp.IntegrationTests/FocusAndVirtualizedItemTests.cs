using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FluentAssertions;
using WpfVisualTreeMcp.Inspector;
using Xunit;

namespace WpfVisualTreeMcp.IntegrationTests;

public class FocusAndVirtualizedItemTests
{
    public sealed class Voucher
    {
        public string Caption { get; set; } = string.Empty;
        public int Year { get; set; }
    }

    [Fact]
    public Task Physical_text_is_never_typed_while_focus_is_outside_the_target() => DiagnosticReporterTests.OnSta(() =>
    {
        var other = new TextBox { Name = "FocusedRefer" };
        // A composite whose editor hands focus away, so typed keys would land in the other field.
        var template = new ControlTemplate(typeof(Control));
        template.VisualTree = new FrameworkElementFactory(typeof(TextBox));
        var composite = new Control { Template = template, Focusable = true };
        var panel = new StackPanel { Children = { other, composite } };
        var window = new Window { Content = panel, Width = 300, Height = 200 };
        window.Show(); window.UpdateLayout();
        try
        {
            var editor = (TextBox)ControlOperations.Descendants(composite).First(d => d is TextBox);
            editor.GotKeyboardFocus += (_, _) => other.Focus();
            editor.Focusable = true;
            window.Activate();
            other.Focus();

            var typing = () => new ControlInteractor().SetText(composite, "SQLite", physical: true);
            typing.Should().Throw<InvalidOperationException>().WithMessage("*FocusedRefer*nothing was typed*");
            other.Text.Should().BeEmpty();
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Virtualized_items_are_matched_against_their_data() => DiagnosticReporterTests.OnSta(() =>
    {
        var items = Enumerable.Range(0, 500).Select(i => new Voucher { Caption = $"B{i:000000}", Year = 2026 }).ToList();
        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Voucher.Caption)));
        template.VisualTree = text;
        var list = new ListBox { ItemsSource = items, ItemTemplate = template, Height = 120 };
        var window = new Window { Content = list, Width = 300, Height = 200 };
        window.Show(); window.UpdateLayout();
        try
        {
            list.ItemContainerGenerator.ContainerFromIndex(409).Should().BeNull("the item starts virtualized");
            var outcome = new ControlInteractor().SelectItem(list, "B000409", null);
            list.SelectedIndex.Should().Be(409);
            outcome.Detail.Should().Contain("Caption: B000409");

            var missing = () => new ControlInteractor().SelectItem(list, "nope", null);
            missing.Should().Throw<InvalidOperationException>().WithMessage("*virtualized*matched against their data*");
        }
        finally { window.Close(); }
    });
}
