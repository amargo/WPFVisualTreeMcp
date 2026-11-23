using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WpfVisualTreeMcp.Server;

/// <summary>
/// Rejects tool calls with parameters the tool does not have. The MCP SDK ignores unknown
/// arguments, so a misspelled filter (type instead of type_name) silently widens a search and a
/// wrong target parameter (window_handle instead of element_handle) acts on another element.
/// </summary>
internal static class ToolArgumentGuard
{
    // Names agents commonly guess, mapped to the parameter they mean.
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["type"] = new[] { "type_name" },
        ["name"] = new[] { "element_name" },
        ["handle"] = new[] { "element_handle", "dialog_handle", "window_handle" },
        ["window_handle"] = new[] { "element_handle", "root_handle" },
        ["root"] = new[] { "root_handle" },
        ["id"] = new[] { "automation_id" },
        ["button"] = new[] { "answer" },
        ["button_text"] = new[] { "answer" },
        ["button_id"] = new[] { "answer" },
        ["timeout"] = new[] { "timeout_ms" },
        ["wait"] = new[] { "wait_ms" },
        ["value"] = new[] { "text" },
        ["keys_to_send"] = new[] { "keys" },
        ["pid"] = new[] { "process_id" },
        // CLI spellings of MCP names.
        ["type-name"] = new[] { "type" },
        ["type_name"] = new[] { "type" },
        ["element-handle"] = new[] { "handle" },
        ["element_handle"] = new[] { "handle" },
        ["element-name"] = new[] { "name" },
        ["root-handle"] = new[] { "root" },
    };

    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Filter => next => async (request, cancellationToken) =>
    {
        var tool = request.MatchedPrimitive as McpServerTool;
        if (tool == null && request.Params?.Name is { } name)
            request.Server.ServerOptions.ToolCollection?.TryGetPrimitive(name, out tool);
        if (tool != null && Check(tool.ProtocolTool, request.Params?.Arguments) is { } error)
            return new CallToolResult { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = error } } };
        return await next(request, cancellationToken).ConfigureAwait(false);
    };

    /// <summary>The error for arguments the tool does not declare, or null when all are known.</summary>
    internal static string? Check(Tool tool, IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        if (arguments == null) return null;
        var known = Parameters(tool.InputSchema);
        var unknown = arguments.Select(a => a.Key).Where(key => !known.Contains(key)).ToList();
        if (unknown.Count == 0) return null;
        var parts = unknown.Select(key => Suggest(key, known) is { } meant
            ? $"'{key}' (did you mean '{meant}'?)" : $"'{key}'");
        return $"Unknown parameter{(unknown.Count > 1 ? "s" : "")} {string.Join(", ", parts)} for {tool.Name}; nothing was done. "
             + $"Valid parameters: {(known.Count == 0 ? "none" : string.Join(", ", known))}.";
    }

    private static List<string> Parameters(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => p.Name).ToList()
            : new List<string>();

    /// <summary>The parameter <paramref name="key"/> most likely meant (MCP snake_case or CLI kebab-case names).</summary>
    internal static string? Suggest(string key, List<string> known)
    {
        if (Aliases.TryGetValue(key, out var meant) && meant.FirstOrDefault(known.Contains) is { } alias) return alias;
        static string Compact(string name) => name.Replace("_", "").Replace("-", "").ToLowerInvariant();
        // Same words in another form (elementHandle, element-handle) or a near miss.
        var compact = Compact(key);
        return known.FirstOrDefault(k => Compact(k) == compact)
            ?? known.Select(k => (Name: k, Distance: Distance(compact, Compact(k))))
                .Where(c => c.Distance <= Math.Max(1, compact.Length / 4)).OrderBy(c => c.Distance).Select(c => c.Name).FirstOrDefault();
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0]; row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }
        return row[b.Length];
    }
}
