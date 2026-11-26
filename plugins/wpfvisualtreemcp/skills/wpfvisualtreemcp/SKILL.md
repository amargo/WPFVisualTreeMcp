---
name: wpfvisualtreemcp
description: Inspect and interact with running Windows WPF applications using the WPF Visual Tree MCP. Use for visual-tree inspection, binding/style diagnosis, diagnostic reports, screenshots, mouse dragging, touch gestures, recording/replaying workflows and exporting xUnit regression tests, or updating this plugin's server/skill. Not for WinUI, Avalonia, or static XAML review without a running WPF app.
---

# WPFVisualTreeMcp

Use the plugin's `wpf-visual-tree` MCP tools. Tool names may carry a client-added prefix; discover the available tools and use their actual schemas.

## Connect to the target

Call `wpf_list_processes` to identify the running application, then `wpf_attach` with its process ID. If multiple processes match and the intended target is unclear, ask which one to inspect.

`wpf_attach` defaults to `auto_inject=false`, which expects the application to have initialized the Inspector itself. For an ordinary WPF app, use `auto_inject=true` when attaching for the user's requested inspection. This loads the Inspector into the target process. Do not require source changes merely to inspect an existing app.

## Investigate the symptom

- Start a broad diagnosis with `wpf_report`; use `root_handle` to narrow it. Check `truncationReasons` and coverage before interpreting results. Disabled/invisible/zero-size counts are observations, while recent binding traces are process-wide history even for a subtree report. Follow current findings with the specific inspection tools below.
- Locate controls with `wpf_find_elements`, filtering by visible text, name or type. Use the returned handles; do not invent or reuse handles from a previous process instance. Use a bounded `wpf_get_visual_tree` when hierarchy is relevant.
- All input tools, assertions and property edits accept `automation_id` directly instead of `element_handle`. IDs match `AutomationProperties.AutomationId` exactly and case-sensitively, or `x:Name` for elements without one (outside templates; newer than v0.20.0), like FlaUI `ById`; missing or duplicate IDs fail before input. Use `root_handle` to scope ID lookup. Drag also accepts `target_automation_id` and independent `target_root_handle`; a destination is exclusive with deltas. CLI equivalents: `--automation-id`, `--root`, `--target-automation-id`, `--target-root`. Find/deep/wait support the same ID filter. Other inspection tools use the handle returned by find. Restart and reattach with the updated Inspector if the capability is unavailable.
- For binding failures, combine `wpf_get_binding_errors`, `wpf_get_bindings`, and `wpf_get_data_context`. Use `wpf_evaluate_binding` to find the failing path segment or explain a property's value source. An empty error list alone does not prove a binding is correct.
- For unexpected styling or disabled controls, inspect properties and use `wpf_explain_triggers` to check the current style/template conditions. `ownerType` distinguishes same-name attached properties; use the control's actual dependency property.
- Backstage pages are included through adorner traversal; find their tabs by type and header text, including composite headers. `visible_only` means WPF visibility, not freedom from overlays. Verify the selected page after clicking and use physical input if a custom control's automation action does not execute its command.
- Native `HwndHost`/`WindowsFormsHost` nodes include `nativeContent`. Their empty WPF children list does not imply empty native content. Use local-coordinate input and screen/auto capture; CAD entity semantics require application accessibility or a dedicated API.
- To start an application use `wpf_launch` (CLI `launch --path`): it waits for the first window and attaches. That window is often a splash or login window, so follow with `wpf_window action="wait" title="..."` (CLI `window --action wait --title`) for the window you need; `wait_closed` waits for one to go away. `wpf_window` also lists top-level windows (their `handle` scopes other tools as `root_handle`) and activates, minimizes, maximizes, restores or closes one. `close` only requests it: answer a confirmation MessageBox with `wpf_native_dialog`. The server never kills a process.
- Save As / Open file dialogs are native dialogs too: `wpf_native_dialog` lists their `fields`; pass `text` (full file path, plus `field` when there are several) together with `answer` (the localized Save/Open caption from the listing) to fill and confirm in one call.
- A MessageBox opened after asynchronous work (an error after a server call, a confirmation after a save) appears later than the click or answer that caused it, so an immediate `wpf_native_dialog` listing can be empty. Pass `wait_ms` (e.g. 5000): listing waits until a dialog is open, answering waits for a follow-up dialog.
- Context menu: open it with `wpf_click_element` `click_type=right`, then `wpf_find_elements` `type_name="=MenuItem"` with `text` (no root_handle) finds its items; click one with `wpf_click_element` (Invoke, which closes the menu).
- To read DataGrid contents, use `wpf_read_grid` (CLI `read-grid`) instead of screenshots or cell searches: it pages rows as text from the data items (`offset`/`limit`, optional `columns` by header or binding path), including virtualized rows, without scrolling. Find grids with `type_name="=DataGrid"` (`=Type` matches that type and its subclasses, not `DataGridCell`). Row indices match `wpf_select_item`, whose `item_text` on a DataGrid matches any visible cell text of a row; the selected row becomes the current row with keyboard focus, so follow with `wpf_send_keys` to the grid (e.g. `Enter`) when the app opens rows on a key or double click, and verify the application reacted. Find results carry a shortened `path` (`full_path=true` for the whole path); `wpf_report` and `wpf_get_binding_errors` merge repeated binding errors with an `occurrences` count. Use `wpf_read_grid` with `min_rows`/`contains_text` to wait for a grid to load, `on_screen`/`has_text` on find to drop scrolled-away or empty elements, and read `inspector_compatibility` from `wpf_attach`: when it reports a mismatch, reconnect the server or restart the app as it says before trusting results. `wpf_send_keys` refuses to type when the app cannot be brought to the foreground and warns when the key never reached WPF. `wpf_read_grid` also reads ListView/GridView; `wpf_edit_grid_cell` (CLI `edit-cell`) edits one DataGrid text cell through the grid's edit/commit cycle and changes application data, so use it only when asked to modify data; `wpf_get_visual_tree outline=true` (CLI `tree --outline`) is the compact way to browse hierarchy. A native MessageBox is invisible to the tree and blocks the app (an action that opened one may time out): list it with `wpf_native_dialog` (CLI `dialog`) and press a button with `answer`, using the caption exactly as listed; answering makes the app act on that choice, so do it only when the task calls for it. Template columns without a binding (`valueSource: visual`) only have text for rows on screen; combo columns return the bound value. Standard WPF DataGrid only; requires a server/Inspector newer than v0.20.0.
- Screenshots are expensive: prefer text tools first, pass `element_handle` to capture only the relevant element, and keep the default 1280x800 bounds (newer than v0.20.0; earlier 1920x1080) unless small text must be legible.
- For layout, combine `wpf_get_layout_info` with a screenshot. Use screen-mode screenshots for visible popups and dropdowns. Default screenshot mode is `auto`: native hosted content uses screen capture with an explicit mode/detail; otherwise render. Explicit render rejects native content instead of silently returning a black area.
- To inspect content beyond a scroll viewport, use `wpf_capture_screenshot(full_content=true, mode="render")` on that scroll control. Virtualized capture temporarily scrolls and restores offsets, can trigger application handlers, and must not overlap other interactions. Memory/deadline limits can reject oversized captures; do not report a failed capture as complete.

