#requires -Version 7.0
<#
.SYNOPSIS
  AgentDock macOS 安装程序（MacSetup）的确定性隔离自测：在 Windows 上用 --root 隔离目录
  驱动同源码构建的 AgentDock.exe，覆盖安装/修复/更新/卸载/门禁/损坏回滚与清单校验。

.DESCRIPTION
  所有用例只操作隔离的 --root 目录与隔离 payload，绝不触碰真实安装根、~/Applications 或 LaunchAgents。
  运行前置：dotnet build src/AgentDock.MacSetup/AgentDock.MacSetup.csproj -c Release

.EXAMPLE
  pwsh -NoProfile -File src/AgentDock.MacSetup/tests/Test-MacSetupGates.ps1
#>
[CmdletBinding()]
param(
    [string]$SetupExe,
    [string]$WorkDir,
    [switch]$KeepTemp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
$script:SetupPath = if ($SetupExe) {
    (Resolve-Path -LiteralPath $SetupExe).Path
} else {
    (Resolve-Path -LiteralPath (Join-Path $script:RepoRoot 'src/AgentDock.MacSetup/bin/Release/net10.0/AgentDock.exe')).Path
}
$script:CreatedWorkDir = $false
if (-not $WorkDir) {
    $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('agentdock-macsetup-tests-' + [Guid]::NewGuid().ToString('N'))
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
        $script:Results.Add([pscustomobject]@{ Name = $Name; Passed = $false; Detail = $_.Exception.Message }) | Out-Null
        Write-Host "   FAIL: $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Assert-True {
    param($Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-Setup {
    param([string[]]$Arguments, [int]$TimeoutSeconds = 180)
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:SetupPath
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

function New-Root {
    param([string]$Name)
    $script:CaseCounter++
    $root = Join-Path $WorkDir ("{0:d2}-{1}" -f $script:CaseCounter, $Name)
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    return $root
}

function New-SyntheticPayload {
    param([string]$Directory, [string]$Version, [string]$Runtime = 'osx-arm64')
    if (Test-Path -LiteralPath $Directory) { Remove-Item -LiteralPath $Directory -Recurse -Force }
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $files = [ordered]@{
        'host/AgentDock.Host'                                    = "synthetic host $Version"
        'desktop/AgentDock.app/Contents/MacOS/AgentDock.Desktop' = "synthetic desktop $Version"
        'desktop/AgentDock.app/Contents/Info.plist'              = "<plist><string>$Version</string></plist>"
        'engine/AgentDock.Engine'                                = "synthetic engine $Version"
        'engines/opencode/opencode'                              = "synthetic opencode"
        'engines/codex/bin/codex'                                = "synthetic codex"
        'engines/git/bin/git'                                    = "synthetic git"
        'plugin/.agents/plugins/marketplace.json'                = '{"name":"agentdock-local"}'
        'plugin/plugins/agentdock/.codex-plugin/plugin.json'     = ('{"name":"agentdock","version":"' + $Version + '"}')
        'plugin/plugins/agentdock/.mcp.json'                     = '{"mcpServers":{"agentdock":{"command":"AGENTDOCK_LAUNCHER_PLACEHOLDER","args":["--mcp"]}}}'
    }
    $entries = @()
    foreach ($relative in $files.Keys) {
        $path = Join-Path $Directory ($relative -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Set-Content -LiteralPath $path -Value $files[$relative] -Encoding utf8 -NoNewline
        $item = Get-Item -LiteralPath $path
        $mode = if ($relative -match '^(host/AgentDock\.Host|desktop/|engine/AgentDock\.Engine|engines/.*/(opencode|codex|git))') { '0755' } else { '0644' }
        $entries += [ordered]@{
            path   = $relative
            size   = [long]$item.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
            mode   = $mode
        }
    }
    $manifest = [ordered]@{
        schema  = 1
        product = 'AgentDock'
        version = $Version
        runtime = $Runtime
        partial = $false
        files   = $entries
    }
    Set-Content -LiteralPath (Join-Path $Directory 'payload-manifest.json') -Value ($manifest | ConvertTo-Json -Depth 8) -Encoding utf8
    return $Directory
}

function Get-Manifest {
    param([string]$PayloadDirectory)
    return (Get-Content -Raw -LiteralPath (Join-Path $PayloadDirectory 'payload-manifest.json') | ConvertFrom-Json)
}

function Save-Manifest {
    param([string]$PayloadDirectory, $Manifest)
    $Manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $PayloadDirectory 'payload-manifest.json') -Encoding utf8
}

function Get-TreeDigest {
    param([string]$Path)
    $lines = Get-ChildItem -LiteralPath $Path -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Path, $_.FullName).Replace('\', '/')
        "$relative=$((Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant())"
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    return ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
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
    param([string]$Root, [string]$PayloadPath)
    return @('--install', '--payload', $PayloadPath, '--silent', '--no-shell-integration', '--root', $Root)
}

# ---- 准备 payload ----
$script:PayloadA = New-SyntheticPayload -Directory (Join-Path $WorkDir 'payload-a') -Version '0.1.0-gate-a'
$script:PayloadB = New-SyntheticPayload -Directory (Join-Path $WorkDir 'payload-b') -Version '0.1.0-gate-b'
$script:InstalledPayload = $script:PayloadA
Write-Host "MacSetup：#$script:SetupPath"
Write-Host "合成 payload：$($script:PayloadA + ' / ' + $script:PayloadB)"

# ---- 1. 全新安装：桌面 bundle 路径、必需 Info.plist、插件 --root、machine.json ----
Invoke-Case 'install-fresh-layout-and-contract' {
    $root = New-Root 'fresh'
    $result = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadA)
    Assert-True ($result.ExitCode -eq 0) "安装应成功（exit $($result.ExitCode)）：$($result.StdErr)$($result.StdOut)"
    $current = Get-Content -Raw -LiteralPath (Join-Path $root 'install/current.json') | ConvertFrom-Json
    Assert-True ($current.version -eq '0.1.0-gate-a') 'current.version 不符'
    $desktopSuffix = 'desktop\AgentDock.app\Contents\MacOS\AgentDock.Desktop'
    Assert-True ($current.desktop.EndsWith($desktopSuffix)) "desktop 必须指向 bundle 内可执行文件，实际 $($current.desktop)"
    Assert-True (Test-Path -LiteralPath (Join-Path $root ('install/0.1.0-gate-a/desktop/AgentDock.app/Contents/Info.plist'))) '缺少桌面 bundle Info.plist'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'install/machine.json')) '缺少 machine.json'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'plugin/plugins/agentdock/runtime/install.json')) '缺少插件 install.json'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/update.lock'))) '安装结束后不应残留更新标记'
    $mcp = Get-Content -Raw -LiteralPath (Join-Path $root 'plugin/plugins/agentdock/.mcp.json') | ConvertFrom-Json
    $args = @($mcp.mcpServers.agentdock.args)
    Assert-True ($args[0] -eq '--mcp') '.mcp.json args 必须以 --mcp 开头'
    Assert-True ($args[1] -eq '--root' -and $args[2] -eq $root) ".mcp.json 必须携带 --root $root，实际：$($args -join ' ')"
    Assert-True ($mcp.mcpServers.agentdock.command -eq (Join-Path $root 'AgentDock.app/Contents/MacOS/AgentDock')) '.mcp.json command 必须指向 <root>/AgentDock.app 内启动器'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'AgentDock.app'))) '隔离环境不应凭空出现稳定 .app（安装器源缺失时跳过）'
    $script:FreshRoot = $root
    $script:PluginDigest = Get-TreeDigest -Path (Join-Path $root 'plugin')
}

