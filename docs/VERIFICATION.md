# Release verification

Nava 1.0.3 targets Windows 10/11 x64 and builds with .NET 10. Distribution packages include the desktop runtime and do not require a separately installed .NET runtime.

## 1.0.3 shell visibility

Overlay, history/settings, and speaker-name windows use `ShowInTaskbar=false`
and native `WS_EX_TOOLWINDOW` without `WS_EX_APPWINDOW`. The policy remains in
place when WPF updates extended styles. It preserves input activation and
transparency. Nava owns file/confirmation dialogs, and opening the workspace
from its tray or character restores it after minimization. The Win32 behavior is
documented in [Microsoft's extended window styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles).

## 1.0.2 pointer fix

The mini character button now renders a full 40 by 44 DIP input surface. Its
control template binds the background, with a visually negligible nonzero alpha
so Windows layered-window hit testing retains the entire target. All ten
characters support the same click and drag area, including transparent margins.

Detached checks passed for center/corner hit paths and nonzero rendered alpha
throughout each character's target. Native mouse automation was attempted with
an isolated real overlay; the Windows automation helper could not activate the
captured layered window (`failed to activate captured window`), so this does not
claim a completed physical mouse test.

## Coverage

The regression runner covers encrypted credential storage, malformed/legacy setting recovery, safe audio cleanup, transcript export, global hold/release state transitions, parser normalization, streaming errors, proxy retry boundaries, native gateway compatibility, ten stable character IDs, character selection persistence, and credential-free installation defaults.

App-owned WPF controls are rendered with synthetic Persian/English content at compact and expanded sizes. The character gallery includes all ten silhouettes at both large and tiny sizes. These are internal render snapshots, not desktop or microphone automation.

Optional live checks use generated speech only and verify text arrives before release, the final words are retained, one held gesture creates one note, and repeated release is harmless. No live service or credentials are required by ordinary tests or CI.

## 1.0.1 regression scope

The patch addresses failure cleanup around opening/stopping capture, locked or
unwritable history, cancellation, and interrupted realtime sends. Pending audio
and existing settings remain recoverable when persistence fails. WAV repair is
restricted to the app's expected PCM layout. Windows lock/suspend releases held
capture, and optional paste requires the original focused window instead of
bringing a background window forward.

The UI updates the duration label after capture, combines realtime notifications
into one presentation frame, avoids scrolling a hidden library, and defers
position changes during a drag. `Nava.exe --settings` and `Nava.exe --history`
open the requested view in the existing instance when one is running.

The regression runner injects failures into its own temporary files and fake
capture. Detached WPF checks cover binding freshness, queued presentation, and
drag-layout deferral. See [TESTING](TESTING.md) for commands and the separate,
opt-in physical capture test. This scope describes automated checks and source
changes; it does not claim that microphone quality, cross-app keyboard behavior,
lock/sleep, or multiple-monitor interaction has passed manual testing.

## Packaging

The installer is per user, creates Start-menu access and an optional desktop shortcut, and preserves data in LocalAppData when updating or uninstalling. Portable builds retain data next to the executable. Installer and archive payloads are created in a fresh directory from an application-file allowlist; local credentials, recordings, test results, debug symbols and history are excluded. SHA256SUMS accompanies the downloadable assets.

Packaging resolves the exact .NET Core and Windows Desktop runtime packs from
restore metadata and the completed publish's dependency manifest. NuGet cache
roots come from `obj/project.assets.json`; no developer-specific cache path or
runtime version is hardcoded. Both runtime licenses are combined in `LICENSE.txt`,
the Core runtime's third-party notices are included in `ThirdPartyNotices.txt`,
and any repository license is retained. Missing runtime license/notice material
stops packaging instead of silently omitting it. When using `-SourceDir`, keep a
completed restore and the runtime packs for that publish available locally.

The GitHub repository and release are private. No service credentials or private connection endpoints are required in the source or release. The installer itself is unsigned; users should obtain it from the authenticated repository release.

## Manual environment checks

Physical microphone quality, keyboard behavior across other applications, optional paste, multiple-monitor DPI behavior, and operating-system installation UI depend on the target user session. Synthetic controller checks do not pretend to be physical keyboard or microphone tests.
