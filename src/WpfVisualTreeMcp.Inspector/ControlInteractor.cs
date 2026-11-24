using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using WpfVisualTreeMcp.Shared.Models;

namespace WpfVisualTreeMcp.Inspector;

/// <summary>
/// Performs interaction (clicks, text input, keyboard shortcuts) on WPF elements.
///
/// UI Automation patterns are the default mechanism — they invoke the control's
/// action directly, raise proper events, need no window focus, and do not move the
/// mouse. An optional physical mode drives the real OS mouse/keyboard for elements
/// that have no automation pattern but must still be driven at the OS input level.
///
/// All methods must be called on the UI dispatcher thread.
/// </summary>
internal sealed class ControlInteractor
{
    /// <summary>Describes how an interaction was carried out.</summary>
    public readonly struct InteractionOutcome
    {
        public InteractionOutcome(string method, string? detail)
        {
            Method = method;
            Detail = detail;
        }

        /// <summary>The mechanism used (Invoke, Toggle, Physical, ValueProvider.SetValue, ...).</summary>
        public string Method { get; }

        /// <summary>Optional extra detail (resulting toggle state, click coordinates, key combo, ...).</summary>
        public string? Detail { get; }
    }

    // ---------------------------------------------------------------------
    // Click
    // ---------------------------------------------------------------------

    /// <summary>
    /// Clicks the given element. When <paramref name="physical"/> is false (default)
    /// the control's action is invoked via UI Automation; when true, a real OS mouse
    /// click is performed at the element's on-screen centre.
    /// <paramref name="clickType"/> selects single (default), "double" or "right" —
    /// double and right clicks are OS-level notions, so they always use the physical path.
    /// </summary>
    public InteractionOutcome Click(UIElement element, bool physical, string? clickType = null, double? x = null, double? y = null)
    {
        MousePointerOptions.ValidatePoint(x, y);
        EnsureInteractable(element, "clicked");

        var type = string.IsNullOrEmpty(clickType) ? "single" : clickType!.ToLowerInvariant();
        if (type != "single" && type != "double" && type != "right")
            throw new ArgumentException($"Unknown click_type '{clickType}'. Expected: single, double, or right.");

        if (type != "single" || x.HasValue)
            return PhysicalClick(element, type, x, y);

        return physical ? PhysicalClick(element, type) : AutomationClick(element);
    }

    private static InteractionOutcome AutomationClick(UIElement element)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        if (peer != null)
        {
            if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
            {
                invoke.Invoke();
                return new InteractionOutcome("Invoke", null);
            }

            if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
            {
                toggle.Toggle();
                return new InteractionOutcome("Toggle", $"new toggle state: {toggle.ToggleState}");
            }

            if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selectionItem)
            {
                selectionItem.Select();
                return new InteractionOutcome("SelectionItem.Select", null);
            }