# ---- 1b. 从 payload.zip 安装（真实产品路径） ----
Invoke-Case 'install-from-zip-payload' {
    $zip = Join-Path $WorkDir 'payload-a.zip'
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($script:PayloadA, $zip)
    $root = New-Root 'fresh-zip'
    $result = Invoke-Setup (Install-Args -Root $root -PayloadPath $zip)
    Assert-True ($result.ExitCode -eq 0) "zip 安装应成功（exit $($result.ExitCode)）：$($result.StdErr)"
    $current = Get-Content -Raw -LiteralPath (Join-Path $root 'install/current.json') | ConvertFrom-Json
    Assert-True ($current.version -eq '0.1.0-gate-a') 'zip 安装后版本指针不符'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'install/0.1.0-gate-a/host/AgentDock.Host')) 'zip 安装缺少版本目录内容'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/update.lock'))) 'zip 安装后不应残留更新标记'
}

# ---- 2. 同版本幂等修复：内容不变 ----
Invoke-Case 'install-repair-same-version-idempotent' {
    $before = Get-TreeDigest -Path (Join-Path $script:FreshRoot 'plugin')
    $result = Invoke-Setup (Install-Args -Root $script:FreshRoot -PayloadPath $script:PayloadA)
    Assert-True ($result.ExitCode -eq 0) "同版本修复应成功（exit $($result.ExitCode)）：$($result.StdErr)"
    $after = Get-TreeDigest -Path (Join-Path $script:FreshRoot 'plugin')
    Assert-True ($before -eq $after) '幂等修复后插件内容不应改变'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $script:FreshRoot 'install/update.lock'))) '修复后不应残留更新标记'
}

