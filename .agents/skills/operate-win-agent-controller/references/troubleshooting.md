# WinAgentController validation and troubleshooting

## Establish current state

1. Read `docs/审计.md` and `docs/交接.md` for current test evidence and unresolved scenarios.
2. Check `git status --short --branch` and preserve unrelated changes.
3. Check the installed .NET SDK against `global.json` before interpreting build failures.
4. Run the smallest relevant validation first, then the full solution checks when the change warrants it.
5. Record environment-dependent or still-failing tests in `docs/审计.md` when the task requires progress tracking; do not label them fixed without evidence.

## Diagnose target connectivity

Check in order:

1. Confirm the target address and TCP port.
2. Confirm the Windows firewall rule or the externally managed equivalent.
3. Run `rcctl discover`, but treat discovery as unauthenticated metadata only.
4. Run `rcctl probe` with the independently verified fingerprint.
5. Confirm the Controller is still paired after any target refresh.
6. If the fingerprint changed, determine whether one-click setup ran with `RegenerateIdentity=true`; re-verify and re-pair only when the identity change was intentional.

## Diagnose services and UI

Inspect these installed components:

- Windows services `RemoteControllerBroker` and `RemoteControllerAgent`.
- Scheduled task or logged-in process for `RemoteControllerUiAgent`.
- The active Windows session and configured `UiUser`.
- Agent data and logs under the configured `DataRoot`.

Use `Start-RemoteController.cmd` only to start services that are already installed. Rerun setup for missing installation files or service registration.

If core commands work but UI commands fail, check that the target is unlocked, the intended user is logged in, and the UI Agent has recently registered. Do not expect control of UAC secure desktop or lock-screen surfaces.

## Diagnose files and jobs

- Confirm remote paths are beneath `RC_AGENT_FILE_ROOT` and contain no forbidden traversal or reparse-point path.
- Use job status and logs before cancelling a long-running job.
- Resume job logs with offsets and file copies with transfer session IDs instead of restarting large operations.
- Confirm overwrite intent before using `fs write --overwrite` or replacing downloaded local data.

## Interpret update results

For current packages, inspect the update session under `DataRoot\updates\<update-id>`:

- `update-detached` selects detached completion semantics.
- `update-ready` means `Applying` was persisted before service shutdown.
- `update-started` means the independent SYSTEM runner began.
- `update-result.json` is the durable final install result.

Once `update-started` exists, do not treat the bootstrap Broker job's `InterruptedByReboot` as the final failure. If no result appears, inspect the scheduled task, installation backup directory, and service state through an independent trusted recovery channel. Agents installed before this lifecycle require one bootstrap refresh.

An update result reports whether the detached install script completed. It does not prove that services are healthy afterward. After success:

1. Probe the pinned fingerprint.
2. Run a harmless authenticated command.
3. Validate UI registration if UI control is required.
4. Check `docs/审计.md` before claiming dual-node update, interrupted transfer, or rollback coverage is complete.

## Keep documentation accurate

When behavior and documentation differ, inspect the implementation and scripts first, then correct README and progress text. Keep commands copyable in PowerShell, distinguish target-side `.cmd` setup from Controller-side `rcctl`, and state security-sensitive defaults next to the instructions that trigger them.