            if (peer.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expandCollapse)
            {
                if (expandCollapse.ExpandCollapseState == ExpandCollapseState.Collapsed)
                {
                    expandCollapse.Expand();
                    return new InteractionOutcome("ExpandCollapse.Expand", null);
                }

                expandCollapse.Collapse();
                return new InteractionOutcome("ExpandCollapse.Collapse", null);
            }
        }

        return SyntheticMouseClick(element);
    }

    private static InteractionOutcome SyntheticMouseClick(UIElement element)
    {
        var device = Mouse.PrimaryDevice;
        var timestamp = Environment.TickCount;

        void Raise(RoutedEvent routedEvent)
        {
            element.RaiseEvent(new MouseButtonEventArgs(device, timestamp, MouseButton.Left)
            {
                RoutedEvent = routedEvent,
                Source = element,
            });
        }

        Raise(UIElement.PreviewMouseLeftButtonDownEvent);
        Raise(UIElement.MouseLeftButtonDownEvent);
        Raise(UIElement.PreviewMouseLeftButtonUpEvent);
        Raise(UIElement.MouseLeftButtonUpEvent);

        return new InteractionOutcome(
            "SyntheticMouse",
            "element exposes no UI Automation pattern; raised routed mouse events (best-effort)");
    }

    private static InteractionOutcome PhysicalClick(UIElement element, string clickType = "single", double? localX = null, double? localY = null)
    {
        var plan = PreparePointer(element, localX, localY);
        var input = new NativeMouseInput();
        input.ValidateStart(plan);
        input.Move(plan.StartX, plan.StartY);
        var button = clickType == "right" ? "right" : "left";
        for (int i = 0; i < (clickType == "double" ? 2 : 1); i++)
        {
            input.VerifyForeground();
            try { input.Press(button); }
            finally { input.Release(button); }
        }
        var method = clickType == "double" ? "Physical.DoubleClick" : clickType == "right" ? "Physical.RightClick" : "Physical";
        return new InteractionOutcome(method, $"OS mouse {clickType} click at screen ({plan.StartX},{plan.StartY})");
    }

    internal static MouseDragPlan PreparePointer(UIElement element, double? x, double? y)
    {
        MousePointerOptions.ValidatePoint(x, y);
        EnsureInteractable(element, "used for pointer input");
        BringToForeground(Window.GetWindow(element));
        ScrollIntoView(element);
        var screen = element.PointToScreen(LocalPoint(element, x, y));
        EnsureHit(element, screen);
        int sx = checked((int)Math.Round(screen.X)), sy = checked((int)Math.Round(screen.Y));
        var window = (PresentationSource.FromVisual(element) as System.Windows.Interop.HwndSource)?.Handle ?? IntPtr.Zero;
        return new MouseDragPlan(sx, sy, sx, sy, "left", 0, element.GetType().Name, window, window);
    }

    /// <summary>
    /// Scrolls the element into its ScrollViewer's viewport (if any) and forces a
    /// layout pass, so PointToScreen returns on-screen coordinates.
    /// </summary>
    private static void ScrollIntoView(UIElement element)
    {
        if (element is FrameworkElement fe)
        {
            fe.BringIntoView();
            fe.UpdateLayout();
        }
    }

    // ---------------------------------------------------------------------
    // Drag preparation (UI thread only; the sequence runs on a worker).
    // ---------------------------------------------------------------------

    public MouseDragPlan PrepareDrag(UIElement source, UIElement? target, DragOptions options)
    {
        options.Validate();
        EnsureInteractable(source, "dragged");
        if (target != null) EnsureInteractable(target, "used as a drag target");
        BringToForeground(Window.GetWindow(source));
        // Scroll before resolving either endpoint: bringing one element into view
        // can move the other. Both must still pass hit testing after layout.
        if (target != null) ScrollIntoView(target);
        ScrollIntoView(source);

        var localStart = LocalPoint(source, options.StartX, options.StartY);
        var start = source.PointToScreen(localStart);
        var end = target != null
            ? target.PointToScreen(LocalPoint(target, options.TargetX, options.TargetY))
            : source.PointToScreen(new Point(localStart.X + options.DeltaX.GetValueOrDefault(),
                localStart.Y + options.DeltaY.GetValueOrDefault()));
        EnsureHit(source, start);
        if (target != null) EnsureHit(target, end);
        var plan = new MouseDragPlan(
            checked((int)Math.Round(start.X)), checked((int)Math.Round(start.Y)),
            checked((int)Math.Round(end.X)), checked((int)Math.Round(end.Y)),
            options.Button, options.DurationMs, source.GetType().Name,
            (PresentationSource.FromVisual(source) as System.Windows.Interop.HwndSource)?.Handle ?? IntPtr.Zero,
            target == null ? IntPtr.Zero :
                (PresentationSource.FromVisual(target) as System.Windows.Interop.HwndSource)?.Handle ?? IntPtr.Zero);
        if (plan.StartX == plan.EndX && plan.StartY == plan.EndY)
            throw new InvalidOperationException("Drag endpoints resolve to the same screen pixel.");
        return plan;
    }

    public TouchGesturePlan PrepareTouch(UIElement element, TouchGestureOptions options)
    {
        options.Validate();
        EnsureInteractable(element, "touched");
        BringToForeground(Window.GetWindow(element));
        ScrollIntoView(element);
        var center = LocalPoint(element, options.X, options.Y);
        var size = element.RenderSize;
        double space = Math.Min(Math.Min(center.X, size.Width - center.X), Math.Min(center.Y, size.Height - center.Y));
        double radius = options.Radius ?? space / (2 * Math.Max(1, options.Scale ?? 1));
        int steps = Math.Max(2, (int)Math.Ceiling(options.EffectiveDurationMs / 16.0));
        var frames = new TouchContactPoint[steps + 1][];
        for (int index = 0; index <= steps; index++)
        {
            var positions = TouchGesturePath.Frame(options, center.X, center.Y, radius, (double)index / steps);
            frames[index] = new TouchContactPoint[positions.Length];
            for (int finger = 0; finger < positions.Length; finger++)
            {
                var local = positions[finger];
                if (local.X < 0 || local.Y < 0 || local.X >= size.Width || local.Y >= size.Height)
                    throw new ArgumentException("The entire touch path must stay inside the element's rendered bounds. Adjust the center, displacement, radius or scale.");
                var screen = element.PointToScreen(new Point(local.X, local.Y));
                EnsureHit(element, screen);
                frames[index][finger] = new TouchContactPoint { X = checked((int)Math.Round(screen.X)), Y = checked((int)Math.Round(screen.Y)) };
            }
            if (positions.Length == 2)
            {
                double dx = (double)frames[index][0].X - frames[index][1].X;
                double dy = (double)frames[index][0].Y - frames[index][1].Y;
                if (dx * dx + dy * dy < 64)
                    throw new ArgumentException("Touch contacts must stay at least 8 screen pixels apart. Increase the radius or use a larger element.");
            }
        }
        return new TouchGesturePlan(options.Gesture, options.EffectiveDurationMs, element.GetType().Name,
            (PresentationSource.FromVisual(element) as System.Windows.Interop.HwndSource)?.Handle ?? IntPtr.Zero, frames);
    }

    private static Point LocalPoint(UIElement element, double? x, double? y)
    {
        var size = element.RenderSize;
        if (size.Width <= 0 || size.Height <= 0 || !element.IsHitTestVisible)
            throw new InvalidOperationException("Input elements must have a positive size and be hit-test visible.");
        var point = new Point(x ?? size.Width / 2, y ?? size.Height / 2);
        if (point.X < 0 || point.Y < 0 || point.X >= size.Width || point.Y >= size.Height)
            throw new ArgumentException("The local input point lies outside the element's rendered bounds.");
        return point;
    }

    private static void EnsureHit(UIElement element, Point screenPoint)
    {
        if (element is System.Windows.Interop.HwndHost host &&
            PhysicalInputGuard.IsHostedPoint(host.Handle, checked((int)Math.Round(screenPoint.X)), checked((int)Math.Round(screenPoint.Y))))
            return;
        var root = PresentationSource.FromVisual(element)?.RootVisual as UIElement;
        DependencyObject? hit = root?.InputHitTest(root.PointFromScreen(screenPoint)) as DependencyObject;
        while (hit != null)
        {
            if (ReferenceEquals(hit, element)) return;
            hit = hit is Visual || hit is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        throw new InvalidOperationException("The input point is clipped or covered by another element. Make the input path visible first.");
    }

    // ---------------------------------------------------------------------
    // Set text / fill value
    // ---------------------------------------------------------------------

    /// <summary>
    /// Replaces the text/value of <paramref name="element"/> with <paramref name="text"/>.
    /// Default: <see cref="IValueProvider.SetValue"/> via UI Automation, with a
    /// <c>TextBox.Text</c> / <c>PasswordBox.Password</c> / reflected <c>Text</c>
    /// fallback. With <paramref name="physical"/>=true, focuses the element and
    /// types the text via OS keyboard input (selecting and deleting any prior
    /// value first).
    /// </summary>
    public InteractionOutcome SetText(UIElement element, string text, bool physical)
    {
        EnsureInteractable(element, "given text");
        text ??= string.Empty;
        return physical ? PhysicalSetText(element, text) : AutomationSetText(element, text);
    }

    private static InteractionOutcome AutomationSetText(UIElement element, string text)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        if (peer?.GetPattern(PatternInterface.Value) is IValueProvider value)
        {
            if (value.IsReadOnly)
                throw new InvalidOperationException("Element value is read-only and cannot be set.");
            value.SetValue(text);
            // Some composites (an AutoCompleteBox-based input) expose Value but do not pass it on to
            // the text box inside, which is what the user types into and what the bindings follow.
            if (value.Value != text && !IsEditor(element)
                && ControlOperations.Descendants(element).OfType<TextBox>().FirstOrDefault(e => !e.IsReadOnly && e.IsEnabled && e.IsVisible) is { } inner
                && UIElementAutomationPeer.CreatePeerForElement(inner)?.GetPattern(PatternInterface.Value) is IValueProvider innerValue)
            {
                innerValue.SetValue(text);
                return new InteractionOutcome("ValueProvider.SetValue",
                    $"the {element.GetType().Name} did not take the text, so it was written into its inner TextBox; " + ReadBackDetail(inner.Text, text));
            }
            return new InteractionOutcome("ValueProvider.SetValue", ReadBackDetail(value.Value, text));
        }

        // Direct-property fallbacks for the common cases that lack a pattern.
        if (element is TextBox tb)
        {
            tb.Text = text;
            return new InteractionOutcome("DirectProperty.Text", ReadBackDetail(tb.Text, text));
        }
        if (element is PasswordBox pb)
        {
            pb.Password = text;
            // Never echo passwords back.
            return new InteractionOutcome("DirectProperty.Password", $"length now: {pb.Password.Length}");
        }

        // Last resort: reflect for a settable string `Text` property (covers many
        // third-party controls that expose Text but no automation peer).
        var prop = element.GetType().GetProperty("Text", BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
        {
            prop.SetValue(element, text);
            return new InteractionOutcome("Reflected.Text", ReadBackDetail(prop.GetValue(element) as string, text));
        }

        throw new InvalidOperationException(
            $"Element type '{element.GetType().Name}' has no IValueProvider and no settable string 'Text' property. " +
            "Try physical=true to type via OS keyboard input.");
    }

    /// <summary>
    /// Builds a detail string reporting the element's value after the write, so the
    /// caller can verify the outcome without an extra round-trip. Flags a mismatch
    /// (e.g. input validation or coercion changed the value).
    /// </summary>
    private static string ReadBackDetail(string? actual, string requested)
    {
        actual ??= string.Empty;
        var shown = actual.Length > 80 ? actual.Substring(0, 80) + "…" : actual;
        return actual == requested
            ? $"value now: '{shown}'"
            : $"value now: '{shown}' (differs from requested text — the control may coerce or validate input)";
    }

    private static InteractionOutcome PhysicalSetText(UIElement element, string text)
    {
        if (!FocusForKeyboardInput(element))
            throw new InvalidOperationException(
                "Neither the element nor anything inside it can take keyboard focus; physical typing has nowhere to land. " +
                "Use the default UI Automation mode or target the inner text box.");
        // A composite control can take focus itself without passing it to its editor (it depends on
        // where focus was before); typed text then bypasses the editor's bindings. Text belongs in
        // the editor, so move there unless focus already is in an editor of this element.
        if (!IsEditor(element) && !(Keyboard.FocusedElement is UIElement current && IsEditor(current) && IsWithin(current, element))
            && ControlOperations.Descendants(element).OfType<UIElement>()
                .FirstOrDefault(e => IsEditor(e) && e.Focusable && e.IsVisible && e.IsEnabled) is { } editor)
        {
            editor.Focus();
            // Some composites hand focus elsewhere on GotFocus; ask the keyboard directly once more.
            if (!editor.IsKeyboardFocusWithin) Keyboard.Focus(editor);
        }
        // Typed keys go wherever keyboard focus is: never type while it is outside the target.
        if (!(Keyboard.FocusedElement is DependencyObject inside && IsWithin(inside, element)))
            throw new InvalidOperationException(
                $"Keyboard focus could not be moved into the {element.GetType().Name} (it is on {(Keyboard.FocusedElement as DependencyObject)?.GetType().Name ?? "nothing"}" +
                $"{((Keyboard.FocusedElement as FrameworkElement)?.Name is { Length: > 0 } n ? " '" + n + "'" : "")}); nothing was typed. " +
                "Target the inner text box, click it physically first, or use the default UI Automation mode.");
        if (!PhysicalInputGuard.OwnsForeground())
            throw new InvalidOperationException(
                "The attached application does not own the foreground, so typed text would go to another window; nothing was typed. " +
                "Bring the application to the front or use the default UI Automation mode.");
        var focus = Keyboard.FocusedElement as DependencyObject;
        var landed = focus != null && !ReferenceEquals(focus, element) ? $" into the inner {focus.GetType().Name}" : "";
        var warning = focus is UIElement target && IsEditor(target) ? ""
            : $" (keyboard focus is on {focus?.GetType().Name ?? "nothing"}, not a text editor: verify the value and its binding)";
        if (CommitsOnLostFocus(focus, element)) warning += LostFocusWarning;

        // Select-all + delete the existing content first.
        NativeMethods.KeyDown(NativeMethods.VK_CONTROL);
        NativeMethods.KeyDown((byte)'A');
        NativeMethods.KeyUp((byte)'A');
        NativeMethods.KeyUp(NativeMethods.VK_CONTROL);
        NativeMethods.KeyDown(NativeMethods.VK_DELETE);
        NativeMethods.KeyUp(NativeMethods.VK_DELETE);

        foreach (var c in text)
        {
            NativeMethods.SendUnicodeChar(c);
        }

        return new InteractionOutcome("Physical", $"typed {text.Length} char(s){landed} after Ctrl+A/Delete{warning}");
    }

    internal const string LostFocusWarning =
        " (the Text binding updates its source on LostFocus and focus is still in the field: the bound value is unchanged until focus leaves; pass commit=true or send Tab)";

    /// <summary>
    /// Commits text written by set_text the way leaving the field does: moves keyboard focus on
    /// (as Tab would) when it is inside the element, then writes any binding still holding a
    /// pending value, and reports the bound source values and validation errors. Returns null
    /// while typed text has not arrived yet (physical input is processed asynchronously), unless
    /// <paramref name="force"/>.
    /// </summary>
    internal static string? CommitText(UIElement element, string text, bool physical, bool force)
    {
        var editor = Keyboard.FocusedElement is UIElement focused && IsEditor(focused) && IsWithin(focused, element) ? focused
            : IsEditor(element) ? element
            : ControlOperations.Descendants(element).OfType<UIElement>().FirstOrDefault(IsEditor);
        if (physical && !force && editor is TextBoxBase && EditorText(editor) != text) return null;
        // Committing a field that did not take the text would write its old or empty content to the source.
        if (!physical && editor is TextBoxBase && EditorText(editor) != text && !string.IsNullOrEmpty(text) && string.IsNullOrEmpty(EditorText(editor)))
            return $"not committed: the field is empty, not '{text}', so nothing was written to the source; check the target (try the inner text box or physical=true)";

        var parts = new List<string>();
        // Applications format and validate in their LostFocus handlers, which run only for a field that
        // had keyboard focus: a value written through UI Automation is entered first, then left.
        if (!element.IsKeyboardFocusWithin && editor != null && editor.Focusable && editor.IsEnabled && editor.IsVisible)
        {
            // Keyboard focus exists only in the active window, as when a user clicks into the field.
            BringToForeground(Window.GetWindow(element));
            editor.Focus();
            if (!element.IsKeyboardFocusWithin)
                parts.Add("the window is not active, so focus could not enter the field: only the bindings were written, the field's own LostFocus logic (formatting, validation) did not run");
        }
        if (element.IsKeyboardFocusWithin && Keyboard.FocusedElement is UIElement current)
        {
            current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            parts.Add(element.IsKeyboardFocusWithin
                ? "focus could not leave the field (the application kept it there, usually because the value is invalid)"
                : $"focus moved on to {(Keyboard.FocusedElement as DependencyObject)?.GetType().Name ?? "nothing"}");
        }

        // Inner editors first: their bindings feed the wrapper's own property, whose binding then holds the new value.
        var targets = ControlOperations.Descendants(element).Where(d => !ReferenceEquals(d, element)).Take(200).Reverse().ToList();
        targets.Add(element);
        var reported = new List<string>();
        foreach (var target in targets)
        {
            // Local values cover bindings set in code or XAML; template-set bindings are found by name.
            var properties = new List<DependencyProperty>();
            var values = target.GetLocalValueEnumerator();
            while (values.MoveNext()) properties.Add(values.Current.Property);
            foreach (var name in ValuePropertyNames)
                if (System.ComponentModel.DependencyPropertyDescriptor.FromName(name, target.GetType(), target.GetType())?.DependencyProperty is { } named
                    && !properties.Contains(named))
                    properties.Add(named);
            foreach (var property in properties)
            {
                if (BindingOperations.GetBindingExpressionBase(target, property) is not { } expression) continue;
                if (expression.IsDirty) expression.UpdateSource();
                if (ValuePropertyNames.Contains(property.Name)
                    && expression is BindingExpression single && single.ResolvedSource != null && single.ResolvedSourcePropertyName != null
                    // Template plumbing between the control and its own parts is not application data.
                    && !(single.ResolvedSource is DependencyObject source && IsWithin(source, element)))
                    reported.Add($"{single.ParentBinding.Path?.Path ?? single.ResolvedSourcePropertyName} = {SourceValue(single, property)}");
            }
            if (Validation.GetHasError(target))
                reported.AddRange(Validation.GetErrors(target).Select(e => $"validation error: {e.ErrorContent}"));
        }
        // Leaving the field may have reformatted it (a date "1231" expanded to a full date).
        if (editor is TextBoxBase && EditorText(editor) != text)
            parts.Add(physical && force ? $"the field shows '{EditorText(editor)}', not the typed text" : $"the field now shows '{EditorText(editor)}'");
        parts.Add(reported.Count == 0 ? "no bound source to report" : "source " + string.Join("; ", reported.Distinct()));
        return "committed: " + string.Join("; ", parts);
    }

    private static readonly string[] ValuePropertyNames = { "Text", "Password", "SelectedValue", "SelectedItem", "SelectedDate", "Value", "IsChecked" };

    private static string? EditorText(UIElement editor) => editor switch
    {
        TextBox box => box.Text,
        RichTextBox rich => new System.Windows.Documents.TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text.TrimEnd('\r', '\n'),
        _ => null
    };

    private static string SourceValue(BindingExpression expression, DependencyProperty property)
    {
        if (property.Name == "Password") return "(hidden)";
        try
        {
            var descriptor = System.ComponentModel.TypeDescriptor.GetProperties(expression.ResolvedSource)[expression.ResolvedSourcePropertyName];
            var value = descriptor?.GetValue(expression.ResolvedSource);
            var text = value == null ? "null" : $"'{Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture)}'";
            return text.Length > 82 ? text.Substring(0, 80) + "…'" : text;
        }
        catch (Exception ex) { return $"(unreadable: {ex.GetType().Name})"; }
    }

    /// <summary>True when a Text binding between the focused editor and the targeted element writes its source only on LostFocus.</summary>
    internal static bool CommitsOnLostFocus(DependencyObject? focus, DependencyObject element)
    {
        for (var current = focus; current != null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            var property = System.ComponentModel.DependencyPropertyDescriptor.FromName("Text", current.GetType(), current.GetType())?.DependencyProperty;
            var binding = property == null ? null : BindingOperations.GetBindingExpression(current, property)?.ParentBinding;
            if (binding != null && binding.Mode is not (BindingMode.OneWay or BindingMode.OneTime))
            {
                var trigger = binding.UpdateSourceTrigger;
                if (trigger == UpdateSourceTrigger.Default)
                    trigger = (property!.GetMetadata(current) as FrameworkPropertyMetadata)?.DefaultUpdateSourceTrigger ?? UpdateSourceTrigger.PropertyChanged;
                if (trigger == UpdateSourceTrigger.LostFocus) return true;
            }
            if (ReferenceEquals(current, element)) break;
        }
        return false;
    }

    private static bool IsEditor(UIElement element) => element is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox;

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current != null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    // ---------------------------------------------------------------------
    // Select item (ComboBox / ListBox / TabControl / any Selector)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Selects an item in a Selector-derived control (ComboBox, ListBox, ListView,
    /// TabControl, ...) by visible text or by index. Works on virtualized items too,
    /// because it drives the Items collection rather than item containers (which do
    /// not exist in the visual tree until realized).
    /// </summary>
    public InteractionOutcome SelectItem(UIElement element, string? itemText, int? index)
    {
        EnsureInteractable(element, "used for selection");

        if (element is not Selector selector)
            throw new InvalidOperationException(
                $"Element type '{element.GetType().Name}' is not a Selector (ComboBox, ListBox, ListView, TabControl, ...). " +
                "Use wpf_click_element for buttons/checkboxes or wpf_set_text for editable text.");

        if (index is null && string.IsNullOrEmpty(itemText))
            throw new ArgumentException("Provide item_text or index to choose the item to select.");

        var count = selector.Items.Count;
        if (count == 0)
            throw new InvalidOperationException("The control has no items to select.");

        if (index is int i)
        {
            if (i < 0 || i >= count)
                throw new ArgumentOutOfRangeException(nameof(index), $"index {i} is out of range; the control has {count} item(s).");
            if (SelectCellGridRow(selector, i) is { } cellOutcome) return cellOutcome;
            selector.SelectedIndex = i;
            return new InteractionOutcome(
                "Selector.SelectedIndex",
                $"selected index {i}: '{DescribeItem(selector, i)}'" + MakeGridRowCurrent(selector, i));
        }

        // DataGrid rows are matched on their cell texts (the same text wpf_read_grid returns),
        // including virtualized rows; the data item's ToString() is rarely what the user sees.
        if (selector is DataGrid grid)
        {
            var row = GridReader.FindRow(grid, itemText!, out var rowText);
            if (row < 0)
                throw new InvalidOperationException(
                    $"No row with a cell containing '{itemText}' among {count} row(s). " +
                    "Read the rows with wpf_read_grid and select by index.");
            if (SelectCellGridRow(selector, row) is { } cellOutcome) return cellOutcome;
            selector.SelectedIndex = row;
            return new InteractionOutcome(
                "Selector.SelectedIndex",
                $"selected row {row}: '{rowText}' (matched '{itemText}')" + MakeGridRowCurrent(selector, row));
        }

        for (int j = 0; j < count; j++)
        {
            var text = DescribeItem(selector, j);
            if (text.IndexOf(itemText, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                selector.SelectedIndex = j;
                return new InteractionOutcome(
                    "Selector.SelectedIndex",
                    $"selected index {j}: '{text}' (matched '{itemText}')");
            }
        }

        var preview = string.Join(", ", Enumerable.Range(0, Math.Min(count, 10))
            .Select(j => $"'{DescribeItem(selector, j)}'"));
        var unrealized = Enumerable.Range(0, count).Count(j => selector.ItemContainerGenerator.ContainerFromIndex(j) == null);
        throw new InvalidOperationException(
            $"No item matching '{itemText}' among {count} item(s). " +
            (unrealized > 0 && !(selector is DataGrid)
                ? $"{unrealized} of them are virtualized (not displayed), so they were matched against their data (public text and number properties), not their displayed template text; the item may still exist under another text: try index or a property value. "
                : "") +
            $"First items: [{preview}{(count > 10 ? ", …" : "")}]");
    }

    /// <summary>
    /// A grid with SelectionUnit=Cell cannot select rows (WPF throws). Selecting "a row" there means
    /// what a click into the row does: one cell of it becomes the selected and current cell.
    /// </summary>
    private static InteractionOutcome? SelectCellGridRow(Selector selector, int index)
    {
        if (selector is not DataGrid { SelectionUnit: DataGridSelectionUnit.Cell } grid) return null;
        var item = grid.Items[index];
        if (item == System.Windows.Data.CollectionView.NewItemPlaceholder)
            throw new InvalidOperationException($"Row {index} is the new-item placeholder and cannot be selected.");
        var column = grid.CurrentCell.Column is { Visibility: Visibility.Visible } current
            ? current
            : grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex).FirstOrDefault()
              ?? throw new InvalidOperationException("The grid has no visible column to select a cell in.");
        var wasForeground = PhysicalInputGuard.OwnsForeground();
        grid.ScrollIntoView(item, column);
        var cell = new DataGridCellInfo(item, column);
        grid.SelectedCells.Clear();
        grid.SelectedCells.Add(cell);
        grid.CurrentCell = cell;
        grid.UpdateLayout();
        var focused = FocusCurrentGridCell(grid);
        return new InteractionOutcome("DataGrid.SelectedCells",
            $"cell grid (SelectionUnit=Cell): selected and current cell set to row {index}, column '{ControlOperations.Display(column.Header) ?? "#" + grid.Columns.IndexOf(column)}': '{GridReader.DescribeRow(grid, index)}'"
            + (focused ? "; it has keyboard focus" : "; keyboard focus could not be moved into it")
            + (!wasForeground && PhysicalInputGuard.OwnsForeground() ? "; focusing it brought the application to the foreground" : ""));
    }

    /// <summary>
    /// A user who selects a grid row also makes it the current row and puts keyboard focus in it,
    /// which is what row actions (Enter, F2, application key handlers) act on. SelectedIndex alone
    /// leaves the current cell and focus wherever they were.
    /// </summary>
    private static string MakeGridRowCurrent(Selector selector, int index)
    {
        if (selector is not DataGrid grid) return string.Empty;
        var item = grid.Items[index];
        if (item == System.Windows.Data.CollectionView.NewItemPlaceholder) return string.Empty;
        try
        {
            var column = grid.CurrentCell.Column is { Visibility: Visibility.Visible } current
                ? current
                : grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex).FirstOrDefault();
            if (column == null) return string.Empty;
            var wasForeground = PhysicalInputGuard.OwnsForeground();
            grid.ScrollIntoView(item, column);
            grid.CurrentCell = new DataGridCellInfo(item, column);
            grid.UpdateLayout();
            var note = FocusCurrentGridCell(grid)
                ? "; row is current and has keyboard focus"
                : "; row is current (keyboard focus could not be moved into it)";
            // Focusing a cell can activate its window; say so, since it changes what is in front.
            return !wasForeground && PhysicalInputGuard.OwnsForeground()
                ? note + "; focusing it brought the application to the foreground" : note;
        }
        catch (InvalidOperationException)
        {
            // The grid is mid-edit or regenerating containers; the selection itself succeeded.
            return string.Empty;
        }
    }

    /// <summary>Moves focus into the grid's current cell (or its row) when that container is realized.</summary>
    private static bool FocusCurrentGridCell(DataGrid grid)
    {
        var info = grid.CurrentCell;
        if (info.Item == null || info.Column == null) return false;
        if (grid.ItemContainerGenerator.ContainerFromItem(info.Item) is not DataGridRow row) return false;
        var cell = info.Column.GetCellContent(row)?.Parent as DataGridCell
            ?? ControlOperations.Descendants(row).OfType<DataGridCell>().FirstOrDefault(c => c.Column == info.Column);
        return cell != null && cell.Focus() || row.Focus();
    }

    /// <summary>
    /// Human-readable text for the item at <paramref name="index"/> — what the user
    /// sees in the dropdown/list. Tries, in order: container Content, DisplayMemberPath,
    /// an overridden ToString(), the realized item container's visible text (covers
    /// ItemTemplate over data objects), and common display properties.
    /// </summary>
    private static string DescribeItem(Selector selector, int index)
    {
        var item = selector.Items[index];
        if (item == null) return "(null)";

        if (selector is DataGrid grid && item != System.Windows.Data.CollectionView.NewItemPlaceholder)
            return GridReader.DescribeRow(grid, index);

        if (item is ContentControl cc && cc.Content != null)
            return cc.Content.ToString() ?? cc.GetType().Name;

        if (!string.IsNullOrEmpty(selector.DisplayMemberPath))
        {
            var prop = item.GetType().GetProperty(selector.DisplayMemberPath, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
                return prop.GetValue(item)?.ToString() ?? "(null)";
        }

        // An overridden ToString() is authoritative; the default one (type name) is not.
        var str = item.ToString();
        var isDefaultToString = str == item.GetType().FullName || str == item.GetType().Name;
        if (!string.IsNullOrEmpty(str) && !isDefaultToString)
            return str!;

        // ItemTemplate case: read the visible text from the realized container.
        if (selector.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container)
        {
            var text = TreeWalker.GetSearchableText(container);
            if (!string.IsNullOrWhiteSpace(text))
                return text!.Length > 100 ? text.Substring(0, 100) + "…" : text;
        }

        // Virtualized (no container yet): fall back to common display properties.
        foreach (var name in new[] { "Name", "Title", "Text", "DisplayName", "Header", "Description" })
        {
            var prop = item.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(string))
            {
                var value = prop.GetValue(item) as string;
                if (!string.IsNullOrWhiteSpace(value))
                    return value!;
            }
        }

        // Still nothing: the item's own data (codes, captions, dates) is what its template shows.
        return ItemDataText(item) ?? str ?? item.GetType().Name;
    }

    /// <summary>The item's public scalar property values ("Caption: B000709 | Date: …"), or null when it has none.</summary>
    internal static string? ItemDataText(object item)
    {
        var values = new List<string>();
        foreach (var property in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (values.Count >= 8 || property.GetIndexParameters().Length > 0 || !property.CanRead) continue;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!(type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(DateTime))) continue;
            object? value;
            try { value = property.GetValue(item); } catch (Exception) { continue; }
            var text = Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture);
            if (!string.IsNullOrWhiteSpace(text)) values.Add($"{property.Name}: {text}");
        }
        if (values.Count == 0) return null;
        var joined = string.Join(" | ", values);
        return joined.Length > 160 ? joined.Substring(0, 160) + "…" : joined;
    }

    // ---------------------------------------------------------------------
    // Send keys (shortcut)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Sends a single key combination (e.g. <c>Ctrl+S</c>, <c>Enter</c>, <c>F5</c>,
    /// <c>Alt+F4</c>) to the given element, or to whatever currently has focus when
    /// <paramref name="element"/> is null. Uses OS keyboard input.
    /// </summary>
    public InteractionOutcome SendKeys(UIElement? element, string keys)
    {
        PrepareKeys(element, keys);
        return SendPreparedKeys(keys);
    }

    /// <summary>
    /// Validates the combo and focuses the target. Returns whether the application had to be
    /// brought forward: the caller must confirm it owns the foreground before sending, because
    /// OS key input always goes to the foreground window, whichever application that is.
    /// </summary>
    public bool PrepareKeys(UIElement? element, string keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
            throw new ArgumentException("Key combo is required (e.g. \"Ctrl+S\", \"Enter\", \"F5\").");

        if (element != null)
            EnsureInteractable(element, "given keys");

        KeyComboParser.Parse(keys);
        var wasForeground = PhysicalInputGuard.OwnsForeground();
        if (element != null)
            FocusForKeyboardInput(element);
        return !wasForeground;
    }

    /// <summary>
    /// Counts how often WPF receives the combo's main key, to tell delivered input from input that
    /// went elsewhere. Modifiers do not count: a system-wide hotkey such as Alt+P lets Alt through
    /// and swallows only the P.
    /// </summary>
    public sealed class KeyDeliveryProbe
    {
        private int _received;
        private readonly NotifyInputEventHandler _handler;

        internal KeyDeliveryProbe(Key expected)
        {
            _handler = (_, e) =>
            {
                if (e.StagingItem.Input.RoutedEvent != Keyboard.PreviewKeyDownEvent || e.StagingItem.Input is not KeyEventArgs key) return;
                var actual = key.Key switch
                {
                    Key.System => key.SystemKey,
                    Key.ImeProcessed => key.ImeProcessedKey,
                    Key.DeadCharProcessed => key.DeadCharProcessedKey,
                    _ => key.Key
                };
                if (actual == expected) System.Threading.Interlocked.Increment(ref _received);
            };
            InputManager.Current.PreNotifyInput += _handler;
        }

        public int Received => System.Threading.Volatile.Read(ref _received);

        /// <summary>Must run on the thread that created the probe.</summary>
        public void Stop() => InputManager.Current.PreNotifyInput -= _handler;
    }

    public KeyDeliveryProbe StartKeyDeliveryProbe(string keys) => new(KeyInterop.KeyFromVirtualKey(KeyComboParser.Parse(keys).Key));

    /// <summary>
    /// After the window has really become the foreground window, WPF may have restored focus to
    /// whatever had it before. Puts keyboard focus back inside the target and reports whether it is there.
    /// </summary>
    public bool EnsureKeyboardFocusWithin(UIElement element)
    {
        if (!element.IsKeyboardFocusWithin) FocusForKeyboardInput(element);
        return element.IsKeyboardFocusWithin;
    }

    public InteractionOutcome SendPreparedKeys(string keys)
    {
        var (modifiers, key) = KeyComboParser.Parse(keys);

        foreach (var modifier in modifiers)
            NativeMethods.KeyDown(modifier);

        NativeMethods.KeyDown(key);
        NativeMethods.KeyUp(key);

        // Release modifiers in reverse order, matching how a human would let go.
        foreach (var modifier in modifiers.Reverse())
            NativeMethods.KeyUp(modifier);

        return new InteractionOutcome("Physical", $"sent '{keys}'");
    }

    // ---------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------

    private static void EnsureInteractable(UIElement element, string action)
    {
        if (!element.IsEnabled)
            throw new InvalidOperationException($"Element is disabled and cannot be {action}. Applications often disable controls while they load or save: wait with wpf_wait_for_element condition=enabled and retry.");
        if (!element.IsVisible)
            throw new InvalidOperationException($"Element is not visible and cannot be {action}.");
    }

    /// <summary>
    /// Brings the host window forward and tries to give keyboard focus to the element.
    /// Returns true when the element actually accepted focus.
    /// </summary>
    /// <summary>Activates a window for physical input, also when Windows refuses a plain activation.</summary>
    internal static void BringToForeground(Window? window)
    {
        if (window == null) return;
        window.Activate();
        if (PhysicalInputGuard.OwnsForeground()) return;
        PhysicalInputGuard.BringToForeground(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        if (PhysicalInputGuard.OwnsForeground()) window.Activate();
    }

    private static bool FocusForKeyboardInput(UIElement element)
    {
        BringToForeground(Window.GetWindow(element));
        // Keys aimed at a grid belong in its current cell: focusing the grid itself would move
        // focus out of the row and application key handlers would not see a cell as the source.
        if (element is DataGrid grid && FocusCurrentGridCell(grid)) return true;
        ScrollIntoView(element);
        if (element.Focus())
        {
            Keyboard.Focus(element);
            return true;
        }
        // A wrapper that cannot take focus itself (a composite control around a TextBox):
        // keys belong in the editor inside it, as when a user clicks into the control.
        if (element.IsKeyboardFocusWithin) return true;
        var candidates = ControlOperations.Descendants(element).OfType<UIElement>()
            .Where(e => !ReferenceEquals(e, element) && e.Focusable && e.IsVisible && e.IsEnabled).ToList();
        var inner = candidates.FirstOrDefault(e => e is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox) ?? candidates.FirstOrDefault();
        return inner != null && inner.Focus();
    }

    // ---------------------------------------------------------------------
    // Native interop (mouse + keyboard input)
    // ---------------------------------------------------------------------

    private static class NativeMethods
    {
        // -- Mouse -------------------------------------------------------
        private const uint MOUSEEVENTF_LEFTDOWN  = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP    = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP   = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

        public static void MouseLeftClick()
        {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        }

        public static void MouseRightClick()
        {
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, IntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, IntPtr.Zero);
        }

        // -- Keyboard ----------------------------------------------------
        public const byte VK_BACK    = 0x08;
        public const byte VK_TAB     = 0x09;
        public const byte VK_RETURN  = 0x0D;
        public const byte VK_SHIFT   = 0x10;
        public const byte VK_CONTROL = 0x11;
        public const byte VK_MENU    = 0x12;  // Alt
        public const byte VK_ESCAPE  = 0x1B;
        public const byte VK_SPACE   = 0x20;
        public const byte VK_PRIOR   = 0x21;  // PageUp
        public const byte VK_NEXT    = 0x22;  // PageDown
        public const byte VK_END     = 0x23;
        public const byte VK_HOME    = 0x24;
        public const byte VK_LEFT    = 0x25;
        public const byte VK_UP      = 0x26;
        public const byte VK_RIGHT   = 0x27;
        public const byte VK_DOWN    = 0x28;
        public const byte VK_INSERT  = 0x2D;
        public const byte VK_DELETE  = 0x2E;
        public const byte VK_LWIN    = 0x5B;

        private const uint INPUT_KEYBOARD     = 1;
        private const uint KEYEVENTF_KEYUP    = 0x0002;
        private const uint KEYEVENTF_UNICODE  = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static void KeyDown(byte vk) => keybd_event(vk, 0, 0, IntPtr.Zero);
        public static void KeyUp(byte vk)   => keybd_event(vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);

        /// <summary>
        /// Types a single Unicode character via SendInput with KEYEVENTF_UNICODE.
        /// Unlike keybd_event (whose scan code is a byte), SendInput's wScan is a
        /// ushort, so this handles the full BMP.
        /// </summary>
        public static void SendUnicodeChar(char c)
        {
            var inputs = new INPUT[2];

            inputs[0].type        = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk    = 0;
            inputs[0].u.ki.wScan  = c;
            inputs[0].u.ki.dwFlags = KEYEVENTF_UNICODE;

            inputs[1].type        = INPUT_KEYBOARD;
            inputs[1].u.ki.wVk    = 0;
            inputs[1].u.ki.wScan  = c;
            inputs[1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }
    }

    // ---------------------------------------------------------------------
    // Key-combo parser
    // ---------------------------------------------------------------------

    private static class KeyComboParser
    {
        /// <summary>
        /// Parses a key combo like <c>Ctrl+S</c>, <c>Ctrl+Shift+F5</c>, <c>Alt+F4</c>,
        /// <c>Enter</c>, <c>F1</c>, <c>Win+R</c>. The last segment is the key; any
        /// preceding segments are modifiers.
        /// </summary>
        public static (byte[] Modifiers, byte Key) Parse(string keys)
        {
            var parts = keys.Split('+');
            if (parts.Length == 0)
                throw new ArgumentException("Empty key combo.");

            var modifiers = new List<byte>();
            byte? key = null;

            for (int i = 0; i < parts.Length; i++)
            {
                var token = parts[i].Trim();
                if (token.Length == 0)
                    throw new ArgumentException($"Empty token in key combo '{keys}'.");

                if (i < parts.Length - 1)
                {
                    var mod = MapModifier(token)
                        ?? throw new ArgumentException(
                            $"Unknown modifier '{token}' in '{keys}'. Expected: Ctrl, Shift, Alt, or Win.");
                    modifiers.Add(mod);
                }
                else
                {
                    key = MapKey(token)
                        ?? throw new ArgumentException(
                            $"Unknown key '{token}' in '{keys}'. " +
                            "Supported: A-Z, 0-9, F1-F12, Enter, Esc, Tab, Space, " +
                            "Backspace, Delete, Insert, Home, End, PageUp, PageDown, " +
                            "Left, Right, Up, Down.");
                }
            }

            return (modifiers.ToArray(), key!.Value);
        }

        private static byte? MapModifier(string token)
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":  return NativeMethods.VK_CONTROL;
                case "shift":    return NativeMethods.VK_SHIFT;
                case "alt":
                case "menu":     return NativeMethods.VK_MENU;
                case "win":
                case "windows":
                case "meta":
                case "lwin":     return NativeMethods.VK_LWIN;
                default:         return null;
            }
        }

        private static byte? MapKey(string token)
        {
            // Single character: letter or digit.
            if (token.Length == 1)
            {
                var c = char.ToUpperInvariant(token[0]);
                if (c >= 'A' && c <= 'Z') return (byte)c;
                if (c >= '0' && c <= '9') return (byte)c;
            }

            var lower = token.ToLowerInvariant();

            // F1..F12
            if (lower.Length >= 2 && lower[0] == 'f'
                && int.TryParse(lower.Substring(1), out var n)
                && n >= 1 && n <= 12)
            {
                return (byte)(0x6F + n);  // VK_F1 = 0x70
            }

            switch (lower)
            {
                case "enter":
                case "return":    return NativeMethods.VK_RETURN;
                case "esc":
                case "escape":    return NativeMethods.VK_ESCAPE;
                case "tab":       return NativeMethods.VK_TAB;
                case "space":     return NativeMethods.VK_SPACE;
                case "back":
                case "backspace": return NativeMethods.VK_BACK;
                case "del":
                case "delete":    return NativeMethods.VK_DELETE;
                case "ins":
                case "insert":    return NativeMethods.VK_INSERT;
                case "home":      return NativeMethods.VK_HOME;
                case "end":       return NativeMethods.VK_END;
                case "pgup":
                case "pageup":    return NativeMethods.VK_PRIOR;
                case "pgdn":
                case "pagedown":  return NativeMethods.VK_NEXT;
                case "left":      return NativeMethods.VK_LEFT;
                case "right":     return NativeMethods.VK_RIGHT;
                case "up":        return NativeMethods.VK_UP;
                case "down":      return NativeMethods.VK_DOWN;
                default:          return null;
            }
        }
    }
}
