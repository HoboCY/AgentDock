#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PackageDir,
    [Parameter(Mandatory)][string]$OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*\z') {
    throw '只允许带测试后缀的版本，例如 0.1.1-beta.1。'
}
if (Test-Path -LiteralPath $OutputDir) { throw 'Release 输出目录必须全新，避免混入旧附件。' }
$manifestPath = Join-Path $PackageDir 'package-manifest.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.product -cne 'AgentDock' -or
    $manifest.runtime -cne 'win-x64' -or $manifest.configuration -cne 'Release' -or
    $manifest.version -cne $Version -or $manifest.hostVersion -cne $Version) {
    throw '包清单的平台、配置或版本与预发布不一致。'
}
if ($manifest.partial -isnot [bool] -or $manifest.partial -or
    $manifest.selfContained -isnot [bool] -or -not $manifest.selfContained -or
    $manifest.frameworkDependent -isnot [bool] -or $manifest.frameworkDependent -or
    @($manifest.missingComponents).Count -ne 0) {
    throw '预发布必须是完整、自包含的安装包。'
}
foreach ($component in 'host', 'desktop', 'engine', 'setup') {
    $present = $manifest.components.$component.present
    if ($present -isnot [bool] -or -not $present) { throw "包缺少组件：$component" }
}

$files = @('package-manifest.json')
foreach ($item in @(
    @{ Key = 'setup'; File = "AgentDockSetup-$Version-win-x64.exe" },
    @{ Key = 'payloadZip'; File = "AgentDock-$Version-win-x64.zip" }
)) {
    $entry = $manifest.artifacts.($item.Key)
    if ($entry.file -cne $item.File) { throw "附件名不符：$($item.Key)" }
    $file = Get-Item -LiteralPath (Join-Path $PackageDir $item.File)
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($file.Length -ne $entry.size -or $hash -ne $entry.sha256) { throw "附件大小或 SHA-256 不符：$($item.File)" }
    $files += $item.File
}

New-Item -ItemType Directory -Path $OutputDir | Out-Null
$checksums = foreach ($name in $files) {
    $source = Join-Path $PackageDir $name
    Copy-Item -LiteralPath $source -Destination (Join-Path $OutputDir $name)
    '{0}  {1}' -f (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant(), $name
}
# Linux 发布 job 用 sha256sum 复核；固定 LF 避免 CR 被当作文件名的一部分。
[IO.File]::WriteAllText((Join-Path ([IO.Path]::GetFullPath($OutputDir)) 'SHA256SUMS.txt'), ($checksums -join "`n") + "`n")
$blockers = ($manifest.releaseBlocked | ForEach-Object { "- $_" }) -join "`n"
@"
Windows x64 测试版，尚未达到稳定发行条件。程序未进行发行签名，下载后可能出现 SmartScreen 提示。

请同时下载 AgentDockSetup-$Version-win-x64.exe 与 AgentDock-$Version-win-x64.zip，放在同一目录后运行安装器。ZIP 已包含固定运行时及 Codex 插件源；安装与接入说明见仓库 docs/installation.md。

构建已通过自动化回归、完整包校验与隔离安装门禁。这不替代该版本在干净系统上的真实下载、首次配置、桌面交互和真实模型任务验收。

清单 releaseReady=$($manifest.releaseReady.ToString().ToLowerInvariant())，构建器保留的发行阻塞项：
$blockers

本次仅提供 Windows 附件；macOS 仍使用独立候选及实机验收流程。测试版不会成为 Latest，也不会由应用内稳定版检查提示更新。
"@ | Set-Content -LiteralPath (Join-Path $OutputDir 'release-notes.md') -Encoding utf8
Write-Host "Release 附件已校验：$OutputDir"
