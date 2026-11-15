# Nava verification

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

Manual native UI checks should verify holding NumLock starts one recording and
releasing it stops capture, holding a shortcut does not retrigger it, NumLock's
existing toggle state is preserved, a conflicting shortcut preserves the
previous working shortcut, closing the library keeps the tray app available,
and exit during recording offers a clear choice. Test a lost network connection,
cancel, retry, and app restart with pending audio before calling recording
recovery complete. Test both microphone permissions denied and no input device.

Service checks must distinguish a successful health request from a real
transcription. Verify the complete create/upload/start/status/result sequence
using generated test audio, and verify realtime final tokens and the end-of-audio
handshake. Never describe these live paths as verified from unit tests alone.
