#requires -version 5.1
<#
.SYNOPSIS
  AgentDock Windows 长时耐久（soak）只读采样器：锁定已运行的 Host/Desktop，持续采样，严格判定成败。

.DESCRIPTION
  对主代理已经启动的 AgentDock 后台（AgentDock.Host）与桌面端（AgentDock.Desktop）做只读监测，
  用于 12 小时（默认 43200 秒）级别的耐久验证。全部证据写入新建的 -Output 目录。

  行为边界（刻意不做的事）：
    - 不启动、不停止任何产品进程或任务；不启动 UI/Hyper-V；不调用任何真实模型；
    - 不修改用户配置、不写入数据目录、不修改电源计划；
    - 仅通过命名管道调用只读 "status" 方法读取未完成数量；不调用 list/get/config.*，
      不读取或写入任务正文与模型配置；
    - connection.token 只在内存中用于命名管道请求；任何输出（JSONL、state.json、控制台、
      异常文本）都不包含其值。

  监测项（任一项出现都计入失败，绝不吞掉）：
    - Host/Desktop 以 PID + StartTime + exePath 锁定；进程退出、被替换或重启（PID 复用）；
    - 采样间隔异常增大、系统睡眠/挂起或墙钟跳变（wall 时钟与 monotonic 时钟差值）；
    - Desktop 顶层窗口数为 0，或窗口 WM_NULL + SendMessageTimeout(500ms) 超时；
    - 命名管道 IPC 失败（连接/读取超时、错误响应、令牌不可用、管道写入失败）与响应 JSON 错误；
    - 结束时 unfinishedTasks > 0 或存在 opencode / AgentDock.Engine / codex 执行进程（有界等待后仍不清）。

  结果判定（state.json，按样本原子更新）：
    - 运行期间 success 只会是 null 或 false；
    - completed=true 要求：完整时长真实经过、无任何异常、采样数量充足，
      且结束时 unfinishedTasks=0 且无执行进程；
    - 采样不足、中断、取消或异常一律记为 failed 或 incomplete；退出码 0 仅代表 completed。

  输出：
    - <Output>\soak-samples.jsonl：每样本一行（UTC/本地时刻、monotonic elapsed、CPU 累计、
      WSS/private/handle/thread、空闲物理内存、只读 status 未完成数量），流式追加，不整批驻留内存；
    - <Output>\state.json：按样本原子更新（临时文件 + File.Replace/Move）。

.PARAMETER DataHome
  产品数据目录（内含 connection.token；命名管道名由该目录完整路径推导）。
  必须已存在；本脚本只读其中的 connection.token，不创建、不写入该目录。

.PARAMETER Output
  必须全新的证据输出目录（不存在，或存在但为空），且与 DataHome 必须完全分离。

.PARAMETER HostPid
  目标 AgentDock.Host 进程 PID，在采样开始时锁定（PID + StartTime + exePath）。

.PARAMETER DesktopPid
  目标 AgentDock.Desktop 进程 PID，在采样开始时锁定（PID + StartTime + exePath）。

.PARAMETER DurationSeconds
  目标持续时长（秒），默认 43200（12 小时）。

.PARAMETER IntervalSeconds
  采样间隔（秒），默认 15。

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Probe-WindowsSoak.ps1 `
    -DataHome "$env:LOCALAPPDATA\AgentDock\data" -Output "C:\AgentDockSoak\run-01" `
    -HostPid 1234 -DesktopPid 5678

.NOTES
  - 兼容 Windows PowerShell 5.1 与 PowerShell 7+；本文件为 UTF-8 BOM。
  - 只记录本轮启动以来的 AgentDock 相关 Application Error(1000) / Application Hang(1002) 事件；
    权限失败明确记录在 state.json 的 permissions 中。
  - workingSet 的单样本参考值（历史文档 Host≈80 MiB / Desktop≈200 MiB）不是本脚本的通过门槛，
    也不构成发布门槛；私有内存持续增长只标记 requiresMemoryReview 供人工复核。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DataHome,
    [Parameter(Mandatory = $true)][string]$Output,
    [Parameter(Mandatory = $true)][int]$HostPid,
    [Parameter(Mandatory = $true)][int]$DesktopPid,
    [int]$DurationSeconds = 43200,
    [int]$IntervalSeconds = 15
)

$ErrorActionPreference = 'Stop'

# =====================================================================
# 常量
# =====================================================================

$script:WindowPulseTimeoutMs = 500
$script:IpcConnectTimeoutMs = 2000
$script:IpcReadTimeoutMs = 8000
$script:CrashQueryIntervalSeconds = 300
$script:MaxAnomalyEvents = 50
$script:ExecutionProcessNames = @('opencode', 'AgentDock.Engine', 'codex')

# =====================================================================
# 原生辅助（C# 5 兼容；PS 5.1 / 7 均可用）
# =====================================================================

function Initialize-Native {
    if (([Management.Automation.PSTypeName]'AgentDockSoakNative').Type) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class AgentDockSoakNative
{
    /// <summary>Ctrl+C 请求停止；由控制台事件处理器写入，主循环只读。</summary>
    public static volatile bool Cancelled = false;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>返回 [顶层窗口数, WM_NULL 超时数]。</summary>
    public static long[] WindowPulse(int processId, uint timeoutMilliseconds)
    {
        int total = 0;
        int failed = 0;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            uint owner;
            GetWindowThreadProcessId(hWnd, out owner);
            if (owner != (uint)processId) { return true; }
            total++;
            IntPtr result;
            if (SendMessageTimeout(hWnd, 0, IntPtr.Zero, IntPtr.Zero, 2, timeoutMilliseconds, out result) == IntPtr.Zero)
            {
                // A toast can close between enumeration and the message send.
                if (IsWindow(hWnd)) { failed++; }
            }
            return true;
        }, IntPtr.Zero);
        return new long[] { (long)total, (long)failed };
    }

    /// <summary>空闲物理内存字节数；失败返回 ulong.MaxValue。</summary>
    public static ulong FreePhysicalBytes()
    {
        MEMORYSTATUSEX status = new MEMORYSTATUSEX();
        status.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        if (!GlobalMemoryStatusEx(ref status)) { return ulong.MaxValue; }
        return status.ullAvailPhys;
    }
}
'@
}

# =====================================================================
# 通用工具
# =====================================================================

function Get-DockPipeName([string]$TaskDataHome) {
    # 与 AgentDock.Core.DockClient.PipeFor 保持一致：完整路径 ToUpperInvariant 的 SHA-256 前 24 个十六进制字符。
    $full = [IO.Path]::GetFullPath($TaskDataHome).ToUpperInvariant()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($full)) } finally { $sha.Dispose() }
    return 'agentdock-' + ([BitConverter]::ToString($bytes) -replace '-', '').Substring(0, 24)
}

function Set-Fatal([string]$Message) {
    if ($null -eq $script:FatalError) { $script:FatalError = $Message }
}

function Test-Cancelled {
    # An operator can end a detached run without killing the sampling process.
    if ($script:OutputDir -and [IO.File]::Exists((Join-Path $script:OutputDir 'stop.requested'))) {
        $script:CancelRequested = $true
    }
    # 同时看脚本作用域与 native 静态标志；后者由 Ctrl+C 事件处理器直接写入，避免作用域歧义。
    if ($script:CancelRequested) { return $true }
    try { return [AgentDockSoakNative]::Cancelled } catch { return $false }
}

