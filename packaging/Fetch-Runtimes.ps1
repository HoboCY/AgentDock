#requires -Version 7.0
<#
.SYNOPSIS
  AgentDock 固定运行时获取脚本。

.DESCRIPTION
  依据 packaging/runtime-lock.json 把固定版本的官方运行时放置到目标目录（通常是打包 staging 根）。

  规则（fail closed）：
  - 只复制/解包清单明确列出的内容；不读取、不打包任何设置、凭据、任务数据。
  - npm 运行时优先使用本机已安装的公共程序位（例如旧 deepseek_agents 工具目录），但必须通过清单 SHA-256 校验。
  - zip 运行时（例如随包分发的 MinGit）：优先使用本地缓存归档（-CacheDir、AGENTDOCK_RUNTIME_CACHE、
    <repo>/artifacts/runtime-downloads、packaging/.cache），否则从官方 URL 下载；先校验整包 SHA-256 与体积，
    再逐条安全解包（拒绝路径越界），最后校验清单 verify 中的关键文件哈希。
  - 任何哈希/体积不符都直接失败，绝不静默降级。

.PARAMETER OutDir
  运行时放置目录。清单中的 to 相对路径在此目录下生成（例如 engines/opencode/opencode.exe）。

.PARAMETER ManifestPath
  输出 runtime-manifest.json 的路径，默认 OutDir\runtime-manifest.json。

.PARAMETER LocalToolsRoot
  可选的旧工具根目录（内含 opencode-1.18.30、codex-0.153.4 等子目录），用于替代锁文件里的 %USERPROFILE% 路径。

.PARAMETER CacheDir
  可选的归档缓存目录（可多次传入）；默认可选位置见上：AGENTDOCK_RUNTIME_CACHE、<repo>/artifacts/runtime-downloads、
  packaging/.cache。缓存文件必须通过清单哈希校验才会被使用。
#>
[CmdletBinding()]
param(
    [string]$LockFile = (Join-Path $PSScriptRoot 'runtime-lock.json'),
    [Parameter(Mandatory)][string]$OutDir,
    [string]$ManifestPath,
    [ValidateSet('win-x64')][string]$Platform = 'win-x64',
    [string]$LocalToolsRoot,
    [string[]]$CacheDir,
    [switch]$Offline,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-Template {
    param([string]$Value)
    $result = $Value
    $result = $result.Replace('%USERPROFILE%', [Environment]::GetFolderPath('UserProfile'))
    $result = $result.Replace('%LOCALAPPDATA%', [Environment]::GetFolderPath('LocalApplicationData'))
    $result = $result.Replace('%ProgramFiles(x86)%', ${env:ProgramFiles(x86)})
    $result = $result.Replace('%ProgramFiles%', $env:ProgramFiles)
    return $result
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-Sha512Base64 {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $hash = [System.Security.Cryptography.SHA512]::Create().ComputeHash($stream)
    }
    finally {
        $stream.Dispose()
    }
    return [Convert]::ToBase64String($hash)
}

function Invoke-Download {
    param([Parameter(Mandatory)][string]$Url, [Parameter(Mandatory)][string]$Destination)
    Write-Host "  下载 $Url"
    $progress = $ProgressPreference
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing -MaximumRedirection 5
    }
    finally {
        $ProgressPreference = $progress
    }
}

function Expand-TarGz {
    param([Parameter(Mandatory)][string]$Archive, [Parameter(Mandatory)][string]$Destination)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if (-not $tar) { $tar = Get-Command tar -ErrorAction SilentlyContinue }
    if (-not $tar) { throw "找不到 tar，无法解包 npm tarball。请安装 Windows 内置 tar 或 Git for Windows。" }
    & $tar.Source -xzf $Archive -C $Destination
    if ($LASTEXITCODE -ne 0) { throw "tar 解包失败（exit $LASTEXITCODE）：$Archive" }
}

