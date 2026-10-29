# Windows background auto-start

SaidNest can start quietly when the current Windows user signs in. It runs the same authenticated loopback archive service and idle refinement worker; it does not open a browser, begin recording, require cloud sign-in, store a password, or run as SYSTEM/administrator.

Build the Windows prototype and create its local vault first, then run:

```powershell
./scripts/windows/install-autostart.ps1
```

The installer creates a single `SaidNest` value under the current user's `Software\Microsoft\Windows\CurrentVersion\Run` registry key. It launches the locally installed PowerShell 7 with a quoted script path and hidden window. This is a sign-in startup entry, not a machine service that runs before login. Windows may delay Run entries. Inspect the entry through **Settings → Apps → Startup**; if Windows has disabled it, enable it there. Moving/deleting the checkout or runtime requires updating/removing the entry. The installer refuses to replace a different installation's existing SaidNest entry.

The background supervisor uses an exclusive installation lock to avoid duplicate supervisors. It adopts a service only when executable path, PID, and process start time match, including the native service's ready marker; stale/reused PIDs do not authorize stopping another process. The native service has its own vault-level singleton lock. Unexpected service exit triggers bounded restart backoff (up to thirty seconds), then the durable queue resumes subject to idle/resource policy. Startup does not automatically queue old recordings.

The existing local launcher reopens an already running service. When auto-start is configured and the service is stopped, it starts the hidden supervisor and opens the ready service. Use **Stop-Local-SaidNest.cmd** to stop for the current session: a stop flag prevents automatic relaunch, and the native service gets a process-identity-bound graceful shutdown request so its decoder can cancel and checkpoint. A verified process-tree stop is the fallback after ten seconds. The next sign-in or explicit background launch clears that stop flag.

To disable future auto-start without stopping the current service:

```powershell
./scripts/windows/uninstall-autostart.ps1
```

Use the Stop shortcut as well if you want the running service to exit. The uninstaller removes only this installation's matching Run entry. Queue jobs, recordings, references, and transcripts are retained.

Local process markers, supervisor lock/stop flags, and logs live under ignored `.saidnest/`. Browser bootstrap keys remain encrypted in the vault, not command-line arguments or Run entries. The service continues to bind `127.0.0.1` with bearer authorization, Host/Origin validation, and no-store responses. GPU checks and live/capture priority remain active regardless of how the service starts.

Verification covers command quoting, process identity, stale PID handling, singleton ownership, real service adoption/restart/explicit stop, and native readiness. A successful registration/start check does not prove execution at a future sign-in; that still needs a real sign-out/sign-in test.

References: [Microsoft Run keys](https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys) and [Windows startup app settings](https://support.microsoft.com/en-us/windows/experience/startup-boot/configure-startup-applications-in-windows).
