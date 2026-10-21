#requires -Version 7.0
<#
.SYNOPSIS
  AgentDock Windows 安装包构建脚本。

.DESCRIPTION
  构建固定版本组合的安装包：
    AgentDockSetup-<version>-win-x64.exe    内置完整程序包的安装程序（每用户安装，无需管理员）
    AgentDock-<version>-win-x64.zip         构建/维护用程序包，普通用户无需另行下载
    package-manifest.json                   构件清单（哈希、组件状态、发布阻塞项）

  组件项目缺失或发布产物缺少预期 exe 时默认失败；-AllowPartial 生成 partial 包（仅用于 CI 与打包链路自检），
  并在 package-manifest.json 中标记 partial=true 与 releaseBlocked，绝不冒充完整发行包。

  默认 self-contained（-FrameworkDependent 仅用于本地自检，且必定标记 releaseBlocked）。

  版本组合与运行时固定见 packaging/runtime-lock.json；运行时先校验哈希再入包。
  其中 Git（MinGit）必须随包分发：官方归档先校验 SHA-256/体积，再整包解包到 engines/git，
  并校验 usr/bin/sh.exe；缺失即构建失败（干净系统不得要求预装 Git）。
  Host 构建后执行 `--version` 记录到 package-manifest.json（可用 -ExpectedHostVersion 断言）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'osx-arm64', 'osx-x64')][string]$Runtime = 'win-x64',
    [string]$OutputDir,
    [ValidateSet('zip', 'folder', 'both')][string]$PayloadFormat = 'zip',
    [switch]$AllowPartial,
    [switch]$FrameworkDependent,
    [switch]$Offline,
    [switch]$ForceRuntimes,
    [string[]]$RuntimeCache,
    [string]$LocalToolsRoot,
    [switch]$SkipSetup,
    [string]$HostProject,
    [string]$DesktopProject,
    [string]$EngineProject,
    [string]$ExpectedHostVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
    throw "版本号不合法（只允许字母、数字、点、下划线、连字符）：$Version"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot "artifacts/package/$Version/$Runtime" }
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$stage = Join-Path $OutputDir '.stage'
$setupStage = Join-Path $OutputDir '.setup'
function Remove-Safe([string]$Path) {
    if (-not $Path) { return }
    $full = [System.IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($OutputDir.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝删除输出目录之外的路径：$full"
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
Remove-Safe $stage
Remove-Safe $setupStage
New-Item -ItemType Directory -Path $stage -Force | Out-Null

$components = [ordered]@{}
$missingComponents = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

function Resolve-ComponentProject {
    param([string]$Name, [string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "指定的 $Name 项目不存在：$Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }
    $hits = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Recurse -File -Filter "AgentDock.$Name.csproj" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' })
    if ($hits.Count -eq 0) { return $null }
    if ($hits.Count -gt 1) { throw "找到多个 AgentDock.$Name 项目，请用参数显式指定：$($hits.FullName -join '; ')" }
    return $hits[0].FullName
}

function Publish-Component {
    param([string]$Project, [string]$Destination, [string]$Label, [string]$VersionOverride, [string]$BundledPayload)
    Write-Host "== 发布 $Label：$Project"
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $arguments = @('publish', $Project, '-c', $Configuration, '-r', $Runtime, '-o', $Destination, '--nologo', '-m:1', '--disable-build-servers', '-nr:false', '-p:UseSharedCompilation=false')
    if ($FrameworkDependent) {
        $arguments += @('--self-contained', 'false')
    } else {
        $arguments += @('--self-contained', 'true')
    }
    if ($VersionOverride) {
        $arguments += @("-p:Version=$VersionOverride")
    }
    if ($BundledPayload) { $arguments += "-p:BundledPayloadPath=$BundledPayload" }
    if ($Offline) {
        # 离线构建无法获取 NuGet 漏洞数据；关闭审计，避免 NU1900 被 TreatWarningsAsErrors 升级为错误。
        $arguments += @('-p:NuGetAudit=false')
    }
    $arguments += @(
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$Label，exit $LASTEXITCODE）。" }
    Get-ChildItem -LiteralPath $Destination -File -Filter '*.pdb' -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
}

function Assert-ExpectedExe {
    param([string]$Directory, [string]$Expected)
    $path = Join-Path $Directory $Expected
    if (-not (Test-Path -LiteralPath $path)) {
        $warnings.Add("发布输出中没有期望的 $Expected（$Directory）；请确认组件项目 AssemblyName。") | Out-Null
        return $false
    }
    return $true
}

# ---- 组件发布（Host/Core 由主工作流构建；Desktop、Engine 为独立工作流）----
$isWindowsTarget = $Runtime -eq 'win-x64'
$componentPlan = [ordered]@{
    host    = @{ Project = (Resolve-ComponentProject -Name 'Host' -Explicit $HostProject); Expected = 'AgentDock.Host.exe' }
    desktop = @{ Project = (Resolve-ComponentProject -Name 'Desktop' -Explicit $DesktopProject); Expected = 'AgentDock.Desktop.exe' }
    engine  = @{ Project = (Resolve-ComponentProject -Name 'Engine' -Explicit $EngineProject); Expected = 'AgentDock.Engine.exe' }
}
foreach ($name in $componentPlan.Keys) {
    $plan = $componentPlan[$name]
    if (-not $plan.Project) {
        $missingComponents.Add($name) | Out-Null
        $warnings.Add("组件 $name 未找到项目（src/AgentDock.$($name.Substring(0,1).ToUpper()+$name.Substring(1))）；$(if ($AllowPartial) { 'partial 包将缺少该组件' } else { '完整包需要该组件' })") | Out-Null
    }
}
if ($missingComponents.Count -gt 0 -and -not $AllowPartial) {
    throw "缺少组件项目：$($missingComponents -join ', ')。可使用 -AllowPartial 生成用于自检的 partial 包。"
}

foreach ($name in $componentPlan.Keys) {
    $plan = $componentPlan[$name]
    $dest = Join-Path $stage $name
    if ($plan.Project) {
        Publish-Component -Project $plan.Project -Destination $dest -Label $name -VersionOverride $Version
        $present = Assert-ExpectedExe -Directory $dest -Expected $plan.Expected
        $projectLabel = [System.IO.Path]::GetRelativePath($repoRoot, $plan.Project)
        if ($present) {
            $components[$name] = [ordered]@{ present = $true; project = $projectLabel }
        } else {
            # 项目存在但发布产物缺少预期 exe：同样视为 partial，防止“完整包”静默缺件。
            $missingComponents.Add($name) | Out-Null
            $components[$name] = [ordered]@{ present = $false; project = $projectLabel; reason = 'expected-exe-missing' }
        }
    } else {
        $components[$name] = [ordered]@{ present = $false; project = $null; reason = 'project-missing' }
    }
}
if ($missingComponents.Count -gt 0 -and -not $AllowPartial) {
    throw "构建结果为 partial（缺少组件：$($missingComponents -join ', ')）。如确需自检包，请显式传入 -AllowPartial。"
}

# ---- Host 版本输出（记录到构件清单；可用 -ExpectedHostVersion 断言）----
$hostVersion = $null
$hostExePath = Join-Path $stage 'host/AgentDock.Host.exe'
if (Test-Path -LiteralPath $hostExePath -PathType Leaf) {
    try {
        $versionOutput = @(& $hostExePath --version 2>$null) | Select-Object -First 1
        if ($versionOutput) { $hostVersion = ([string]$versionOutput).Trim() }
    }
    catch {
        $hostVersion = $null
    }
    if (-not $hostVersion) {
        $warnings.Add('无法读取 host/AgentDock.Host.exe --version 输出。') | Out-Null
    }
    elseif ($ExpectedHostVersion -and $hostVersion -ne $ExpectedHostVersion) {
        throw "Host --version 输出不符：$hostVersion != $ExpectedHostVersion"
    }
    else {
        $components['host'].version = $hostVersion
        Write-Host "== Host --version：$hostVersion"
    }
}

# ---- 固定运行时：校验哈希后入包 ----
$gitInfo = $null
if ($isWindowsTarget) {
    Write-Host "== 获取固定运行时（packaging/Fetch-Runtimes.ps1）"
    $fetchArgs = @{
        LockFile = Join-Path $repoRoot 'packaging/runtime-lock.json'
        OutDir   = $stage
        Platform = 'win-x64'
    }
    if ($Offline) { $fetchArgs.Offline = $true }
    if ($ForceRuntimes) { $fetchArgs.Force = $true }
    if ($RuntimeCache) { $fetchArgs.CacheDir = $RuntimeCache }
    if ($LocalToolsRoot) { $fetchArgs.LocalToolsRoot = $LocalToolsRoot }
    & (Join-Path $repoRoot 'packaging/Fetch-Runtimes.ps1') @fetchArgs

    # Git（MinGit）必须随包分发：核对 runtime-manifest 归档哈希、实际文件与关键 sh.exe。
    $lock = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'packaging/runtime-lock.json') | ConvertFrom-Json
    $git = $lock.platforms.'win-x64'.runtimes.'git-bash'
    if ($git.kind -ne 'zip' -or -not $git.archive) {
        throw 'runtime-lock.json 的 git-bash 必须是随包分发的 zip 运行时；系统 Git 前置条件已不再接受。'
    }
    $runtimeManifestPath = Join-Path $stage 'runtime-manifest.json'
    if (-not (Test-Path -LiteralPath $runtimeManifestPath -PathType Leaf)) { throw "Fetch-Runtimes 未生成 runtime-manifest.json：$runtimeManifestPath" }
    $runtimeManifest = Get-Content -Raw -LiteralPath $runtimeManifestPath | ConvertFrom-Json
    $gitArchive = @($runtimeManifest.archives) | Where-Object { $_.runtime -eq 'git-bash' } | Select-Object -First 1
    if (-not $gitArchive) { throw 'runtime-manifest.json 缺少 git-bash 归档记录。' }
    if (([string]$gitArchive.sha256).ToLowerInvariant() -ne ([string]$git.archive.sha256).ToLowerInvariant()) {
        throw "Git 归档 SHA-256 与锁清单不符：$($gitArchive.sha256) != $($git.archive.sha256)"
    }
    $shExePath = Join-Path $stage 'engines/git/usr/bin/sh.exe'
    if (-not (Test-Path -LiteralPath $shExePath -PathType Leaf)) {
        throw '包内缺少 engines/git/usr/bin/sh.exe；Git 必须随包分发（干净系统无 Git 前置条件）。'
    }
    $shSpec = @($git.verify) | Where-Object { $_.path -eq 'usr/bin/sh.exe' } | Select-Object -First 1
    if (-not $shSpec) { throw 'runtime-lock.json 缺少 usr/bin/sh.exe 校验项。' }
    $shActual = (Get-FileHash -Algorithm SHA256 -LiteralPath $shExePath).Hash.ToLowerInvariant()
    if ($shActual -ne ([string]$shSpec.sha256).ToLowerInvariant()) {
        throw "包内 engines/git/usr/bin/sh.exe SHA-256 不符：$shActual"
    }
    $gitInfo = [ordered]@{
        version       = [string]$git.version
        upstream      = [string]$git.upstreamRelease
        archiveSha256 = ([string]$git.archive.sha256).ToLowerInvariant()
        archiveSize   = [long]$git.archive.size
        shExeSha256   = $shActual
        fileCount     = [int]$gitArchive.fileCount
        source        = [string]$gitArchive.source
    }
    $gitDescriptor = [ordered]@{
        schema    = 1
        kind      = 'bundled-zip'
        name      = 'git-bash'
        version   = [string]$git.version
        license   = $git.license
        homepage  = $git.homepage
        archive   = [ordered]@{
            fileName = [string]$git.archive.fileName
            url      = [string]$git.archive.url
            sha256   = ([string]$git.archive.sha256).ToLowerInvariant()
            size     = [long]$git.archive.size
        }
        shell     = [ordered]@{
            path   = 'usr/bin/sh.exe'
            sha256 = $shActual
            note   = 'Host PackageDefaults 默认 shell；GNU Bash。'
        }
        note      = '安装包内置官方 MinGit（整包解包）；安装程序优先记录该路径，缺失时才回退系统 Git Bash。'
    }
    $gitDir = Join-Path $stage 'engines/git'
    New-Item -ItemType Directory -Path $gitDir -Force | Out-Null
    $gitDescriptor | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $gitDir 'git-runtime.json') -Encoding utf8
    Write-Host "== 内置 Git 校验通过：$($git.version)（sh.exe $($shActual.Substring(0,12))...，$($gitArchive.fileCount) 个文件）"
} else {
    $warnings.Add("目标 $Runtime 不包含 Windows 执行引擎（OpenCode/Codex sandbox/Git Bash）；发布前需要对应平台的固定运行时清单。") | Out-Null
}

# ---- 插件源 ----
Write-Host "== 组装 Codex 插件源"
$pluginSource = Join-Path $repoRoot 'packaging/plugin'
$pluginDest = Join-Path $stage 'plugin'
Copy-Item -LiteralPath $pluginSource -Destination $pluginDest -Recurse -Force
$pluginJsonPath = Join-Path $pluginDest 'plugins/agentdock/.codex-plugin/plugin.json'
$pluginJson = Get-Content -Raw -LiteralPath $pluginJsonPath | ConvertFrom-Json
$pluginJson.version = $Version
$pluginJson | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $pluginJsonPath -Encoding utf8

$hostExe = Join-Path $stage 'host/AgentDock.Host.exe'
$pluginRuntime = Join-Path $pluginDest 'plugins/agentdock/runtime'
if (Test-Path -LiteralPath $hostExe) {
    New-Item -ItemType Directory -Path $pluginRuntime -Force | Out-Null
    Copy-Item -LiteralPath $hostExe -Destination (Join-Path $pluginRuntime 'AgentDock.Host.exe') -Force
} else {
    $warnings.Add('Host 缺失，插件 runtime 未注入 AgentDock.Host.exe（partial 构建）。') | Out-Null
}

Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/runtime-lock.json') -Destination (Join-Path $stage 'runtime-lock.json') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/licenses') -Destination (Join-Path $stage 'licenses') -Recurse -Force

# ---- 安装程序 ----
$setupArtifact = $null
if ($isWindowsTarget -and -not $SkipSetup) {
    Write-Host "== 发布精简启动器"
    Publish-Component -Project (Join-Path $repoRoot 'src/AgentDock.Setup/AgentDock.Setup.csproj') -Destination (Join-Path $stage 'setup') -Label 'launcher' -VersionOverride $Version
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'setup/AgentDock.Setup.exe'))) { throw '启动器发布输出中没有 exe。' }
    $components['setup'] = [ordered]@{ present = $true; file = "AgentDockSetup-$Version-$Runtime.exe" }
} elseif ($isWindowsTarget) {
    $components['setup'] = [ordered]@{ present = $false; reason = 'skipped' }
    $warnings.Add('已跳过安装程序构建（-SkipSetup）。') | Out-Null
} else {
    $components['setup'] = [ordered]@{ present = $false; reason = 'installer-pending-for-macos' }
    $warnings.Add('macOS 安装程序尚未实现；当前仅生成未签名、未验证的 payload 配方（不得作为正式发布）。') | Out-Null
}

