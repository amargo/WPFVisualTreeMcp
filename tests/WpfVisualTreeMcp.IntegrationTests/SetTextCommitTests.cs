using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FluentAssertions;
using WpfVisualTreeMcp.Inspector;
using Xunit;

namespace WpfVisualTreeMcp.IntegrationTests;

public class SetTextCommitTests
{
    private sealed class Invoice : INotifyPropertyChanged
    {
        private string _partner = "old";
        public string Partner { get => _partner; set { _partner = value; PropertyChanged?.Invoke(this, new(nameof(Partner))); } }
        public int Quantity { get; set; } = 1;
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static (Window Window, StackPanel Panel) Show(Invoice invoice, params UIElement[] children)
    {
        var panel = new StackPanel { DataContext = invoice };
        foreach (var child in children) panel.Children.Add(child);
        var window = new Window { Content = panel, Width = 300, Height = 200 };
        window.Show(); window.UpdateLayout();
        return (window, panel);
    }

    [Fact]
    public Task Commit_writes_a_lost_focus_binding_and_reports_the_source_value() => DiagnosticReporterTests.OnSta(() =>
    {
        var invoice = new Invoice();
        var box = new TextBox();
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(Invoice.Partner)));
        var (window, _) = Show(invoice, box);
        try
        {
            new ControlInteractor().SetText(box, "Kovács Kft.", physical: false);
            invoice.Partner.Should().Be("old", "a TextBox Text binding updates its source on LostFocus");

            var detail = ControlInteractor.CommitText(box, "Kovács Kft.", physical: false, force: false);
            invoice.Partner.Should().Be("Kovács Kft.");
            detail.Should().Contain("committed").And.Contain("Partner = 'Kovács Kft.'");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Commit_leaves_the_field_like_tab_and_reports_validation_errors() => DiagnosticReporterTests.OnSta(() =>
    {
        var invoice = new Invoice();
        var quantity = new TextBox();
        quantity.SetBinding(TextBox.TextProperty, new Binding(nameof(Invoice.Quantity)) { ValidatesOnExceptions = true });
        var next = new TextBox();
        var (window, _) = Show(invoice, quantity, next);
        try
        {
            window.Activate();
            quantity.Focus().Should().BeTrue();
            quantity.Text = "abc";

            var detail = ControlInteractor.CommitText(quantity, "abc", physical: false, force: false);
            next.IsKeyboardFocused.Should().BeTrue("commit moves focus on as Tab does");
            detail.Should().Contain("focus moved on to TextBox").And.Contain("validation error");
            invoice.Quantity.Should().Be(1);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Commit_enters_and_leaves_the_field_so_the_application_formats_it() => DiagnosticReporterTests.OnSta(() =>
    {
        var invoice = new Invoice();
        // Like KUR's CustomTextBox date field: the text is normalised when the editor loses keyboard focus.
        var date = new TextBox();
        date.SetBinding(TextBox.TextProperty, new Binding(nameof(Invoice.Partner)));
        date.PreviewLostKeyboardFocus += (_, e) => { if (ReferenceEquals(e.OriginalSource, date) && date.Text == "1231") date.Text = "2026.12.31."; };
        var (window, _) = Show(invoice, date, new TextBox());
        try
        {
            window.Activate();
            new ControlInteractor().SetText(date, "1231", physical: false);
            date.IsKeyboardFocusWithin.Should().BeFalse("UI Automation writes without focus");

            var detail = ControlInteractor.CommitText(date, "1231", physical: false, force: false);
            invoice.Partner.Should().Be("2026.12.31.");
            detail.Should().Contain("focus moved on").And.Contain("the field now shows '2026.12.31.'").And.Contain("Partner = '2026.12.31.'");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Commit_reaches_the_binding_of_a_composite_control_through_its_inner_editor() => DiagnosticReporterTests.OnSta(() =>
    {
        var invoice = new Invoice();
        // A wrapper whose own Text is bound to the view model and whose template binds the inner TextBox to it.
        var template = new ControlTemplate(typeof(Wrapper));
        var inner = new FrameworkElementFactory(typeof(TextBox));
        inner.SetBinding(TextBox.TextProperty, new Binding(nameof(Wrapper.Text))
        {
            RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
        });
        template.VisualTree = inner;
        var wrapper = new Wrapper { Template = template };
        wrapper.SetBinding(Wrapper.TextProperty, new Binding(nameof(Invoice.Partner)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });
        var (window, _) = Show(invoice, wrapper);
        try
        {
            var editor = (TextBox)ControlOperations.Descendants(wrapper).First(d => d is TextBox);
            editor.Text = "Inner Bt.";
            invoice.Partner.Should().Be("old");

            var detail = ControlInteractor.CommitText(wrapper, "Inner Bt.", physical: false, force: false);
            invoice.Partner.Should().Be("Inner Bt.", "the inner binding is written first, then the wrapper's");
            detail.Should().Contain("Partner = 'Inner Bt.'").And.NotContain("Text = ", "template plumbing is not reported");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Physical_commit_waits_until_the_typed_text_has_arrived() => DiagnosticReporterTests.OnSta(() =>
    {
        var invoice = new Invoice();
        var box = new TextBox();
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(Invoice.Partner)));
        var (window, _) = Show(invoice, box);
        try
        {
            ControlInteractor.CommitText(box, "typed", physical: true, force: false).Should().BeNull("the keys are not processed yet");
            ControlInteractor.CommitText(box, "typed", physical: true, force: true).Should().Contain("not the typed text");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Text_a_composite_ignores_goes_into_its_inner_text_box_and_an_empty_field_is_never_committed() => DiagnosticReporterTests.OnSta(() =>
    {
        var invoice = new Invoice();
        var template = new ControlTemplate(typeof(DeafInput));
        var inner = new FrameworkElementFactory(typeof(TextBox));
        inner.SetBinding(TextBox.TextProperty, new Binding(nameof(Invoice.Partner)));
        template.VisualTree = inner;
        var input = new DeafInput { Template = template };
        var (window, _) = Show(invoice, input, new TextBox());
        try
        {
            var outcome = new ControlInteractor().SetText(input, "Kovács Kft.", physical: false);
            outcome.Detail.Should().Contain("written into its inner TextBox").And.Contain("value now: 'Kovács Kft.'");
            ControlInteractor.CommitText(input, "Kovács Kft.", physical: false, force: false).Should().Contain("Partner = 'Kovács Kft.'");

            // A field that did not take the text must not overwrite the source with its empty content.
            var editor = (TextBox)ControlOperations.Descendants(input).First(d => d is TextBox);
            editor.Text = string.Empty;
            ControlInteractor.CommitText(input, "Other Bt.", physical: false, force: false).Should().StartWith("not committed");
            invoice.Partner.Should().Be("Kovács Kft.");
        }
        finally { window.Close(); }
    });

    /// <summary>Exposes the Value pattern but drops what is written, like a composite that does not forward it.</summary>
    public sealed class DeafInput : Control
    {
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer, System.Windows.Automation.Provider.IValueProvider
        {
            public Peer(DeafInput owner) : base(owner) { }
            public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface pattern) =>
                pattern == System.Windows.Automation.Peers.PatternInterface.Value ? this : base.GetPattern(pattern);
            public void SetValue(string value) { }
            public string Value => string.Empty;
            public bool IsReadOnly => false;
        }
    }

    public sealed class Wrapper : Control
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(Wrapper));
        public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    }
}