# ---- 3. 同版本损坏文件修复（版本目录备份 + 重构） ----
Invoke-Case 'install-repair-corrupt-version-dir' {
    $victim = Join-Path $script:FreshRoot 'install/0.1.0-gate-a/host/AgentDock.Host'
    Set-Content -LiteralPath $victim -Value 'corrupted' -Encoding utf8 -NoNewline
    $result = Invoke-Setup (Install-Args -Root $script:FreshRoot -PayloadPath $script:PayloadA)
    Assert-True ($result.ExitCode -eq 0) "损坏修复应成功（exit $($result.ExitCode)）：$($result.StdErr)"
    $expected = (Get-Manifest -PayloadDirectory $script:PayloadA).files | Where-Object { $_.path -eq 'host/AgentDock.Host' } | Select-Object -First 1
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $victim).Hash.ToLowerInvariant()
    Assert-True ($actual -eq $expected.sha256) '损坏文件必须被恢复为包内内容'
    $pointer = Get-Content -Raw -LiteralPath (Join-Path $script:FreshRoot 'install/current.json') | ConvertFrom-Json
    Assert-True ($pointer.version -eq '0.1.0-gate-a') '修复后版本指针应保持本版本'
}

# ---- 4. 更新：保留旧版本与用户数据 ----
Invoke-Case 'apply-update-keeps-old-version-and-data' {
    $data = Join-Path $script:FreshRoot 'data/keep.me'
    New-Item -ItemType Directory -Path (Split-Path -Parent $data) -Force | Out-Null
    Set-Content -LiteralPath $data -Value 'user data' -Encoding utf8
    $result = Invoke-Setup @('--apply-update', '--payload', $script:PayloadB, '--silent', '--no-shell-integration', '--root', $script:FreshRoot)
    Assert-True ($result.ExitCode -eq 0) "更新应成功（exit $($result.ExitCode)）：$($result.StdErr)"
    $pointer = Get-Content -Raw -LiteralPath (Join-Path $script:FreshRoot 'install/current.json') | ConvertFrom-Json
    Assert-True ($pointer.version -eq '0.1.0-gate-b') '更新后版本指针未切换'
    Assert-True (Test-Path -LiteralPath (Join-Path $script:FreshRoot 'install/0.1.0-gate-a')) '更新必须保留旧版本目录'
    Assert-True (Test-Path -LiteralPath $data) '更新必须保留用户数据'
}

