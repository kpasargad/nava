# Security and private data

Nava stores API credentials using Windows DPAPI for the current Windows account.
Transcripts and retained audio are local files. Installed builds use
`%LOCALAPPDATA%\Nava`; portable builds use a `Data` folder next to the executable.
An optional request-specific proxy is used only for the configured transcription
workflow. System proxy settings are never modified.

Do not commit or attach `Data`, `.local`, credentials, transcripts, recordings,
or configuration files to an issue or release. Official packages are built from
a fresh staging directory and contain no user connection settings or API key.

Report a suspected issue privately to the repository owner. Avoid posting keys,
proxy passwords, audio or transcript contents in diagnostics.
