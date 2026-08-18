# WinAgentController command recipes

Set reusable values in PowerShell when running several commands:

```powershell
$target = '192.168.1.50:43001'
$fingerprint = '<SHA256>'
```

Alternatively, save and select a controller-side target profile:

```powershell
rcctl target add lab $target --fingerprint $fingerprint --text
rcctl target use lab --text
rcctl target refresh lab --timeout-ms 4000 --text
```

Use `lab` in place of `<IP:port>`, or omit both the endpoint and fingerprint after `target use`. A refresh may update only the endpoint discovered for the saved device ID and pinned fingerprint; discovery must never replace the saved fingerprint.

Use `--text` for readable output. Without it, preserve structured output when feeding results to automation.

## Execute commands and jobs

Run a one-shot command:

```powershell
rcctl exec $target --fingerprint $fingerprint --command 'whoami' --text
rcctl exec $target --fingerprint $fingerprint --shell cmd --command 'dir' --workdir 'C:\Temp' --text
```

Add `--elevated` only after confirming the operation requires administrator rights.

Use a job for long-running, interactive, or resumable work:

```powershell
rcctl job start $target --fingerprint $fingerprint --command 'ping -t 127.0.0.1' --text
rcctl job status $target --fingerprint $fingerprint --job <jobId> --text
rcctl job logs $target --fingerprint $fingerprint --job <jobId> --follow --text
rcctl job wait $target --fingerprint $fingerprint --job <jobId> --timeout-ms 60000 --text
```

Available lifecycle operations include `list`, `input`, `close-input`, `cancel`, and `resize`. Use `--pty` with `--cols` and `--rows` for terminal-style jobs. Use output offsets when resuming log reads.

## Read, write, and transfer files

All remote paths are constrained to `RC_AGENT_FILE_ROOT`. Prefer relative paths:

```powershell
rcctl fs list $target '.' --fingerprint $fingerprint --recursive
rcctl fs stat $target 'logs\agent.log' --fingerprint $fingerprint --text
rcctl fs read $target 'logs\agent.log' --fingerprint $fingerprint --offset 0 --max-bytes 262144 --text
rcctl fs write $target 'notes\hello.txt' --fingerprint $fingerprint --data 'hello'
rcctl fs write $target 'notes\binary.bin' --fingerprint $fingerprint --source .\binary.bin --overwrite
```

Transfer a file or directory and resume with the returned session ID:

```powershell
rcctl copy upload $target .\build-output --fingerprint $fingerprint --to 'incoming\build-output'
rcctl copy download $target 'incoming\build-output' --fingerprint $fingerprint --to .\restored
rcctl copy status $target <transferSessionId> --fingerprint $fingerprint
```

`copy` requires `binary-transfer-v1` and `streaming-integrity-v2`, uses raw binary streaming inside the authenticated TLS session, and defaults to 64 MiB chunks. Upgrade an outdated Agent when either capability is missing; the current CLI does not fall back to JSON. It hashes each chunk while streaming, keeps active session state in memory, and persists a resume checkpoint every 256 MiB. The CLI writes the transfer session ID first, reports `currentMiB/s` to stderr about once per second while bytes are moving, then prints `minMiB/s`, `maxMiB/s`, and `avgMiB/s` at completion. The JSON result remains isolated on stdout. Pass `--session <transferSessionId>` to continue an existing upload or download; after an abrupt interruption, up to the most recent 256 MiB may be retransmitted. Confirm before overwriting data. Do not try to escape the configured root through absolute paths, `..`, junctions, or reparse points.

For a real bidirectional benchmark with post-transfer SHA-256 verification, run `scripts/Test-RemoteControllerFileTransfer.ps1`. Its fixed dataset contains 100 files spanning 1 KiB through 5 MiB and one 1 GiB file. Compare the external process timings, use a fresh remote root for each run, and verify the reported digests before declaring success. Treat an unavailable comparison transport such as SCP as unavailable; do not substitute estimated throughput.

## Operate the interactive desktop

UI commands require the configured `UiUser` to have an active logged-in session and a recently registered UI Agent:

```powershell
rcctl ui status $target --fingerprint $fingerprint --text
rcctl ui displays $target --fingerprint $fingerprint --text
rcctl ui windows $target --fingerprint $fingerprint --text
rcctl ui elements $target --fingerprint $fingerprint window <handle> --depth 4 --limit 500 --text
rcctl ui screenshot $target --fingerprint $fingerprint window <handle>
```

Prefer semantic UI Automation operations over coordinates:

```powershell
rcctl ui element $target --fingerprint $fingerprint window <handle> <runtime-id> focus
rcctl ui element $target --fingerprint $fingerprint window <handle> <runtime-id> setvalue 'value'
rcctl ui element $target --fingerprint $fingerprint window <handle> <runtime-id> invoke
```

Use `window`, `move`, `mouse`, `key`, `shortcut`, `type`, and `clipboard` only with explicit targets. Secure desktops such as UAC prompts and the lock screen are outside normal UI automation support.

## Operate a browser

