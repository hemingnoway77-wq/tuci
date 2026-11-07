param([string]$OutputDirectory = 'dist/win-x64')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & (Join-Path $PSScriptRoot 'verify.ps1')
    & dotnet publish src/VocabCollector.Desktop/VocabCollector.Desktop.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:NuGetLockFilePath=packages.win-x64.lock.json -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded -p:DebugSymbols=false -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    $exePath = Join-Path $OutputDirectory '兔词.exe'
    $exePath = (Resolve-Path -LiteralPath $exePath).Path
    if (-not (Test-Path -LiteralPath $exePath)) { throw 'Published exe is missing.' }
    $artifact = Get-Item -LiteralPath $exePath
    $hash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash
    $bundlePath = Join-Path $artifact.DirectoryName 'Assets/OfflineTranslation'
    $dictionaryPath = Join-Path $artifact.DirectoryName 'Assets/OfflineDictionary'
    & dotnet run --project tests/VocabCollector.Desktop.Smoke -c Release --no-build -- --offline-translation-only --offline-bundle $bundlePath
    if ($LASTEXITCODE -ne 0) { throw 'Published offline translation bundle failed real-model checks.' }
    $offlineFiles = @(Get-ChildItem -LiteralPath @($bundlePath, $dictionaryPath) -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($artifact.DirectoryName, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [ordered]@{ file = '兔词.exe'; bytes = $artifact.Length; sha256 = $hash; platform = 'Windows x64'; verification = 'unit tests, real WPF controls, and actual bundled offline English-to-Chinese model'; offlineTranslation = [ordered]@{ language = 'en-zh'; requiresModelDownload = $false; files = $offlineFiles } } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifact.DirectoryName 'release.json') -Encoding utf8
    Write-Output "Published: $exePath"
    Write-Output "Bytes: $($artifact.Length); SHA256: $hash"
} finally { Pop-Location }
