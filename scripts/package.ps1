[CmdletBinding()]
param(
    [Alias('AppVersion')]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0',
    [string]$Publisher = 'Kourosh Soleimani',
    [string]$RepoUrl = 'https://github.com/kpasargad/nava',
    [string]$IsccPath = '',
    [string]$SourceDir = '',
    [string]$ArtifactsDirectory = '',
    [string]$DotnetPath = 'dotnet',
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectFile = Join-Path $projectRoot 'Nava.csproj'
$installerScript = Join-Path $projectRoot 'packaging\Nava.iss'

foreach ($value in @($Publisher, $RepoUrl)) {
    if ($value -match '["\r\n]') { throw 'Installer metadata must not contain quotes or line breaks.' }
}
$repositoryUri = $null
if (-not [Uri]::TryCreate($RepoUrl, [UriKind]::Absolute, [ref]$repositoryUri) -or
    $repositoryUri.Scheme -ne 'https' -or $repositoryUri.UserInfo.Length -gt 0) {
    throw 'RepoUrl must be an HTTPS URL without embedded credentials.'
}

if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    $ArtifactsDirectory = Join-Path $projectRoot 'artifacts'
}
$artifactsRoot = [IO.Path]::GetFullPath($ArtifactsDirectory)
$releaseDirectory = Join-Path $artifactsRoot 'release'
$runId = [Guid]::NewGuid().ToString('N')
$runDirectory = Join-Path (Join-Path $artifactsRoot 'app') $runId
$payloadDirectory = Join-Path $runDirectory 'payload'

# Every run gets a fresh staging directory. No prior publish, user data, or
# release is recursively deleted or moved by this script.
[void][IO.Directory]::CreateDirectory($runDirectory)
[void][IO.Directory]::CreateDirectory($releaseDirectory)

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $publishDirectory = Join-Path $runDirectory 'publish'
    $numericVersion = ($Version -split '[-+]')[0] + '.0'
    $publishArguments = @(
        'publish', $projectFile, '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-o', $publishDirectory,
        '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false',
        "-p:Version=$Version", "-p:AssemblyVersion=$numericVersion", "-p:FileVersion=$numericVersion"
    )
    if ($NoRestore) { $publishArguments += '--no-restore' }
    & $DotnetPath @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    $SourceDir = $publishDirectory
}
$sourceDirectory = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $SourceDir).Path)
$sourcePrefix = [IO.Path]::TrimEndingDirectorySeparator($sourceDirectory) + [IO.Path]::DirectorySeparatorChar
if ($payloadDirectory.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'SourceDir must select one completed publish folder, not a parent of staging.'
}

foreach ($required in @('Nava.exe', 'Nava.dll', 'Nava.deps.json', 'Nava.runtimeconfig.json', 'coreclr.dll', 'hostpolicy.dll', 'nava.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $required) -PathType Leaf)) {
        throw "The source is not a complete self-contained Windows publish: missing $required."
    }
}

