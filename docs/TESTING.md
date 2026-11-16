# Nava verification

Native shell-policy checks create only synthetic Nava windows, verify all native
tool-window flags through show/hide/restore, and exercise workspace reopening:

```powershell
dotnet run --project Tests/Nava.Tests.csproj -c Release -- --shell-window-smoke .
dotnet run --project Tests/Nava.Tests.csproj -c Release -- --inspect-shell <Nava-PID>
```

The installed-process inspector is read-only: it checks visible Nava HWND flags
without reading titles, transcript text, screenshots, or sending any input.

The mini-button WPF regression checks center and corner input paths, full target
pixel coverage, and click routing for all ten character choices. It uses detached
controls and does not claim a physical mouse check. For an optional visible
fixture with isolated settings and no recording or network activity, run:

```powershell
dotnet run --project Tests/Nava.Tests.csproj -c Release -- --overlay-pointer .
```

The fixture exits after two minutes and writes geometry/expanded-state metadata
to `.local/pointer-smoke-state.json`.

The regression runner uses only .NET libraries and the app project. It does not
record a microphone, upload user audio, log credentials, or need a package feed.

```powershell
dotnet build Nava.csproj -c Release
dotnet run --project Tests/Nava.Tests.csproj -c Release
```

Detached WPF previews use synthetic data and render the app's own visual trees;
they do not show, control, or capture any desktop window:

```powershell
dotnet run --project Tests/Nava.Tests.csproj -c Release -- --visual-smoke .\.local
```

The opt-in live mode requires the generated `.local/test-english.wav` fixture and
Windows-user encrypted `.local/credentials.bin`. It sends only that synthetic
fixture, tests health, realtime completion, the full batch workflow, formatting,
analysis, and speaker naming, and prints status/lengths rather than credentials
or transcript contents. A failed create request emits a redacted validation body.

```powershell
dotnet run --project Tests/Nava.Tests.csproj -c Release -- --live .
```

Use `--live-rt` or `--live-batch` for health plus only the named audio path.
Realtime checks require nonempty streamed text before the finish signal, report
first callback latency, verify the ending words from the full generated fixture,
and save the synthetic final result to `.local/live-rt-result.json`.
Batch stores its remote job ID at `.local/live-test-pending-id.txt` immediately
after creation and resumes that job on the next run. Progress prints stage names;
completed synthetic output is stored in `.local/live-test-result.json`.
On a detected native Soniox service, ordinary `--live` reports analysis,
formatting, and remote naming as unavailable instead of making expected-failing
requests. `--live-strict` exercises the originally supplied extended contract.

The real controller can be exercised without any desktop window, hotkey
registration, microphone access, sound, or clipboard operation:

```powershell
dotnet run --project Tests/Nava.Tests.csproj -c Release -- --controller-live .
```

This mode creates a base WPF `Application` on an STA thread and injects a
synthetic audio capture implementation into the production controller. It sends
the generated speech in 100 ms frames at speech speed, verifies text arrives while
PTT remains held, checks repeat-down/up idempotency, and checks one completed
history record and temporary-audio cleanup after release. Test output contains
counts and timings; the synthetic result stays in an isolated
`.local/controller-ptt-smoke/<id>/` directory.

Covered behavior:

- First-run defaults and explicit opt-in for automatic paste and Windows startup.
- Windows-user DPAPI credential round trip; plaintext key/password absence.
- Recovery from malformed settings/credentials/history files.
- Persian/English transcript, speaker names, analyses, and failed job persistence.
- Atomic record replacement and preserving the original of imported audio.
- Audio cleanup restricted to the app-owned audio directory.
- Readable SRT cue grouping, speaker/long-pause boundaries, valid timestamps,
  text-only results, and ensuring export does not mutate original tokens.
- Final-token parsing, second/millisecond conversion, realtime error-code null/zero
  handling, and bounded credential-redacted error details.
- Local loopback HTTP fixtures exercise the full custom upload workflow, confirm
  the service bearer is never sent to presigned storage, exercise native Soniox
  fallback/model selection, verify resume does not upload/create duplicates,
  and verify HTTP authentication errors never trigger proxy retry. These tests
  use placeholder credentials and synthetic bytes only.

The 1.0.1 failure regressions inject locked temporary history and settings files,
fake audio capture, and synthetic completion. They check that a failed initial
save never leaves capture open, cancel and completion failures clear recording
and busy state, recoverable audio survives a failed save, and an unreadable
pending record does not prevent startup. Settings-write failure must preserve
the previous credentials and endpoint. Null history fields are normalized before
rendering or export. Ordinary tests also check that WAV recovery leaves unrelated
or differently sampled audio unchanged, and automatic paste requires the original
foreground window with released modifiers.

The detached WPF smoke includes behavior assertions in addition to images:
duration text updates when capture finishes, realtime property notifications
share one presentation frame, and a layout update cannot reset a window's position
while it is being dragged. These checks exercise own controls, not physical mouse
movement or monitor switching.

An explicit local microphone smoke is available separately:

```powershell
dotnet run --project Tests/Nava.Tests.csproj -c Release -- --native-capture .
```

It captures two short 450 ms local recordings, checks PCM/header/sample counts and
stop timing, and deletes its own temporary audio. It never uploads, plays back,
or prints captured audio. Running it requires a microphone and is not part of
ordinary tests or CI. A passing capture check does not establish transcription
quality, physical shortcut behavior, or microphone permission UX.

For native UI checks, the installed app supports direct navigation without
launching a second instance:

```powershell
& "$env:LOCALAPPDATA\Programs\Nava\Nava.exe" --settings
& "$env:LOCALAPPDATA\Programs\Nava\Nava.exe" --history
```

Verify each command also works with an already running app. `--settings` must open
settings; `--history` must open notes. These commands do not themselves validate
microphone or input behavior.

Manual native UI checks should verify holding NumLock starts one recording and
releasing it stops capture, holding a shortcut does not retrigger it, NumLock's
existing toggle state is preserved, a conflicting shortcut preserves the
previous working shortcut, closing the library keeps the tray app available,
and exit during recording offers a clear choice. Test a lost network connection,
cancel, retry, and app restart with pending audio before calling recording
recovery complete. Test both microphone permissions denied and no input device.
Also check a held recording across Windows lock/sleep, cancel during connection
setup and finalization, repeated recordings, clipboard contention, and dragging
the live panel across monitors with different scaling. Record which native checks
were actually performed; source fixes and detached snapshots are not evidence
that these session-dependent paths passed.

Service checks must distinguish a successful health request from a real
transcription. Verify the complete create/upload/start/status/result sequence
using generated test audio, and verify realtime final tokens and the end-of-audio
handshake. Never describe these live paths as verified from unit tests alone.
