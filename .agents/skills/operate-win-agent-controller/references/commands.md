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

With a current Agent, `copy` negotiates raw binary streaming inside the authenticated TLS session and defaults to 64 MiB chunks. It hashes each chunk while streaming, keeps active session state in memory, and persists a resume checkpoint every 256 MiB. An older Agent automatically falls back to the 4 MiB JSON path. The CLI writes the transfer session ID first and the current invocation's byte count, elapsed seconds, and MiB/s at completion to stderr while keeping the JSON result on stdout. Pass `--session <transferSessionId>` to continue an existing upload or download; after an abrupt interruption, up to the most recent 256 MiB may be retransmitted. Confirm before overwriting data. Do not try to escape the configured root through absolute paths, `..`, junctions, or reparse points.

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
