param(
    [switch]$SkipBuild,
    [switch]$SkipPublicVerification,
    [string]$IdentityDirectory = 'D:/CodexData/home/secrets/tuci-updates',
    [string]$KnownHostsPath = 'D:/CodexData/cache/tuci-known_hosts'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    if (!$SkipBuild) { & node website/build.mjs; if ($LASTEXITCODE -ne 0) { throw 'Website build failed.' } }
    $dist = (Resolve-Path -LiteralPath 'website/dist').Path
    $artifacts = Join-Path $repoRoot '.artifacts/website'
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    $archivePath = Join-Path $artifacts ('website-' + [guid]::NewGuid().ToString('N') + '.zip')
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $dist -Recurse -File) {
            $relative = [IO.Path]::GetRelativePath($dist, $file.FullName).Replace('\','/')
            # Only the local preview needs this copy; the live update API is managed separately.
            if ($relative.StartsWith('tuci-updates/')) { continue }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
    $package = Get-Item -LiteralPath $archivePath
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $start = [Diagnostics.ProcessStartInfo]::new('ssh')
    $start.UseShellExecute = $false; $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($arg in @('-T','-o','BatchMode=yes','-o','IdentitiesOnly=yes','-o','StrictHostKeyChecking=yes','-o','ConnectTimeout=15','-o','ServerAliveInterval=15','-o',('UserKnownHostsFile=' + [IO.Path]::GetFullPath($KnownHostsPath)),'-i',(Join-Path $IdentityDirectory 'website_deploy_ed25519'),'root@101.133.134.128','deploy-site-v1')) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'The website publisher connection could not start.' }
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $header = [Text.Encoding]::ASCII.GetBytes((@{ bytes = $package.Length; sha256 = $hash } | ConvertTo-Json -Compress) + "`n")
        $uploadStream = $process.StandardInput.BaseStream; $uploadStream.Write($header)
        $source = [IO.File]::OpenRead($archivePath)
        try { $source.CopyTo($uploadStream) } finally { $source.Dispose() }
        $process.StandardInput.Close(); $process.WaitForExit()
        $resultText = $stdout.GetAwaiter().GetResult(); $failure = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw ('Website deployment failed: ' + $failure.Trim()) }
        $result = $resultText | ConvertFrom-Json
        if ($result.status -ne 'published' -or $result.sha256 -ne $hash) { throw 'The server did not confirm the expected website.' }
        if (!$SkipPublicVerification) {
            $response = Invoke-WebRequest -Uri $result.url -TimeoutSec 20
            if ([int]$response.StatusCode -ne 200 -or $response.Content -notmatch '让每个遇见') { throw 'The public homepage verification failed.' }
        }
        $resultText
        Write-Output (($SkipPublicVerification ? 'Website files published: ' : 'Website deployed and verified: ') + $result.url)
    } finally { $process.Dispose() }
} finally { Pop-Location }