# ---- 5. 未完成任务（Paused/Queued/Running）拒绝安装 ----
foreach ($status in @('Paused', 'Queued', 'Running')) {
    Invoke-Case "unfinished-$status-blocks-install" {
        $root = New-Root ("unfinished-" + $status.ToLowerInvariant())
        New-Item -ItemType Directory -Path (Join-Path $root 'data') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $root 'data/tasks.json') -Encoding utf8 `
            -Value ('{"schema":1,"tasks":{"t1":{"id":"t1","status":"' + $status + '"}},"requests":{}}')
        $result = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadA)
        Assert-True ($result.ExitCode -ne 0) "存在 $status 任务时必须拒绝安装"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/current.json'))) '被拒绝时不得发布版本指针'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/update.lock'))) '失败后应清理本次更新标记'
    }
}

# ---- 6. tasks.json 损坏 fail closed ----
Invoke-Case 'corrupted-state-fails-closed' {
    $root = New-Root 'corrupted-state'
    New-Item -ItemType Directory -Path (Join-Path $root 'data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $root 'data/tasks.json') -Value '{ not json' -Encoding utf8
    $result = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadA)
    Assert-True ($result.ExitCode -ne 0) 'tasks.json 损坏时必须 fail closed'
}

# ---- 7. 存活更新标记拒绝；陈旧标记清理放行 ----
Invoke-Case 'live-update-marker-refuses-and-stale-recovers' {
    $root = New-Root 'markers'
    $live = Write-UpdateMarker -Root $root -ProcessId $PID
    $blocked = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadA)
    Assert-True ($blocked.ExitCode -ne 0) '存活 PID 的更新标记必须拒绝安装'
    Assert-True (Test-Path -LiteralPath $live) '拒绝时不得删除他人的更新标记'
    Remove-Item -LiteralPath $live -Force
    $stale = Write-UpdateMarker -Root $root -ProcessId (Get-DeadProcessId)
    $recovered = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadA)
    Assert-True ($recovered.ExitCode -eq 0) "陈旧标记应清理并继续（exit $($recovered.ExitCode)）：$($recovered.StdErr)"
    Assert-True (-not (Test-Path -LiteralPath $stale)) '陈旧标记应被删除'
}

# ---- 8. 清单校验：partial/runtime/缺失/hash/路径/mode/重复/未知文件 ----
Invoke-Case 'manifest-partial-rejected' {
    $payload = New-SyntheticPayload -Directory (Join-Path $WorkDir 'bad-partial') -Version '0.0.1-bad'
    $manifest = Get-Manifest -PayloadDirectory $payload
    $manifest.partial = $true
    Save-Manifest -PayloadDirectory $payload -Manifest $manifest
    $root = New-Root 'bad-partial-root'
    Assert-True ((Invoke-Setup (Install-Args -Root $root -PayloadPath $payload)).ExitCode -ne 0) 'partial 包必须被拒绝'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/current.json'))) 'partial 包不得发布指针'
}

Invoke-Case 'manifest-wrong-runtime-rejected' {
    $payload = New-SyntheticPayload -Directory (Join-Path $WorkDir 'bad-runtime') -Version '0.0.1-bad' -Runtime 'osx-x64'
    Assert-True ((Invoke-Setup (Install-Args -Root (New-Root 'bad-runtime-root') -PayloadPath $payload)).ExitCode -ne 0) '未知 runtime 必须被拒绝'
}

Invoke-Case 'manifest-missing-component-rejected' {
    $payload = New-SyntheticPayload -Directory (Join-Path $WorkDir 'bad-missing') -Version '0.0.1-bad'
    Remove-Item -LiteralPath (Join-Path $payload 'desktop/AgentDock.app/Contents/MacOS/AgentDock.Desktop') -Force
    Remove-Item -LiteralPath (Join-Path $payload 'desktop/AgentDock.app/Contents/MacOS')
    $manifest = Get-Manifest -PayloadDirectory $payload
    $manifest.files = @($manifest.files | Where-Object { $_.path -ne 'desktop/AgentDock.app/Contents/MacOS/AgentDock.Desktop' })
    Save-Manifest -PayloadDirectory $payload -Manifest $manifest
    Assert-True ((Invoke-Setup (Install-Args -Root (New-Root 'bad-missing-root') -PayloadPath $payload)).ExitCode -ne 0) '缺少必需组件必须被拒绝'
}

Invoke-Case 'manifest-hash-mismatch-rejected' {
    $payload = New-SyntheticPayload -Directory (Join-Path $WorkDir 'bad-hash') -Version '0.0.1-bad'
    $manifest = Get-Manifest -PayloadDirectory $payload
    ($manifest.files | Where-Object { $_.path -eq 'host/AgentDock.Host' }).sha256 = 'a' * 64
    Save-Manifest -PayloadDirectory $payload -Manifest $manifest
    Assert-True ((Invoke-Setup (Install-Args -Root (New-Root 'bad-hash-root') -PayloadPath $payload)).ExitCode -ne 0) 'hash 不符必须被拒绝'
}

Invoke-Case 'manifest-path-traversal-rejected' {
    $payload = New-SyntheticPayload -Directory (Join-Path $WorkDir 'bad-path') -Version '0.0.1-bad'
    $manifest = Get-Manifest -PayloadDirectory $payload
    $manifest.files = @($manifest.files) + @([pscustomobject]@{ path = '../escape.txt'; size = 1; sha256 = 'b' * 64; mode = '0644' })
    Save-Manifest -PayloadDirectory $payload -Manifest $manifest
    Assert-True ((Invoke-Setup (Install-Args -Root (New-Root 'bad-path-root') -PayloadPath $payload)).ExitCode -ne 0) '路径越界必须被拒绝'
}

Invoke-Case 'manifest-missing-mode-rejected' {
    $payload = New-SyntheticPayload -Directory (Join-Path $WorkDir 'bad-mode') -Version '0.0.1-bad'
    $manifest = Get-Manifest -PayloadDirectory $payload
    ($manifest.files | Where-Object { $_.path -eq 'host/AgentDock.Host' }).mode = $null
    Save-Manifest -PayloadDirectory $payload -Manifest $manifest
    Assert-True ((Invoke-Setup (Install-Args -Root (New-Root 'bad-mode-root') -PayloadPath $payload)).ExitCode -ne 0) '清单文件缺少 mode 必须被拒绝'
}

Invoke-Case 'manifest-executable-without-exec-bit-rejected' {
    $payload = New-SyntheticPayload -Directory (Join-Path $WorkDir 'bad-exec') -Version '0.0.1-bad'
    $manifest = Get-Manifest -PayloadDirectory $payload
    ($manifest.files | Where-Object { $_.path -eq 'host/AgentDock.Host' }).mode = '0644'
    Save-Manifest -PayloadDirectory $payload -Manifest $manifest
    Assert-True ((Invoke-Setup (Install-Args -Root (New-Root 'bad-exec-root') -PayloadPath $payload)).ExitCode -ne 0) '入口缺少可执行位必须被拒绝'
}

Invoke-Case 'manifest-setuid-and-duplicate-path-rejected' {
    $payload = New-SyntheticPayload -Directory (Join-Path $WorkDir 'bad-setuid') -Version '0.0.1-bad'
    $manifest = Get-Manifest -PayloadDirectory $payload
    ($manifest.files | Where-Object { $_.path -eq 'host/AgentDock.Host' }).mode = '4755'
    Save-Manifest -PayloadDirectory $payload -Manifest $manifest
    Assert-True ((Invoke-Setup (Install-Args -Root (New-Root 'bad-setuid-root') -PayloadPath $payload)).ExitCode -ne 0) 'setuid 位必须被拒绝'

    $payload2 = New-SyntheticPayload -Directory (Join-Path $WorkDir 'bad-dup') -Version '0.0.1-bad'
    $manifest2 = Get-Manifest -PayloadDirectory $payload2
    $first = $manifest2.files | Where-Object { $_.path -eq 'host/AgentDock.Host' } | Select-Object -First 1
    $manifest2.files = @($manifest2.files) + @($first)
    Save-Manifest -PayloadDirectory $payload2 -Manifest $manifest2
    Assert-True ((Invoke-Setup (Install-Args -Root (New-Root 'bad-dup-root') -PayloadPath $payload2)).ExitCode -ne 0) '重复清单路径必须被拒绝'
}

Invoke-Case 'manifest-unknown-file-and-symlink-rejected' {
    $payload = New-SyntheticPayload -Directory (Join-Path $WorkDir 'bad-extra') -Version '0.0.1-bad'
    Set-Content -LiteralPath (Join-Path $payload 'host/extra.bin') -Value 'extra' -Encoding utf8
    Assert-True ((Invoke-Setup (Install-Args -Root (New-Root 'bad-extra-root') -PayloadPath $payload)).ExitCode -ne 0) '清单外文件必须被拒绝'

    $payload2 = New-SyntheticPayload -Directory (Join-Path $WorkDir 'bad-link') -Version '0.0.1-bad'
    $junction = Join-Path $payload2 'host/linked'
    try {
        New-Item -ItemType Junction -Path $junction -Target (Join-Path $payload2 'engine') -ErrorAction Stop | Out-Null
    }
    catch {
        Write-Host '   跳过 symlink 子用例（当前环境不允许创建重解析点）。' -ForegroundColor Yellow
        return
    }
    Assert-True ((Invoke-Setup (Install-Args -Root (New-Root 'bad-link-root') -PayloadPath $payload2)).ExitCode -ne 0) 'payload 含符号链接/重解析点必须被拒绝'
}

# ---- 9. 失败回滚：machine.json 被占用时保持旧指针与旧插件 ----
Invoke-Case 'finalize-failure-rolls-back-stable-assets' {
    $root = New-Root 'rollback'
    $install = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadA)
    Assert-True ($install.ExitCode -eq 0) "初始安装失败：$($install.StdErr)"
    $pluginDigest = Get-TreeDigest -Path (Join-Path $root 'plugin')
    $machinePath = Join-Path $root 'install/machine.json'
    $before = Get-Content -Raw -LiteralPath $machinePath
    Remove-Item -LiteralPath $machinePath -Force
    New-Item -ItemType Directory -Path $machinePath -Force | Out-Null
    $result = Invoke-Setup @('--apply-update', '--payload', $script:PayloadB, '--silent', '--no-shell-integration', '--root', $root)
    Assert-True ($result.ExitCode -ne 0) 'machine.json 写入失败时更新必须失败'
    $pointer = Get-Content -Raw -LiteralPath (Join-Path $root 'install/current.json') | ConvertFrom-Json
    Assert-True ($pointer.version -eq '0.1.0-gate-a') '失败后 current.json 必须保持旧版本'
    Assert-True ((Get-TreeDigest -Path (Join-Path $root 'plugin')) -eq $pluginDigest) '失败后稳定插件必须回滚到原内容'
    $machineAgain = Get-Content -Raw -LiteralPath (Join-Path $root 'install/machine.json') -ErrorAction SilentlyContinue
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/update.lock'))) '失败后应清理更新标记'
    Remove-Item -LiteralPath $machinePath -Recurse -Force
    Set-Content -LiteralPath $machinePath -Value $before -Encoding utf8 -NoNewline
}

# ---- 9a. current.json 被占用：发布失败必须回滚插件与 machine.json ----
Invoke-Case 'publish-failure-keeps-old-stable-assets' {
    $root = New-Root 'publish-failure'
    $install = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadA)
    Assert-True ($install.ExitCode -eq 0) "初始安装失败：$($install.StdErr)"
    $pluginDigest = Get-TreeDigest -Path (Join-Path $root 'plugin')
    $machineBefore = Get-Content -Raw -LiteralPath (Join-Path $root 'install/machine.json')
    $pointerPath = Join-Path $root 'install/current.json'
    Remove-Item -LiteralPath $pointerPath -Force
    New-Item -ItemType Directory -Path $pointerPath -Force | Out-Null
    $result = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadB)
    Assert-True ($result.ExitCode -ne 0) 'current.json 无法发布时安装必须失败'
    Assert-True ((Get-TreeDigest -Path (Join-Path $root 'plugin')) -eq $pluginDigest) '发布失败后稳定插件必须回滚到原内容'
    Assert-True ((Get-Content -Raw -LiteralPath (Join-Path $root 'install/machine.json')) -eq $machineBefore) '发布失败后 machine.json 必须还原'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/update.lock'))) '失败后应清理更新标记'
}

# ---- 9b. 损坏修复失败：损坏目录必须原样还原（不销毁、不虚假提交） ----
Invoke-Case 'repair-failure-restores-damaged-version-dir' {
    $root = New-Root 'repair-rollback'
    $install = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadA)
    Assert-True ($install.ExitCode -eq 0) "初始安装失败：$($install.StdErr)"
    $victim = Join-Path $root 'install/0.1.0-gate-a/host/AgentDock.Host'
    Set-Content -LiteralPath $victim -Value 'damaged-keep' -Encoding utf8 -NoNewline
    $machinePath = Join-Path $root 'install/machine.json'
    Remove-Item -LiteralPath $machinePath -Force
    New-Item -ItemType Directory -Path $machinePath -Force | Out-Null
    $result = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadA)
    Assert-True ($result.ExitCode -ne 0) 'machine.json 写入失败时损坏修复必须失败'
    Assert-True ((Get-Content -Raw -LiteralPath $victim) -eq 'damaged-keep') '修复失败后损坏目录必须还原（不得销毁原内容）'
    $pointer = Get-Content -Raw -LiteralPath (Join-Path $root 'install/current.json') | ConvertFrom-Json
    Assert-True ($pointer.version -eq '0.1.0-gate-a') '失败后版本指针必须保持'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install/update.lock'))) '失败后应清理更新标记'
}

# ---- 10. 卸载：门禁拒绝 + 保留数据/无关文件 + 只删本安装目标 ----
Invoke-Case 'uninstall-gated-keeps-data-and-unrelated' {
    $root = New-Root 'uninstall'
    $install = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadA)
    Assert-True ($install.ExitCode -eq 0) "准备安装失败：$($install.StdErr)"
    $data = Join-Path $root 'data/keep.me'
    New-Item -ItemType Directory -Path (Split-Path -Parent $data) -Force | Out-Null
    Set-Content -LiteralPath $data -Value 'user data' -Encoding utf8
    $unrelated = Join-Path $root 'unrelated.txt'
    Set-Content -LiteralPath $unrelated -Value 'not ours' -Encoding utf8
    New-Item -ItemType Directory -Path (Join-Path $root 'AgentDock.app/Contents/MacOS') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $root 'AgentDock.app/Contents/MacOS/AgentDock') -Value 'fake launcher' -Encoding utf8

    $marker = Write-UpdateMarker -Root $root -ProcessId $PID
    $blocked = Invoke-Setup @('--uninstall', '--silent', '--no-shell-integration', '--root', $root)
    Assert-True ($blocked.ExitCode -ne 0) '存活更新标记时必须拒绝卸载'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'install/current.json')) '被拒绝的卸载不得删除程序文件'
    Assert-True (Test-Path -LiteralPath $marker) '被拒绝的卸载不得删除他人标记'
    Remove-Item -LiteralPath $marker -Force

    $removed = Invoke-Setup @('--uninstall', '--silent', '--no-shell-integration', '--root', $root)
    Assert-True ($removed.ExitCode -eq 0) "卸载应成功（exit $($removed.ExitCode)）：$($removed.StdErr)"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'install'))) '卸载后 install 目录应删除'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'plugin'))) '卸载后插件源应删除'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'AgentDock.app'))) '卸载后稳定 .app 应删除'
    Assert-True (Test-Path -LiteralPath $data) '卸载必须保留用户数据'
    Assert-True (Test-Path -LiteralPath $unrelated) '卸载必须保留无关 root 文件'
    Assert-True (Test-Path -LiteralPath $root) '卸载绝不删除安装根'
}

Invoke-Case 'uninstall-refuses-unfinished-and-foreign-dirs' {
    $root = New-Root 'uninstall-guard'
    $install = Invoke-Setup (Install-Args -Root $root -PayloadPath $script:PayloadA)
    Assert-True ($install.ExitCode -eq 0) "准备安装失败：$($install.StdErr)"
    Set-Content -LiteralPath (Join-Path $root 'data/tasks.json') -Encoding utf8 `
        -Value '{"schema":1,"tasks":{"t1":{"id":"t1","status":"Running"}},"requests":{}}'
    $blocked = Invoke-Setup @('--uninstall', '--silent', '--no-shell-integration', '--root', $root)
    Assert-True ($blocked.ExitCode -ne 0) '未完成任务存在时必须拒绝卸载'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'install/current.json')) '被拒绝的卸载不得删除程序文件'
    Remove-Item -LiteralPath (Join-Path $root 'data/tasks.json') -Force

    $foreign = Join-Path $root 'install/user-files'
    New-Item -ItemType Directory -Path $foreign -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $foreign 'keep.txt') -Value 'manual' -Encoding utf8
    $removed = Invoke-Setup @('--uninstall', '--silent', '--no-shell-integration', '--root', $root)
    Assert-True ($removed.ExitCode -eq 0) "卸载应成功（exit $($removed.ExitCode)）：$($removed.StdErr)"
    Assert-True (Test-Path -LiteralPath (Join-Path $foreign 'keep.txt')) '无法确认属于本安装的 install 子目录必须保留'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'plugin'))) '插件源仍应删除'
}

# ---- 11. 非 macOS 隔离保护：无 --root 拒绝安装 ----
Invoke-Case 'non-macos-refuses-install-without-root' {
    $result = Invoke-Setup @('--install', '--payload', $script:PayloadA, '--silent', '--no-shell-integration')
    Assert-True ($result.ExitCode -ne 0) '非 macOS 且无 --root 时必须拒绝安装（隔离保护）'
}

# ---- 汇总 ----
Write-Host ''
$failed = @($script:Results | Where-Object { -not $_.Passed })
$script:Results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $WorkDir 'results.json') -Encoding utf8
Write-Host ("MacSetup 门禁自测：{0} 通过 / {1} 失败" -f ($script:Results.Count - $failed.Count), $failed.Count) `
    -ForegroundColor ($(if ($failed.Count -eq 0) { 'Green' } else { 'Red' }))
foreach ($failure in $failed) { Write-Host ("  FAIL {0}: {1}" -f $failure.Name, $failure.Detail) -ForegroundColor Red }

if ($script:CreatedWorkDir -and -not $KeepTemp) {
    Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
} elseif ($KeepTemp) {
    Write-Host "工作目录保留：$WorkDir"
}

if ($failed.Count -gt 0) { exit 1 }
exit 0
