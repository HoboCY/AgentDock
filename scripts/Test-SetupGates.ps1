#requires -Version 7.0
<#
.SYNOPSIS
  AgentDock 安装程序门禁自测（可在真实 Windows 上直接运行，不依赖命名管道）。

.DESCRIPTION
  在隔离的 --root 目录上验证安装 / 同版本修复 / 更新 / 卸载的统一门禁：
    - 存活 PID 的更新标记拒绝操作，且不删除他人标记；
    - 陈旧标记（持有进程已退出）放行并清理；
    - 后台离线（命名管道不存在）时，持久化任务 Paused/Queued/Running 任一存在即拒绝；
      仅终态任务放行；tasks.json 损坏一律 fail closed；
    - launch.lock 被独占持有时拒绝（不会死锁、不会静默跳过）；
    - 卸载同样受门禁保护，用户数据目录保留；
    - 提供真实 payload 时校验内置 Git（engines/git/usr/bin/sh.exe）与 runtime-manifest 的归档哈希。
  若提供 -HostExe（完整包内 host/AgentDock.Host.exe），额外验证“后台在线且空闲 → 正常退出后完成安装”，
  该场景需要可用的 Windows 命名管道。

.EXAMPLE
  pwsh -NoProfile -File scripts/Test-SetupGates.ps1 `
    -SetupExe .\artifacts\package\0.1.0\win-x64\AgentDockSetup-0.1.0-win-x64.exe `
    -Payload .\artifacts\package\0.1.0\win-x64\AgentDock-0.1.0-win-x64.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupExe,
    [string]$BundledSetupExe,
    [string]$Payload,
    [string]$HostExe,
    [string]$WorkDir,
    [switch]$KeepTemp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:SetupPath = (Resolve-Path -LiteralPath $SetupExe).Path
$script:PayloadPath = if ($Payload) { (Resolve-Path -LiteralPath $Payload).Path } else { $null }
$script:HostPath = if ($HostExe) { (Resolve-Path -LiteralPath $HostExe).Path } else { $null }
$script:CreatedWorkDir = $false
if (-not $WorkDir) {
    $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('agentdock-gate-tests-' + [Guid]::NewGuid().ToString('N'))
    $script:CreatedWorkDir = $true
}
$WorkDir = [System.IO.Path]::GetFullPath($WorkDir)
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

$script:Results = New-Object System.Collections.Generic.List[object]
$script:CaseCounter = 0

function Invoke-Case {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "== $Name" -ForegroundColor Cyan
    try {
        & $Body
        $script:Results.Add([pscustomobject]@{ Name = $Name; Passed = $true; Detail = '' }) | Out-Null
        Write-Host '   PASS' -ForegroundColor Green
    }
    catch {
        $message = $_.Exception.Message
        $script:Results.Add([pscustomobject]@{ Name = $Name; Passed = $false; Detail = $message }) | Out-Null
        Write-Host "   FAIL: $message" -ForegroundColor Red
    }
}

