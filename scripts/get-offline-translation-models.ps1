#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../src/VocabCollector.Desktop/Assets/OfflineTranslation'),
    [string]$ManifestPath = (Join-Path $PSScriptRoot '../src/VocabCollector.Desktop/Assets/OfflineTranslation/models.manifest.json'),
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-PlainPath([string]$Path) {
    $probe = [IO.Path]::GetFullPath($Path)
    while ($probe) {
        if (Test-Path -LiteralPath $probe) {
            $item = Get-Item -LiteralPath $probe -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a linked path: $probe"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($probe)
        if ($parent -eq $probe) { break }
        $probe = $parent
    }
}

function Resolve-AssetPath([string]$RelativePath) {
    if ($RelativePath -notmatch '\A[A-Za-z0-9][A-Za-z0-9._-]*(?:/[A-Za-z0-9][A-Za-z0-9._-]*)*\z') {
        throw "Invalid asset path in manifest: $RelativePath"
    }
    $resolved = [IO.Path]::GetFullPath((Join-Path $script:assetRoot $RelativePath))
    if (-not $resolved.StartsWith($script:rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Asset path escapes the destination: $RelativePath"
    }
    Assert-PlainPath $resolved
    return $resolved
}

function Test-Asset([string]$Path, [long]$Bytes, [string]$Sha256) {
    Assert-PlainPath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -ne $Bytes) { return $false }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256
}

function Assert-Asset([string]$Path, [long]$Bytes, [string]$Sha256) {
    if (-not (Test-Asset $Path $Bytes $Sha256)) {
        throw "Size or SHA256 verification failed: $Path"
    }
}

$assetRoot = [IO.Path]::GetFullPath($OutputDirectory)
$rootPrefix = $assetRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
Assert-PlainPath $ManifestPath
Assert-PlainPath $assetRoot
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.formatVersion -ne 1 -or $manifest.sourceLanguage -ne 'en' -or $manifest.targetLanguage -ne 'zh') {
    throw 'Unsupported offline translation manifest.'
}
$assets = @($manifest.files) + @($manifest.license)
$paths = @{}
foreach ($asset in $assets) {
    $null = Resolve-AssetPath $asset.path
    if ($paths.ContainsKey($asset.path)) { throw "Duplicate asset path: $($asset.path)" }
    $paths[$asset.path] = $true
    if ([long]$asset.bytes -le 0 -or $asset.sha256 -notmatch '\A[0-9a-fA-F]{64}\z') { throw 'Invalid asset size or checksum.' }
    $source = [Uri]$asset.sourceUrl
    if (-not $source.IsAbsoluteUri -or $source.Scheme -ne 'https' -or
        $source.Host -notin @('storage.googleapis.com', 'raw.githubusercontent.com')) {
        throw "Unsupported asset source: $source"
    }
    if ($asset.PSObject.Properties.Name -contains 'gzip' -and
        ([long]$asset.gzip.bytes -le 0 -or $asset.gzip.sha256 -notmatch '\A[0-9a-fA-F]{64}\z')) {
        throw 'Invalid compressed size or checksum.'
    }
}

if ($VerifyOnly) {
    foreach ($asset in $assets) {
        Assert-Asset (Resolve-AssetPath $asset.path) $asset.bytes $asset.sha256
        Write-Output "Verified: $($asset.path)"
    }
    Write-Output 'Offline translation model and license verified without network access.'
    return
}

if (Test-Path -LiteralPath $assetRoot -PathType Leaf) { throw 'The output directory is a file.' }
$null = [IO.Directory]::CreateDirectory($assetRoot)
Assert-PlainPath $assetRoot
$lockPath = Resolve-AssetPath 'offline-models.download.lock'
$lock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite,
    [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
$temporaryFiles = [Collections.Generic.List[string]]::new()
$pending = [Collections.Generic.List[object]]::new()
try {
    foreach ($asset in $assets) {
        $target = Resolve-AssetPath $asset.path
        if (Test-Asset $target $asset.bytes $asset.sha256) {
            Write-Output "Already verified: $($asset.path)"
            continue
        }
        $archive = Resolve-AssetPath ('download-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        $staged = Resolve-AssetPath ('unpacked-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        $temporaryFiles.Add($archive); $temporaryFiles.Add($staged)
        Write-Output "Downloading pinned asset: $($asset.path)"
        Invoke-WebRequest -UseBasicParsing -Uri $asset.sourceUrl -OutFile $archive -TimeoutSec 120 -MaximumRedirection 0
        if ($asset.PSObject.Properties.Name -contains 'gzip') {
            Assert-Asset $archive $asset.gzip.bytes $asset.gzip.sha256
            $inputFile = [IO.File]::OpenRead($archive)
            try {
                $gzip = [IO.Compression.GZipStream]::new($inputFile, [IO.Compression.CompressionMode]::Decompress)
                try {
                    $outputFile = [IO.File]::Open($staged, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try { $gzip.CopyTo($outputFile) } finally { $outputFile.Dispose() }
                } finally { $gzip.Dispose() }
            } finally { $inputFile.Dispose() }
        } else {
            [IO.File]::Move($archive, $staged)
        }
        Assert-Asset $staged $asset.bytes $asset.sha256
        $pending.Add([pscustomobject]@{ Asset = $asset; Target = $target; Staged = $staged })
    }

    # Validate every download before replacing any target. Each same-volume
    # replacement is atomic; a download/checksum failure leaves all targets alone.
    foreach ($item in $pending) {
        Assert-PlainPath $item.Target
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($item.Target))
        Assert-PlainPath $item.Target
        if (-not (Test-Asset $item.Target $item.Asset.bytes $item.Asset.sha256)) {
            if (Test-Path -LiteralPath $item.Target) { [IO.File]::Replace($item.Staged, $item.Target, [NullString]::Value) }
            else { [IO.File]::Move($item.Staged, $item.Target) }
        }
        Assert-Asset $item.Target $item.Asset.bytes $item.Asset.sha256
        Write-Output "Verified: $($item.Asset.path)"
    }
    Write-Output 'Pinned offline translation model and license are ready.'
} finally {
    try {
        foreach ($temporaryFile in $temporaryFiles) {
            # Only exact files created by this invocation are removed, never folders.
            if (-not [IO.Path]::GetFullPath($temporaryFile).StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Unexpected temporary file path.'
            }
            Assert-PlainPath $temporaryFile
            if (Test-Path -LiteralPath $temporaryFile -PathType Leaf) { Remove-Item -LiteralPath $temporaryFile -Force }
        }
    } finally { $lock.Dispose() }
}
