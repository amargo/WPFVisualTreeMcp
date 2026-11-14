using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;

namespace WpfVisualTreeMcp.Automation.Infrastructure;

public partial class InspectorBridge
{
    private async Task EnsureAdvancedAutomationAsync(bool subscriptions = false)
    {
        var capabilities = await SendRequestAsync<GetCapabilitiesRequest, GetCapabilitiesResponse>(EnsureConnected().ProcessId, new());
        if (!capabilities.Success || !(subscriptions ? capabilities.PropertySubscriptions : capabilities.AdvancedAutomation))
            throw CreateException("This operation requires Inspector 0.19 or later. Restart the target application and attach with the current SDK.");
    }

    public async Task<GridReadResult> ReadGridAsync(string elementHandle, int offset, int limit, IReadOnlyList<string>? columns = null)
    {
        var request = new ReadGridRequest { ElementHandle = elementHandle, Offset = offset, Limit = limit, Columns = columns?.ToList() ?? new() };
        try { request.Validate(); }
        catch (ArgumentException ex) { throw CreateException(ex.Message); }
        // Older Inspectors answer unknown requests with an untyped error; explain how to recover instead.
        var capabilities = await SendRequestAsync<GetCapabilitiesRequest, GetCapabilitiesResponse>(EnsureConnected().ProcessId, new());
        if (!capabilities.Success || !capabilities.GridRead)
            throw CreateException("Grid reads require a newer Inspector than the one loaded in the attached app. Save your work, restart the app, and reattach using the updated server.");
        var response = await SendRequestAsync<ReadGridRequest, ReadGridResponse>(EnsureConnected().ProcessId, request);
        if (!response.Success || response.Result == null) throw CreateException(response.Error ?? "Cannot read the DataGrid.");
        return response.Result;
    }

    public async Task<ControlOperationResponse> ControlOperationAsync(ControlOperationRequest request)
    {
        request.Validate();
        await EnsureAdvancedAutomationAsync().ConfigureAwait(false);
        var response = await SendRequestAsync<ControlOperationRequest, ControlOperationResponse>(EnsureConnected().ProcessId, request);
        if (!response.Success) throw CreateException(response.Error ?? "WPF control operation failed.");
        return response;
    }

    public async Task<WindowResponse> WindowAsync(WindowRequest request)
    {
        await EnsureAdvancedAutomationAsync().ConfigureAwait(false);
        var response = await SendRequestAsync<WindowRequest, WindowResponse>(EnsureConnected().ProcessId, request);
        if (!response.Success) throw CreateException(response.Error ?? "WPF window operation failed.");
        return response;
    }

    public async Task<PropertySubscription> ObservePropertyAsync(string elementHandle, string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        await EnsureAdvancedAutomationAsync(true).ConfigureAwait(false);
        return await PropertySubscription.ConnectAsync(EnsureConnected().ProcessId, elementHandle, propertyName, _cancellation.Value).ConfigureAwait(false);
    }

    public async Task UnwatchPropertyAsync(string watchId)
    {
        var response = await SendRequestAsync<UnwatchPropertyRequest, UnwatchPropertyResponse>(EnsureConnected().ProcessId, new() { WatchId = watchId });
        if (!response.Success) throw CreateException(response.Error ?? "Cannot remove property watch.");
    }
}