function Assert-True {
    param($Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function New-Root {
    param([string]$Name)
    $script:CaseCounter++
    $root = Join-Path $WorkDir ("{0:d2}-{1}" -f $script:CaseCounter, $Name)
    Assert-InWorkDir $root
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    return $root
}

function Assert-InWorkDir([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $prefix = $WorkDir.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "测试路径越界：$absolute" }
}

function Invoke-Setup {
    param([string[]]$Arguments, [int]$TimeoutSeconds = 240, [string]$Executable = $script:SetupPath)
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            throw "安装程序超时（$TimeoutSeconds 秒）：$($Arguments -join ' ')"
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut   = $stdout.GetAwaiter().GetResult()
            StdErr   = $stderr.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function New-SyntheticPayload {
    param([string]$Directory, [string]$Version)
    Assert-InWorkDir $Directory
    if (Test-Path -LiteralPath $Directory) { Remove-Item -LiteralPath $Directory -Recurse -Force }
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $files = [ordered]@{
        'host/AgentDock.Host.exe'       = "synthetic host placeholder $Version"
        'desktop/AgentDock.Desktop.exe' = "synthetic desktop placeholder $Version"
        'engine/AgentDock.Engine.exe'   = "synthetic engine placeholder $Version"
        'engines/git/usr/bin/sh.exe'    = "synthetic sh placeholder (gate tests only)"
        'engines/opencode/opencode.exe' = "synthetic opencode placeholder"
        'engines/codex/bin/codex.exe' = "synthetic sandbox placeholder"
        'engines/codex/codex-path/rg.exe' = "synthetic rg placeholder"
    }
    $entries = @()
    foreach ($relative in $files.Keys) {
        $path = Join-Path $Directory ($relative -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Set-Content -LiteralPath $path -Value $files[$relative] -Encoding utf8 -NoNewline
        $item = Get-Item -LiteralPath $path
        $entries += [ordered]@{
            path   = $relative
            size   = [long]$item.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        }
    }
    $manifest = [ordered]@{
        schema  = 1
        product = 'AgentDock'
        version = $Version
        runtime = 'win-x64'
        partial = $false
        files   = $entries
    }
    Set-Content -LiteralPath (Join-Path $Directory 'payload-manifest.json') -Value ($manifest | ConvertTo-Json -Depth 6) -Encoding utf8
    return $Directory
}

function Get-PayloadVersion {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path -PathType Container) {
        return (Get-Content -Raw -LiteralPath (Join-Path $Path 'payload-manifest.json') | ConvertFrom-Json).version
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'payload-manifest.json' } | Select-Object -First 1
        if (-not $entry) { throw "payload 缺少 payload-manifest.json：$Path" }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { return ($reader.ReadToEnd() | ConvertFrom-Json).version }
        finally { $reader.Dispose() }
    }
    finally {
        $zip.Dispose()
    }
}

function Write-UpdateMarker {
    param([string]$Root, [int]$ProcessId, [string]$Operation = 'test')
    $dir = Join-Path $Root 'install'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $marker = [ordered]@{
        schema        = 1
        targetVersion = 'test'
        processId     = $ProcessId
        createdAt     = (Get-Date).ToUniversalTime().ToString('o')
        payloadSha256 = $null
        operation     = $Operation
    }
    Set-Content -LiteralPath (Join-Path $dir 'update.lock') -Value ($marker | ConvertTo-Json -Depth 4) -Encoding utf8
    return (Join-Path $dir 'update.lock')
}

function Get-PwshPath {
    $path = (Get-Process -Id $PID).Path
    if (-not $path) { $path = Join-Path $PSHOME 'pwsh.exe' }
    return $path
}

function Get-DeadProcessId {
    $process = Start-Process -FilePath (Get-PwshPath) -ArgumentList @('-NoProfile', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('exit 0'))) -PassThru -WindowStyle Hidden
    $process.WaitForExit(15000) | Out-Null
    return $process.Id
}

function Install-Args {
    param([string]$Root, [string]$PayloadPath = $script:SyntheticPayload)
    return @('--install', '--payload', $PayloadPath, '--silent', '--no-shell-integration', '--root', $Root)
}

# ---- 准备 payload ----
$script:SyntheticPayloadA = New-SyntheticPayload -Directory (Join-Path $WorkDir 'payload-a') -Version '0.1.0-gate-a'
$script:SyntheticPayloadB = New-SyntheticPayload -Directory (Join-Path $WorkDir 'payload-b') -Version '0.1.0-gate-b'
$script:SyntheticPayload = $script:SyntheticPayloadA
if ($script:PayloadPath) {
    Write-Host "真实 payload：$($script:PayloadPath)"
} else {
    Write-Host "未提供 -Payload，使用合成 payload：$($script:SyntheticPayloadA)"
}
$script:ExpectedVersion = if ($script:PayloadPath) { Get-PayloadVersion -Path $script:PayloadPath } else { '0.1.0-gate-a' }
$script:PrimaryPayload = if ($script:PayloadPath) { $script:PayloadPath } else { $script:SyntheticPayloadA }
Write-Host "预期安装版本：$($script:ExpectedVersion)"
if ($script:HostPath) { Write-Host "真实 Host：$($script:HostPath)" } else { Write-Host '未提供 -HostExe：跳过在线停机场景。' }

# ---- 1. 全新安装 + 状态输出 ----
$script:FreshRoot = $null
Invoke-Case 'install-fresh-and-status' {
    $root = New-Root 'fresh'
    $script:FreshRoot = $root
    $result = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PrimaryPayload)
    Assert-True ($result.ExitCode -eq 0) "安装应成功（exit $($result.ExitCode)）：$($result.StdErr)$($result.StdOut)"
    $current = Get-Content -Raw -LiteralPath (Join-Path $root 'install/current.json') | ConvertFrom-Json
    Assert-True ($current.version -eq $script:ExpectedVersion) "current.json 版本应为 $($script:ExpectedVersion)，实际 $($current.version)"
    Assert-True (Test-Path -LiteralPath (Join-Path $root ("install/{0}" -f $script:ExpectedVersion))) '版本目录缺失'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'install/machine.json')) '缺少 machine.json'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/update.lock'))) '安装结束后不应残留更新标记'
    $statusPath = Join-Path $root 'status.json'
    $status = Invoke-Setup @('--status', '--root', $root, '--out', $statusPath)
    Assert-True ($status.ExitCode -eq 0) "status 应成功（exit $($status.ExitCode)）"
    $statusJson = Get-Content -Raw -LiteralPath $statusPath | ConvertFrom-Json
    Assert-True ($statusJson.installed -eq $true) 'status.installed 应为 true'
    Assert-True ($statusJson.version -eq $script:ExpectedVersion) 'status.version 不符'
    Assert-True ($statusJson.updateLock -eq $false) 'status.updateLock 应为 false'
}

