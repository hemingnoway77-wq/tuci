$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & (Join-Path $PSScriptRoot 'get-offline-translation-models.ps1') -VerifyOnly
    & (Join-Path $PSScriptRoot 'verify-offline-dictionary.ps1')
    & dotnet restore VocabCollector.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    & dotnet test VocabCollector.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    & dotnet build VocabCollector.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    & dotnet run --project tests/VocabCollector.Desktop.Smoke -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'WPF smoke checks failed.' }
    & dotnet run --project tests/VocabCollector.Desktop.Smoke -c Release --no-build -- --practice-only
    if ($LASTEXITCODE -ne 0) { throw 'Roaming practice checks failed.' }
    & dotnet run --project tests/VocabCollector.Desktop.Smoke -c Release --no-build -- --reading-only
    if ($LASTEXITCODE -ne 0) { throw 'Article reading navigation checks failed.' }
    & dotnet run --project tests/VocabCollector.Desktop.Smoke -c Release --no-build -- --startup-only
    if ($LASTEXITCODE -ne 0) { throw 'Prepared startup checks failed.' }
    & dotnet run --project tests/VocabCollector.Desktop.Smoke -c Release --no-build -- --navigation-only
    if ($LASTEXITCODE -ne 0) { throw 'Rapid module navigation checks failed.' }
    & dotnet run --project tests/VocabCollector.Desktop.Smoke -c Release --no-build -- --updates-only
    if ($LASTEXITCODE -ne 0) { throw 'Signed update UI checks failed.' }
    & dotnet run --project tests/VocabCollector.Desktop.Smoke -c Release --no-build -- --offline-translation-only
    if ($LASTEXITCODE -ne 0) { throw 'Bundled offline translation checks failed.' }
    Write-Output 'PASS: all verification checks.'
} finally { Pop-Location }
