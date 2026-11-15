$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish Nava.csproj -c Release -o dist --no-self-contained
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    Copy-Item -LiteralPath 'README.md' -Destination 'dist\README.md' -Force
    $bootstrap = Join-Path $PSScriptRoot '.local\credentials.bin'
    $destination = Join-Path $PSScriptRoot 'dist\Data'
    if (Test-Path -LiteralPath $bootstrap) {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        if (-not (Test-Path -LiteralPath (Join-Path $destination 'credentials.bin'))) {
            Copy-Item -LiteralPath $bootstrap -Destination (Join-Path $destination 'credentials.bin')
        }
    }
    Write-Output 'Ready: dist\Nava.exe'
} finally { Pop-Location }
