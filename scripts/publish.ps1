param([string]$OutputDirectory = 'dist/win-x64', [string]$Version, [switch]$SkipVerification)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Join-Path $PSScriptRoot '..')
try {
    if (!$Version) { $Version = ([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version }
    if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Version must use major.minor.patch.' }
    if (!$SkipVerification) { & (Join-Path $PSScriptRoot 'verify.ps1') }
    & dotnet publish src/VocabCollector.Desktop/VocabCollector.Desktop.csproj -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:RestoreLockedMode=true -p:NuGetLockFilePath=packages.win-x64.lock.json -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded -p:DebugSymbols=false -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    $exePath = Join-Path $OutputDirectory '兔词.exe'
    $exePath = (Resolve-Path -LiteralPath $exePath).Path
    if (-not (Test-Path -LiteralPath $exePath)) { throw 'Published exe is missing.' }
    $artifact = Get-Item -LiteralPath $exePath
    $updaterOutput = Join-Path '.artifacts/updater-publish' ($Version + '-' + [guid]::NewGuid().ToString('N'))
    & dotnet publish src/VocabCollector.Updater/VocabCollector.Updater.csproj -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:RestoreLockedMode=true -p:NuGetLockFilePath=packages.win-x64.lock.json -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded -p:DebugSymbols=false -o $updaterOutput
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained updater publish failed.' }
    Copy-Item -LiteralPath (Join-Path $updaterOutput '兔词更新器.exe') -Destination $artifact.DirectoryName -Force
    $hash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash
    $bundlePath = Join-Path $artifact.DirectoryName 'Assets/OfflineTranslation'
    $dictionaryPath = Join-Path $artifact.DirectoryName 'Assets/OfflineDictionary'
    & dotnet run --project tests/VocabCollector.Desktop.Smoke -c Release --no-build -- --offline-translation-only --offline-bundle $bundlePath
    if ($LASTEXITCODE -ne 0) { throw 'Published offline translation bundle failed real-model checks.' }
    $offlineFiles = @(Get-ChildItem -LiteralPath @($bundlePath, $dictionaryPath) -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($artifact.DirectoryName, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [ordered]@{ version = $Version; file = '兔词.exe'; bytes = $artifact.Length; sha256 = $hash; platform = 'Windows x64'; verification = 'unit tests, real WPF controls, rapid navigation, signed updates and actual bundled offline English-to-Chinese model'; offlineTranslation = [ordered]@{ language = 'en-zh'; requiresModelDownload = $false; files = $offlineFiles } } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifact.DirectoryName 'release.json') -Encoding utf8
    Write-Output "Published: $exePath"
    Write-Output "Bytes: $($artifact.Length); SHA256: $hash"
} finally { Pop-Location }
