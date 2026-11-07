param([string]$FxcPath)
$ErrorActionPreference = 'Stop'
if (-not $FxcPath) {
    $sdk = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits/10/bin'
    $FxcPath = Get-ChildItem -LiteralPath $sdk -Filter fxc.exe -Recurse |
        Where-Object FullName -Like '*\x64\fxc.exe' |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $FxcPath -or -not (Test-Path -LiteralPath $FxcPath)) { throw 'Install the Windows SDK HLSL compiler, or pass -FxcPath.' }
& $FxcPath /nologo /T ps_2_0 /E Main /O3 /Fo (Join-Path $PSScriptRoot 'InkDistortion.ps') (Join-Path $PSScriptRoot 'InkDistortion.hlsl')
if ($LASTEXITCODE -ne 0) { throw 'InkDistortion shader compilation failed.' }
