param([string]$OutputDirectory = 'dist/win-x64')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & (Join-Path $PSScriptRoot 'verify.ps1')
    & dotnet publish src/VocabCollector.Desktop/VocabCollector.Desktop.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded -p:DebugSymbols=false -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    $exePath = Join-Path $OutputDirectory '兔词.exe'
    $exePath = (Resolve-Path -LiteralPath $exePath).Path
    if (-not (Test-Path -LiteralPath $exePath)) { throw 'Published exe is missing.' }
    $artifact = Get-Item -LiteralPath $exePath
    $hash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash
    [ordered]@{ file = '兔词.exe'; bytes = $artifact.Length; sha256 = $hash; platform = 'Windows x64'; verification = 'unit tests and real WPF controls with local mock API' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifact.DirectoryName 'release.json') -Encoding utf8
    Write-Output "Published: $exePath"
    Write-Output "Bytes: $($artifact.Length); SHA256: $hash"
} finally { Pop-Location }