# ---- payload 清单 ----
Write-Host "== 生成 payload-manifest.json"
$payloadFiles = New-Object System.Collections.Generic.List[object]
foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse) {
    $relative = [System.IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/')
    if ($relative -eq 'payload-manifest.json') { continue }
    $payloadFiles.Add([ordered]@{
            path   = $relative
            size   = [long]$file.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        }) | Out-Null
}
$payloadManifest = [ordered]@{
    schema   = 1
    product  = 'AgentDock'
    version  = $Version
    runtime  = $Runtime
    partial  = ($missingComponents.Count -gt 0)
    files    = [object[]]$payloadFiles
}
$payloadManifestJson = $payloadManifest | ConvertTo-Json -Depth 6
Set-Content -LiteralPath (Join-Path $stage 'payload-manifest.json') -Value $payloadManifestJson -Encoding utf8

# ---- 产物：payload zip / folder ----
function Get-Sha256([string]$Path) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

$zipArtifact = $null
$folderArtifact = $null
if ($PayloadFormat -in @('zip', 'both') -or ($isWindowsTarget -and -not $SkipSetup)) {
    $zipArtifact = Join-Path $OutputDir "AgentDock-$Version-$Runtime.zip"
    if (Test-Path -LiteralPath $zipArtifact) { Remove-Item -LiteralPath $zipArtifact -Force }
    Write-Host "== 生成 payload 压缩包：$zipArtifact"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipArtifact, [System.IO.Compression.CompressionLevel]::Optimal, $false)
}
if ($PayloadFormat -in @('folder', 'both')) {
    $folderArtifact = Join-Path $OutputDir "payload-$Runtime"
    Remove-Safe $folderArtifact
    Copy-Item -LiteralPath $stage -Destination $folderArtifact -Recurse -Force
}