# ---- 2. 同版本修复 ----
Invoke-Case 'install-repair-same-version' {
    $result = Invoke-Setup (Install-Args -Root $script:FreshRoot -PayloadPath $script:PrimaryPayload)
    Assert-True ($result.ExitCode -eq 0) "同版本修复应成功（exit $($result.ExitCode)）：$($result.StdErr)"
}

# ---- 3. 存活更新标记拒绝 ----
Invoke-Case 'install-refuses-live-update-marker' {
    $marker = Write-UpdateMarker -Root $script:FreshRoot -ProcessId $PID
    $result = Invoke-Setup (Install-Args -Root $script:FreshRoot -PayloadPath $script:PrimaryPayload)
    Assert-True ($result.ExitCode -ne 0) '存活 PID 的更新标记必须拒绝安装'
    Assert-True (Test-Path -LiteralPath $marker) '拒绝时不得删除他人的更新标记'
    Remove-Item -LiteralPath $marker -Force
}

# ---- 4. 陈旧更新标记清理并放行 ----
Invoke-Case 'install-recovers-stale-update-marker' {
    $deadPid = Get-DeadProcessId
    $marker = Write-UpdateMarker -Root $script:FreshRoot -ProcessId $deadPid
    $result = Invoke-Setup (Install-Args -Root $script:FreshRoot -PayloadPath $script:PrimaryPayload)
    Assert-True ($result.ExitCode -eq 0) "陈旧标记应被清理并继续安装（exit $($result.ExitCode)）：$($result.StdErr)"
    Assert-True (-not (Test-Path -LiteralPath $marker)) '陈旧标记应被删除'
}

# ---- 5. 离线持久化未完成任务拒绝（Paused/Queued/Running） ----
foreach ($taskStatus in @('Paused', 'Queued', 'Running')) {
    Invoke-Case "offline-$taskStatus-blocks-install" {
        $root = New-Root ("persist-" + $taskStatus.ToLowerInvariant())
        New-Item -ItemType Directory -Path (Join-Path $root 'data') -Force | Out-Null
        $tasks = [ordered]@{
            schema  = 1
            tasks   = [ordered]@{ 't1' = [ordered]@{ id = 't1'; status = $taskStatus } }
            requests = [ordered]@{}
        }
        Set-Content -LiteralPath (Join-Path $root 'data/tasks.json') -Value ($tasks | ConvertTo-Json -Depth 6) -Encoding utf8
        $result = Invoke-Setup (Install-Args -Root $root)
        Assert-True ($result.ExitCode -ne 0) "后台离线且存在 $taskStatus 任务时必须拒绝安装"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/update.lock'))) '失败后应清理本次更新标记'
    }
}