function Add-Anomaly([string]$Type, [string]$Detail) {
    $script:Anomalies[$Type] = [int]$script:Anomalies[$Type] + 1
    $script:Anomalies.total = [int]$script:Anomalies.total + 1
    if ($script:AnomalyEvents.Count -lt $script:MaxAnomalyEvents) {
        $script:AnomalyEvents.Add([ordered]@{
            atUtc          = [DateTime]::UtcNow.ToString('o')
            elapsedSeconds = [Math]::Round($script:Watch.Elapsed.TotalSeconds, 3)
            type           = $Type
            detail         = $Detail
        })
    } else {
        $script:AnomalyEventsDropped++
    }
    # running 期间 success 只允许 null 或 false；出现异常立即置 false。
    if ($null -ne $script:State -and $null -eq $script:State.success) { $script:State.success = $false }
}

function Add-PermissionNote([string]$Note) {
    if ($script:PermissionNotes.Count -lt 20) { $script:PermissionNotes.Add($Note) }
    else { $script:PermissionNotesDropped++ }
}

# =====================================================================
# 只读 IPC：status（令牌只在本函数内存中存在，绝不输出）
# =====================================================================

function Invoke-DockStatusRead {
    $result = [ordered]@{ ok = $false; unfinished = $null; errorKind = $null; errorDetail = $null }
    $pipe = $null; $writer = $null; $reader = $null
    $token = $null; $payload = $null
    try {
        try {
            $token = [IO.File]::ReadAllText((Join-Path $script:DataHomeDir 'connection.token'))
        } catch {
            $result.errorKind = 'token-unavailable'
            $result.errorDetail = $_.Exception.GetType().Name
            if ($_.Exception.GetType().Name -in @('UnauthorizedAccessException', 'SecurityException', 'IOException')) {
                Add-PermissionNote ('connection.token 读取失败: ' + $_.Exception.GetType().Name)
            }
            return $result
        }
        try {
            $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $script:PipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
            $pipe.Connect($script:IpcConnectTimeoutMs)
        } catch {
            if ($_.Exception -is [TimeoutException]) { $result.errorKind = 'connect-timeout' }
            else { $result.errorKind = 'connect-failed' }
            $result.errorDetail = $_.Exception.GetType().Name
            return $result
        }
        try {
            $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 1024, $true)
            $reader = [IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $true, 1024, $true)
            $writer.AutoFlush = $true
            # 请求体（含令牌）只是本函数局部变量；写入管道后立即丢弃。
            $payload = @{ method = 'status'; parameters = @{}; token = $token } | ConvertTo-Json -Compress -Depth 5
            $token = $null
            $writePending = $writer.WriteLineAsync($payload)
            if (-not $writePending.Wait($script:IpcReadTimeoutMs)) {
                $result.errorKind = 'write-timeout'
                $pipe.Dispose()
                return $result
            }
            $writePending.GetAwaiter().GetResult()
            $payload = $null
        } catch {
            $payload = $null
            $result.errorKind = 'request-write-failed'
            $result.errorDetail = $_.Exception.GetType().Name
            return $result
        }
        $pending = $reader.ReadLineAsync()
        if (-not $pending.Wait($script:IpcReadTimeoutMs)) {
            $result.errorKind = 'read-timeout'
            return $result
        }
        try { $line = $pending.GetAwaiter().GetResult() }
        catch { $result.errorKind = 'read-failed'; $result.errorDetail = $_.Exception.GetType().Name; return $result }
        if ([string]::IsNullOrEmpty($line)) { $result.errorKind = 'empty-response'; return $result }
        $response = $null
        try { $response = $line | ConvertFrom-Json } catch { }
        if ($null -eq $response) {
            $result.errorKind = 'json-parse'
            return $result
        }
        $errorProp = $response.PSObject.Properties['error']
        if ($null -ne $errorProp -and $null -ne $errorProp.Value) {
            $result.errorKind = 'error-response'
            $codeProp = $errorProp.Value.PSObject.Properties['code']
            if ($null -ne $codeProp) { $result.errorDetail = [string]$codeProp.Value }
            return $result
        }
        $resultProp = $response.PSObject.Properties['result']
        if ($null -eq $resultProp -or $null -eq $resultProp.Value) {
            $result.errorKind = 'result-shape'
            return $result
        }
        $unfinishedProp = $resultProp.Value.PSObject.Properties['unfinishedTasks']
        if ($null -eq $unfinishedProp) {
            $result.errorKind = 'result-shape'
            return $result
        }
        try { $result.unfinished = [int]$unfinishedProp.Value }
        catch { $result.errorKind = 'result-shape'; return $result }
        if ($resultProp.Value.processId -ne $HostPid) {
            $result.errorKind = 'host-pid-mismatch'
            return $result
        }
        $result.ok = $true
        return $result
    } catch {
        $result.errorKind = 'unexpected'
        $result.errorDetail = $_.Exception.GetType().Name
        return $result
    } finally {
        # 令牌与请求体不保留在任何长期状态中；异常文本也只携带类型名。
        $token = $null
        $payload = $null
        if ($reader) { try { $reader.Dispose() } catch { } }
        if ($writer) { try { $writer.Dispose() } catch { } }
        if ($pipe) { try { $pipe.Dispose() } catch { } }
    }
}

# =====================================================================
# 进程锁定与观察
# =====================================================================

function Get-ProcessIdentity([string]$Role, [int]$ProcessId, [string]$ExpectedName) {
    $p = Get-Process -Id $ProcessId -ErrorAction Stop
    if ($p.ProcessName -ne $ExpectedName) { throw ($Role + '-process-name-mismatch') }
    if ($Role -eq 'desktop' -and $p.SessionId -ne (Get-Process -Id $PID).SessionId) {
        throw 'desktop-session-mismatch: run the probe interactively with the visible Desktop'
    }
    $identity = [ordered]@{
        role               = $Role
        pid                = [int]$p.Id
        name               = [string]$p.ProcessName
        nameMatchesExpected = ([string]$p.ProcessName -eq $ExpectedName)
        startTimeUtc       = $p.StartTime.ToUniversalTime().ToString('o')
        exePath            = $null
        exePathError       = $null
    }
    try { $identity.exePath = [string]$p.Path }
    catch {
        $identity.exePathError = $_.Exception.GetType().Name
        Add-PermissionNote ('exePath:' + $Role + ' pid=' + $ProcessId + ': ' + $_.Exception.GetType().Name)
    }
    return $identity
}

