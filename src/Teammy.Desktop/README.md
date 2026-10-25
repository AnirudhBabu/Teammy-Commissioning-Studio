# Teammy desktop

The Windows client is a native WPF application on .NET 10. Its five workspaces are Overview, Workboard, Drive studio, Releases, and Activity. All project metrics, release gates, charts, and audit rows come from the shared application model.

## Run on Windows

```powershell
dotnet run --project src/Teammy.Desktop -- --demo
```

The default mode is the same local demo. It uses an in-memory `DemoWorkspaceClient` and the real `WorkspaceEngine`; data resets when the process closes. The identity switcher explicitly demonstrates Engineer, Approver, and Viewer permissions. It does not authenticate real users or connect to equipment.

For the local API started with its Development demo profile:

```powershell
dotnet run --project src/Teammy.Desktop -- --api-demo http://localhost:5080
```

The development identity switcher is available only with `--api-demo`, and that option is restricted to loopback addresses. The server must separately enable its development authentication mode.

For an authenticated API:

```powershell
# Supply a short-lived access token for the API through your existing identity workflow.
# TEAMMY_API_TOKEN must be set in this process environment; do not put tokens in scripts or source control.
dotnet run --project src/Teammy.Desktop -- --api https://your-api.example
```

The application reads `TEAMMY_API_TOKEN` once, holds it in memory, and does not save it. Remote connections require HTTPS. The API supplies the current identity and authorizes every mutation. This client does not yet implement an interactive OAuth sign-in or token refresh flow.

## Demonstrate the complete workflow

1. Keep the Packaging line commissioning project selected and the Engineer identity active.
2. In Drive studio, run the conveyor simulation. Inspect the speed curve, current, temperature, and individual checks.
3. In Workboard, move the critical conveyor ramp work item through Review to Done. Move the high-priority stop-sequence review to Done.
4. In Releases, inspect the now-passing release gates and request independent review.
5. Switch to Elena Torres, the demo Approver. Approve the candidate and inspect its fingerprint and review metadata.
6. Open Activity to inspect the recorded transitions, simulation, request, and approval.

Changing drive parameters requires a fresh simulation. Unsaved parameter edits disable project/drive selection; save or discard them first. A refresh preserves the parameter draft. Concurrent-edit conflicts reload the latest state while preserving the draft, and never automatically retry a write. An approved candidate is historical evidence tied to its fingerprint; it does not claim that later settings are approved.

## Windows smoke verification

```powershell
Teammy.Desktop.exe --smoke-test artifacts/desktop-smoke.png
```

This creates a real WPF window, loads the seeded workspace, completes the simulate-to-independent-approval workflow, visits every tab, and saves rendered PNGs for overview, workboard, drive studio, releases, and activity. It exits nonzero on a workflow failure, unusable layout, or WPF binding error. On a failure, it also attempts to write an adjacent `.error.txt` diagnostic file.

The Windows CI job runs this smoke check. Linux can compile the WPF project using `EnableWindowsTargeting`, but cannot execute or visually verify WPF. A successful cross-build must not be reported as a successful Windows smoke run.

## Presentation design

- The desktop project owns XAML, startup composition, native vector telemetry rendering, and the screenshot smoke entry point.
- `Teammy.Presentation` owns cancellable commands, bindable view state, demo and HTTP clients, draft preservation, and error feedback. It has no dependency on WPF and is unit tested on Linux and Windows.
- The shared application engine owns business validation, lifecycle transitions, release gates, authorization rules, and deterministic simulation.
- F5 refreshes the workspace; Escape cancels an active operation. Named controls, visible focus indicators, keyboard-accessible tabs, semantic labels, and live error/status announcements support desktop accessibility.
- No charting or visual-theme package is required. The speed chart is a small native WPF vector control rendering only the persisted simulation samples.