# ---- 6. 离线仅终态任务放行（对照） ----
Invoke-Case 'offline-completed-only-allows-install' {
    $root = New-Root 'persist-completed'
    New-Item -ItemType Directory -Path (Join-Path $root 'data') -Force | Out-Null
    $tasks = [ordered]@{
        schema  = 1
        tasks   = [ordered]@{ 't1' = [ordered]@{ id = 't1'; status = 'Completed' } }
        requests = [ordered]@{}
    }
    Set-Content -LiteralPath (Join-Path $root 'data/tasks.json') -Value ($tasks | ConvertTo-Json -Depth 6) -Encoding utf8
    $result = Invoke-Setup (Install-Args -Root $root)
    Assert-True ($result.ExitCode -eq 0) "仅终态任务时应放行（exit $($result.ExitCode)）：$($result.StdErr)"
}

# ---- 7. tasks.json 损坏 fail closed ----
Invoke-Case 'offline-malformed-state-fails-closed' {
    $root = New-Root 'persist-malformed'
    New-Item -ItemType Directory -Path (Join-Path $root 'data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $root 'data/tasks.json') -Value '{ this is not valid json' -Encoding utf8
    $result = Invoke-Setup (Install-Args -Root $root)
    Assert-True ($result.ExitCode -ne 0) 'tasks.json 损坏时必须 fail closed'
}

# ---- 8. launch.lock 被独占持有时拒绝 ----
Invoke-Case 'launch-lock-held-refuses-install' {
    $root = New-Root 'launch-lock'
    New-Item -ItemType Directory -Path (Join-Path $root 'data') -Force | Out-Null
    $lockPath = Join-Path $root 'data/launch.lock'
    $holderCode = "`$stream = [System.IO.File]::Open('$lockPath', [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None); Start-Sleep -Seconds 120; `$stream.Dispose()"
    $holderEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($holderCode))
    $holder = Start-Process -FilePath (Get-PwshPath) -ArgumentList @('-NoProfile', '-EncodedCommand', $holderEncoded) -PassThru -WindowStyle Hidden
    try {
        $held = $false
        for ($i = 0; $i -lt 100; $i++) {
            try {
                $probe = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
                $probe.Dispose()
            }
            catch {
                $held = $true
                break
            }
            Start-Sleep -Milliseconds 100
        }
        Assert-True $held 'launch.lock 未被测试进程独占持有'
        $result = Invoke-Setup (Install-Args -Root $root)
        Assert-True ($result.ExitCode -ne 0) 'launch.lock 被独占时必须拒绝安装（不得静默跳过）'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/update.lock'))) '失败后应清理本次更新标记'
    }
    finally {
        if (-not $holder.HasExited) { try { $holder.Kill($true) } catch { } }
        $holder.Dispose()
    }
}

# ---- 9. 卸载门禁与数据保留 ----
Invoke-Case 'uninstall-gated-and-keeps-data' {
    $root = New-Root 'uninstall'
    $install = Invoke-Setup (Install-Args -Root $root)
    Assert-True ($install.ExitCode -eq 0) "准备安装失败：$($install.StdErr)"
    $sentinel = Join-Path $root 'data/keep.me'
    New-Item -ItemType Directory -Path (Split-Path -Parent $sentinel) -Force | Out-Null
    Set-Content -LiteralPath $sentinel -Value 'user data' -Encoding utf8
    $marker = Write-UpdateMarker -Root $root -ProcessId $PID
    $blocked = Invoke-Setup @('--uninstall', '--silent', '--no-shell-integration', '--root', $root)
    Assert-True ($blocked.ExitCode -ne 0) '存活更新标记时必须拒绝卸载'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'install')) '被拒绝的卸载不得删除程序文件'
    Remove-Item -LiteralPath $marker -Force
    $removed = Invoke-Setup @('--uninstall', '--silent', '--no-shell-integration', '--root', $root)
    Assert-True ($removed.ExitCode -eq 0) "卸载应成功（exit $($removed.ExitCode)）：$($removed.StdErr)"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install'))) '卸载后 install 目录应删除'
    Assert-True (Test-Path -LiteralPath $sentinel) '卸载必须保留用户数据'
}

