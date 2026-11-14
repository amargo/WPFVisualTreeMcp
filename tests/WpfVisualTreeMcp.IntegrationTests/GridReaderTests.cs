using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using FluentAssertions;
using WpfVisualTreeMcp.Inspector;
using WpfVisualTreeMcp.Shared.Ipc;
using Xunit;

namespace WpfVisualTreeMcp.IntegrationTests;

public class GridReaderTests
{
    private static DataGrid CreateGrid(int rows = 500)
    {
        var data = Enumerable.Range(0, rows)
            .Select(i => new Row { Id = i, Name = "name" + i, Amount = i + 0.5m, Paid = i % 2 == 0 }).ToArray();
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, ItemsSource = data, EnableRowVirtualization = true, CanUserAddRows = false,
            Language = XmlLanguage.GetLanguage("hu-HU")
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Id", Binding = new Binding("Id") });
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding("Name") });
        grid.Columns.Add(new DataGridTextColumn { Header = "Amount", Binding = new Binding("Amount") { StringFormat = "N2" } });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Paid", Binding = new Binding("Paid") });
        return grid;
    }

    [Fact]
    public Task Reads_virtualized_rows_from_data_without_scrolling_or_realizing_them() => DiagnosticReporterTests.OnSta(() =>
    {
        var grid = CreateGrid();
        var window = new Window { Content = grid, Width = 400, Height = 200 }; window.Show(); window.UpdateLayout();
        try
        {
            grid.ItemContainerGenerator.ContainerFromIndex(490).Should().BeNull("the row starts virtualized");
            var result = GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h", Offset = 490, Limit = 50 }, new TreeWalker());

            result.RowCount.Should().Be(500);
            result.Returned.Should().Be(10);
            result.HasMore.Should().BeFalse();
            result.Columns.Select(c => c.Header).Should().Equal("Id", "Name", "Amount", "Paid");
            result.Columns.Should().OnlyContain(c => c.ValueSource == "binding");
            result.Rows[0].Index.Should().Be(490);
            // StringFormat is applied in the grid's language (hu-HU decimal comma), like the visible cell.
            result.Rows[0].Cells.Should().Equal("490", "name490", 490.5m.ToString("N2", CultureInfo.GetCultureInfo("hu-HU")), "True");
            result.Note.Should().BeNull();
            grid.ItemContainerGenerator.ContainerFromIndex(490).Should().BeNull("reading must not scroll or realize rows");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Pages_follow_the_sorted_view_and_report_selection() => DiagnosticReporterTests.OnSta(() =>
    {
        var grid = CreateGrid(20);
        var window = new Window { Content = grid, Width = 400, Height = 200 }; window.Show(); window.UpdateLayout();
        try
        {
            grid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("Id", System.ComponentModel.ListSortDirection.Descending));
            grid.SelectedIndex = 1;
            var result = GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h", Offset = 0, Limit = 3 }, new TreeWalker());

            result.HasMore.Should().BeTrue();
            result.Rows.Select(r => r.Cells[0]).Should().Equal("19", "18", "17");
            result.Rows.Select(r => r.Index).Should().Equal(0, 1, 2);
            result.SelectedIndices.Should().Equal(1);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Column_filter_uses_exact_header_or_binding_path_and_respects_display_order_by_default() => DiagnosticReporterTests.OnSta(() =>
    {
        var grid = CreateGrid(5);
        grid.Columns[1].DisplayIndex = 0;
        grid.Columns[3].Visibility = Visibility.Collapsed;
        var walker = new TreeWalker();

        var all = GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h" }, walker);
        all.Columns.Select(c => c.Header).Should().Equal("Name", "Id", "Amount");
        all.Columns.Select(c => c.Index).Should().Equal(1, 0, 2);

        var some = GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h", Limit = 1, Columns = new() { "Paid", "Name" } }, walker);
        some.Rows[0].Cells.Should().Equal("True", "name0");

        var unknown = () => GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h", Columns = new() { "name" } }, walker);
        unknown.Should().Throw<ArgumentException>().WithMessage("*'name' matches 0 columns*'Name'*");
    });

    [Fact]
    public Task Template_columns_use_sort_member_path_or_realized_cell_text_and_skip_the_new_item_placeholder() => DiagnosticReporterTests.OnSta(() =>
    {
        var data = new System.Collections.ObjectModel.ObservableCollection<Row>(
            Enumerable.Range(0, 300).Select(i => new Row { Id = i, Name = "name" + i }));
        var grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = data, CanUserAddRows = true, EnableRowVirtualization = true, Height = 120 };
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Sorted", SortMemberPath = "Id", CellTemplate = Template("Name") });
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Visual", CellTemplate = Template("Name") });
        var panel = new StackPanel(); panel.Children.Add(grid);
        var window = new Window { Content = panel, Width = 400, Height = 200 }; window.Show(); window.UpdateLayout();
        try
        {
            // A container holding exactly one DataGrid is accepted.
            var result = GridReader.Read(panel, new ReadGridRequest { ElementHandle = "h", Offset = 0, Limit = 500 }, new TreeWalker());

            result.RowCount.Should().Be(300, "the new-item placeholder is not a data row");
            result.Columns.Select(c => c.ValueSource).Should().Equal("sortMemberPath", "visual");
            result.Columns.Select(c => c.SortMemberPath).Should().Equal("Id", null);
            result.Rows[0].Cells.Should().Equal("0", "name0");
            result.Rows[299].Cells.Should().Equal("299", null);
            result.Note.Should().Contain("visual");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Rejects_targets_without_a_single_grid() => DiagnosticReporterTests.OnSta(() =>
    {
        var none = () => GridReader.Read(new Button(), new ReadGridRequest { ElementHandle = "h" }, new TreeWalker());
        none.Should().Throw<ArgumentException>().WithMessage("*Expected a DataGrid*");
    });

    [Fact]
    public Task Select_item_matches_grid_rows_by_cell_text_including_virtualized_rows() => DiagnosticReporterTests.OnSta(() =>
    {
        var grid = CreateGrid();
        var window = new Window { Content = grid, Width = 400, Height = 200 }; window.Show(); window.UpdateLayout();
        try
        {
            grid.ItemContainerGenerator.ContainerFromIndex(473).Should().BeNull("the row starts virtualized");
            var interactor = new ControlInteractor();

            // Row.ToString() is the type name; the visible cell text must be what matches.
            var outcome = interactor.SelectItem(grid, "NAME473", null);
            grid.SelectedIndex.Should().Be(473);
            outcome.Detail.Should().Contain("selected row 473").And.Contain("name473").And.Contain("row is current");
            // Like a user's click: the row is scrolled into view and becomes the current row,
            // so a following Enter acts on it.
            grid.CurrentCell.Item.Should().BeSameAs(grid.Items[473]);
            grid.ItemContainerGenerator.ContainerFromIndex(473).Should().NotBeNull();

            // A realized row is searchable by its cell text.
            int Rows(string text) => System.Text.RegularExpressions.Regex.Matches(
                new TreeWalker().FindElements(window, new FindCriteria { TypeName = "=DataGridRow", Text = text }, 100), "\"handle\"").Count;
            Rows("name473").Should().Be(1);
            Rows("no such text").Should().Be(0);

            // Formatted cell text (hu-HU decimal comma) is searchable, not just raw property values.
            interactor.SelectItem(grid, 12.5m.ToString("N2", CultureInfo.GetCultureInfo("hu-HU")), null);
            grid.SelectedIndex.Should().Be(12);

            interactor.SelectItem(grid, null, 7).Detail.Should().Contain("name7");

            var missing = () => interactor.SelectItem(grid, "no such text", null);
            missing.Should().Throw<InvalidOperationException>().WithMessage("*No row with a cell containing 'no such text'*wpf_read_grid*");
            grid.SelectedIndex.Should().Be(7, "a failed search must not change the selection");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Whole_item_bindings_report_a_dot_path_and_their_converter() => DiagnosticReporterTests.OnSta(() =>
    {
        var grid = CreateGrid(3);
        grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding { Converter = new StatusConverter() } });

        var result = GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h", Columns = new() { "Status", "Id" } }, new TreeWalker());

        result.Columns[0].ValueSource.Should().Be("binding");
        result.Columns[0].BindingPath.Should().Be(".");
        result.Columns[0].Converter.Should().Be(nameof(StatusConverter));
        result.Columns[1].BindingPath.Should().Be("Id");
        result.Columns[1].Converter.Should().BeNull();
        result.Rows.Select(r => r.Cells[0]).Should().Equal("row 0", "row 1", "row 2");

        var multi = new MultiBinding { Converter = new JoinConverter() };
        multi.Bindings.Add(new Binding("Id")); multi.Bindings.Add(new Binding("Name"));
        grid.Columns.Add(new DataGridTextColumn { Header = "Both", Binding = multi });
        var both = GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h", Limit = 1, Columns = new() { "Both" } }, new TreeWalker());
        both.Columns[0].BindingPath.Should().Be("Id+Name");
        both.Columns[0].Converter.Should().Be(nameof(JoinConverter));
        both.Rows[0].Cells.Should().Equal("0/name0");
        // The converter name must survive the IPC round trip to the server.
        IpcSerializer.Serialize(new ReadGridResponse { Result = both }).Should().Contain("JoinConverter");
    });

    private sealed class JoinConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => string.Join("/", values);
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    [Fact]
    public void Exact_type_filter_matches_the_type_and_subclasses_but_not_name_substrings()
    {
        TreeWalker.IsOrDerivesFrom(typeof(DataGrid), "DataGrid").Should().BeTrue();
        TreeWalker.IsOrDerivesFrom(typeof(DerivedGrid), "datagrid").Should().BeTrue("subclasses such as vendor-derived grids are grids");
        TreeWalker.IsOrDerivesFrom(typeof(DerivedGrid), "System.Windows.Controls.DataGrid").Should().BeTrue();
        TreeWalker.IsOrDerivesFrom(typeof(DataGridCell), "DataGrid").Should().BeFalse();
        TreeWalker.IsOrDerivesFrom(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), "DataGrid").Should().BeFalse();
    }

    [Fact]
    public Task Find_with_an_equals_prefix_returns_only_grids() => DiagnosticReporterTests.OnSta(() =>
    {
        var panel = new StackPanel();
        panel.Children.Add(new DerivedGrid { ItemsSource = new[] { new Row() }, Height = 100 });
        var window = new Window { Content = panel, Width = 400, Height = 200 }; window.Show(); window.UpdateLayout();
        try
        {
            var walker = new TreeWalker();
            int Matches(string typeName) => System.Text.RegularExpressions.Regex.Matches(
                walker.FindElements(panel, new FindCriteria { TypeName = typeName }, 100), "\"handle\"").Count;

            Matches("DataGrid").Should().BeGreaterThan(1, "the substring also matches cells, headers and panels");
            Matches("=DataGrid").Should().Be(1);
        }
        finally { window.Close(); }
    });

    private sealed class DerivedGrid : DataGrid { }

    private sealed class StatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => "row " + ((Row)value).Id;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private static DataTemplate Template(string path)
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new Binding(path));
        return new DataTemplate { VisualTree = factory };
    }

    public sealed class Row
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Amount { get; set; }
        public bool Paid { get; set; }
    }
}
