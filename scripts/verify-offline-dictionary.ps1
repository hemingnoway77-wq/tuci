#Requires -Version 5.1
[CmdletBinding()]
param([string]$Directory = (Join-Path $PSScriptRoot '../src/VocabCollector.Desktop/Assets/OfflineDictionary'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($Directory)
$prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

function Assert-PlainPath([string]$Path) {
    $probe = [IO.Path]::GetFullPath($Path)
    while ($probe) {
        if (Test-Path -LiteralPath $probe) {
            if (((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a linked dictionary path: $probe"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($probe)
        if ($parent -eq $probe) { break }
        $probe = $parent
    }
}

Assert-PlainPath $root
$manifestPath = Join-Path $root 'dictionary.manifest.json'
Assert-PlainPath $manifestPath
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.formatVersion -ne 1) { throw 'Unsupported dictionary manifest version.' }
$seen = @{}
foreach ($file in $manifest.files) {
    if ($file.path -notmatch '\A[A-Za-z0-9][A-Za-z0-9._-]*\z' -or $seen.ContainsKey($file.path)) {
        throw "Invalid or duplicate dictionary asset path: $($file.path)"
    }
    $seen[$file.path] = $true
    $target = [IO.Path]::GetFullPath((Join-Path $root $file.path))
    if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Dictionary asset escapes its directory.' }
    Assert-PlainPath $target
    if ($file.sha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or [long]$file.bytes -lt 0 -or
        -not (Test-Path -LiteralPath $target -PathType Leaf) -or
        (Get-Item -LiteralPath $target).Length -ne [long]$file.bytes -or
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) {
        throw "Dictionary size or SHA256 verification failed: $($file.path)"
    }
}
foreach ($letter in 'abcdefghijklmnopqrstuvwxyz'.ToCharArray()) {
    if (-not $seen.ContainsKey("$letter.jsonl")) { throw "Dictionary shard is missing from manifest: $letter" }
}
if (-not $seen.ContainsKey('LICENSE.MIT.txt')) { throw 'Dictionary license is missing from manifest.' }
Write-Output "Offline dictionary verified without network access: 26 shards and MIT license ($($manifest.statistics.entryCount) entries)."