# ---- 10. 真实 payload：内置 Git 与 manifest 哈希 ----
if ($script:PayloadPath) {
    Invoke-Case 'real-payload-bundles-verified-git' {
        $lockPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging/runtime-lock.json'
        $lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json
        $git = $lock.platforms.'win-x64'.runtimes.'git-bash'
        Assert-True ($git.kind -eq 'zip') '锁清单 git-bash 应为随包分发的 zip 运行时'
        $root = New-Root 'real-payload'
        $result = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadPath)
        Assert-True ($result.ExitCode -eq 0) "真实 payload 安装失败（exit $($result.ExitCode)）：$($result.StdErr)"
        $current = Get-Content -Raw -LiteralPath (Join-Path $root 'install/current.json') | ConvertFrom-Json
        $versionRoot = $current.root
        $shExe = Join-Path $versionRoot 'engines/git/usr/bin/sh.exe'
        Assert-True (Test-Path -LiteralPath $shExe -PathType Leaf) '包内缺少 engines/git/usr/bin/sh.exe（Git 必须随包分发）'
        $shSpec = @($git.verify) | Where-Object { $_.path -eq 'usr/bin/sh.exe' } | Select-Object -First 1
        if ($shSpec) {
            $shActual = (Get-FileHash -Algorithm SHA256 -LiteralPath $shExe).Hash.ToLowerInvariant()
            Assert-True ($shActual -eq ([string]$shSpec.sha256).ToLowerInvariant()) "包内 sh.exe 哈希不符：$shActual"
        }
        $runtimeManifest = Get-Content -Raw -LiteralPath (Join-Path $versionRoot 'runtime-manifest.json') | ConvertFrom-Json
        $gitArchive = @($runtimeManifest.archives) | Where-Object { $_.runtime -eq 'git-bash' } | Select-Object -First 1
        Assert-True ($null -ne $gitArchive) 'runtime-manifest.json 缺少 git-bash 归档记录'
        Assert-True (([string]$gitArchive.sha256).ToLowerInvariant() -eq ([string]$git.archive.sha256).ToLowerInvariant()) 'runtime-manifest Git 归档哈希与锁清单不符'
        Assert-True ([int]$gitArchive.fileCount -gt 300) "Git 归档文件数异常：$($gitArchive.fileCount)"
        $machine = Get-Content -Raw -LiteralPath (Join-Path $root 'install/machine.json') | ConvertFrom-Json
        Assert-True ($machine.shellSource -eq 'bundled') 'machine.json 应指向内置 Git'
        Assert-True ($machine.shellPath -eq [IO.Path]::GetFullPath($shExe)) 'machine.json shellPath 不符'
        if ($shSpec) {
            Assert-True (([string]$machine.shellSha256).ToLowerInvariant() -eq ([string]$shSpec.sha256).ToLowerInvariant()) 'machine.json shellSha256 不符'
        }
    }
} else {
    Write-Host '跳过 real-payload-bundles-verified-git（未提供 -Payload）。' -ForegroundColor Yellow
}

