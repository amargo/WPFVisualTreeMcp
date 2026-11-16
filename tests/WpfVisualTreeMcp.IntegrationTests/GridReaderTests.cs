using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using FluentAssertions;
using WpfVisualTreeMcp.Inspector;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;
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
    public Task Combo_columns_show_the_display_text_for_a_bound_key() => DiagnosticReporterTests.OnSta(() =>
    {
        var grid = CreateGrid(3);
        var journals = new[] { new Journal { Key = 0, Label = "Vevő" }, new Journal { Key = 1, Label = "Szállító" } };
        grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Journal", ItemsSource = journals, SelectedValuePath = "Key", DisplayMemberPath = "Label",
            SelectedValueBinding = new Binding("Id")
        });
        // Items supplied elsewhere (for example by a style): the bound value is all the column knows.
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "Raw", SelectedValueBinding = new Binding("Id") });

        var result = GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h", Columns = new() { "Journal", "Raw" } }, new TreeWalker());

        result.Rows.Select(r => r.Cells[0]).Should().Equal("Vevő", "Szállító", "2");
        result.Rows.Select(r => r.Cells[1]).Should().Equal("0", "1", "2");
    });

    public sealed class Journal
    {
        public int Key { get; set; }
        public string Label { get; set; } = "";
    }

    [Fact]
    public Task Reads_a_list_view_with_a_grid_view_like_a_grid() => DiagnosticReporterTests.OnSta(() =>
    {
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Id", DisplayMemberBinding = new Binding("Id") });
        view.Columns.Add(new GridViewColumn { Header = "Amount", DisplayMemberBinding = new Binding("Amount") { StringFormat = "N2" } });
        view.Columns.Add(new GridViewColumn { Header = "Templated", CellTemplate = Template("Name") });
        var list = new ListView
        {
            View = view, Height = 120, Language = XmlLanguage.GetLanguage("hu-HU"),
            ItemsSource = Enumerable.Range(0, 300).Select(i => new Row { Id = i, Name = "name" + i, Amount = i + 0.5m }).ToArray()
        };
        var panel = new StackPanel(); panel.Children.Add(list);
        var window = new Window { Content = panel, Width = 400, Height = 200 }; window.Show(); window.UpdateLayout();
        try
        {
            list.SelectedIndex = 2;
            var result = GridReader.Read(panel, new ReadGridRequest { ElementHandle = "h", Offset = 0, Limit = 500 }, new TreeWalker());

            result.RowCount.Should().Be(300);
            result.Columns.Select(c => c.Header).Should().Equal("Id", "Amount", "Templated");
            result.Columns.Select(c => c.ValueSource).Should().Equal("binding", "binding", "visual");
            result.Columns[1].BindingPath.Should().Be("Amount");
            result.Rows[0].Cells.Should().Equal("0", 0.5m.ToString("N2", CultureInfo.GetCultureInfo("hu-HU")), "name0");
            // Off-screen rows: bound columns come from the data, the template column has no visual.
            result.Rows[299].Cells.Should().Equal("299", 299.5m.ToString("N2", CultureInfo.GetCultureInfo("hu-HU")), null);
            result.Note.Should().Contain("visual");
            result.SelectedIndices.Should().Equal(2);

            var some = GridReader.Read(list, new ReadGridRequest { ElementHandle = "h", Offset = 298, Limit = 10, Columns = new() { "Amount", "Id" } }, new TreeWalker());
            some.Returned.Should().Be(2);
            some.HasMore.Should().BeFalse();
            some.Rows[1].Cells[1].Should().Be("299");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Select_item_on_a_cell_selection_grid_selects_a_cell_of_the_row() => DiagnosticReporterTests.OnSta(() =>
    {
        var grid = CreateGrid(50);
        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        var window = new Window { Content = grid, Width = 400, Height = 200 }; window.Show(); window.UpdateLayout();
        try
        {
            var interactor = new ControlInteractor();

            // WPF throws when a row is selected on such a grid; a cell of the row is selected instead.
            var byIndex = interactor.SelectItem(grid, null, 40);
            byIndex.Method.Should().Be("DataGrid.SelectedCells");
            byIndex.Detail.Should().Contain("SelectionUnit=Cell").And.Contain("row 40").And.Contain("column 'Id'").And.Contain("name40");
            grid.SelectedCells.Should().ContainSingle().Which.Item.Should().BeSameAs(grid.Items[40]);
            grid.CurrentCell.Item.Should().BeSameAs(grid.Items[40]);

            interactor.SelectItem(grid, "name7", null);
            grid.SelectedCells.Should().ContainSingle().Which.Item.Should().BeSameAs(grid.Items[7]);
            ControlOperations.Descendants(grid).OfType<DataGridCell>().Should().NotContain(c => c.IsEditing, "selecting must not start an edit");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Header_tooltip_is_reported_apart_and_deep_template_cells_are_read() => DiagnosticReporterTests.OnSta(() =>
    {
        var grid = CreateGrid(3);
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = new TextBlock { Text = "Stair", ToolTip = "Code of the staircase." }, Binding = new Binding("Name")
        });
        // Text nested deeper than the shallow searchable-text scan, in two parts.
        var deep = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border><Grid><Border><Grid><Border><StackPanel Orientation='Horizontal'>"
            + "<TextBlock Text='{Binding Id}'/><TextBlock Text='{Binding Name}'/></StackPanel></Border></Grid></Border></Grid></Border></DataTemplate>");
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Deep", CellTemplate = deep });
        var window = new Window { Content = grid, Width = 700, Height = 300 }; window.Show(); window.UpdateLayout();
        try
        {
            var result = GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h", Offset = 0, Limit = 10, Columns = new() { "Stair", "Deep" } }, new TreeWalker());

            result.Columns[0].Header.Should().Be("Stair");
            result.Columns[0].HeaderToolTip.Should().Be("Code of the staircase.");
            result.Columns[1].HeaderToolTip.Should().BeNull();
            result.Rows[1].Cells.Should().Equal("name1", "1 name1");

            // The caption with its tooltip appended, as older versions reported it, still selects the column.
            GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h", Offset = 0, Limit = 1, Columns = new() { "Stair Code of the staircase." } }, new TreeWalker())
                .Columns.Single().Header.Should().Be("Stair");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Physical_typing_focuses_the_editor_inside_a_wrapper_that_cannot_take_focus() => DiagnosticReporterTests.OnSta(() =>
    {
        var box = new TextBox();
        var wrapper = new ContentControl { Focusable = false, Content = new Border { Child = box } };
        var other = new Button { Content = "other" };
        var panel = new StackPanel(); panel.Children.Add(other); panel.Children.Add(wrapper);
        var window = new Window { Content = panel, Width = 300, Height = 200 }; window.Show(); window.UpdateLayout();
        try
        {
            other.Focus();
            new ControlInteractor().EnsureKeyboardFocusWithin(wrapper).Should().BeTrue();
            box.IsKeyboardFocused.Should().BeTrue();
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Repeated_headers_are_selected_by_index_name_and_the_error_says_so() => DiagnosticReporterTests.OnSta(() =>
    {
        var grid = CreateGrid(3);
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding("Amount") });
        var window = new Window { Content = grid, Width = 500, Height = 200 }; window.Show(); window.UpdateLayout();
        try
        {
            var duplicate = () => GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h", Limit = 1, Columns = new() { "Name" } }, new TreeWalker());
            duplicate.Should().Throw<ArgumentException>().WithMessage("*matches 2 columns*'#1' (binding path 'Name')*'#4' (binding path 'Amount')*'#index'*");

            var byIndex = GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h", Limit = 1, Columns = new() { "#4", "#0" } }, new TreeWalker());
            byIndex.Columns.Select(c => c.BindingPath).Should().Equal("Amount", "Id");

            var outside = () => GridReader.Read(grid, new ReadGridRequest { ElementHandle = "h", Limit = 1, Columns = new() { "#9" } }, new TreeWalker());
            outside.Should().Throw<ArgumentException>().WithMessage("*matches 0 columns*");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Cell_edit_reports_the_value_of_the_data_item() => DiagnosticReporterTests.OnSta(() =>
    {
        var grid = CreateGrid(3);
        grid.IsReadOnly = false;
        var window = new Window { Content = grid, Width = 500, Height = 200 }; window.Show(); window.UpdateLayout();
        try
        {
            var result = new ControlOperations(new TreeWalker()).Execute(grid, new ControlOperationRequest
            {
                ElementHandle = "h", Operation = "grid_edit", Value = "renamed", Row = ItemLocator.At(1), Column = ColumnLocator.At(1)
            });

            result.Result.Changed.Should().BeTrue();
            result.Result.Value.Should().Be("renamed");
            ((Row)grid.Items[1]).Name.Should().Be("renamed");
        }
        finally { window.Close(); }
    });

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
