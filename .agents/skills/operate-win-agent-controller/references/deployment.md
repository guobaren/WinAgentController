# WinAgentController deployment and lifecycle

## Build and publish

Run from the repository root with the SDK pinned by `global.json`:

```powershell
dotnet restore Rc.RemoteController.sln -p:NuGetAudit=false
dotnet build Rc.RemoteController.sln --no-restore -warnaserror
dotnet test Rc.RemoteController.sln --no-build --no-restore -v minimal
```

Create the canonical complete deployment package:

```powershell
.\scripts\Publish-RemoteController.ps1 -OutputPath .\artifacts\publish -Configuration Release
```

Deploy the whole publish directory. Do not assemble an update package from selected binaries because updates expect a complete package and manifest.

## Initialize a target

On the managed Windows computer:

1. Copy the complete published directory locally.
2. Edit `RemoteController.Agent.config.json`.
3. Double-click `Setup-RemoteControllerAgent.cmd`, or run it from an administrator terminal.
4. Accept the UAC prompt.
5. Record the device ID and TLS SHA-256 fingerprint through a trusted channel.
6. Handle the one-time pairing code as a secret; it is valid for 10 minutes by default.

Important configuration values:

| Setting | Meaning |
| --- | --- |
| `SourcePath` | Package source; `.` means the directory containing the config. |
| `InstallPath` | Installed binaries, normally `C:\Program Files\RemoteController`. |
| `DataRoot` | Persistent identity, pairing, jobs, and audit data, normally `C:\ProgramData\RemoteController`. |
| `TcpPort` | Agent TCP listener, default `43001`. |
| `UiUser` | Interactive Windows user for UI control; blank selects the current administrator. |
| `NoFirewallRule` | Set only when firewall rules are managed separately. |
| `RegenerateIdentity` | Default `false`; preserves the existing TLS identity and pairing. Set `true` only for an intentional identity reset that requires fingerprint verification and re-pairing. |
| `ArmPairing` | Default `true`; emits a new 10-minute one-time code. |

Use `scripts/Install-RemoteController.ps1` only for manual or advanced installation. The configuration-driven `.cmd` is the normal target-side one-click entry point and invokes the setup PowerShell script with elevation.

`scripts/Start-RemoteController.cmd` starts an existing installation; it does not install one.

## Pair a controller

Locate and verify the node:

```powershell
rcctl discover --timeout-ms 4000 --text
rcctl probe 192.168.1.50:43001 --fingerprint <SHA256> --text
```

Verify `<SHA256>` out of band, then pair using the locally armed code:

```powershell
rcctl pair 192.168.1.50:43001 --fingerprint <SHA256> --code <one-time-code> --name MyController --text
```

Omit `--code` to enter the code through standard input instead of command history. Remote operation requires pairing, fingerprint pinning, TLS, and authenticated sessions. The Agent supports one paired controller at a time.

## Update an installed target

Build a fresh complete package. It must include `Invoke-RemoteControllerDetachedUpdate.ps1`, then apply it:

```powershell
rcctl update apply 192.168.1.50:43001 --fingerprint <SHA256> --package .\artifacts\publish --wait --timeout-seconds 600 --text
rcctl update status 192.168.1.50:43001 --fingerprint <SHA256> --update <GUID> --text
```

The Agent persists `Applying`, then a Broker bootstrap job registers a detached `SYSTEM` scheduled task. The detached runner waits for `update-ready`, writes `update-started`, stops UI/Agent/Broker, installs with rollback protection, and atomically writes `update-result.json`. The restarted Agent uses that result instead of treating the expected Broker interruption as the final outcome.

The updater retains `C:\ProgramData\RemoteController`, backs up the installed directory, and restores it when the install script fails. A `Succeeded` result is not a post-update service health check. Verify the pinned fingerprint, pairing, a harmless authenticated command, Agent/Broker status, UI registration, and relevant UI acceptance behavior. An older Agent without detached-update support needs one trusted local or recovery-channel refresh first.

For a local package refresh, rerun one-click setup with `RegenerateIdentity=false` unless re-pairing is intentional.

## Remove an installation

Use `scripts/Uninstall-RemoteController.ps1` from the complete package. Inspect its current parameters before running it, confirm whether persistent data must remain, and treat removal as destructive.
