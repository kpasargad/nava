$ErrorActionPreference = 'Stop'
$navaFixtureRoot = Join-Path (Split-Path $PSScriptRoot -Parent) '.local'
New-Item -ItemType Directory -Path $navaFixtureRoot -Force | Out-Null
$navaVoice = New-Object -ComObject SAPI.SpVoice
$navaStream = New-Object -ComObject SAPI.SpFileStream
$navaStream.Format.Type = 18
$navaStream.Open((Join-Path $navaFixtureRoot 'test-english.wav'), 3, $false)
$navaVoice.AudioOutputStream = $navaStream
try {
    $navaVoice.Speak('Hello. This is a test of Nava, the small voice assistant. Please write the meeting notes. Our next meeting is on Monday at ten. Thank you.') | Out-Null
} finally {
    $navaStream.Close()
}
Write-Output 'Generated .local/test-english.wav; no microphone was opened.'