# ---- 11. 真实 Host 在线且空闲：正常退出后完成安装 ----
if ($script:HostPath) {
    Invoke-Case 'host-online-idle-stops-before-swap' {
        $root = New-Root 'host-online'
        New-Item -ItemType Directory -Path (Join-Path $root 'data') -Force | Out-Null
        $hostProcess = Start-Process -FilePath $script:HostPath -ArgumentList @('--host', '--home', (Join-Path $root 'data')) -PassThru -WindowStyle Hidden
        try {
            Start-Sleep -Seconds 2
            Assert-True (-not $hostProcess.HasExited) "Host 提前退出（exit $($hostProcess.ExitCode)）"
            $result = Invoke-Setup (Install-Args -Root $root)
            Assert-True ($result.ExitCode -eq 0) "后台在线时应先正常退出再安装（exit $($result.ExitCode)）：$($result.StdErr)"
            Assert-True ($hostProcess.WaitForExit(15000)) 'Host 应已被安装程序正常关闭'
        }
        finally {
            if (-not $hostProcess.HasExited) { try { $hostProcess.Kill($true) } catch { } }
            $hostProcess.Dispose()
        }
    }
} else {
    Write-Host '跳过 host-online-idle-stops-before-swap（未提供 -HostExe）。' -ForegroundColor Yellow
}

# ---- 汇总 ----
Invoke-Case 'version-update-preserves-data-and-rejects-unfinished' {
    $root = New-Root 'update'
    $first = New-SyntheticPayload (Join-Path $WorkDir 'update-first') '0.0.1-test'
    $next = New-SyntheticPayload (Join-Path $WorkDir 'update-next') '0.0.2-test'
    Assert-True ((Invoke-Setup (Install-Args -Root $root -PayloadPath $first)).ExitCode -eq 0) '初始安装失败'
    New-Item -ItemType Directory -Path (Join-Path $root 'data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $root 'data/keep.me') -Value 'keep'
    $state = Join-Path $root 'data/tasks.json'
    Set-Content -LiteralPath $state -Value '{"schema":1,"tasks":{"t1":{"id":"t1","status":"Paused"}},"requests":{}}'
    $arguments = @('--apply-update','--payload',$next,'--root',$root,'--silent','--no-shell-integration')
    Assert-True ((Invoke-Setup $arguments).ExitCode -ne 0) '暂停任务必须阻止跨版本更新'
    $pointer = Join-Path $root 'install/current.json'
    Assert-True ((Get-Content -Raw -LiteralPath $pointer | ConvertFrom-Json).version -eq '0.0.1-test') '被拒绝的更新不得改变当前版本'
    Set-Content -LiteralPath $state -Value '{"schema":1,"tasks":{},"requests":{}}'
    Assert-True ((Invoke-Setup $arguments).ExitCode -eq 0) '空闲跨版本更新失败'
    Assert-True ((Get-Content -Raw -LiteralPath $pointer | ConvertFrom-Json).version -eq '0.0.2-test') '版本指针未切换'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'data/keep.me')) '更新必须保留数据'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'install/0.0.1-test')) '更新必须保留旧版本'
}

Invoke-Case 'partial-payload-is-rejected' {
    $root = New-Root 'partial'
    $payload = New-SyntheticPayload (Join-Path $WorkDir 'partial-payload') '0.0.1-test'
    $manifest = Join-Path $payload 'payload-manifest.json'
    $value = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
    $value.partial = $true
    $value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifest
    $result = Invoke-Setup (Install-Args -Root $root -PayloadPath $payload)
    Assert-True ($result.ExitCode -ne 0) '不完整包不得安装'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/current.json'))) '不完整包不得发布版本指针'
    Assert-True ($result.StdErr.Contains('此安装包不完整（partial）')) 'stderr 必须保留具体拒绝原因'
    $log = Get-Content -Raw -LiteralPath (Join-Path $root 'install/setup.log')
    Assert-True ($log.Contains('此安装包不完整（partial）')) '进入门禁前的安装失败原因必须落盘'
}

