$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & dotnet restore server/Tuci.Sync.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Account backend dependency restore failed.' }
    & python server/tests/integration.py
    if ($LASTEXITCODE -ne 0) { throw 'Legacy sync compatibility checks failed.' }
    & python server/tests/accounts.py
    if ($LASTEXITCODE -ne 0) { throw 'Account isolation and authentication checks failed.' }
    & node --test @((Get-ChildItem -LiteralPath web/tests -Filter '*.test.mjs').FullName)
    if ($LASTEXITCODE -ne 0) { throw 'Mobile web unit checks failed.' }
    & node web/tests/storage-account-run.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Mobile IndexedDB account checks failed. Install fake-indexeddb as documented in web/README.md.' }
    & node web/tests/account-roundtrip.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Windows to mobile account roundtrip failed.' }
    & python web/build.py
    if ($LASTEXITCODE -ne 0) { throw 'Mobile PWA build failed.' }
    Write-Output 'PASS: account backend, offline storage, Windows/mobile roundtrip and PWA build.'
} finally { Pop-Location }