# 在程序包完成后构建单文件安装器；内置 ZIP 与维护用 ZIP 是相同字节。
if ($isWindowsTarget -and -not $SkipSetup) {
    Publish-Component -Project (Join-Path $repoRoot 'src/AgentDock.Setup/AgentDock.Setup.csproj') -Destination $setupStage -Label 'bundled setup' -VersionOverride $Version -BundledPayload $zipArtifact
    $setupExe = Join-Path $setupStage 'AgentDock.Setup.exe'
    if (-not (Test-Path -LiteralPath $setupExe)) { throw '完整安装程序发布输出中没有 exe。' }
    $setupArtifact = Join-Path $OutputDir "AgentDockSetup-$Version-$Runtime.exe"
    Copy-Item -LiteralPath $setupExe -Destination $setupArtifact -Force
}

# ---- package-manifest.json ----
$artifacts = [ordered]@{}
if ($zipArtifact) {
    $artifacts['payloadZip'] = [ordered]@{
        file   = (Split-Path -Leaf $zipArtifact)
        size   = [long](Get-Item -LiteralPath $zipArtifact).Length
        sha256 = Get-Sha256 $zipArtifact
    }
}
if ($folderArtifact) {
    $artifacts['payloadFolder'] = [ordered]@{ dir = (Split-Path -Leaf $folderArtifact) }
}
if ($setupArtifact) {
    $artifacts['setup'] = [ordered]@{
        file   = (Split-Path -Leaf $setupArtifact)
        size   = [long](Get-Item -LiteralPath $setupArtifact).Length
        sha256 = Get-Sha256 $setupArtifact
        embeddedPayload = $true
        payloadSha256 = Get-Sha256 $zipArtifact
    }
}