function Get-ProcessObservation($Identity) {
    $obs = [ordered]@{
        alive          = $false
        state          = 'exited'
        cpuSeconds     = $null
        workingSetMiB  = $null
        privateMiB     = $null
        handles        = $null
        threads        = $null
        identityError  = $null
    }
    $p = Get-Process -Id $Identity.pid -ErrorAction SilentlyContinue
    if ($null -eq $p) { return $obs }
    $obs.alive = $true
    $locked = $true
    try {
        $startNow = $p.StartTime.ToUniversalTime().ToString('o')
        if (-not [string]::Equals($startNow, [string]$Identity.startTimeUtc, [StringComparison]::Ordinal)) {
            $locked = $false
            $obs.state = 'changed'
        }
    } catch {
        $locked = $false
        $obs.state = 'unreadable'
        $obs.identityError = $_.Exception.GetType().Name
    }
    if ($locked -and $null -ne $Identity.exePath) {
        try {
            $pathNow = [string]$p.Path
            if (-not [string]::Equals($pathNow, [string]$Identity.exePath, [StringComparison]::OrdinalIgnoreCase)) {
                $locked = $false
                $obs.state = 'changed'
            }
        } catch {
            $locked = $false
            $obs.state = 'unreadable'
            $obs.identityError = $_.Exception.GetType().Name
        }
    }
    if ($locked) { $obs.state = 'running' }
    try {
        $obs.cpuSeconds    = [Math]::Round($p.TotalProcessorTime.TotalSeconds, 3)
        $obs.workingSetMiB = [Math]::Round($p.WorkingSet64 / 1MB, 3)
        $obs.privateMiB    = [Math]::Round($p.PrivateMemorySize64 / 1MB, 3)
        $obs.handles       = [int]$p.HandleCount
        $obs.threads       = [int]$p.Threads.Count
    } catch {
        if ($obs.state -eq 'running') { $obs.state = 'unreadable' }
        $obs.identityError = $_.Exception.GetType().Name
        $obs.alive = $false
        $obs.cpuSeconds = $null; $obs.workingSetMiB = $null; $obs.privateMiB = $null
        $obs.handles = $null; $obs.threads = $null
    }
    return $obs
}

function Update-ProcessAnomalies([string]$Role, $Obs, $RowErrors) {
    $prev = [string]$script:ProcessRunState[$Role]
    $pidValue = $script:Identities[$Role].pid
    if ($Obs.state -eq 'exited') {
        if ($prev -eq 'running') {
            Add-Anomaly 'processExits' ($Role + ' pid=' + $pidValue + ' 已退出')
            $RowErrors.Add('processExit:' + $Role)
        }
        $script:ProcessRunState[$Role] = 'exited'
    } elseif ($Obs.state -eq 'changed') {
        if ($prev -ne 'changed') {
            Add-Anomaly 'processRestarts' ($Role + ' pid=' + $pidValue + ' 疑似退出/被替换（PID 复用或映像变化）')
            $RowErrors.Add('processRestart:' + $Role)
        }
        $script:ProcessRunState[$Role] = 'changed'
    } elseif ($Obs.state -eq 'unreadable') {
        Add-Anomaly 'identityErrors' ($Role + ' pid=' + $pidValue + ' 身份/资源读取失败: ' + $Obs.identityError)
        $RowErrors.Add('identityUnreadable:' + $Role)
        $script:ProcessRunState[$Role] = 'unreadable'
    } else {
        $script:ProcessRunState[$Role] = 'running'
    }
}

function Update-ProcessAggregate([string]$Role, $Obs, [double]$WallElapsed) {
    if ($Obs.state -ne 'running') { return }
    $a = $script:ProcAgg[$Role]
    $a.samplesAlive++
    if ($null -eq $a.cpuFirst) { $a.cpuFirst = $Obs.cpuSeconds }
    $a.cpuLast = $Obs.cpuSeconds
    if ($null -eq $a.wssFirst) { $a.wssFirst = $Obs.workingSetMiB }
    $a.wssLast = $Obs.workingSetMiB
    if ($null -eq $a.wssPeak -or $Obs.workingSetMiB -gt $a.wssPeak) { $a.wssPeak = $Obs.workingSetMiB }
    if ($null -eq $a.privFirst) { $a.privFirst = $Obs.privateMiB }
    $a.privLast = $Obs.privateMiB
    if ($null -eq $a.privPeak -or $Obs.privateMiB -gt $a.privPeak) { $a.privPeak = $Obs.privateMiB }
    if ($null -eq $a.handleFirst) { $a.handleFirst = $Obs.handles }
    $a.handleLast = $Obs.handles
    if ($null -eq $a.handlePeak -or $Obs.handles -gt $a.handlePeak) { $a.handlePeak = $Obs.handles }
    if ($null -eq $a.threadFirst) { $a.threadFirst = $Obs.threads }
    $a.threadLast = $Obs.threads
    if ($null -eq $a.threadPeak -or $Obs.threads -gt $a.threadPeak) { $a.threadPeak = $Obs.threads }

    $hourIndex = [int][Math]::Floor($WallElapsed / 3600.0)
    $bucket = $a.hours[$hourIndex]
    if ($null -eq $bucket) {
        $bucket = [ordered]@{
            hour              = $hourIndex
            samplesAlive      = 0
            firstWallSeconds  = $WallElapsed
            lastWallSeconds   = $WallElapsed
            privateStartMiB   = $null
            privateEndMiB     = $null
            cpuStartSeconds   = $null
            cpuEndSeconds     = $null
        }
        $a.hours[$hourIndex] = $bucket
    }
    $bucket.samplesAlive++
    $bucket.lastWallSeconds = $WallElapsed
    if ($null -eq $bucket.privateStartMiB) { $bucket.privateStartMiB = $Obs.privateMiB }
    $bucket.privateEndMiB = $Obs.privateMiB
    if ($null -eq $bucket.cpuStartSeconds) { $bucket.cpuStartSeconds = $Obs.cpuSeconds }
    $bucket.cpuEndSeconds = $Obs.cpuSeconds
}

