using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using WpfVisualTreeMcp.Shared.Models;

namespace WpfVisualTreeMcp.Inspector;

/// <summary>
/// Lists and answers native Win32 dialogs (MessageBox and other #32770 windows) of this process.
/// They are not part of the WPF visual tree, and while one is open the code that opened it is
/// blocked on the UI thread, so everything here is plain Win32 and never touches the Dispatcher.
/// </summary>
internal static class NativeDialogs
{
    private const string DialogClass = "#32770";

    internal static List<NativeDialogInfo> List()
    {
        var processId = (uint)Process.GetCurrentProcess().Id;
        var foreground = GetForegroundWindow();
        var dialogs = new List<NativeDialogInfo>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == processId && IsWindowVisible(window) && ClassOf(window) == DialogClass)
                dialogs.Add(Describe(window, foreground));
            return true;
        }, IntPtr.Zero);
        return dialogs;
    }

    /// <summary>Presses one push button of a dialog and waits briefly for the dialog to close.</summary>
    internal static string Answer(long? dialogHandle, string button)
    {
        if (string.IsNullOrWhiteSpace(button)) throw new ArgumentException("A button text is required.");
        var dialogs = List();
        if (dialogs.Count == 0) throw new InvalidOperationException("The application has no native dialog open.");
        NativeDialogInfo dialog;
        if (dialogHandle is long handle)
            dialog = dialogs.FirstOrDefault(d => d.Handle == handle)
                ?? throw new InvalidOperationException($"No open native dialog has handle {handle}. Open dialogs: {Summary(dialogs)}");
        else if (dialogs.Count == 1) dialog = dialogs[0];
        else throw new InvalidOperationException($"{dialogs.Count} native dialogs are open; pass dialog_handle. Open dialogs: {Summary(dialogs)}");

        var wanted = Caption(button);
        var matches = dialog.Buttons.Where(b => string.Equals(b.Text, wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Button '{button}' matches {matches.Length} buttons of dialog '{dialog.Title}'. Available: "
                + string.Join(", ", dialog.Buttons.Select(b => "'" + b.Text + "'")));
        if (!matches[0].IsEnabled) throw new InvalidOperationException($"Button '{matches[0].Text}' is disabled.");

        // Posted, not sent: the handler may open another dialog and would otherwise block this call.
        var window = new IntPtr(dialog.Handle);
        var control = new IntPtr(matches[0].Handle);
        if (!PostMessage(GetParent(control), WM_COMMAND, new IntPtr(matches[0].Id & 0xFFFF), control))
            throw new InvalidOperationException("Windows refused the button message (error " + Marshal.GetLastWin32Error() + ").");

        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 2000)
        {
            if (!IsWindow(window) || !IsWindowVisible(window))
                return $"pressed '{matches[0].Text}' on '{dialog.Title}'; the dialog closed";
            Thread.Sleep(20);
        }
        return $"pressed '{matches[0].Text}' on '{dialog.Title}'; the dialog is still open after 2 s";
    }

    private static string Summary(IEnumerable<NativeDialogInfo> dialogs) =>
        string.Join("; ", dialogs.Select(d => $"{d.Handle} '{d.Title}'"));

    private static NativeDialogInfo Describe(IntPtr window, IntPtr foreground)
    {
        var info = new NativeDialogInfo
        {
            Handle = window.ToInt64(), Title = TextOf(window), IsForeground = window == foreground
        };
        var owner = GetWindow(window, GW_OWNER);
        if (owner != IntPtr.Zero) info.OwnerTitle = TextOf(owner);
        var texts = new List<string>();
        EnumChildWindows(window, (child, _) =>
        {
            if (!IsWindowVisible(child)) return true;
            var type = ClassOf(child);
            var text = TextOf(child);
            var style = GetWindowLong(child, GWL_STYLE);
            if (type == "Button")
            {
                // Push buttons only: check boxes, radio buttons and group boxes do not answer a dialog.
                var kind = style & 0xF;
                if (kind is BS_PUSHBUTTON or BS_DEFPUSHBUTTON or BS_COMMANDLINK or BS_DEFCOMMANDLINK)
                    info.Buttons.Add(new NativeDialogButton
                    {
                        Handle = child.ToInt64(), Id = GetDlgCtrlID(child), Text = Caption(text),
                        IsEnabled = IsWindowEnabled(child), IsDefault = kind is BS_DEFPUSHBUTTON or BS_DEFCOMMANDLINK
                    });
            }
            else if ((type == "Static" || type == "Edit" || type == "RICHEDIT50W") && !string.IsNullOrWhiteSpace(text))
                texts.Add(text.Trim());
            return true;
        }, IntPtr.Zero);
        info.Text = string.Join("\n", texts);
        if (info.Text.Length == 0 && info.Buttons.Count > 0)
            info.Note = "The dialog draws its message itself (task dialog); only the title and buttons can be read. Use a screen-mode screenshot to see the message.";
        return info;
    }

    /// <summary>A button caption without its access-key marker ("&amp;Yes" reads "Yes").</summary>
    private static string Caption(string text) => text.Replace("&&", "\u0001").Replace("&", string.Empty).Replace("\u0001", "&").Trim();

    private static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    private static string TextOf(IntPtr window)
    {
        // WM_GETTEXT with a timeout: GetWindowText does not read controls, and a hung thread must not hang this one.
        if (SendMessageTimeout(window, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out var length) == IntPtr.Zero)
            return string.Empty;
        var size = Math.Min(length.ToInt32(), 8192) + 1;
        if (size <= 1) return string.Empty;
        var text = new StringBuilder(size);
        return SendMessageTimeoutText(window, WM_GETTEXT, new IntPtr(size), text, SMTO_ABORTIFHUNG, 500, out _) == IntPtr.Zero
            ? string.Empty : text.ToString();
    }

    private const int GWL_STYLE = -16;
    private const uint GW_OWNER = 4;
    private const uint WM_COMMAND = 0x0111, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int BS_PUSHBUTTON = 0x0, BS_DEFPUSHBUTTON = 0x1, BS_COMMANDLINK = 0xE, BS_DEFCOMMANDLINK = 0xF;

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint relation);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutText(IntPtr window, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
}