## Interact and verify

For requested UI interaction, prefer `wpf_click_element`, `wpf_select_item`, and `wpf_set_text`. Use physical input when the application needs real OS input; it requires the visible desktop and can move focus. A TextBox binding updates its view model on LostFocus by default, so before saving or reading the result pass `commit=true` to `wpf_set_text`: it leaves the field as Tab would and reports the bound source value and any validation error. Use `wpf_wait_for_element` to wait for a UI condition; when the awaited text may still show from before (a status line after a reload), pass `after_change=true` and heed `already_met`/`note` in the result. Unknown parameter names are rejected with the likely meant name: fix the call, nothing was done.

- For hover, use `wpf_move_mouse` with optional paired local `x`/`y` and `hover_ms` (0-5000). For vertical/horizontal wheel input use `wpf_scroll`, signed `delta` in wheel units (120 per notch, nonzero -12000..12000), and optional `horizontal=true`. The app may scroll or zoom; verify the result.
- `wpf_click_element` accepts paired local `x`/`y`, which imply physical input. This supports arbitrary points inside native hosts without substituting touch for mouse. UI Automation Toggle may only change state; use physical input when a custom button needs its click command.
- For mouse dragging, use `wpf_drag` with `element_handle` or `automation_id` and either `target_handle`, `target_automation_id` or nonzero `delta_x`/`delta_y`. Optional paired start/target coordinates are local to their elements; `button` is left/right/middle and `duration_ms` controls the sequence.
- For touch, use `wpf_touch_gesture` with `element_handle` or `automation_id` and `gesture`: `tap`, `long_press`, `swipe`, `pinch`, or `rotate`. Swipe performs a touch pan and needs nonzero deltas; pinch performs a touch zoom and needs `scale` (above 1 spreads, below 1 contracts); rotation uses `angle_degrees` (positive clockwise). Optional `x`/`y` select the center and `radius` sets the initial finger radius for pinch/rotate. Use the tool schema for bounds and duration limits.
- Drag/touch coordinates are local WPF DIPs, not absolute screen pixels. Targets must be visible and unobstructed on an interactive desktop; touch paths must fit inside the element. Mutating interactions are serialized, while inspection remains available during a gesture.
- Success confirms input submission, not a recognized gesture or accepted drop. Verify the resulting value, layout or UI state. WPF applications must handle touch/manipulations; legacy input stacks can ignore injected touch. Do not change the target application's input configuration to force support.

