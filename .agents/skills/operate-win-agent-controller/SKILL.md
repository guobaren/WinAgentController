---
name: operate-win-agent-controller
description: Deploy, install, pair, operate, update, validate, and troubleshoot the WinAgentController Windows LAN remote-control project. Use when working in this repository or with its published package, rcctl CLI, managed Agent services, file transfer, jobs, UI/browser automation, or target-side one-click setup.
---

# Operate WinAgentController

## Overview

Operate WinAgentController from source checkout through target deployment and authenticated control. Treat the repository README, CLI usage strings, and `docs/CURRENT_PROGRESS.md` as the live sources of truth.

## Choose the workflow

- For source validation or packaging, follow [references/deployment.md](references/deployment.md#build-and-publish).
- For first-time target setup, follow [references/deployment.md](references/deployment.md#initialize-a-target).
- For controller pairing or identity changes, follow [references/deployment.md](references/deployment.md#pair-a-controller).
- For commands, jobs, files, UI, or browser control, follow [references/commands.md](references/commands.md).
- For remote updates or removal, follow [references/deployment.md](references/deployment.md#update-an-installed-target).
- For failures, environment checks, and known limitations, follow [references/troubleshooting.md](references/troubleshooting.md).

## Apply safety rules

1. Operate only authorized Windows nodes on a trusted LAN.
2. Verify the target TLS SHA-256 fingerprint through a trusted out-of-band channel before probing or pairing. Do not trust UDP discovery alone.
3. Keep one-time pairing codes, controller private keys, and Agent private keys out of chat, source control, and ordinary logs.
4. Preserve the installed identity during routine refreshes. The one-click config and setup fallback default `RegenerateIdentity` to `false`; set it to `true` only when the user explicitly intends to remove the existing pairing, generate a new certificate, and pair again.
5. Use `--elevated` only when the requested remote command genuinely requires administrator rights.
6. Keep `RC_AGENT_FILE_ROOT` narrow and use paths relative to that root for remote file operations.
7. Confirm before uninstalling, regenerating identity, clearing pairing, closing windows, cancelling jobs, or replacing remote files.
8. Do not claim a test or deployment scenario passes without current evidence. Read `docs/CURRENT_PROGRESS.md` and record unresolved validation failures there when requested.
9. For `copy` performance work, distinguish legacy JSON/Base64 chunks from negotiated binary streaming. Compare external process time on the same dataset, use fresh destinations, and require post-transfer SHA-256 equality; a successful exit code alone is insufficient.

## Treat updates as a detached lifecycle

1. Build and upload the complete package, including `Invoke-RemoteControllerDetachedUpdate.ps1`; do not assemble a partial release directory.
2. The Agent must persist `Applying` before it writes `update-ready`. The elevated Broker job only registers and starts a `SYSTEM`/highest scheduled task.
3. The detached runner writes `update-started`, stops UI/Agent/Broker, runs the rollback-protected installer, and atomically writes `update-result.json` before removing its scheduled task.
4. After the Agent restarts, treat the detached result as the update outcome. An `InterruptedByReboot` state on the bootstrap Broker job is not the final result once the detached runner has started.
5. A `Succeeded` update still requires a pinned-fingerprint probe, a harmless authenticated command, service/UI registration checks, and relevant UI acceptance tests.
6. An Agent installed before detached-update support needs one trusted local or recovery-channel refresh before it can perform the new standard update lifecycle.

## Work from repository truth

Resolve discrepancies in this order:

1. Inspect the current implementation and CLI usage strings under `src/Rc.Cli/Commands`.
2. Inspect the scripts and their configuration under `scripts/`.
3. Align `README.md` with implemented behavior.
4. Use `docs/CURRENT_PROGRESS.md` for validation status and known gaps, not as a substitute for implemented behavior.

Before changing or committing repository content, inspect the working tree, preserve unrelated user changes, run proportionate validation, and review the final diff.
