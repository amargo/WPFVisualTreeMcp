using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;

namespace WpfVisualTreeMcp.Inspector;

/// <summary>
/// Resolves an exact ID only when uniqueness in the requested scope is known. Like UI Automation
/// (and FlaUI ById), an element without AutomationProperties.AutomationId is identified by its
/// x:Name; an explicit AutomationId always wins over name-only matches.
/// </summary>
internal static class AutomationIdResolver
{
    internal const string NotFoundHint = " It is matched exactly (case-sensitive) against AutomationProperties.AutomationId, or x:Name for elements without one (names inside templates are ignored).";

    /// <summary>
    /// The x:Name that stands in for a missing AutomationId. Names inside control/data templates
    /// repeat per instance or row, so only elements outside a template qualify.
    /// </summary>
    internal static string? NameId(DependencyObject element) => element switch
    {
        FrameworkElement f when f.TemplatedParent == null && !string.IsNullOrEmpty(f.Name) => f.Name,
        FrameworkContentElement c when c.TemplatedParent == null && !string.IsNullOrEmpty(c.Name) => c.Name,
        _ => null
    };

    /// <summary>The explicit AutomationId, or the x:Name fallback when there is none.</summary>
    internal static string? EffectiveId(DependencyObject element)
    {
        var id = AutomationProperties.GetAutomationId(element);
        return string.IsNullOrEmpty(id) ? NameId(element) : id;
    }

    internal static DependencyObject Resolve(string automationId, IEnumerable<DependencyObject> roots)
        => ResolveOrDefault(automationId, roots) ?? throw new InvalidOperationException(NotFoundMessage(automationId, roots));

    internal static string NotFoundMessage(string automationId, IEnumerable<DependencyObject> roots) =>
        "AutomationId '" + automationId + "' was not found in the requested scope."
        + (DescribeUnrendered(automationId, roots) ?? NotFoundHint);

    /// <summary>
    /// Explains an ID that exists only in the logical tree: items of a closed menu, an unselected
    /// tab or a collapsed container have no visuals yet, so they cannot be targeted until opened.
    /// </summary>
    internal static string? DescribeUnrendered(string automationId, IEnumerable<DependencyObject> roots)
    {
        var queue = new Queue<DependencyObject>(roots);
        var visited = new HashSet<DependencyObject>();
        var clock = Stopwatch.StartNew();
        while (queue.Count > 0 && visited.Count < 10000 && clock.ElapsedMilliseconds < 1000)
        {
            var element = queue.Dequeue();
            if (!visited.Add(element)) continue;
            if (string.Equals(EffectiveId(element), automationId, StringComparison.Ordinal))
            {
                var menus = new List<string>();
                for (var parent = LogicalTreeHelper.GetParent(element); parent != null; parent = LogicalTreeHelper.GetParent(parent))
                    if (parent is System.Windows.Controls.MenuItem menu)
                        menus.Insert(0, WithoutAccessKey(TreeWalker.GetSearchableText(menu)) ?? menu.Name ?? "MenuItem");
                return menus.Count > 0
                    ? " A " + element.GetType().Name + " with this ID exists inside the closed menu '" + string.Join(" > ", menus)
                      + "'. Open that menu first (click its MenuItem), then target the item."
                    : " A " + element.GetType().Name + " with this ID exists but is not rendered yet (closed popup, unselected tab or collapsed container). Open its container first.";
            }
            foreach (var child in LogicalTreeHelper.GetChildren(element))
                if (child is DependencyObject next) queue.Enqueue(next);
        }
        return null;
    }

    internal static DependencyObject? ResolveOrDefault(string automationId, IEnumerable<DependencyObject> roots, Func<DependencyObject, string>? handleOf = null)
    {
        if (string.IsNullOrWhiteSpace(automationId) || automationId.Length > 2048)
            throw new ArgumentException("automation_id must contain 1-2048 characters and cannot be blank.");

        var explicitMatches = new List<DependencyObject>();
        var nameMatches = new List<DependencyObject>();
        var queue = new Queue<DependencyObject>(roots);
        var visited = new HashSet<DependencyObject>();
        var clock = Stopwatch.StartNew();
        while (queue.Count > 0)
        {
            if (visited.Count >= 10000 || queue.Count > 10000 || clock.ElapsedMilliseconds > 2000)
                throw new InvalidOperationException("AutomationId search exceeded its 10000-element / two-second budget; uniqueness is unknown. Narrow the search with root_handle.");
            var element = queue.Dequeue();
            if (!visited.Add(element)) continue;
            var explicitId = AutomationProperties.GetAutomationId(element);
            if (!string.IsNullOrEmpty(explicitId))
            {
                if (string.Equals(explicitId, automationId, StringComparison.Ordinal)) explicitMatches.Add(element);
            }
            else if (string.Equals(NameId(element), automationId, StringComparison.Ordinal)) nameMatches.Add(element);
            foreach (var child in TreeWalker.GetAllVisualChildren(element)) queue.Enqueue(child);
        }

        var matches = explicitMatches.Count > 0 ? explicitMatches : nameMatches;
        if (matches.Count > 1)
            throw new InvalidOperationException(
                "Ambiguous AutomationId '" + automationId + "': " + matches.Count + " elements match by "
                + (explicitMatches.Count > 0 ? "AutomationProperties.AutomationId" : "x:Name (none has an explicit AutomationId)")
                + ". Candidates: " + string.Join("; ", matches.Take(MaxCandidates).Select(m => Describe(m, handleOf)))
                + (matches.Count > MaxCandidates ? "; …" : "")
                + ". Use root_handle to scope the search (a window or container handle), or use one of these as element_handle.");
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>"_File" is shown as "File"; a doubled underscore is a literal one.</summary>
    private static string? WithoutAccessKey(string? header)
    {
        if (string.IsNullOrEmpty(header)) return header;
        var text = new System.Text.StringBuilder(header!.Length);
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] != '_') { text.Append(header[i]); continue; }
            if (i + 1 < header.Length && header[i + 1] == '_') { text.Append('_'); i++; }
        }
        return text.ToString();
    }

    private const int MaxCandidates = 6;

    private static string Describe(DependencyObject element, Func<DependencyObject, string>? handleOf)
    {
        var text = element.GetType().Name;
        if (handleOf != null) text += " " + handleOf(element);
        try
        {
            if (Window.GetWindow(element) is { } window && !ReferenceEquals(window, element))
                text += " in window '" + window.Title + "'";
        }
        catch (InvalidOperationException) { }
        if (element is UIElement ui) text += ui.IsVisible ? " (visible)" : " (not visible)";
        return text;
    }
}