function Get-ArchiveCacheCandidates {
    param([Parameter(Mandatory)][string]$FileName, [Parameter(Mandatory)][string]$LockDir)
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($dir in @($CacheDir)) {
        if ($dir) { $roots.Add($dir) }
    }
    foreach ($part in @(([string]$env:AGENTDOCK_RUNTIME_CACHE)) -split [System.IO.Path]::PathSeparator) {
        if ($part) { $roots.Add($part) }
    }
    $repoRoot = Split-Path -Parent $LockDir
    $roots.Add((Join-Path $repoRoot 'artifacts/runtime-downloads'))
    $roots.Add((Join-Path $LockDir '.cache'))
    $seenRoots = New-Object System.Collections.Generic.List[string]
    foreach ($root in $roots) {
        $full = [System.IO.Path]::GetFullPath($root)
        if ($seenRoots.Contains($full)) { continue }
        $seenRoots.Add($full) | Out-Null
        (Join-Path $full $FileName)
    }
}

function Resolve-ZipArchive {
    param([Parameter(Mandatory)]$Runtime, [Parameter(Mandatory)][string]$RuntimeName,
        [Parameter(Mandatory)][string]$LockDir, [Parameter(Mandatory)][string]$TmpRoot)
    $expectedHash = ([string]$Runtime.archive.sha256).ToLowerInvariant()
    $expectedSize = [long]$Runtime.archive.size
    $cacheCandidates = @(Get-ArchiveCacheCandidates -FileName ([string]$Runtime.archive.fileName) -LockDir $LockDir)
    if (-not $Force) {
        foreach ($candidate in $cacheCandidates) {
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
            if ((Get-Item -LiteralPath $candidate).Length -ne $expectedSize) {
                Write-Warning "  缓存归档体积不符，忽略：$candidate"
                continue
            }
            if ((Get-Sha256 -Path $candidate) -ne $expectedHash) {
                Write-Warning "  缓存归档 SHA-256 不符，忽略：$candidate"
                continue
            }
            Write-Host "  使用本地已验证归档：$candidate"
            # 只记录归档文件名：runtime-manifest.json 会随包分发，不得包含本机绝对路径。
            return [pscustomobject]@{ Path = $candidate; Source = 'cache'; SourceDetail = [string]$Runtime.archive.fileName; Temporary = $false }
        }
    }
    if ($Offline) {
        throw ("离线模式下找不到通过校验的 $RuntimeName 归档（$($Runtime.archive.fileName)）。已检查：" + ($cacheCandidates -join '; '))
    }
    $download = Join-Path $TmpRoot ([string]$Runtime.archive.fileName)
    if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
    Invoke-Download -Url ([string]$Runtime.archive.url) -Destination $download
    if ((Get-Item -LiteralPath $download).Length -ne $expectedSize) {
        Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue
        throw "下载归档体积不符：$($Runtime.archive.url)"
    }
    if ((Get-Sha256 -Path $download) -ne $expectedHash) {
        Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue
        throw "下载归档 SHA-256 不符：$($Runtime.archive.url)"
    }
    Write-Host "  官方归档 SHA-256 校验通过"
    return [pscustomobject]@{ Path = $download; Source = 'download'; SourceDetail = [string]$Runtime.archive.url; Temporary = $true }
}