function Get-ExecutionProcessSummary {
    $summary = [ordered]@{}
    $total = 0
    $installPrefix = (Split-Path (Split-Path $script:Identities.host.exePath -Parent) -Parent).TrimEnd('\') + '\'
    foreach ($name in $script:ExecutionProcessNames) {
        $count = 0
        foreach ($candidate in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            try { $candidatePath = $candidate.Path } catch { $candidatePath = $null }
            if (-not $candidatePath -or $candidatePath.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) { $count++ }
        }
        $summary[$name] = $count
        $total += $count
    }
    $summary.total = $total
    return $summary
}

# =====================================================================
# 样本采集（流式：不保留历史行）
# =====================================================================

function Take-Sample([string]$Phase) {
    $script:SampleSeq++
    $wallNow = [DateTime]::UtcNow
    $mono = $script:Watch.Elapsed.TotalSeconds
    $wallElapsed = ($wallNow - $script:StartUtc).TotalSeconds
    $gapMono = $mono - $script:PrevMono
    $gapWall = $wallElapsed - $script:PrevWall
    $rowErrors = [Collections.Generic.List[string]]::new()

    $hostObs = Get-ProcessObservation $script:Identities.host
    $desktopObs = Get-ProcessObservation $script:Identities.desktop
    $desktopObs['windows'] = $null
    $desktopObs['windowTimeouts'] = $null
    $desktopObs['windowCheckMs'] = $null
    Update-ProcessAnomalies 'host' $hostObs $rowErrors
    Update-ProcessAnomalies 'desktop' $desktopObs $rowErrors
    Update-ProcessAggregate 'host' $hostObs $wallElapsed
    Update-ProcessAggregate 'desktop' $desktopObs $wallElapsed

    # Desktop 窗口响应性（仅在其进程存活且身份锁定时）
    if ($desktopObs.state -eq 'running') {
        $pulseWatch = [Diagnostics.Stopwatch]::StartNew()
        try {
            $pulse = [AgentDockSoakNative]::WindowPulse($script:Identities.desktop.pid, [uint32]$script:WindowPulseTimeoutMs)
            $desktopObs['windows'] = [int]$pulse[0]
            $desktopObs['windowTimeouts'] = [int]$pulse[1]
            $desktopObs['windowCheckMs'] = [Math]::Round($pulseWatch.Elapsed.TotalMilliseconds, 1)
            $script:Agg.windowLastCount = [int]$pulse[0]
            $script:Agg.windowLastFailed = [int]$pulse[1]
            if ([int]$pulse[0] -eq 0) {
                $script:Agg.windowMissingSamples++
                Add-Anomaly 'windowMissing' 'Desktop 顶层窗口数为 0'
                $rowErrors.Add('windowMissing')
            } elseif ([int]$pulse[1] -gt 0) {
                $script:Agg.windowTimeoutSamples++
                Add-Anomaly 'windowTimeouts' ('Desktop WM_NULL 超时: windows=' + [int]$pulse[0] + ' failed=' + [int]$pulse[1])
                $rowErrors.Add('windowTimeouts')
            }
        } catch {
            Add-Anomaly 'windowCheckError' ('窗口检查异常: ' + $_.Exception.GetType().Name)
            $rowErrors.Add('windowCheckError')
        }
    }

    # 空闲物理内存（native，避免 WMI 在负载下卡顿）
    $freeMiB = $null
    try {
        $freeBytes = [AgentDockSoakNative]::FreePhysicalBytes()
        if ($freeBytes -ne [UInt64]::MaxValue) { $freeMiB = [Math]::Round($freeBytes / 1MB, 3) }
    } catch { $freeMiB = $null }
    if ($null -ne $freeMiB) {
        if ($null -eq $script:Agg.freeMin -or $freeMiB -lt $script:Agg.freeMin) { $script:Agg.freeMin = $freeMiB }
        if ($null -eq $script:Agg.freeMax -or $freeMiB -gt $script:Agg.freeMax) { $script:Agg.freeMax = $freeMiB }
        $script:Agg.freeLast = $freeMiB
    }

    # 只读 status（唯一使用的 IPC 方法）
    $ipcWatch = [Diagnostics.Stopwatch]::StartNew()
    $read = Invoke-DockStatusRead
    $ipcMs = [Math]::Round($ipcWatch.Elapsed.TotalMilliseconds, 1)
    $script:Agg.ipcAttempts++
    $script:Agg.ipcLastMs = $ipcMs
    if ($ipcMs -gt $script:Agg.ipcMaxMs) { $script:Agg.ipcMaxMs = $ipcMs }
    $statusField = $null
    if ($read.ok) {
        $script:Agg.ipcOk++
        $script:Agg.lastStatusOk = $true
        $script:Agg.unfinishedLast = [int]$read.unfinished
        if ([int]$read.unfinished -gt $script:Agg.unfinishedMax) { $script:Agg.unfinishedMax = [int]$read.unfinished }
        if ([int]$read.unfinished -gt 0) { $script:Agg.unfinishedSamplesGT0++ }
        $statusField = [ordered]@{ unfinished = [int]$read.unfinished }
    } else {
        $script:Agg.lastStatusOk = $false
        $script:Agg.unfinishedLast = $null
        $kinds = $script:Agg.ipcErrorKinds
        $kinds[$read.errorKind] = [int]$kinds[$read.errorKind] + 1
        $detail = [string]$read.errorKind
        if ($read.errorDetail) { $detail = $detail + '/' + $read.errorDetail }
        $statusField = [ordered]@{ error = [string]$read.errorKind }
        if ($read.errorKind -in @('json-parse', 'result-shape')) {
            $script:Agg.ipcJsonErrors++
            Add-Anomaly 'jsonErrors' $detail
            $rowErrors.Add('json:' + $read.errorKind)
        } else {
            $script:Agg.ipcFailures++
            Add-Anomaly 'ipcFailures' $detail
            $rowErrors.Add('ipc:' + $read.errorKind)
        }
    }

    # 执行进程（opencode / AgentDock.Engine / codex）
    $exec = Get-ExecutionProcessSummary
    if ($exec.total -gt $script:Agg.execMaxTotal) { $script:Agg.execMaxTotal = $exec.total }
    $script:Agg.execLastTotal = $exec.total
    $script:Agg.execLastBreakdown = $exec

    # 时间线异常：超长间隔 / 睡眠或墙钟跳变
    $gapWarn = [Math]::Max(3 * $script:IntervalSeconds, 60)
    if ($gapMono -gt $script:Agg.maxGapSeconds) { $script:Agg.maxGapSeconds = [Math]::Round($gapMono, 3) }
    if ($gapMono -gt $gapWarn -or $gapWall -gt $gapWarn) {
        Add-Anomaly 'gapAnomalies' ('monoGap=' + [Math]::Round($gapMono, 1) + 's wallGap=' + [Math]::Round($gapWall, 1) + 's')
        $rowErrors.Add('gap')
    }
    $sleepDelta = $gapWall - $gapMono
    if ($sleepDelta -gt 5) {
        $script:Agg.sleepSeconds = [Math]::Round($script:Agg.sleepSeconds + $sleepDelta, 1)
        Add-Anomaly 'sleepSuspected' ('wall 与 monotonic 差值 ' + [Math]::Round($sleepDelta, 1) + 's（睡眠/挂起或时钟跳变）')
        $rowErrors.Add('sleepSuspected')
    }

    # 行（一次性构建），追加到 JSONL；随后释放，不驻留内存。
    $row = [ordered]@{
        seq                  = $script:SampleSeq
        phase                = $Phase
        atUtc                = $wallNow.ToString('o')
        atLocal              = $wallNow.ToLocalTime().ToString('o')
        elapsedSeconds       = [Math]::Round($mono, 3)
        wallElapsedSeconds   = [Math]::Round($wallElapsed, 3)
        gapSeconds           = [Math]::Round($gapMono, 3)
        wallGapSeconds       = [Math]::Round($gapWall, 3)
        host                 = $hostObs
        desktop              = $desktopObs
        freePhysicalMiB      = $freeMiB
        status               = $statusField
        executionProcesses   = $exec
        errors               = @($rowErrors)
    }
    try {
        [IO.File]::AppendAllText($script:SamplePath,
            ($row | ConvertTo-Json -Compress -Depth 8) + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
    } catch {
        $script:Agg.sampleWriteErrors++
        if ($script:Agg.sampleWriteErrors -le 3) {
            Add-PermissionNote ('soak-samples.jsonl 写入失败: ' + $_.Exception.GetType().Name)
            Write-Warning ('soak-samples.jsonl 写入失败: ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message)
        }
    }

    $script:PrevMono = $mono
    $script:PrevWall = $wallElapsed
    $script:Agg.wallElapsed = $wallElapsed
    if ($Phase -eq 'settle') { $script:Agg.settleSamples++ } else { $script:Agg.runSamples++ }

    # 每样本原子更新 state.json（崩溃查询按 5 分钟节流）
    Update-CrashEvents $false
    Sync-State
    Save-SoakState

    if (($mono - $script:LastConsoleAt) -ge 300) {
        $script:LastConsoleAt = $mono
        Write-Host ('[soak] t=' + [Math]::Round($wallElapsed, 0) + 's samples=' + $script:SampleSeq +
            ' unfinished=' + $script:Agg.unfinishedLast + ' anomalies=' + $script:Anomalies.total)
    }
}

# =====================================================================
# 崩溃/挂起事件（仅本轮启动以来，仅 AgentDock 相关）
# =====================================================================

function Update-CrashEvents([bool]$Force) {
    $mono = $script:Watch.Elapsed.TotalSeconds
    if (-not $Force -and $null -ne $script:LastCrashQueryAt -and
        ($mono - $script:LastCrashQueryAt) -lt $script:CrashQueryIntervalSeconds) { return }
    $script:LastCrashQueryAt = $mono
    $result = [ordered]@{ ok = $false; error = $null; permissionDenied = $false; count = 0; items = @() }
    try {
        $events = @(Get-WinEvent -FilterHashtable @{
            LogName      = 'Application'
            ProviderName = @('Application Error', 'Application Hang')
            StartTime    = $script:StartLocal
        } -ErrorAction Stop)
        $matched = @()
        foreach ($e in $events) {
            $createdUtc = $e.TimeCreated.ToUniversalTime()
            if ($createdUtc -lt $script:StartUtc) { continue }
            $app = $null
            try { if ($e.Properties.Count -ge 1) { $app = [string]$e.Properties[0].Value } } catch { }
            $message = $null
            try { $message = [string]$e.Message } catch { }
            $related = $false
            if ($null -ne $app -and $app -like '*AgentDock*') { $related = $true }
            elseif ($null -ne $message -and $message -like '*AgentDock*') { $related = $true }
            if (-not $related) { continue }
            $item = [ordered]@{
                atUtc       = $createdUtc.ToString('o')
                eventId     = [int]$e.Id
                provider    = [string]$e.ProviderName
                application = $app
                message     = $null
            }
            if ($message) { $item.message = $message.Substring(0, [Math]::Min(400, $message.Length)) }
            $matched += $item
        }
        $result.ok = $true
        $result.count = $matched.Count
        if ($matched.Count -gt 20) { $result.items = @($matched[($matched.Count - 20)..($matched.Count - 1)]) }
        else { $result.items = @($matched) }
    } catch {
        if ([string]$_.FullyQualifiedErrorId -like '*NoMatchingEventsFound*') {
            $result.ok = $true
        } else {
            $result.error = $_.Exception.GetType().Name + ': ' + $_.Exception.Message
            if ($_.Exception.GetType().Name -in @('UnauthorizedAccessException', 'SecurityException')) {
                $result.permissionDenied = $true
                Add-PermissionNote ('Application 日志查询失败: ' + $_.Exception.GetType().Name)
            }
        }
    }
    $script:State.crashEvents.queryOk = $result.ok
    $script:State.crashEvents.queryError = $result.error
    $script:State.crashEvents.permissionDenied = $result.permissionDenied
    $script:State.crashEvents.lastQueryAtUtc = [DateTime]::UtcNow.ToString('o')
    $script:State.crashEvents.count = $result.count
    $script:State.crashEvents.items = $result.items
    if ($Force -and (-not $result.ok -or $result.count -gt 0)) {
        Add-Anomaly 'crashEventCheck' ('queryOk=' + $result.ok + ' AgentDockEvents=' + $result.count)
    }
}

# =====================================================================
# state.json 同步与原子写入
# =====================================================================

function Sync-State {
    $s = $script:State
    $s.elapsedSeconds = [Math]::Round($script:Watch.Elapsed.TotalSeconds, 3)
    $s.wallElapsedSeconds = [Math]::Round($script:Agg.wallElapsed, 3)
    $s.samples.runSamples = $script:Agg.runSamples
    $s.samples.settleSamples = $script:Agg.settleSamples
    $s.samples.total = $script:Agg.runSamples + $script:Agg.settleSamples
    $s.samples.maxGapSeconds = $script:Agg.maxGapSeconds
    $s.samples.sleepSeconds = $script:Agg.sleepSeconds
    $s.samples.writeErrors = $script:Agg.sampleWriteErrors
    $s.ipc.attempts = $script:Agg.ipcAttempts
    $s.ipc.ok = $script:Agg.ipcOk
    $s.ipc.failures = $script:Agg.ipcFailures
    $s.ipc.jsonErrors = $script:Agg.ipcJsonErrors
    $s.ipc.errorKinds = $script:Agg.ipcErrorKinds
    $s.ipc.lastMs = $script:Agg.ipcLastMs
    $s.ipc.maxMs = $script:Agg.ipcMaxMs
    $s.windows.desktopMissingSamples = $script:Agg.windowMissingSamples
    $s.windows.desktopTimeoutSamples = $script:Agg.windowTimeoutSamples
    $s.windows.lastCount = $script:Agg.windowLastCount
    $s.windows.lastFailed = $script:Agg.windowLastFailed
    $s.status.unfinishedMax = $script:Agg.unfinishedMax
    $s.status.unfinishedLast = $script:Agg.unfinishedLast
    $s.status.unfinishedSamplesGT0 = $script:Agg.unfinishedSamplesGT0
    $s.execution.maxTotal = $script:Agg.execMaxTotal
    $s.execution.lastTotal = $script:Agg.execLastTotal
    $s.execution.lastBreakdown = $script:Agg.execLastBreakdown
    $s.freePhysicalMiB.min = $script:Agg.freeMin
    $s.freePhysicalMiB.max = $script:Agg.freeMax
    $s.freePhysicalMiB.last = $script:Agg.freeLast
    $s.anomalies = $script:Anomalies
    $s.anomalyEvents = $script:AnomalyEvents
    $s.anomalyEventsDropped = $script:AnomalyEventsDropped
    $s.permissions.notes = $script:PermissionNotes
    $s.permissions.dropped = $script:PermissionNotesDropped
    $s.processRunState = $script:ProcessRunState
    $s.stateWriteErrors = $script:StateWriteErrors
}

function Save-SoakState {
    try {
        $json = $script:State | ConvertTo-Json -Depth 14
        $tmp = $script:StatePath + '.tmp'
        [IO.File]::WriteAllText($tmp, $json, [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($script:StatePath)) {
            [IO.File]::Replace($tmp, $script:StatePath, [System.Management.Automation.Language.NullString]::Value, $true)
        } else {
            [IO.File]::Move($tmp, $script:StatePath)
        }
    } catch {
        $script:StateWriteErrors++
        if ($script:StateWriteErrors -le 3) {
            Write-Warning ('state.json 原子写入失败: ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message)
        }
    }
}

# =====================================================================
# 汇总
# =====================================================================

function Get-HourlyArray($ProcessAgg) {
    $result = @()
    foreach ($key in @($ProcessAgg.hours.Keys | Sort-Object)) {
        $bucket = $ProcessAgg.hours[$key]
        $span = [Math]::Round([double]$bucket.lastWallSeconds - [double]$bucket.firstWallSeconds, 1)
        $growth = $null
        if ($null -ne $bucket.privateStartMiB -and $null -ne $bucket.privateEndMiB -and $bucket.samplesAlive -ge 2) {
            $growth = [Math]::Round([double]$bucket.privateEndMiB - [double]$bucket.privateStartMiB, 3)
        }
        $cpuDelta = $null
        $cpuPercent = $null
        if ($null -ne $bucket.cpuStartSeconds -and $null -ne $bucket.cpuEndSeconds -and $bucket.samplesAlive -ge 2) {
            $cpuDelta = [Math]::Round([double]$bucket.cpuEndSeconds - [double]$bucket.cpuStartSeconds, 3)
            if ($span -gt 0) { $cpuPercent = [Math]::Round($cpuDelta / $span / [Environment]::ProcessorCount * 100, 4) }
        }
        $result += [ordered]@{
            hour                  = [int]$key
            samplesAlive          = $bucket.samplesAlive
            spanSeconds           = $span
            privateStartMiB       = $bucket.privateStartMiB
            privateEndMiB         = $bucket.privateEndMiB
            privateGrowthMiB      = $growth
            cpuDeltaSeconds       = $cpuDelta
            cpuPercentOfAllCores  = $cpuPercent
        }
    }
    return $result
}

function Get-MemoryReview($Hourly) {
    $usable = @($Hourly | Where-Object {
        $_.samplesAlive -ge 2 -and $_.spanSeconds -ge 1800 -and $null -ne $_.privateGrowthMiB
    })
    $positive = @($usable | Where-Object { $_.privateGrowthMiB -gt 0.25 })
    $totalGrowth = $null
    if ($usable.Count -ge 1) {
        $totalGrowth = [Math]::Round([double]$usable[-1].privateEndMiB - [double]$usable[0].privateStartMiB, 3)
    }
    $flag = $false
    if ($usable.Count -ge 3 -and
        $positive.Count -ge [Math]::Ceiling($usable.Count * 0.75) -and
        $null -ne $totalGrowth -and $totalGrowth -gt 2.0) {
        $flag = $true
    }
    return [ordered]@{
        requiresMemoryReview = $flag
        usableHours          = $usable.Count
        positiveGrowthHours  = $positive.Count
        totalGrowthMiB       = $totalGrowth
        rule                 = 'usableHours>=3 且 >=75% 小时私有内存增长 >0.25 MiB 且累计增长 >2 MiB 时标记；仅供人工复核。'
    }
}

function Build-ProcessSummary([string]$Role, [double]$Wall, [int]$Cores) {
    $a = $script:ProcAgg[$Role]
    $id = $script:Identities[$Role]
    $cpuDelta = $null
    if ($null -ne $a.cpuFirst -and $null -ne $a.cpuLast) { $cpuDelta = [Math]::Round([double]$a.cpuLast - [double]$a.cpuFirst, 3) }
    $cpuPercent = $null
    if ($null -ne $cpuDelta -and $Wall -gt 0) { $cpuPercent = [Math]::Round($cpuDelta / $Wall / $Cores * 100, 4) }
    $privGrowth = $null
    if ($null -ne $a.privFirst -and $null -ne $a.privLast) { $privGrowth = [Math]::Round([double]$a.privLast - [double]$a.privFirst, 3) }
    return [ordered]@{
        role                 = $Role
        pid                  = $id.pid
        name                 = $id.name
        exePath              = $id.exePath
        startTimeUtc         = $id.startTimeUtc
        samplesAlive         = $a.samplesAlive
        cpuSecondsStart      = $a.cpuFirst
        cpuSecondsEnd        = $a.cpuLast
        cpuDeltaSeconds      = $cpuDelta
        cpuPercentOfAllCores = $cpuPercent
        workingSetMiBStart   = $a.wssFirst
        workingSetMiBEnd     = $a.wssLast
        workingSetMiBPeak    = $a.wssPeak
        privateMiBStart      = $a.privFirst
        privateMiBEnd        = $a.privLast
        privateMiBPeak       = $a.privPeak
        privateGrowthMiB     = $privGrowth
        handlesStart         = $a.handleFirst
        handlesEnd           = $a.handleLast
        handlesPeak          = $a.handlePeak
        threadsStart         = $a.threadFirst
        threadsEnd           = $a.threadLast
        threadsPeak          = $a.threadPeak
        hourly               = @(Get-HourlyArray $a)
    }
}

function Build-Summary {
    $wall = [double]$script:RunPhaseWallElapsed
    $cores = [Environment]::ProcessorCount
    $hostSummary = Build-ProcessSummary 'host' $wall $cores
    $desktopSummary = Build-ProcessSummary 'desktop' $wall $cores
    $memoryReview = [ordered]@{
        host                     = Get-MemoryReview $hostSummary.hourly
        desktop                  = Get-MemoryReview $desktopSummary.hourly
        requiresMemoryReview     = $false
        note                     = '仅提示人工复核；未标记不代表无泄漏，也不改变本次成败判定。'
    }
    $memoryReview.requiresMemoryReview = ($memoryReview.host.requiresMemoryReview -or $memoryReview.desktop.requiresMemoryReview)
    return [ordered]@{
        runPhaseWallSeconds = [Math]::Round($wall, 1)
        host                = $hostSummary
        desktop             = $desktopSummary
        freePhysicalMiB     = [ordered]@{ min = $script:Agg.freeMin; max = $script:Agg.freeMax; last = $script:Agg.freeLast }
        memoryReview        = $memoryReview
    }
}

# =====================================================================
# 收尾（幂等）
# =====================================================================

function Complete-Run([string]$Reason) {
    if ($script:Finalized) { return }
    $script:Finalized = $true
    $script:Watch.Stop()
    $finishedUtc = [DateTime]::UtcNow
    $wallElapsed = ($finishedUtc - $script:StartUtc).TotalSeconds
    $monoElapsed = $script:Watch.Elapsed.TotalSeconds
    if ($null -eq $script:RunPhaseWallElapsed) { $script:RunPhaseWallElapsed = $wallElapsed }

    # 采样充足性（按运行阶段，不含 settle 收尾样本）
    $expectedRun = [int][Math]::Floor([double]$script:RunPhaseWallElapsed / [double]$script:IntervalSeconds)
    $requiredRun = [Math]::Max(1, [int][Math]::Floor($expectedRun * 0.98))
    $sufficient = ($script:Agg.runSamples -ge $requiredRun)
    if (-not $sufficient) {
        Add-Anomaly 'insufficientSamples' ('run samples=' + $script:Agg.runSamples + ' required>=' + $requiredRun + ' expected=' + $expectedRun)
    }

    Update-CrashEvents $true
    $endClean = ($script:State.endState.clean -eq $true)
    $fullDuration = ($wallElapsed -ge $script:DurationSeconds -and $monoElapsed -ge $script:DurationSeconds)

    $script:State.summary = Build-Summary

    $outcome = 'incomplete'
    if ($null -ne $script:FatalError) { $outcome = 'failed' }
    elseif (Test-Cancelled) { $outcome = 'incomplete' }
    elseif ($fullDuration) {
        if ($script:Anomalies.total -eq 0 -and $sufficient -and $endClean -and $script:Agg.sampleWriteErrors -eq 0 -and $script:StateWriteErrors -eq 0) { $outcome = 'completed' }
        else { $outcome = 'failed' }
    } else {
        $outcome = 'incomplete'
    }

    $script:State.outcome = $outcome
    $script:State.success = ($outcome -eq 'completed')
    $script:State.completed = ($outcome -eq 'completed')
    $script:State.fullDurationElapsed = $fullDuration
    $script:State.cancelled = [bool](Test-Cancelled)
    $script:State.fatalError = $script:FatalError
    $script:State.finalizeReason = $Reason
    $script:State.finishedUtc = $finishedUtc.ToString('o')
    $script:State.elapsedSeconds = [Math]::Round($monoElapsed, 3)
    $script:State.wallElapsedSeconds = [Math]::Round($wallElapsed, 3)
    $script:State.samples.expectedRun = $expectedRun
    $script:State.samples.requiredRun = $requiredRun
    $script:State.samples.sufficient = $sufficient
    $script:Agg.wallElapsed = $wallElapsed

    Sync-State
    Save-SoakState
    if ($script:StateWriteErrors -gt 0) {
        $script:State.outcome = 'failed'
        $script:State.success = $false
        $script:State.completed = $false
        Save-SoakState
    }
    $tmp = $script:StatePath + '.tmp'
    if ([IO.File]::Exists($tmp)) { try { [IO.File]::Delete($tmp) } catch { } }

    Write-Host ('[soak] 结束: outcome=' + $outcome + '；fullDurationElapsed=' + $fullDuration +
        '；wallElapsed=' + [Math]::Round($wallElapsed, 1) + 's；samples=' + ($script:Agg.runSamples + $script:Agg.settleSamples) +
        '；anomalies=' + $script:Anomalies.total)
    Write-Host ('[soak] 异常统计: ' + ($script:Anomalies | ConvertTo-Json -Compress))
    Write-Host ('[soak] state: ' + $script:StatePath)
}

# =====================================================================
# 校验与初始化
# =====================================================================

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw '本探针仅支持 Windows。' }
if ($DurationSeconds -lt 1) { throw 'DurationSeconds 必须 >= 1。' }
if ($IntervalSeconds -lt 1) { throw 'IntervalSeconds 必须 >= 1。' }
if ($HostPid -le 0 -or $DesktopPid -le 0) { throw 'HostPid/DesktopPid 必须是有效的进程 ID。' }
if ($HostPid -eq $DesktopPid) { throw 'HostPid 与 DesktopPid 不能相同。' }

$script:OutputDir = [IO.Path]::GetFullPath($Output)
if ([IO.File]::Exists($script:OutputDir)) { throw ('Output 必须是目录：' + $script:OutputDir) }
if ([IO.Directory]::Exists($script:OutputDir)) {
    if (@(Get-ChildItem -LiteralPath $script:OutputDir -Force).Count -gt 0) {
        throw ('Output 必须是全新（不存在或为空）目录：' + $script:OutputDir)
    }
} else {
    [IO.Directory]::CreateDirectory($script:OutputDir) | Out-Null
}

$script:DataHomeDir = [IO.Path]::GetFullPath($DataHome)
$separator = [string][IO.Path]::DirectorySeparatorChar
$dataPrefix = $script:DataHomeDir.TrimEnd([char[]]@('\', '/')) + $separator
$outPrefix = $script:OutputDir.TrimEnd([char[]]@('\', '/')) + $separator
if ($script:OutputDir -eq $script:DataHomeDir -or
    $script:OutputDir.StartsWith($dataPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $script:DataHomeDir.StartsWith($outPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output 与 DataHome 必须完全分离。'
}
$script:PipeName = Get-DockPipeName $script:DataHomeDir

$script:StartUtc = [DateTime]::UtcNow
$script:StartLocal = [DateTime]::Now
$script:Watch = [Diagnostics.Stopwatch]::StartNew()
$script:PrevMono = 0.0
$script:PrevWall = 0.0
$script:SampleSeq = 0
$script:LastConsoleAt = 0.0
$script:LastCrashQueryAt = $null
$script:Finalized = $false
$script:CancelRequested = $false
$script:FatalError = $null
$script:RunPhaseWallElapsed = $null
$script:StateWriteErrors = 0
$script:CancelHandler = $null

$script:Identities = @{}
$script:ProcessRunState = [ordered]@{ host = 'running'; desktop = 'running' }
$script:Anomalies = [ordered]@{
    total             = 0
    processExits      = 0
    processRestarts   = 0
    identityErrors    = 0
    ipcFailures       = 0
    jsonErrors        = 0
    windowMissing     = 0
    windowTimeouts    = 0
    windowCheckError  = 0
    gapAnomalies      = 0
    sleepSuspected    = 0
    insufficientSamples = 0
    endStateNotClean  = 0
}
$script:AnomalyEvents = [Collections.Generic.List[object]]::new()
$script:AnomalyEventsDropped = 0
$script:PermissionNotes = [Collections.Generic.List[string]]::new()
$script:PermissionNotesDropped = 0

$script:Agg = [ordered]@{
    runSamples            = 0
    settleSamples         = 0
    ipcAttempts           = 0
    ipcOk                 = 0
    ipcFailures           = 0
    ipcJsonErrors         = 0
    ipcErrorKinds         = @{}
    ipcLastMs             = $null
    ipcMaxMs              = 0
    unfinishedMax         = 0
    unfinishedLast        = $null
    unfinishedSamplesGT0  = 0
    execMaxTotal          = 0
    execLastTotal         = $null
    execLastBreakdown     = $null
    freeMin               = $null
    freeMax               = $null
    freeLast              = $null
    windowMissingSamples  = 0
    windowTimeoutSamples  = 0
    windowLastCount       = $null
    windowLastFailed      = $null
    maxGapSeconds         = 0
    sleepSeconds          = 0
    sampleWriteErrors     = 0
    wallElapsed           = 0
    lastStatusOk          = $null
    expectedRun           = 0
    requiredRun           = 0
}

$script:ProcAgg = @{}
foreach ($role in @('host', 'desktop')) {
    $script:ProcAgg[$role] = [ordered]@{
        role          = $role
        samplesAlive  = 0
        cpuFirst      = $null
        cpuLast       = $null
        wssFirst      = $null
        wssLast       = $null
        wssPeak       = $null
        privFirst     = $null
        privLast      = $null
        privPeak      = $null
        handleFirst   = $null
        handleLast    = $null
        handlePeak    = $null
        threadFirst   = $null
        threadLast    = $null
        threadPeak    = $null
        hours         = @{}
    }
}

$script:StatePath = Join-Path $script:OutputDir 'state.json'
$script:SamplePath = Join-Path $script:OutputDir 'soak-samples.jsonl'

$script:State = [ordered]@{
    schema              = 1
    script              = 'Probe-WindowsSoak.ps1'
    outcome             = 'running'
    success             = $null
    completed           = $false
    fullDurationElapsed = $false
    cancelled           = $false
    fatalError          = $null
    finalizeReason      = $null
    startedUtc          = $script:StartUtc.ToString('o')
    startedLocal        = $script:StartLocal.ToString('o')
    finishedUtc         = $null
    durationSeconds     = $DurationSeconds
    intervalSeconds     = $IntervalSeconds
    processorCount      = [Environment]::ProcessorCount
    dataHome            = $script:DataHomeDir
    output              = $script:OutputDir
    pipeName            = $script:PipeName
    identity            = $script:Identities
    processRunState     = $script:ProcessRunState
    samples             = [ordered]@{
        runSamples     = 0
        settleSamples  = 0
        total          = 0
        expectedRun    = $null
        requiredRun    = $null
        sufficient     = $null
        maxGapSeconds  = 0
        sleepSeconds   = 0
        writeErrors    = 0
    }
    ipc = [ordered]@{
        attempts   = 0
        ok         = 0
        failures   = 0
        jsonErrors = 0
        errorKinds = [ordered]@{}
        lastMs     = $null
        maxMs      = $null
    }
    windows = [ordered]@{
        desktopMissingSamples = 0
        desktopTimeoutSamples = 0
        lastCount             = $null
        lastFailed            = $null
    }
    status = [ordered]@{
        unfinishedMax         = 0
        unfinishedLast        = $null
        unfinishedSamplesGT0  = 0
    }
    execution = [ordered]@{
        maxTotal      = 0
        lastTotal     = $null
        lastBreakdown = $null
        names         = $script:ExecutionProcessNames
    }
    freePhysicalMiB = [ordered]@{ min = $null; max = $null; last = $null }
    anomalies           = $script:Anomalies
    anomalyEvents       = $script:AnomalyEvents
    anomalyEventsDropped = 0
    permissions         = [ordered]@{ notes = $script:PermissionNotes; dropped = 0 }
    crashEvents = [ordered]@{
        queryOk          = $null
        queryError       = $null
        permissionDenied = $false
        lastQueryAtUtc   = $null
        count            = 0
        items            = @()
    }
    endState = [ordered]@{
        checked             = $false
        clean               = $null
        unfinished          = $null
        executionProcesses  = $null
        settleSeconds       = $null
    }
    summary = $null
    gateNotes = @(
        '本脚本只读监测：从不启动/停止产品进程或任务，不改配置与电源计划。',
        'completed 需同时满足：完整时长真实经过、无任何异常、采样充足、结束时 unfinishedTasks=0 且 opencode/AgentDock.Engine/codex 执行进程为 0。',
        'workingSet 的单样本参考值（历史文档 Host≈80 MiB / Desktop≈200 MiB）不是本脚本的通过门槛，也不构成发布门槛。',
        '私有内存持续增长只标记 summary.memoryReview.requiresMemoryReview 供人工复核；未标记不代表无泄漏。',
        '崩溃/挂起事件只记录本轮启动以来（Application Error 1000 / Application Hang 1002）且与 AgentDock 相关的记录。'
    )
}

# =====================================================================
# 主流程
# =====================================================================

$exitCode = 0
try {
    Initialize-Native
    Update-CrashEvents $false
    Sync-State
    Save-SoakState

    $initOk = $true
    try {
        $script:Identities.host = Get-ProcessIdentity 'host' $HostPid 'AgentDock.Host'
        $script:Identities.desktop = Get-ProcessIdentity 'desktop' $DesktopPid 'AgentDock.Desktop'
        if ($script:Identities.host.exePathError) {
            Add-Anomaly 'identityErrors' ('host exePath 不可读（无法完整锁定）: ' + $script:Identities.host.exePathError)
        }
        if ($script:Identities.desktop.exePathError) {
            Add-Anomaly 'identityErrors' ('desktop exePath 不可读（无法完整锁定）: ' + $script:Identities.desktop.exePathError)
        }
    } catch {
        $initOk = $false
        Set-Fatal ('process-acquire: ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message)
        if ($_.Exception.GetType().Name -in @('UnauthorizedAccessException', 'SecurityException')) {
            Add-PermissionNote ('进程锁定权限失败: ' + $_.Exception.GetType().Name)
        }
    }

    if (-not $initOk) {
        Complete-Run 'init-failed'
    } else {
        $script:CancelHandler = $null
        try {
            $script:CancelHandler = [ConsoleCancelEventHandler]{
                param($sender, $eventArgs)
                $eventArgs.Cancel = $true
                if ($script:CancelRequested) { return }
                [AgentDockSoakNative]::Cancelled = $true
                $script:CancelRequested = $true
                $eventArgs.Cancel = $true
            }
            [Console]::add_CancelKeyPress($script:CancelHandler)
        } catch {
            $script:CancelHandler = $null
            Add-PermissionNote ('Ctrl+C 处理注册失败（将依赖 finally 收尾）: ' + $_.Exception.GetType().Name)
        }

        Write-Host ('[soak] 开始: host=' + $HostPid + '(' + $script:Identities.host.name + ') desktop=' +
            $DesktopPid + '(' + $script:Identities.desktop.name + ') 时长=' + $DurationSeconds + 's 间隔=' + $IntervalSeconds + 's')
        Write-Host ('[soak] dataHome=' + $script:DataHomeDir)
        Write-Host ('[soak] output=' + $script:OutputDir)
        Sync-State
        Save-SoakState

        # ---- 运行阶段：立即首采，之后按 IntervalSeconds 对齐 ----
        $nextDue = 0.0
        while ($true) {
            if (Test-Cancelled) { break }
            $wallNowSeconds = ([DateTime]::UtcNow - $script:StartUtc).TotalSeconds
            if ($wallNowSeconds -ge $script:DurationSeconds) { break }
            if ($script:Watch.Elapsed.TotalSeconds -ge $nextDue) {
                Take-Sample 'run'
                $nextDue += [double]$script:IntervalSeconds
                if ($script:Watch.Elapsed.TotalSeconds -gt $nextDue) {
                    $nextDue = $script:Watch.Elapsed.TotalSeconds + [double]$script:IntervalSeconds
                }
            }
            $remaining = $nextDue - $script:Watch.Elapsed.TotalSeconds
            if ($remaining -le 0) { continue }
            $sleepMs = [int][Math]::Ceiling([Math]::Min(1.0, $remaining) * 1000)
            if ($sleepMs -lt 1) { $sleepMs = 1 }
            Start-Sleep -Milliseconds $sleepMs
        }
        $script:RunPhaseWallElapsed = ([DateTime]::UtcNow - $script:StartUtc).TotalSeconds

        # ---- 结束收敛（有界）：等未完成归零、执行进程清理干净 ----
        if (-not (Test-Cancelled) -and $null -eq $script:FatalError) {
            $settleCap = [Math]::Min(120.0, [Math]::Max(15.0, [Math]::Floor([double]$script:DurationSeconds / 4.0)))
            $settleStartMono = $script:Watch.Elapsed.TotalSeconds
            $settleDeadline = $settleStartMono + $settleCap
            $clean = $false
            while ($true) {
                if (Test-Cancelled) { break }
                Take-Sample 'settle'
                if ($script:Agg.lastStatusOk -eq $true -and $script:Agg.unfinishedLast -eq 0 -and $script:Agg.execLastTotal -eq 0) {
                    $clean = $true
                    break
                }
                if ($script:Watch.Elapsed.TotalSeconds -ge $settleDeadline) { break }
                Start-Sleep -Milliseconds 1000
            }
            $script:State.endState.checked = $true
            $script:State.endState.clean = $clean
            $script:State.endState.unfinished = $script:Agg.unfinishedLast
            $script:State.endState.executionProcesses = $script:Agg.execLastTotal
            $script:State.endState.settleSeconds = [Math]::Round($script:Watch.Elapsed.TotalSeconds - $settleStartMono, 1)
            if (-not $clean -and -not (Test-Cancelled)) {
                Add-Anomaly 'endStateNotClean' ('结束时 unfinished=' + $script:Agg.unfinishedLast +
                    ' 执行进程=' + $script:Agg.execLastTotal)
            }
        }

        Complete-Run 'normal'
    }
} catch {
    Set-Fatal ('unhandled: ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message)
    try { Complete-Run 'exception' } catch { Write-Warning ('收尾失败: ' + $_.Exception.Message) }
} finally {
    if (-not $script:Finalized) {
        try { Complete-Run 'finally' } catch { Write-Warning ('收尾失败: ' + $_.Exception.Message) }
    }
    if ($null -ne $script:CancelHandler) {
        try { [Console]::remove_CancelKeyPress($script:CancelHandler) } catch { }
        $script:CancelHandler = $null
    }
}

if ($script:State.outcome -ne 'completed') { $exitCode = 1 }
Write-Host ('[soak] 结论: ' + $script:State.outcome + '；exitCode=' + $exitCode)
exit $exitCode
