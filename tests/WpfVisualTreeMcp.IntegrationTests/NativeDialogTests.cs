using System.Diagnostics;
using System.Windows;
using FluentAssertions;
using WpfVisualTreeMcp.Inspector;
using Xunit;

namespace WpfVisualTreeMcp.IntegrationTests;

public class NativeDialogTests
{
    [Fact]
    public void Lists_a_message_box_and_answers_it_with_the_chosen_button()
    {
        var title = "Confirm " + Guid.NewGuid().ToString("N");
        MessageBoxResult? result = null;
        var thread = new Thread(() => result = MessageBox.Show("Delete the record?\nThis cannot be undone.", title, MessageBoxButton.YesNo, MessageBoxImage.Question));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            var clock = Stopwatch.StartNew();
            var dialog = NativeDialogs.List().FirstOrDefault(d => d.Title == title);
            while (dialog == null && clock.ElapsedMilliseconds < 10000)
            {
                Thread.Sleep(50);
                dialog = NativeDialogs.List().FirstOrDefault(d => d.Title == title);
            }

            dialog.Should().NotBeNull("the message box is a window of this process");
            dialog!.Text.Should().Contain("Delete the record?").And.Contain("This cannot be undone.");
            dialog.Buttons.Should().HaveCount(2).And.OnlyContain(b => b.IsEnabled && b.Text.Length > 0 && !b.Text.Contains('&'));
            dialog.Buttons.Should().ContainSingle(b => b.IsDefault);

            var wrong = () => NativeDialogs.Answer(dialog.Handle, "no such button");
            wrong.Should().Throw<InvalidOperationException>().WithMessage("*matches 0 buttons*Available: '" + dialog.Buttons[0].Text + "'*");
            thread.IsAlive.Should().BeTrue("a wrong caption presses nothing");

            // Captions follow the Windows display language; the second button of Yes/No is "No".
            NativeDialogs.Answer(dialog.Handle, dialog.Buttons[1].Text.ToUpperInvariant()).Should().Contain("the dialog closed");
            thread.Join(5000).Should().BeTrue();
            result.Should().Be(MessageBoxResult.No);
            NativeDialogs.List().Should().NotContain(d => d.Title == title);
        }
        finally
        {
            if (thread.IsAlive)
            {
                foreach (var open in NativeDialogs.List().Where(d => d.Title == title))
                    NativeDialogs.Answer(open.Handle, open.Buttons[0].Text);
                thread.Join(5000);
            }
        }
    }
}
