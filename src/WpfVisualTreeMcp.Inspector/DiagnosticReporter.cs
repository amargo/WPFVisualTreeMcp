using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WpfVisualTreeMcp.Shared.Models;

namespace WpfVisualTreeMcp.Inspector;

internal static class DiagnosticReporter
{
    internal static DiagnosticReport Capture(IEnumerable<DependencyObject> roots, TreeWalker walker,
        BindingAnalyzer bindings, string? rootHandle, int maxElements, int maxDepth, int maxFindings)
    {
        if (maxElements < 1 || maxElements > 2000 || maxDepth < 1 || maxDepth > 100 || maxFindings < 1 || maxFindings > 500)
            throw new ArgumentException("Report limits: max_elements 1-2000, max_depth 1-100, max_findings 1-500.");
        var result = new DiagnosticReport
        {
            ProcessId = Process.GetCurrentProcess().Id,
            RootHandle = rootHandle,
            Scope = rootHandle == null ? "all_windows" : "subtree"
        };
        var clock = Stopwatch.StartNew();
        var pending = new Queue<(DependencyObject element, int depth)>();
        foreach (var root in roots) pending.Enqueue((root, 0));
        var seen = new HashSet<DependencyObject>();
        var reader = new PropertyReader();
        void Truncated(string reason)
        {
            if (!result.TruncationReasons.Contains(reason)) result.TruncationReasons.Add(reason);
        }
        void Finding(DependencyObject element, string code, string message, string? property = null)
        {
            result.FindingCount++;
            if (result.Findings.Count >= maxFindings) { Truncated("max_findings"); return; }
            result.Findings.Add(new DiagnosticFinding
            {
                Code = code, ElementHandle = walker.GetOrCreateHandle(element),
                TypeName = Clip(element.GetType().FullName ?? element.GetType().Name),
                Name = element is FrameworkElement fe ? Clip(fe.Name) : null,
                Property = property, Message = Clip(message)
            });
        }
        while (pending.Count > 0)
        {
            if (result.ScannedElements >= maxElements) { Truncated("max_elements"); break; }
            if (clock.ElapsedMilliseconds >= 3000) { Truncated("time_budget"); break; }
            var (element, depth) = pending.Dequeue();
            if (!seen.Add(element)) continue;
            result.ScannedElements++;
            try
            {
                if (element is UIElement ui)
                {
                    if (!ui.IsEnabled) result.DisabledElements++;
                    if (!ui.IsVisible) result.InvisibleElements++;
                    if (ui.RenderSize.Width <= 0 || ui.RenderSize.Height <= 0) result.ZeroSizeElements++;
                }
                if (Validation.GetHasError(element))
                    Finding(element, "validation_error", "WPF validation reports an error. Inspect this element's bindings.");
                foreach (var property in reader.GetDependencyProperties(element))
                {
                    if (clock.ElapsedMilliseconds >= 3000) { Truncated("time_budget"); break; }
                    var expression = BindingOperations.GetBindingExpressionBase(element, property);
                    if (expression == null) continue;
                    result.BindingsChecked++;
                    if (expression.HasError || expression.Status is BindingStatus.PathError or BindingStatus.UpdateSourceError or BindingStatus.UpdateTargetError)
                        Finding(element, "binding_error", $"Binding status: {expression.Status}; HasError: {expression.HasError}.{DescribeBinding(expression)}", property.Name);
                }
                var children = TreeWalker.GetAllVisualChildren(element);
                if (depth >= maxDepth)
                {
                    if (children.Count > 0) Truncated("max_depth");
                }
                else
                {
                    foreach (var child in children)
                    {
                        if (seen.Contains(child)) continue;
                        if (pending.Count + result.ScannedElements >= maxElements) { Truncated("max_elements"); break; }
                        pending.Enqueue((child, depth + 1));
                    }
                }
            }
            catch (Exception ex)
            {
                Finding(element, "inspection_error", ex.GetType().Name + ": " + ex.Message);
            }
        }
        // The same trace repeats once per realized row or cell; merge identical errors so the
        // report stays small and distinct problems are not buried.
        var errors = bindings.GetRecentErrors(500, out var totalErrors);
        result.CapturedBindingErrorCount = totalErrors;
        if (totalErrors > errors.Count) Truncated("binding_error_history");
        var groups = errors
            .GroupBy(e => (e.ElementType, e.ElementName, e.Property, e.BindingPath, e.ErrorType, Message: InstanceIds.Replace(e.Message, "$1")))
            .Select(g => (Latest: g.OrderByDescending(e => e.Timestamp).First(), Count: g.Count(), First: g.Min(e => e.Timestamp), g.Key.Message))
            .OrderByDescending(g => g.Latest.Timestamp).ToList();
        if (groups.Count > 50) Truncated("binding_error_groups");
        foreach (var group in groups.Take(50))
            result.RecentBindingErrors.Add(new BindingError
            {
                ElementType = Clip(group.Latest.ElementType), ElementName = group.Latest.ElementName == null ? null : Clip(group.Latest.ElementName),
                Property = Clip(group.Latest.Property), BindingPath = Clip(group.Latest.BindingPath), ErrorType = Clip(group.Latest.ErrorType),
                Message = Clip(group.Message), Timestamp = group.Latest.Timestamp,
                Occurrences = group.Count, FirstSeen = group.Count > 1 ? group.First : null
            });
        return result;
    }

    // WPF traces identify instances by hash code; that alone must not split one problem into many.
    private static readonly System.Text.RegularExpressions.Regex InstanceIds =
        new(@"(HashCode)=-?\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string DescribeBinding(BindingExpressionBase expression)
    {
        if (expression is not BindingExpression single) return string.Empty;
        var path = single.ParentBinding?.Path?.Path;
        var source = single.DataItem?.GetType().Name;
        return " Path: '" + (string.IsNullOrEmpty(path) ? "." : path) + "'; source: " + (source ?? "none (null DataContext or unresolved source)") + ".";
    }

    private static string Clip(string value) => value.Length <= 2048 ? value : value.Substring(0, 2048) + "…";
}