Browser control supports Edge or Chrome in the target user session. Use HTTPS URLs and an explicit window handle for navigation and DOM reads:

```powershell
rcctl ui browser $target --fingerprint $fingerprint launch edge https://example.com
rcctl ui browser $target --fingerprint $fingerprint navigate <handle> https://example.com/path
rcctl ui browser $target --fingerprint $fingerprint dom <handle> --depth 8 --limit 2000 --text
```

Prefer DOM or accessibility information to coordinate guessing. DOM access uses the controlled Chromium CDP session and is not a general attachment mechanism for arbitrary browser instances.

## Operating notes for agents

Pitfalls observed while operating a real target; follow these to operate reliably.

### Default shell and quoting

- The default remote shell is **Windows PowerShell 5.1**: `&&`/`||` are **not** valid separators. Use `--shell cmd` for `&&`, or `;` in PowerShell.
- PowerShell 5.1 lacks `Join-String` (use `-join`); `$home` is a reserved variable (do not assign it).
- Nested quotes and `$` variables are expanded by every layer (local pwsh → CLI → remote shell). Avoid `$` and backslashes inside `--command`; for complex scripts, pipe the script through stdin instead:
  ```powershell
  $script | ssh $host "powershell -NoProfile -Command -"
  ```
  Do not build `powershell -Command \"...\"` chains.
- Remote output may be GBK-encoded: Chinese filter words (e.g. `findstr "状态"`) can fail. Prefer ASCII patterns, or prefix `chcp 65001` when locale matters.
- When passing CLI arguments from PowerShell, do not pre-concatenate options into one string variable (`$fp = '--fingerprint x'` becomes a single argument); pass each argument separately or use an array.
- Under `--shell cmd`, avoid nested quotes around redirected paths: `echo x > "%USERPROFILE%\f.txt"` can fail through `cmd /c` argument splitting. Omit quotes when the path has no spaces (`echo x > %USERPROFILE%\f.txt`), or use a PowerShell one-liner instead.

### exec and jobs

- Prefer `fs list`/`fs stat` for read-only queries: every `rcctl exec` creates a job record on the target.
- Use `job start` for long-running work and keep `exec` commands short.
- For stdin-interactive jobs use a simple reader (e.g. `findstr .`) to avoid nested-escape corruption of the payload; write input with `job input` then close it with `job close-input`.

### File root constraint

- `fs`/`copy` are confined to `RC_AGENT_FILE_ROOT` (default: the Agent service account's user profile). Absolute paths outside it are rejected. To reach paths outside the root, use `exec`/`job` (process-level access) or an out-of-band transport such as SCP.
- To change the root: set `RC_AGENT_FILE_ROOT` in the `RemoteControllerAgent` service Environment (multi-string) and restart the service.

### UI and browser

- `ui window <handle> <action>` takes the handle **without** a `window` keyword (unlike `screenshot`/`elements`, which take `window <handle>`).
- `ui screenshot` returns JSON with base64 `result.pngBytes`; decode it to obtain the PNG file.
- `ui browser dom` output is JSON with `\uXXXX`-escaped Chinese text; decode or grep the escape sequences, not raw Chinese.
- DOM traversal skips inert nodes (style/script/head/svg), so `--limit` is spent on visible content; raise `--depth`/`--limit` for deeply nested pages.
- Killing a target user's process (e.g. a test browser) from the Agent requires `exec --elevated`; the LocalService account cannot terminate interactive-user processes.

### Long-running processes

- Processes started inside an SSH session (e.g. `Start-Process node ...`) are **killed when the session ends** (OpenSSH tree cleanup). Long-lived services belong in a scheduled task or Windows service; `Start-RemoteController.cmd` only starts already-installed services.
- Prefer the "create then `/run`" pattern for scheduled tasks; a `/st` earlier than now warns but still runs manually:
  ```powershell
  schtasks /create /tn RemoteControllerJobs /tr "powershell -NoProfile -Command -" /sc once /st 00:00 /f
  schtasks /run /tn RemoteControllerJobs
  ```

### Disk and service probes

- Do **not** stop `RemoteControllerBroker` alone while testing: the Agent service depends on it (`RemoteControllerAgent` DEPENDENCIES=RemoteControllerBroker), so Windows SCM stops the Agent too and the endpoint becomes unreachable (recover with `sc start RemoteControllerBroker` then `sc start RemoteControllerAgent`, or the pre-deployed test recovery channel). Prefer non-destructive negative tests (wrong port/fingerprint → connection or auth failure).
- `fsutil volume diskfree` is denied under LocalService and `wmic` may fail (exit 44029). Use a PowerShell 5.1-compatible .NET probe instead (also used by `rcctl health`):
  ```powershell
  powershell -NoProfile -Command "[IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady } | ForEach-Object { '{0} total={1:N1}GB free={2:N1}GB' -f $_.Name, ($_.TotalSize/1GB), ($_.AvailableFreeSpace/1GB) }"
  ```
- For a combined service/port/disk probe, run `rcctl health <IP:port> --fingerprint <SHA256> --text` (exit 0 when both Agent services are running and the control port is listening).
