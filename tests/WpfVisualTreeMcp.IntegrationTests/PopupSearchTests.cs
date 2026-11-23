using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using WpfVisualTreeMcp.Inspector;
using Xunit;
using TreeWalker = WpfVisualTreeMcp.Inspector.TreeWalker;

namespace WpfVisualTreeMcp.IntegrationTests;

public class PopupSearchTests
{
    [Fact]
    public Task An_open_context_menu_is_searchable_and_its_items_can_be_clicked() => DiagnosticReporterTests.OnSta(() =>
    {
        var clicked = 0;
        var item = new MenuItem { Header = "Kiegyenlítés törlése" };
        item.Click += (_, _) => clicked++;
        var menu = new ContextMenu { Items = { new MenuItem { Header = "Másolás" }, item } };
        var window = new Window { Content = new Border { ContextMenu = menu }, Width = 300, Height = 200 };
        window.Show(); window.UpdateLayout();
        try
        {
            var walker = new TreeWalker();
            walker.FindFirstMatch(new FindCriteria { TypeName = "=MenuItem" }).Should().BeNull("a closed context menu is not shown");

            menu.IsOpen = true; menu.UpdateLayout();
            TreeWalker.GetAllSearchRoots().Should().Contain(menu);
            var hit = walker.FindFirstMatch(new FindCriteria { TypeName = "=MenuItem", Text = "törlése", VisibleOnly = true });
            hit.Should().NotBeNull();
            var element = (UIElement)walker.ResolveHandle(hit!.Value.Handle)!;
            element.Should().BeSameAs(item);
            var outcome = new ControlInteractor().Click(element, physical: false);
            // MenuItem raises Click after the next render pass, once the menu has closed.
            var frame = new System.Windows.Threading.DispatcherFrame();
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            clicked.Should().Be(1, outcome.Method + " " + outcome.Detail);
        }
        finally { menu.IsOpen = false; window.Close(); }
    });

    [Fact]
    public Task Template_popups_are_not_listed_again_as_separate_roots() => DiagnosticReporterTests.OnSta(() =>
    {
        var combo = new ComboBox { Items = { "a", "b" } };
        var window = new Window { Content = combo, Width = 300, Height = 200 };
        window.Show(); window.UpdateLayout();
        try
        {
            combo.IsDropDownOpen = true; combo.UpdateLayout();
            TreeWalker.GetDetachedPopupContents().Should().BeEmpty("the drop-down is reached through the window's tree");
        }
        finally { combo.IsDropDownOpen = false; window.Close(); }
    });
}