$releaseBlocked = New-Object System.Collections.Generic.List[string]
if ($isWindowsTarget) {
    $warnings.Add('unsigned：安装程序与程序文件均未代码签名，可能出现 SmartScreen 提示（Windows 签名为可选项）') | Out-Null
} else {
    $releaseBlocked.Add('unsigned：安装程序与程序文件均未代码签名') | Out-Null
}
$releaseBlocked.Add('clean-vm-acceptance-missing：尚未在干净 Windows 虚机完成安装/接入/最小任务验收') | Out-Null
if (-not $isWindowsTarget) { $releaseBlocked.Add('macos-real-device-acceptance-missing：未经 V2 真机验收，不得声称支持 macOS') | Out-Null }
if ($missingComponents.Count -gt 0) { $releaseBlocked.Add("partial-package：缺少组件 $($missingComponents -join ', ')") | Out-Null }
if ($FrameworkDependent) { $releaseBlocked.Add('framework-dependent：仅为本地自检构建，正式发布必须 self-contained') | Out-Null }
if ($SkipSetup -and $isWindowsTarget) { $releaseBlocked.Add('setup-skipped：已跳过安装程序构建（-SkipSetup）') | Out-Null }

$releaseReady = $releaseBlocked.Count -eq 0
$packageManifest = [ordered]@{
    schema             = 1
    product            = 'AgentDock'
    version            = $Version
    runtime            = $Runtime
    configuration      = $Configuration
    createdAt          = (Get-Date).ToUniversalTime().ToString('o')
    partial            = ($missingComponents.Count -gt 0)
    releaseReady       = $releaseReady
    frameworkDependent = [bool]$FrameworkDependent
    selfContained      = (-not $FrameworkDependent)
    hostVersion        = $hostVersion
    git                = $gitInfo
    components         = $components
    missingComponents  = [string[]]$missingComponents
    warnings           = [string[]]$warnings
    artifacts          = $artifacts
    releaseBlocked     = [string[]]$releaseBlocked
    notes              = @(
        '安装布局与更新协议见 packaging/README.md。',
        '版本检查只由用户在桌面程序中主动触发；已安装引擎不自动更新。',
        'Git（MinGit）随包分发在 engines/git；干净系统无需预装 Git 或任何开发环境。',
        'payload 不含任何个人配置、凭据或任务数据。'
    )
}
$packageManifestPath = Join-Path $OutputDir 'package-manifest.json'
$packageManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $packageManifestPath -Encoding utf8

Remove-Safe $stage
Remove-Safe $setupStage

Write-Host ''
Write-Host "构建完成：$OutputDir"
if ($missingComponents.Count -gt 0) { Write-Host "警告：partial 包，缺少组件：$($missingComponents -join ', ')" }
foreach ($warning in $warnings) { Write-Host "警告：$warning" }
[pscustomobject]@{
    Version      = $Version
    Runtime      = $Runtime
    Output       = $OutputDir
    Partial      = ($missingComponents.Count -gt 0)
    ReleaseReady = $releaseReady
    Missing      = [string[]]$missingComponents
    HostVersion  = $hostVersion
    GitVersion   = if ($gitInfo) { $gitInfo.version } else { $null }
    GitArchive   = if ($gitInfo) { $gitInfo.archiveSha256 } else { $null }
    ShExe        = if ($gitInfo) { $gitInfo.shExeSha256 } else { $null }
    Artifacts    = @($artifacts.Keys)
} | ConvertTo-Json -Compress