function Test-DistributionFile {
    param([IO.FileInfo]$File, [string]$RelativePath)
    if (($File.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
    $parts = $RelativePath -split '[\\/]'
    foreach ($part in $parts) {
        if ($part.StartsWith('.') -or $part -in @('Data', 'History', 'Audio', 'Records', 'Tests', 'obj', 'node_modules')) {
            return $false
        }
    }
    if ($File.Name -match '^(?:credentials(?:\..*)?|settings\.json|installed\.(?:flag|marker)|unins\d*\..*)$') {
        return $false
    }
    if ($File.Extension -eq '.json') {
        return $File.Name -like '*.deps.json' -or $File.Name -like '*.runtimeconfig.json'
    }
    if ($File.Extension -eq '.txt') {
        return $File.Name -in @('ThirdPartyNotices.txt', 'LICENSE.txt', 'NOTICE.txt')
    }
    return $File.Extension -in @('.exe', '.dll', '.ico', '.dat', '.manifest', '.png', '.jpg', '.jpeg', '.svg', '.woff', '.woff2', '.ttf', '.otf')
}

[void][IO.Directory]::CreateDirectory($payloadDirectory)
$distributionFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -File -Recurse -Force | Sort-Object FullName)
foreach ($file in $distributionFiles) {
    $relative = [IO.Path]::GetRelativePath($sourceDirectory, $file.FullName)
    if (-not (Test-DistributionFile -File $file -RelativePath $relative)) { continue }
    $destination = Join-Path $payloadDirectory $relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    [IO.File]::Copy($file.FullName, $destination, $false)
}

foreach ($documentName in @('README.md', 'LICENSE', 'LICENSE.txt', 'NOTICE', 'NOTICE.txt')) {
    $document = Join-Path $projectRoot $documentName
    if (Test-Path -LiteralPath $document -PathType Leaf) {
        [IO.File]::Copy($document, (Join-Path $payloadDirectory $documentName), $true)
    }
}

# Keep README-relative release links usable without copying unrelated project
# documents, diagnostic captures, or future private notes.
foreach ($documentRelativePath in @('SECURITY.md', 'docs\TESTING.md', 'docs\VERIFICATION.md', 'docs\images\characters.png')) {
    $document = Join-Path $projectRoot $documentRelativePath
    if (Test-Path -LiteralPath $document -PathType Leaf) {
        $destination = Join-Path $payloadDirectory $documentRelativePath
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        [IO.File]::Copy($document, $destination, $true)
    }
}

# Fail without printing the matching material if release text contains a real
# authorization value, credential-bearing URL, or a private key.
foreach ($file in Get-ChildItem -LiteralPath $payloadDirectory -File -Recurse) {
    if ($file.Extension -notin @('.json', '.md', '.txt') -and $file.Name -notin @('LICENSE', 'NOTICE')) { continue }
    $content = [IO.File]::ReadAllText($file.FullName)
    if ($content -match '(?i)\bBearer\s+[A-Za-z0-9_./+=:-]{20,}' -or
        $content -match '(?i)https?://[^\s/@:"]+:[^\s/@"]+@' -or
        $content -match '(?i)"(?:ApiKey|api_key|ProxyUrl|password)"\s*:\s*"[^"\r\n]{16,}"' -or
        $content -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----') {
        throw ('Potential credential material in release text: ' + [IO.Path]::GetRelativePath($payloadDirectory, $file.FullName))
    }
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $compilerCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $compilerCommand) { $IsccPath = $compilerCommand.Source }
    else {
        $compilerRoots = @($env:LOCALAPPDATA, $env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        foreach ($compilerRoot in $compilerRoots) {
            foreach ($relativeCompiler in @('Programs\Inno Setup 7\ISCC.exe', 'Programs\Inno Setup 6\ISCC.exe', 'Inno Setup 7\ISCC.exe', 'Inno Setup 6\ISCC.exe')) {
                $candidate = Join-Path $compilerRoot $relativeCompiler
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $IsccPath = $candidate; break }
            }
            if (-not [string]::IsNullOrWhiteSpace($IsccPath)) { break }
        }
    }
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup compiler was not found. Pass -IsccPath with the ISCC.exe path.'
}
$compilerArguments = @(
    "/DSourceDir=$payloadDirectory", "/DOutputDir=$releaseDirectory", "/DAppVersion=$Version",
    "/DAppPublisher=$Publisher", "/DRepoUrl=$RepoUrl", $installerScript
)
& $IsccPath @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
$installerPath = Join-Path $releaseDirectory "Nava-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw 'Installer compiler did not produce the expected file.' }

$portablePath = Join-Path $releaseDirectory "Nava-$Version-win-x64-portable.zip"
$temporaryZip = Join-Path $releaseDirectory ".portable-$runId.tmp"
$zipStream = [IO.File]::Open($temporaryZip, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$archive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    foreach ($file in Get-ChildItem -LiteralPath $payloadDirectory -File -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($payloadDirectory, $file.FullName).Replace('\', '/')
        $entry = $archive.CreateEntry('Nava/' + $relative, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $entryStream = $entry.Open()
        $inputStream = $file.OpenRead()
        try { $inputStream.CopyTo($entryStream) }
        finally { $inputStream.Dispose(); $entryStream.Dispose() }
    }
}
finally { $archive.Dispose(); $zipStream.Dispose() }
Move-Item -LiteralPath $temporaryZip -Destination $portablePath -Force

$hashLines = foreach ($asset in @($installerPath, $portablePath) | Sort-Object) {
    $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    $hash + '  ' + [IO.Path]::GetFileName($asset)
}
$checksumsPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
[IO.File]::WriteAllText($checksumsPath, ($hashLines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    Version = $Version
    PayloadDirectory = $payloadDirectory
    Installer = $installerPath
    PortableArchive = $portablePath
    Checksums = $checksumsPath
    IncludedFiles = @(Get-ChildItem -LiteralPath $payloadDirectory -File -Recurse).Count
}