function Expand-VerifiedZip {
    param([Parameter(Mandatory)][string]$Archive, [Parameter(Mandatory)][string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $root = [System.IO.Path]::GetFullPath($Destination)
        $rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name.Length -eq 0) { continue }
            $segments = $name.TrimEnd('/').Split('/')
            if ($name.StartsWith('/') -or $name.Contains(':') -or ($segments | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_ -eq '' })) {
                throw "归档路径非法：$($entry.FullName)"
            }
            $target = [System.IO.Path]::GetFullPath((Join-Path $root $name.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
            if (-not $target.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "归档路径越界：$($entry.FullName)"
            }
            if ($name.EndsWith('/')) {
                New-Item -ItemType Directory -Path $target -Force | Out-Null
                continue
            }
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Test-FileMatch {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Spec)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne [long]$Spec.size) { return $false }
    return (Get-Sha256 -Path $Path) -eq $Spec.sha256.ToLowerInvariant()
}

function Copy-AllowlistedFile {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
    $parent = Split-Path -Parent $Destination
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

$lock = Get-Content -Raw -LiteralPath $LockFile | ConvertFrom-Json
if ($lock.schema -ne 1) { throw "不支持的 runtime-lock schema：$($lock.schema)" }
$platformBlock = $lock.platforms.$Platform
if ($null -eq $platformBlock) { throw "runtime-lock 中缺少平台：$Platform" }

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
if (-not $ManifestPath) { $ManifestPath = Join-Path $OutDir 'runtime-manifest.json' }
$tmpRoot = Join-Path $OutDir '.tmp'
New-Item -ItemType Directory -Path $tmpRoot -Force | Out-Null

$manifestFiles = New-Object System.Collections.Generic.List[object]
$manifestArchives = New-Object System.Collections.Generic.List[object]
$manifestSystem = New-Object System.Collections.Generic.List[object]

foreach ($runtimeName in $platformBlock.runtimes.PSObject.Properties.Name) {
    $runtime = $platformBlock.runtimes.$runtimeName
    Write-Host "== 运行时 $runtimeName（$($runtime.displayName) $($runtime.version)）"

    if ($runtime.kind -eq 'system') {
        Write-Host "  系统组件，不在包内分发；由安装程序只读检测。"
        $manifestSystem.Add([ordered]@{
                name        = $runtimeName
                displayName = $runtime.displayName
                required    = [bool]$runtime.required
                version     = $runtime.version
                homepage    = $runtime.homepage
                detect      = $runtime.detect
            }) | Out-Null
        continue
    }

    if ($runtime.kind -eq 'zip') {
        $to = [string]$runtime.to
        if (-not $runtime.archive -or -not $to) { throw "zip 运行时缺少 to/archive：$runtimeName" }
        $resolved = Resolve-ZipArchive -Runtime $runtime -RuntimeName $runtimeName -LockDir (Split-Path -Parent $LockFile) -TmpRoot $tmpRoot
        $extractDir = Join-Path $tmpRoot ("zip-{0}" -f $runtimeName)
        if (Test-Path -LiteralPath $extractDir) { Remove-Item -LiteralPath $extractDir -Recurse -Force }
        New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
        Expand-VerifiedZip -Archive $resolved.Path -Destination $extractDir

        $verified = New-Object System.Collections.Generic.List[object]
        foreach ($spec in @($runtime.verify)) {
            if (-not $spec.path) { continue }
            $path = Join-Path $extractDir ([string]$spec.path)
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "归档缺少校验文件：$($spec.path)（$runtimeName）" }
            $hash = Get-Sha256 -Path $path
            if ($hash -ne ([string]$spec.sha256).ToLowerInvariant()) {
                throw "归档关键文件 SHA-256 不符：$($spec.path)（$runtimeName）"
            }
            $verified.Add([ordered]@{
                    path   = ([string]$spec.path -replace '\\', '/')
                    sha256 = $hash
                    size   = [long](Get-Item -LiteralPath $path).Length
                }) | Out-Null
        }

        $targetDir = Join-Path $OutDir $to
        if (Test-Path -LiteralPath $targetDir) { Remove-Item -LiteralPath $targetDir -Recurse -Force }
        New-Item -ItemType Directory -Path (Split-Path -Parent $targetDir) -Force | Out-Null
        Move-Item -LiteralPath $extractDir -Destination $targetDir
        $fileCount = @(Get-ChildItem -LiteralPath $targetDir -File -Recurse).Count
        if ($resolved.Temporary) { Remove-Item -LiteralPath $resolved.Path -Force -ErrorAction SilentlyContinue }

        $manifestArchives.Add([ordered]@{
                runtime        = $runtimeName
                kind           = 'zip'
                to             = ($to -replace '\\', '/')
                fileName       = [string]$runtime.archive.fileName
                url            = [string]$runtime.archive.url
                sha256         = ([string]$runtime.archive.sha256).ToLowerInvariant()
                size           = [long]$runtime.archive.size
                source         = $resolved.Source
                sourceDetail   = $resolved.SourceDetail
                fileCount      = $fileCount
                verifiedFiles  = [object[]]$verified
            }) | Out-Null
        Write-Host "  已解包 $fileCount 个文件到 $to（$($verified.Count) 项关键文件校验通过）"
        continue
    }

    if ($runtime.kind -ne 'npm') { throw "运行时不支持的类型：$($runtime.kind)（$runtimeName）" }

    # 1. 解析本机候选根目录
    $localRoots = New-Object System.Collections.Generic.List[string]
    foreach ($root in @($runtime.localRoots)) {
        $resolved = Resolve-Template $root
        $localRoots.Add($resolved)
        if ($LocalToolsRoot) {
            $localRoots.Add((Join-Path $LocalToolsRoot (Split-Path -Leaf $resolved)))
        }
    }

    # 2. 尝试使用本机已安装位（全部文件都要通过校验）
    $localReady = $false
    $localMatch = [ordered]@{}
    $localDetail = [ordered]@{}
    if (-not $Force) {
        foreach ($root in $localRoots) {
            if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
            $allOk = $true
            $attempt = [ordered]@{}
            $attemptDetail = [ordered]@{}
            foreach ($file in $runtime.files) {
                $source = $null
                $relative = $null
                foreach ($candidate in @($file.sources)) {
                    if ($candidate.PSObject.Properties.Name -contains 'local') {
                        $path = Join-Path $root $candidate.local
                        if (Test-Path -LiteralPath $path -PathType Leaf) { $source = $path; $relative = [string]$candidate.local; break }
                    }
                }
                if ($null -eq $source -or -not (Test-FileMatch -Path $source -Spec $file)) {
                    $allOk = $false
                    break
                }
                $attempt[$file.to] = $source
                $attemptDetail[$file.to] = $relative
            }
            if ($allOk) {
                $localReady = $true
                $localMatch = $attempt
                $localDetail = $attemptDetail
                Write-Host "  使用本机已验证的程序位：$root"
                break
            }
        }
        if (-not $localReady) {
            foreach ($root in $localRoots) {
                if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
                # 目录存在但校验失败：显式报错，除非用户用 -Force 强制走官方下载
                foreach ($file in $runtime.files) {
                    foreach ($candidate in @($file.sources)) {
                        if ($candidate.PSObject.Properties.Name -notcontains 'local') { continue }
                        $path = Join-Path $root $candidate.local
                        if (Test-Path -LiteralPath $path -PathType Leaf) {
                            throw "本机程序位与清单不匹配：$path。如确需改用官方下载，请加 -Force。"
                        }
                    }
                }
            }
        }
    }

    if ($localReady) {
        foreach ($file in $runtime.files) {
            $source = $localMatch[$file.to]
            $destination = Join-Path $OutDir $file.to
            Copy-AllowlistedFile -Source $source -Destination $destination
            if ((Get-Sha256 -Path $destination) -ne $file.sha256) { throw "复制后校验失败：$destination" }
            $manifestFiles.Add([ordered]@{
                    runtime      = $runtimeName
                    to           = ($file.to -replace '\\', '/')
                    sha256       = $file.sha256
                    size         = [long]$file.size
                    source       = 'local'
                    # 只记录清单内的相对来源：runtime-manifest.json 随包分发，不得包含本机绝对路径。
                    sourceDetail = [string]$localDetail[$file.to]
                }) | Out-Null
        }
        continue
    }

    if ($Offline) { throw "离线模式下找不到通过校验的本机程序位：$runtimeName" }

    # 3. 官方下载：先校验 tarball，再解包，再逐文件校验
    $tarballTmp = Join-Path $tmpRoot ("{0}.tgz" -f $runtimeName)
    if (Test-Path -LiteralPath $tarballTmp) { Remove-Item -LiteralPath $tarballTmp -Force }
    Invoke-Download -Url $runtime.tarball -Destination $tarballTmp

    $integrity = [string]$runtime.integrity
    if (-not $integrity.StartsWith('sha512-')) { throw "不支持的 integrity 格式：$integrity" }
    $expected = $integrity.Substring('sha512-'.Length)
    $actual = Get-Sha512Base64 -Path $tarballTmp
    if ($actual -ne $expected) {
        Remove-Item -LiteralPath $tarballTmp -Force -ErrorAction SilentlyContinue
        throw "tarball integrity 校验失败：$($runtime.tarball)"
    }
    Write-Host "  tarball sha512 校验通过"

    $extractDir = Join-Path $tmpRoot ("extract-{0}" -f $runtimeName)
    if (Test-Path -LiteralPath $extractDir) { Remove-Item -LiteralPath $extractDir -Recurse -Force }
    Expand-TarGz -Archive $tarballTmp -Destination $extractDir
    $packageRoot = Join-Path $extractDir 'package'
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) { throw "npm tarball 缺少 package/ 根：$($runtime.tarball)" }

    foreach ($file in $runtime.files) {
        $npmPath = $null
        foreach ($candidate in @($file.sources)) {
            if ($candidate.PSObject.Properties.Name -contains 'npmPath') { $npmPath = $candidate.npmPath; break }
        }
        if (-not $npmPath) { throw "清单缺少 npmPath：$($file.to)" }
        $source = Join-Path $packageRoot $npmPath
        if (-not (Test-FileMatch -Path $source -Spec $file)) { throw "下载解包后校验失败：$($file.to)" }
        $destination = Join-Path $OutDir $file.to
        Copy-AllowlistedFile -Source $source -Destination $destination
        if ((Get-Sha256 -Path $destination) -ne $file.sha256) { throw "复制后校验失败：$destination" }
        $manifestFiles.Add([ordered]@{
                runtime      = $runtimeName
                to           = ($file.to -replace '\\', '/')
                sha256       = $file.sha256
                size         = [long]$file.size
                source       = 'npm'
                sourceDetail = [string]$runtime.tarball
            }) | Out-Null
    }
    Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tarballTmp -Force -ErrorAction SilentlyContinue
}

$lockSha = Get-Sha256 -Path $LockFile
$manifest = [ordered]@{
    schema      = 1
    platform    = $Platform
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    lockSha256  = $lockSha
    files       = [object[]]$manifestFiles
    archives    = [object[]]$manifestArchives
    system      = [object[]]$manifestSystem
}
$manifestJson = $manifest | ConvertTo-Json -Depth 8
Set-Content -LiteralPath $ManifestPath -Value $manifestJson -Encoding utf8

$tmpLeft = Get-ChildItem -LiteralPath $tmpRoot -Force -ErrorAction SilentlyContinue
if (-not $tmpLeft) { Remove-Item -LiteralPath $tmpRoot -Force -ErrorAction SilentlyContinue }

Write-Host "运行时清单：$ManifestPath（$($manifestFiles.Count) 个文件，$($manifestArchives.Count) 个归档）"
[pscustomobject]@{
    Platform   = $Platform
    OutDir     = $OutDir
    Manifest   = $ManifestPath
    FileCount  = $manifestFiles.Count
    Archives   = @($manifestArchives | ForEach-Object { $_.runtime })
    SystemDeps = @($manifestSystem | ForEach-Object { $_.name })
} | ConvertTo-Json -Compress