Invoke-Case 'corrupt-update-keeps-current-version' {
    $root = New-Root 'corrupt-update'
    $first = New-SyntheticPayload (Join-Path $WorkDir 'corrupt-first') '0.0.1-test'
    $next = New-SyntheticPayload (Join-Path $WorkDir 'corrupt-next') '0.0.2-test'
    Assert-True ((Invoke-Setup (Install-Args -Root $root -PayloadPath $first)).ExitCode -eq 0) '初始安装失败'
    $currentPath = Join-Path $root 'install/current.json'
    $configPath = Join-Path $root 'data/config.json'
    Set-Content -LiteralPath $configPath -Value '{"acceptanceFixture":true}' -Encoding utf8
    $currentHash = (Get-FileHash -LiteralPath $currentPath).Hash
    $configHash = (Get-FileHash -LiteralPath $configPath).Hash
    Set-Content -LiteralPath (Join-Path $next 'host/AgentDock.Host.exe') -Value 'corrupted'
    $result = Invoke-Setup @('--apply-update','--payload',$next,'--root',$root,'--silent','--no-shell-integration')
    Assert-True ($result.ExitCode -ne 0) '损坏更新必须失败'
    Assert-True ((Get-FileHash -LiteralPath $currentPath).Hash -eq $currentHash) '失败后版本指针必须保留'
    Assert-True ((Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) '失败后用户配置必须保留'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/update.lock'))) '失败后必须释放本次更新标记'
    $reason = 'payload 文件体积不符：host/AgentDock.Host.exe'
    Assert-True ($result.StdErr.Contains($reason)) 'stderr 必须保留具体错误原因'
    $log = Get-Content -Raw -LiteralPath (Join-Path $root 'install/setup.log')
    Assert-True ($log.Contains($reason)) '损坏更新的具体错误原因必须落盘'
}

if ($BundledSetupExe) {
    if (-not $script:PayloadPath) { throw '单文件安装验收需要 -Payload 对照版本。' }
    $standalone = New-Root 'standalone-download'
    $bundle = Join-Path $standalone 'AgentDockSetup.exe'
    Copy-Item -LiteralPath (Resolve-Path -LiteralPath $BundledSetupExe).Path -Destination $bundle
    $bundleRoot = New-Root 'bundled-install'
    $bundleData = Join-Path $bundleRoot 'data/keep.me'
    Invoke-Case 'bundled-exe-alone-installs-with-lean-launcher' {
        Assert-True (@(Get-ChildItem -LiteralPath $standalone -File).Count -eq 1) '下载目录应只有一个 EXE'
        $statusPath = Join-Path $WorkDir 'bundled-status.json'
        $status = Invoke-Setup @('--status', '--root', $bundleRoot, '--out', $statusPath) -Executable $bundle
        Assert-True ($status.ExitCode -eq 0) "读取内置包失败：$($status.StdErr)"
        $view = Get-Content -Raw -LiteralPath $statusPath | ConvertFrom-Json
        Assert-True ($view.bundledPayload -and $view.payloadVersion -eq $script:ExpectedVersion) '内置包版本必须与构建清单一致'
        $installed = Invoke-Setup @('--install', '--silent', '--no-shell-integration', '--root', $bundleRoot) -Executable $bundle
        Assert-True ($installed.ExitCode -eq 0) "单独 EXE 安装失败：$($installed.StdErr)"
        $current = Get-Content -Raw -LiteralPath (Join-Path $bundleRoot 'install/current.json') | ConvertFrom-Json
        Assert-True ($current.version -eq $script:ExpectedVersion) '安装了错误版本'
        $launcher = Join-Path $bundleRoot 'AgentDock.exe'
        $expectedLauncher = Join-Path $current.root 'setup/AgentDock.Setup.exe'
        Assert-True ((Get-FileHash -LiteralPath $launcher).Hash -eq (Get-FileHash -LiteralPath $expectedLauncher).Hash) '稳定启动器必须来自已校验的内置精简程序'
        Assert-True ((Get-Item -LiteralPath $launcher).Length -lt (Get-Item -LiteralPath $bundle).Length) '启动器不应携带整份分发包'
        $thinStatus = Invoke-Setup @('--status', '--root', $bundleRoot, '--out', $statusPath) -Executable $launcher
        Assert-True ($thinStatus.ExitCode -eq 0) '安装后的启动器不能运行'
        Assert-True (-not (Get-Content -Raw -LiteralPath $statusPath | ConvertFrom-Json).bundledPayload) '精简启动器不应内嵌 payload'
        Set-Content -LiteralPath $bundleData -Value 'user data stays'
    }
    Invoke-Case 'bundled-repair-ignores-nearby-payload' {
        Set-Content -LiteralPath (Join-Path $standalone 'payload.zip') -Value 'wrong or incomplete nearby download'
        $before = (Get-FileHash -LiteralPath $bundleData).Hash
        $repair = Invoke-Setup @('--install', '--silent', '--no-shell-integration', '--root', $bundleRoot) -Executable $bundle
        Assert-True ($repair.ExitCode -eq 0) "修复不应读取旁边的 ZIP：$($repair.StdErr)"
        Assert-True ((Get-FileHash -LiteralPath $bundleData).Hash -eq $before) '修复不得改动用户数据'
    }
    Invoke-Case 'bundled-update-is-gated-and-keeps-data' {
        $root = New-Root 'bundled-update'
        $first = Invoke-Setup (Install-Args -Root $root)
        Assert-True ($first.ExitCode -eq 0) "准备旧版失败：$($first.StdErr)"
        $config = Join-Path $root 'data/config.json'
        Set-Content -LiteralPath $config -Value '{"acceptanceFixture":true}'
        $before = (Get-FileHash -LiteralPath $config).Hash
        $state = Join-Path $root 'data/tasks.json'
        Set-Content -LiteralPath $state -Value '{"schema":1,"tasks":{"t1":{"id":"t1","status":"Paused"}},"requests":{}}'
        $arguments = @('--apply-update', '--silent', '--no-shell-integration', '--root', $root)
        $blocked = Invoke-Setup $arguments -Executable $bundle
        Assert-True ($blocked.ExitCode -ne 0) '内置包更新必须拒绝未完成任务'
        Assert-True ($blocked.StdErr -match '排队|运行|暂停|未完成') '更新必须因任务门禁拒绝，而非参数或包解析错误'
        $pointer = Join-Path $root 'install/current.json'
        Assert-True ((Get-Content -Raw -LiteralPath $pointer | ConvertFrom-Json).version -eq '0.1.0-gate-a') '被拒绝的更新不得切换版本'
        Set-Content -LiteralPath $state -Value '{"schema":1,"tasks":{},"requests":{}}'
        $updated = Invoke-Setup $arguments -Executable $bundle
        Assert-True ($updated.ExitCode -eq 0) "内置包更新失败：$($updated.StdErr)"
        Assert-True ((Get-Content -Raw -LiteralPath $pointer | ConvertFrom-Json).version -eq $script:ExpectedVersion) '更新未切换到内置版本'
        Assert-True ((Get-FileHash -LiteralPath $config).Hash -eq $before) '更新不得改动用户配置'
        Assert-True (Test-Path -LiteralPath (Join-Path $root 'install/0.1.0-gate-a')) '更新应保留旧版本'
    }
    Invoke-Case 'bundled-uninstall-keeps-data' {
        $before = (Get-FileHash -LiteralPath $bundleData).Hash
        $removed = Invoke-Setup @('--uninstall', '--silent', '--no-shell-integration', '--root', $bundleRoot) -Executable $bundle
        Assert-True ($removed.ExitCode -eq 0) "单文件安装器卸载失败：$($removed.StdErr)"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $bundleRoot 'install'))) '程序文件未删除'
        Assert-True ((Get-FileHash -LiteralPath $bundleData).Hash -eq $before) '卸载不得删除用户数据'
    }
}

Write-Host ''
$failed = @($script:Results | Where-Object { -not $_.Passed })
$script:Results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $WorkDir 'results.json') -Encoding utf8
Write-Host ("门禁自测：{0} 通过 / {1} 失败" -f ($script:Results.Count - $failed.Count), $failed.Count) -ForegroundColor ($(if ($failed.Count -eq 0) { 'Green' } else { 'Red' }))
foreach ($failure in $failed) { Write-Host ("  FAIL {0}: {1}" -f $failure.Name, $failure.Detail) -ForegroundColor Red }

if ($script:CreatedWorkDir -and -not $KeepTemp) {
    Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
} elseif ($KeepTemp) {
    Write-Host "工作目录保留：$WorkDir"
}

if ($failed.Count -gt 0) { exit 1 }
exit 0
