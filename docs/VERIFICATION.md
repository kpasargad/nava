# Release verification

Nava 1.0.0 targets Windows 10/11 x64 and builds with .NET 10. Distribution packages include the desktop runtime and do not require a separately installed .NET runtime.

## Coverage

The regression runner covers encrypted credential storage, malformed/legacy setting recovery, safe audio cleanup, transcript export, global hold/release state transitions, parser normalization, streaming errors, proxy retry boundaries, native gateway compatibility, ten stable character IDs, character selection persistence, and credential-free installation defaults.

App-owned WPF controls are rendered with synthetic Persian/English content at compact and expanded sizes. The character gallery includes all ten silhouettes at both large and tiny sizes. These are internal render snapshots, not desktop or microphone automation.

Optional live checks use generated speech only and verify text arrives before release, the final words are retained, one held gesture creates one note, and repeated release is harmless. No live service or credentials are required by ordinary tests or CI.

## Packaging

The installer is per user, creates Start-menu access and an optional desktop shortcut, and preserves data in LocalAppData when updating or uninstalling. Portable builds retain data next to the executable. Installer and archive payloads are created in a fresh directory from an application-file allowlist; local credentials, recordings, test results, debug symbols and history are excluded. SHA256SUMS accompanies the downloadable assets.

The GitHub repository and release are private. No service credentials or private connection endpoints are required in the source or release. The installer itself is unsigned; users should obtain it from the authenticated repository release.

## Manual environment checks

Physical microphone quality, keyboard behavior across other applications, optional paste, multiple-monitor DPI behavior, and operating-system installation UI depend on the target user session. Synthetic controller checks do not pretend to be physical keyboard or microphone tests.