For a requested temporary property experiment, take a `wpf_snapshot`, apply `wpf_set_property`, and compare with `wpf_diff`. Restore experimental values with `wpf_revert_property` unless the user wants them retained. Runtime edits do not update source XAML.

Report the observed control, relevant property or binding, and evidence for the diagnosis. Distinguish a verified cause from an inference and state whether an interaction or experiment changed application state.

## Record a repeatable test

For a requested regression test, start `wpf_record(action="start")`, then perform the workflow sequentially. Existing input tools, including drag/touch, finds, successful waits and assertions are captured. Use explicit `wpf_wait_for_element` calls for asynchronous changes and `wpf_assert` for exact dependency-property values (`expected_value` uses invariant strings; `{null}` means null).

Stop recording, inspect `wpf_get_recording`, then use `wpf_export_test(format="xunit")`. Save its file-name/content map in a new directory. The generated test runs with `WPF_TEST_APP` and `WPFINSPECT_PATH` set to absolute executable paths, then `dotnet test`; it starts a fresh app. Verify that test before reporting the workflow reproducible. CLI equivalents are `record start|stop|status|clear`, `get-recording`, `assert`, `replay --file`, and `export-test --format xunit --out NEW_DIRECTORY`.

Recordings live in the inspected process and vanish when it exits. Export before clearing; input values are plain text. Selectors use exact identities and reject missing/ambiguous controls. Do not replace a failed selector with a stale handle or arbitrary first match. Version 1 is linear, limited to 100 steps/512 KiB; replay has a maximum 120-second deadline and stops at the first failure without undoing earlier actions. Physical input still requires the interactive desktop and app gesture support. Inspect explicit assertions and application state when replaying; input delivery alone does not prove the intended result.

## Connection failures

This plugin runs locally on Windows and starts the latest stable NuGet package through `dotnet dnx`. It requires .NET 10 SDK version 10.0.300 or newer within .NET 10, and the .NET 8 runtime. Each MCP start checks nuget.org for the latest stable package, bypassing HTTP metadata caches; downloaded packages are reused. Restart the MCP server or Codex to pick up a new release. Attaching to x86 .NET apps also requires the x86 .NET 8 runtime. If the MCP tools are unavailable, report the startup failure and check those prerequisites before diagnosing the target app.

## Updates and CLI fallback

When an update is requested, identify which installation the user is using:

- **This plugin's server:** restart its MCP server or Codex to resolve the newest stable NuGet package. Do not run self-update against the shared `dnx` cache.
- **Plugin configuration and this skill:** refresh the configured marketplace (`codex plugin marketplace upgrade WPFVisualTreeMcp` for a Git-backed source), reinstall with `codex plugin add wpfvisualtreemcp@WPFVisualTreeMcp`, and start a new session. For a local marketplace, update its source checkout first. Updating the server does not update the skill.
- **Separately installed CLI (v0.14.0+):** `wpfinspect update --check` compares versions; `wpfinspect update` schedules an update; `wpfinspect update --status` reports completion or failure. For a portable copy, invoke that exact `WpfVisualTreeMcp.Server.exe` path. Older global tools first need `dotnet tool update -g WpfVisualTreeMcp`. Source builds use their Git/build workflow.

CLI self-update changes only the invoked installation and is not an MCP tool. On Windows, the worker installs after the CLI exits: `scheduled` does not mean finished. Confirm `updated`; failures return exit code 1 and a log path. Close processes using the same files before retrying, without stopping the user's applications automatically. Restart the MCP client and previously inspected apps after an update to reload their server/Inspector.

For an available CLI fallback, use `help` or `<command> --help`. Mouse and touch equivalents are `click --x/--y`, `move-mouse`, `scroll`, `drag` and `touch`; CLI rotation uses `--angle`, while the MCP parameter is `angle_degrees`. Never append a CLI subcommand to the plugin's stdio launch configuration.

## Version compatibility

Direct AutomationId targeting and scoped drag destinations require v0.18.0 or newer.
For hand-written C# tests, use the `WpfVisualTreeMcp.Automation` NuGet package:
`WpfApplication.AttachAsync(pid)` and `app.ByAutomationId(id)` expose the same engine.
`WaitForTextAsync("loaded")` compares the exact Text property and throws on timeout;
client-side property waits and predicates are not serialized into recordings.

The hover/wheel commands, click coordinates and screenshot auto mode require v0.17.0 or newer (38 tools). Release 0.16.0 has 36 tools. Discover the live schema before using them. CLI `update` installs the published stable release. Restart and reattach the target after replacing an injected Inspector; an already loaded DLL is not hot-reloaded.
