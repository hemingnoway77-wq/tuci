$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & dotnet restore VocabCollector.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    & dotnet test VocabCollector.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    & dotnet build VocabCollector.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    & dotnet run --project tests/VocabCollector.Desktop.Smoke -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'WPF smoke checks failed.' }
    Write-Output 'PASS: all verification checks.'
} finally { Pop-Location }
