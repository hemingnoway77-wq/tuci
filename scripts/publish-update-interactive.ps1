$ErrorActionPreference = 'Stop'
try {
    Write-Host '兔词 · 发布 Windows 新版本'
    Write-Host '先完成代码修改。每次发布使用更大的版本号，例如 1.1.1。'
    $version = Read-Host '新版本号'
    $note = Read-Host '更新说明'
    & (Join-Path $PSScriptRoot 'publish-update.ps1') -Version $version.Trim() -ReleaseNotes @($note.Trim())
    Write-Host ''
    Write-Host '发布完成。用户打开兔词后会发现新版，也可以点击「检查更新」。'
    Read-Host '按 Enter 关闭' | Out-Null
} catch {
    Write-Host ('发布失败：' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
