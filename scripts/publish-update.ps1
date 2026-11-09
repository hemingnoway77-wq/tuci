param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string[]]$ReleaseNotes,
    [string]$KeyDirectory = 'D:/CodexData/home/secrets/tuci-updates',
    [string]$KnownHostsPath = 'D:/CodexData/cache/tuci-known_hosts',
    [switch]$PackageOnly,
    [switch]$SkipBuild,
    [switch]$SkipVerification
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -notmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') { throw 'Version must use major.minor.patch.' }
if (!$ReleaseNotes.Count -or $ReleaseNotes.Count -gt 40 -or @($ReleaseNotes | Where-Object { !$_.Trim() -or $_.Length -gt 2000 }).Count) { throw 'Provide 1-40 short release notes.' }
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    if (Test-Path -LiteralPath 'D:/DevEnv/dotnet10-locked/dotnet.exe') {
        $env:DOTNET_ROOT = 'D:/DevEnv/dotnet10-locked'
        $env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
        $env:DOTNET_CLI_HOME = 'D:/CodexData/cache/dotnet-cli-home'
        $env:NUGET_PACKAGES = 'D:/CodexData/cache/nuget/packages'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    }
    $releaseDirectory = Join-Path $repoRoot ('dist/releases/' + $Version)
    $applicationDirectory = Join-Path $releaseDirectory 'win-x64'
    if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'publish.ps1') -OutputDirectory $applicationDirectory -Version $Version -SkipVerification:$SkipVerification }
    $release = Get-Content -LiteralPath (Join-Path $applicationDirectory 'release.json') -Raw | ConvertFrom-Json
    if ($release.version -ne $Version) { throw 'Published application and requested versions differ.' }
    $application = Get-Item -LiteralPath (Join-Path $applicationDirectory '兔词.exe')
    if ((Get-FileHash -LiteralPath $application.FullName -Algorithm SHA256).Hash -ne $release.sha256) { throw 'Published executable integrity check failed.' }
    if (!(Test-Path -LiteralPath (Join-Path $applicationDirectory '兔词更新器.exe'))) { throw 'The release must include its standalone updater.' }
    $packageName = 'tuci-win-x64-' + $Version + '.zip'
    $packagePath = Join-Path $releaseDirectory $packageName
    if (!(Test-Path -LiteralPath $packagePath)) { [IO.Compression.ZipFile]::CreateFromDirectory($applicationDirectory, $packagePath, [IO.Compression.CompressionLevel]::Optimal, $false) }
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entries = @($archive.Entries | Where-Object { !$_.FullName.EndsWith('/') })
        $files = @(Get-ChildItem -LiteralPath $applicationDirectory -Recurse -File)
        if ($entries.Count -ne $files.Count) { throw 'This version already has a different ZIP; increase the version before publishing.' }
        foreach ($entry in $entries) {
            $expected = [IO.Path]::GetFullPath((Join-Path $applicationDirectory $entry.FullName))
            if (!$expected.StartsWith($applicationDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $expected -PathType Leaf)) { throw 'The existing release ZIP differs from its application directory.' }
            $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)) } finally { $sha.Dispose(); $stream.Dispose() }
            if ($hash -ne (Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash) { throw 'This version already has a different ZIP; increase the version before publishing.' }
        }
    } finally { $archive.Dispose() }
    $package = Get-Item -LiteralPath $packagePath
    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $payload = [ordered]@{
        version = $Version; platform = 'win-x64'
        packageUrl = 'https://101.133.134.128/tuci-updates/packages/' + $packageName
        sizeBytes = $package.Length; sha256 = $packageHash; releaseNotes = @($ReleaseNotes)
        publishedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $payloadBytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 5 -Compress))
    $encryptedKey = [IO.File]::ReadAllBytes((Join-Path $KeyDirectory 'release-signing.dpapi'))
    $privateKey = [Security.Cryptography.ProtectedData]::Unprotect($encryptedKey, [Text.Encoding]::UTF8.GetBytes('TuciUpdatesSigningV1'), [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $readBytes = 0; $rsa.ImportPkcs8PrivateKey($privateKey, [ref]$readBytes)
        $signature = $rsa.SignData($payloadBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)
    } finally { [Array]::Clear($privateKey, 0, $privateKey.Length); $rsa.Dispose() }
    $envelope = [ordered]@{ payload = [Convert]::ToBase64String($payloadBytes); signature = [Convert]::ToBase64String($signature) }
    $manifestPath = Join-Path $releaseDirectory 'stable.json'
    $manifestText = $envelope | ConvertTo-Json -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($manifestText) -gt 65536) { throw 'The signed manifest exceeds 64 KiB. Shorten the release notes.' }
    [IO.File]::WriteAllText($manifestPath, $manifestText, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(($packagePath + '.sha256'), $packageHash + '  ' + $packageName + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-Output "Prepared version $Version; package $($package.Length) bytes."
    Write-Output "Package: $packagePath"
    if ($PackageOnly) { Write-Output "Signed manifest: $manifestPath"; return }
    $ssh = [Diagnostics.ProcessStartInfo]::new('ssh')
    $ssh.UseShellExecute = $false
    $ssh.RedirectStandardInput = $true; $ssh.RedirectStandardOutput = $true; $ssh.RedirectStandardError = $true
    foreach ($argument in @('-T','-o','BatchMode=yes','-o','IdentitiesOnly=yes','-o','StrictHostKeyChecking=yes','-o',('UserKnownHostsFile=' + [IO.Path]::GetFullPath($KnownHostsPath)),'-i',(Join-Path $KeyDirectory 'deploy_ed25519'),'root@101.133.134.128','publish-v1')) { $ssh.ArgumentList.Add($argument) }
    $upload = [Diagnostics.Process]::Start($ssh)
    if ($null -eq $upload) { throw 'Could not start the restricted publisher connection.' }
    try {
        $outputTask = $upload.StandardOutput.ReadToEndAsync(); $errorTask = $upload.StandardError.ReadToEndAsync()
        $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
        $header = [Text.Encoding]::ASCII.GetBytes((@{ manifestLength = $manifestBytes.Length; packageLength = $package.Length } | ConvertTo-Json -Compress) + "`n")
        $destination = $upload.StandardInput.BaseStream
        $destination.Write($header); $destination.Write($manifestBytes)
        $source = [IO.File]::OpenRead($packagePath)
        try { $source.CopyTo($destination, 1048576) } finally { $source.Dispose() }
        $upload.StandardInput.Close(); $upload.WaitForExit()
        $output = $outputTask.GetAwaiter().GetResult(); $failure = $errorTask.GetAwaiter().GetResult()
        if ($upload.ExitCode -ne 0) { throw ('Publishing failed: ' + $failure.Trim()) }
        $published = $output | ConvertFrom-Json
        if ($published.status -ne 'published' -or $published.version -ne $Version -or $published.sha256 -ne $packageHash) { throw 'The server did not confirm the expected release.' }
        $remote = Invoke-WebRequest -Uri 'https://101.133.134.128/tuci-updates/stable.json' -TimeoutSec 20
        if ($remote.Content.Trim() -ne ([IO.File]::ReadAllText($manifestPath)).Trim()) { throw 'The public feed differs from the uploaded signed manifest.' }
        Write-Output "Published and verified: $($published.manifestUrl)"
    } finally { $upload.Dispose() }
} finally { Pop-Location }
