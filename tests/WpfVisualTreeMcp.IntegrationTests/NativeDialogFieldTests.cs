using System.Diagnostics;
using FluentAssertions;
using WpfVisualTreeMcp.Inspector;
using WpfVisualTreeMcp.Shared.Models;
using Xunit;

namespace WpfVisualTreeMcp.IntegrationTests;

public class NativeDialogFieldTests
{
    [Fact]
    public void The_file_name_of_a_save_dialog_can_be_written_and_saved_without_input()
    {
        var title = "Save test " + Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), "dialog-" + Guid.NewGuid().ToString("N") + ".txt");
        bool? result = null;
        string? chosen = null;
        var thread = new Thread(() =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = "Text|*.txt" };
            result = dialog.ShowDialog();
            chosen = dialog.FileName;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            NativeDialogInfo? open = null;
            var clock = Stopwatch.StartNew();
            while (open == null && clock.ElapsedMilliseconds < 10000)
            {
                open = NativeDialogs.List().FirstOrDefault(d => d.Title == title);
                if (open == null) Thread.Sleep(100);
            }
            open.Should().NotBeNull("the save dialog opens");
            open!.Fields.Should().NotBeEmpty("the file name box is an editable field");

            // The file name box is the field labelled like the localized "File name".
            var name = open.Fields.Single(f => f.IsEnabled && f.Text.Length == 0 || f.Label.Length > 0 && open.Fields.Count == 1);
            NativeDialogs.SetField(open.Handle, name.Id.ToString(), path).Should().Contain(path);

            var save = open.Buttons.Single(b => b.Id == 1);
            NativeDialogs.Answer(open.Handle, save.Text).Should().Contain("the dialog closed");
            thread.Join(5000).Should().BeTrue();
            result.Should().BeTrue();
            chosen.Should().BeEquivalentTo(path);
        }
        finally
        {
            if (thread.IsAlive)
                foreach (var dialog in NativeDialogs.List().Where(d => d.Title == title))
                    NativeDialogs.Answer(dialog.Handle, dialog.Buttons.First(b => b.Id == 2).Text);
        }
    }
}
