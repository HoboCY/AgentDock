#requires -version 5.1
<#
.SYNOPSIS
  AgentDock 安装版 Windows 交互探针：隔离 home + 真实 UIA 控件交互。

.DESCRIPTION
  在已安装的 AgentDock（每用户安装根，例如 %LOCALAPPDATA%\AgentDock）之上，
  用全新隔离的数据目录启动安装版 Host 与 Desktop，只通过本机命名管道和
  UIAutomationClient 与“自己启动的进程”交互，证据只写入 -Output。

  覆盖范围（对 scripts/Probe-Desktop.ps1 的缺口补充，两者不重复）：
    1) 合成 Paused 任务（TaskService tasks.json 格式）→ 启动后未完成数必须为 1；
    2) UIA 选择“设置”页，读取必要控件的名称 / 类型 / IsKeyboardFocusable / 窗口 DPI 元信息，
       不读取任何输入框值（本探针不会调用 Value/Text 模式）；
    3) 真实“完全退出 AgentDock…”按钮 → 出现“完全退出”确认窗口 → “取消”后 Host 同 PID、任务仍 Paused；
       再次退出并“确认”后 Host 与 Desktop 退出、Paused 任务按代码契约变为 Cancelled，文件哨兵保留；
    4) 可选：没有后台时退出出现“仅退出界面”确认；
    5) 可选：独立隔离 fixture 合成 Paused 任务，凭据为空时任务在本地失败（不联网、不设置任何密钥），
       主面板隐藏后记录 Toast 的出现 / 置顶 / 自动消失 / 点击动作；
    6) 辅助：对有界 WM_NULL + SendMessageTimeout 做窗口响应性采样。

  安全边界（本脚本刻意不做的事）：
    - 不读取安装根 data\、不读取真实凭据或用户配置、不写系统注册表、不调用任何模型；
    - 除 install\current.json 与程序文件存在性外，不读取安装目录中的任何用户数据；
    - 只操作自己启动进程（PID 记录在案）的窗口；不删除任何现有目录或文件；
    - 不做登录自启设置或验证（隔离 home 不是安装 data，由主代理在 VM 安装账号另行覆盖）。

  诚实声明（会写入 result.json）：
    - UIA 元信息读取不等于屏幕阅读器验收，也不等于完整键盘导航或 DPI/渲染适配验收；
    - WM_NULL 采样只是窗口消息响应性辅助，不代表完整交互流畅度；
    - “NotCovered” 表示该用例无法可靠执行（例如缺少可自动化的 seam），不会伪造通过。

  退出码：0 = 整体 success；1 = 用例失败、必需用例未通过或环境不可用。

.PARAMETER InstallRoot
  已安装根目录（即包含 install\current.json 的目录），例如：
  "%LOCALAPPDATA%\AgentDock"。版本路径只从 install\current.json 解析。

.PARAMETER Output
  必须全新的测试输出目录（不存在，或存在但为空）。所有证据写入这里；
  隔离数据目录为 <Output>\data、<Output>\no-host-data、<Output>\toast-data。

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Probe-WindowsInteraction.ps1 `
    -InstallRoot "$env:LOCALAPPDATA\AgentDock" -Output "C:\AgentDockProbe\run-20261016-01"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'

$script:ScriptWatch = [System.Diagnostics.Stopwatch]::StartNew()
$script:Results = New-Object System.Collections.ArrayList
$script:OwnedProcesses = New-Object System.Collections.ArrayList
$script:Manifest = $null
$script:SyntheticSettings = @{}
$script:TopError = $null
$script:Phases = [ordered]@{}
$script:DpiEvidence = $null
$script:ResponsivenessEvidence = $null

# ---------------- 原生辅助（PS 5.1 兼容的 C# 5 代码） ----------------

$script:NativeSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DockProbe
{
    public static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static IntPtr[] VisibleTopLevelWindows(uint processId)
        {
            List<IntPtr> found = new List<IntPtr>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == processId && IsWindowVisible(hWnd))
                {
                    found.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);
            return found.ToArray();
        }

        public static string WindowTitle(IntPtr hWnd)
        {
            StringBuilder text = new StringBuilder(512);
            GetWindowText(hWnd, text, text.Capacity);
            return text.ToString();
        }

        public static bool IsTopmost(IntPtr hWnd)
        {
            // GWL_EXSTYLE = -20, WS_EX_TOPMOST = 0x8
            return (GetWindowLong(hWnd, -20) & 0x00000008) != 0;
        }

        public static int[] WindowRect(IntPtr hWnd)
        {
            Rect rect;
            if (!GetWindowRect(hWnd, out rect)) return new int[4];
            return new int[] { rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top };
        }

        public static int[] ClientSize(IntPtr hWnd)
        {
            Rect rect;
            if (!GetClientRect(hWnd, out rect)) return new int[2];
            return new int[] { rect.Right, rect.Bottom };
        }

        public static bool NullMessageResponds(IntPtr hWnd, uint timeoutMilliseconds, out long elapsedMilliseconds)
        {
            IntPtr result;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            IntPtr ok = SendMessageTimeout(hWnd, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, timeoutMilliseconds, out result);
            watch.Stop();
            elapsedMilliseconds = watch.ElapsedMilliseconds;
            return ok != IntPtr.Zero;
        }
    }
}
'@

# ---------------- 通用工具 ----------------

function Write-JsonFile([string]$Path, $Value, [int]$Depth = 12) {
    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function Read-JsonFile([string]$Path) {
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Get-PipeName([string]$FixtureHome) {
    $full = [IO.Path]::GetFullPath($FixtureHome).ToUpperInvariant()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($full)) } finally { $sha.Dispose() }
    return 'agentdock-' + ([BitConverter]::ToString($hash) -replace '-', '').Substring(0, 24)
}

function Invoke-Dock([string]$FixtureHome, [string]$Method, [hashtable]$Parameters = @{}, [int]$TimeoutMs = 10000) {
    $pipe = $null
    $writer = $null
    $reader = $null
    try {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', (Get-PipeName $FixtureHome), [IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect(700)
        $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 1024, $true)
        $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $true, 1024, $true)
        $writer.AutoFlush = $true
        $token = [IO.File]::ReadAllText((Join-Path $FixtureHome 'connection.token'))
        $payload = @{ method = $Method; parameters = $Parameters; token = $token } | ConvertTo-Json -Compress -Depth 8
        $writer.WriteLine($payload)
        $pending = $reader.ReadLineAsync()
        if (-not $pending.Wait($TimeoutMs)) { throw ("后台 '" + $Method + "' 请求超时（" + $TimeoutMs + " ms）。") }
        $response = $pending.Result | ConvertFrom-Json
        if ($response.PSObject.Properties['error'] -and $response.error) {
            throw (($response.error | ConvertTo-Json -Compress))
        }
        return $response.result
    } finally {
        if ($reader) { $reader.Dispose() }
        if ($writer) { $writer.Dispose() }
        if ($pipe) { $pipe.Dispose() }
    }
}

function Quote-Arg([string]$Value) {
    # Windows 文件名不允许引号字符；这里只处理空白参数的分隔需求。
    if ($Value -match '\s') { return '"' + $Value + '"' }
    return $Value
}

function Start-App([string]$ExePath, [string[]]$Arguments, [bool]$Hidden) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = [IO.Path]::GetFullPath($ExePath)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $Hidden
    if ($Hidden) { $info.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden }
    $info.Arguments = (@($Arguments) | ForEach-Object { Quote-Arg $_ }) -join ' '
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($info.FileName)
    $process = [System.Diagnostics.Process]::Start($info)
    if (-not $process) { throw ("进程启动失败：" + $ExePath) }
    [void]$script:OwnedProcesses.Add($process)
    return $process
}

function Stop-OwnedProcesses {
    foreach ($process in @($script:OwnedProcesses)) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }
        } catch { }
        try { $process.Dispose() } catch { }
    }
}

function Wait-Host([string]$FixtureHome, $Process, [int]$TimeoutMs = 25000) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $last = 'unknown'
    do {
        try { return (Invoke-Dock $FixtureHome 'status' @{} 3000) } catch { $last = $_.Exception.Message }
        if ($Process) {
            try { $Process.Refresh() } catch { }
            if ($Process.HasExited) { throw ('后台进程已退出（ExitCode=' + $Process.ExitCode + '）：' + $last) }
        }
        Start-Sleep -Milliseconds 250
    } while ($watch.ElapsedMilliseconds -lt $TimeoutMs)
    throw ('后台在 ' + $TimeoutMs + ' ms 内未就绪：' + $last)
}

# ---------------- 安装指针与隔离 fixture ----------------

function Read-InstallManifest([string]$Root) {
    $pointer = Join-Path $Root 'install\current.json'
    if (-not (Test-Path -LiteralPath $pointer -PathType Leaf)) { throw ("未找到安装指针：" + $pointer) }
    $manifest = Get-Content -LiteralPath $pointer -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schema -ne 1) { throw '安装指针 schema 不是 1。' }
    $installDir = [IO.Path]::GetFullPath((Join-Path $Root 'install')).TrimEnd('\') + '\'
    foreach ($name in @('host', 'desktop')) {
        $path = [string]$manifest.$name
        if (-not [IO.Path]::IsPathRooted($path)) { throw ("安装指针 " + $name + " 不是绝对路径。") }
        $full = [IO.Path]::GetFullPath($path)
        if (-not $full.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw ("安装指针 " + $name + " 不可用：" + $full)
        }
    }
    $versionDir = [IO.Path]::GetFullPath([string]$manifest.root)
    if (-not $versionDir.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase)) {
        throw '安装指针 root 不在 install 目录内。'
    }
    return $manifest
}

function Get-EngineSettings($Manifest) {
    $versionDir = [IO.Path]::GetFullPath([string]$Manifest.root)
    $paths = [ordered]@{
        enginePath  = Join-Path $versionDir 'engines\opencode\opencode.exe'
        sandboxPath = Join-Path $versionDir 'engines\codex\bin\codex.exe'
        shellPath   = Join-Path $versionDir 'engines\git\usr\bin\sh.exe'
    }
    foreach ($path in @($paths.Values)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return @{} }
    }
    return $paths
}

function New-SyntheticState([string]$FixtureDir, [int]$Count, [string]$Tag) {
    [IO.Directory]::CreateDirectory($FixtureDir) | Out-Null
    $tasks = @{}
    $requests = @{}
    $taskIds = New-Object System.Collections.ArrayList
    $requestIds = New-Object System.Collections.ArrayList
    $sentinels = New-Object System.Collections.ArrayList
    for ($index = 0; $index -lt $Count; $index++) {
        $project = Join-Path $FixtureDir ('project-' + $Tag + '-' + $index)
        [IO.Directory]::CreateDirectory($project) | Out-Null
        $sentinel = Join-Path $project 'sentinel.txt'
        [IO.File]::WriteAllText($sentinel, 'Synthetic probe sentinel; Do not delete.', [System.Text.UTF8Encoding]::new($false))
        [void]$sentinels.Add($sentinel)
        $id = [Guid]::NewGuid().ToString('N')
        $requestId = 'probe-' + $Tag + '-' + $index + '-' + $id.Substring(0, 8)
        $now = [DateTimeOffset]::UtcNow.ToString('O')
        $tasks[$id] = [ordered]@{
            id            = $id
            requestId     = $requestId
            project       = $project
            title         = '合成暂停任务 ' + $Tag + '-' + $index
            role          = 'explorer'
            prompt        = 'Synthetic probe task; must never execute real work.'
            configuration = [ordered]@{ prompt = 'Synthetic probe role.'; timeoutSeconds = 60 }
            settings      = $script:SyntheticSettings
            status        = 'Paused'
            acceptedAt    = $now
            bodyAvailable = $true
            changes       = @()
            events        = @()
            usage         = @{}
            cursor        = 0
        }
        $requests[$requestId] = [ordered]@{ id = $id; fingerprint = 'synthetic-probe'; status = 'Paused' }
        [void]$taskIds.Add($id)
        [void]$requestIds.Add($requestId)
    }
    return [ordered]@{
        tasks      = $tasks
        requests   = $requests
        taskIds    = $taskIds
        requestIds = $requestIds
        sentinels  = $sentinels
    }
}

function Write-SyntheticState([string]$FixtureHome, $State) {
    [IO.Directory]::CreateDirectory($FixtureHome) | Out-Null
    $path = Join-Path $FixtureHome 'tasks.json'
    Write-JsonFile $path ([ordered]@{ schema = 1; tasks = $State.tasks; requests = $State.requests }) 12
    return $path
}

# ---------------- 用例账本 ----------------

function New-CaseResult([string]$Name, [bool]$Required) {
    return [ordered]@{
        name       = $Name
        required   = $Required
        status     = 'NotCovered'
        error      = ''
        assertions = (New-Object System.Collections.ArrayList)
        timingsMs  = [ordered]@{}
        evidence   = [ordered]@{}
    }
}

function Add-Assertion($Case, [string]$Name, [bool]$Passed, [string]$Detail = '') {
    [void]$Case.assertions.Add([ordered]@{ name = $Name; passed = $Passed; detail = $Detail })
    return $Passed
}

function Finalize-Case($Case, [string]$FallbackError) {
    if ($Case.status -eq 'NotCovered') {
        # 用例显式标记为无法可靠执行；保留原因，不追加结果。
    } elseif (@($Case.assertions | Where-Object { -not $_.passed }).Count -gt 0) {
        $Case.status = 'Failed'
        if (-not $Case.error) { $Case.error = $FallbackError }
    } else {
        $Case.status = 'Passed'
    }
    [void]$script:Results.Add($Case)
}

# ---------------- UIA 与窗口辅助 ----------------

function Initialize-Uia {
    if (-not ('DockProbe.Native' -as [type])) {
        Add-Type -TypeDefinition $script:NativeSource -Language CSharp
    }
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Windows.Forms
}

function Get-TopWindows([int]$ProcessId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    return @([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition))
}

function Find-Window([int]$ProcessId, [string]$Name, [int]$TimeoutMs = 20000) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        foreach ($handle in @([DockProbe.Native]::VisibleTopLevelWindows([uint32]$ProcessId))) {
            try {
                if ([DockProbe.Native]::WindowTitle($handle) -eq $Name) {
                    return [Windows.Automation.AutomationElement]::FromHandle($handle)
                }
            } catch { }
        }
        if ($watch.ElapsedMilliseconds -ge $TimeoutMs) { break }
        Start-Sleep -Milliseconds 250
    } while ($true)
    return $null
}

function Wait-WindowGone([int]$ProcessId, [string]$Name, [int]$TimeoutMs = 10000) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        if (-not (Find-Window $ProcessId $Name 0)) { return $true }
        Start-Sleep -Milliseconds 200
    } while ($watch.ElapsedMilliseconds -lt $TimeoutMs)
    return $false
}

function Find-Child($Root, [string]$Name, [int]$TimeoutMs = 8000, [string]$ControlTypeName = '') {
    if (-not $Root) { return $null }
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            $found = @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
            foreach ($element in $found) {
                try {
                    if ([string]::IsNullOrEmpty($ControlTypeName) -or
                        $element.Current.ControlType.ProgrammaticName -eq $ControlTypeName) { return $element }
                } catch { }
            }
        } catch { }
        if ($watch.ElapsedMilliseconds -ge $TimeoutMs) { break }
        Start-Sleep -Milliseconds 200
    } while ($true)
    return $null
}

function Find-ChildText($Root, [string]$Substring, [int]$TimeoutMs = 5000) {
    if (-not $Root) { return $null }
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            $all = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($element in $all) {
                try {
                    $name = $element.Current.Name
                    if ($name -and $name.Contains($Substring)) { return $name }
                } catch { }
            }
        } catch { }
        if ($watch.ElapsedMilliseconds -ge $TimeoutMs) { break }
        Start-Sleep -Milliseconds 200
    } while ($true)
    return $null
}

function Invoke-PointerClick($Element) {
    # 回退路径：真实指针点击由本探针启动的窗口（仅在其确实位于交互桌面时使用）。
    if ($Element.Current.ProcessId -notin @($script:OwnedProcesses | ForEach-Object { $_.Id })) { throw '拒绝点击非探针拥有的进程。' }
    $ancestor = $Element
    for ($index = 0; $index -lt 24 -and $ancestor -and $ancestor.Current.NativeWindowHandle -eq 0; $index++) {
        $ancestor = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($ancestor)
    }
    if (-not $ancestor -or $ancestor.Current.NativeWindowHandle -eq 0) { throw '无法确认点击目标窗口。' }
    $target = [DockProbe.Native]::GetAncestor([IntPtr]$ancestor.Current.NativeWindowHandle, 2)
    [void][DockProbe.Native]::SetForegroundWindow($target)
    Start-Sleep -Milliseconds 200
    if ([DockProbe.Native]::GetForegroundWindow() -ne $target) { throw '点击目标没有获得前台。' }
    $point = $Element.GetClickablePoint()
    if (-not [DockProbe.Native]::SetCursorPos([int]$point.X, [int]$point.Y)) { throw '无法移动指针。' }
    Start-Sleep -Milliseconds 80
    [DockProbe.Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 50
    [DockProbe.Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 200
}

function Invoke-Action($Element, [string]$Reason = '操作') {
    if (-not $Element) { throw ($Reason + '：元素不存在。') }
    $pattern = $null
    try {
        if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            $pattern.Invoke()
            return 'InvokePattern'
        }
    } catch { }
    $pattern = $null
    try {
        if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            $pattern.Select()
            return 'SelectionItemPattern'
        }
    } catch { }
    Invoke-PointerClick $Element
    return 'PointerClick'
}

function Open-SettingsTab($MainWindow, [int]$TimeoutMs = 15000) {
    $tab = Find-Child $MainWindow '设置' $TimeoutMs 'ControlType.TabItem'
    if (-not $tab) { $tab = Find-Child $MainWindow '设置' 2000 }
    if (-not $tab) { throw '未找到“设置”页签。' }
    $method = Invoke-Action $tab '选择设置页签'
    Start-Sleep -Milliseconds 600
    $save = Find-Child $MainWindow '保存配置' $TimeoutMs
    if (-not $save) { throw '设置页内容未出现（未找到“保存配置”）。' }
    foreach($name in @('角色提示词与超时（五个固定角色）','通知与启动','悬浮窗','历史清理')) {
        [void](Expand-SettingsSection $MainWindow $name)
    }
    return $method
}

function Expand-SettingsSection($Root, [string]$Name) {
    $element=Find-Child $Root $Name 2000
    if(-not $element){return 'not-found'}
    $pattern=$null
    if($element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern,[ref]$pattern)) {
        if($pattern.Current.ToggleState -ne [Windows.Automation.ToggleState]::On){$pattern.Toggle();Start-Sleep -Milliseconds 100}
        return 'TogglePattern.On'
    }
    if($element.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern,[ref]$pattern)) {
        $pattern.Expand();return 'ExpandCollapsePattern.Expand'
    }
    return 'no-expand-pattern'
}

function Find-ExitAction($Root) {
    return Find-Child $Root '完全退出' 2000 'ControlType.Button'
}

function Get-WindowHandle($Element) {
    try { return [IntPtr][int]$Element.Current.NativeWindowHandle } catch { return [IntPtr]::Zero }
}

function Get-ControlMeta($Element) {
    # 只读取元信息；刻意不调用 ValuePattern/TextPattern，不读取任何输入框内容。
    $meta = [ordered]@{}
    try { $meta.name = $Element.Current.Name } catch { $meta.name = $null }
    try { $meta.controlType = $Element.Current.ControlType.ProgrammaticName } catch { $meta.controlType = $null }
    try { $meta.isKeyboardFocusable = $Element.Current.IsKeyboardFocusable } catch { $meta.isKeyboardFocusable = $null }
    try { $meta.isEnabled = $Element.Current.IsEnabled } catch { $meta.isEnabled = $null }
    try {
        $rect = $Element.Current.BoundingRectangle
        $meta.bounds = [ordered]@{
            x = [int]$rect.X; y = [int]$rect.Y; width = [int]$rect.Width; height = [int]$rect.Height; isEmpty = [bool]$rect.IsEmpty
        }
    } catch { $meta.bounds = $null }
    return $meta
}

function Measure-NullMessage([IntPtr]$Handle, [int]$Samples = 3, [uint32]$TimeoutMs = 2000) {
    $list = New-Object System.Collections.ArrayList
    $allResponded = $true
    for ($index = 0; $index -lt $Samples; $index++) {
        $elapsed = [long]0
        $responded = $false
        try { $responded = [DockProbe.Native]::NullMessageResponds($Handle, $TimeoutMs, [ref]$elapsed) } catch { $responded = $false }
        [void]$list.Add([ordered]@{ index = $index; responded = $responded; elapsedMs = $elapsed })
        if (-not $responded) { $allResponded = $false }
        Start-Sleep -Milliseconds 100
    }
    return [ordered]@{
        samples      = $list
        allResponded = $allResponded
        note         = 'WM_NULL + SendMessageTimeout（SMTO_ABORTIFHUNG，每样本 2000 ms）辅助采样；不代表完整交互响应性。'
    }
}

function Find-ToastWindow([int]$ProcessId, [int]$TimeoutMs = 15000) {
    # Avalonia 未显式设置标题时可能使用 Window；使用置顶、大小和主窗口排除条件。
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        foreach ($handle in @([DockProbe.Native]::VisibleTopLevelWindows([uint32]$ProcessId))) {
            $title = [DockProbe.Native]::WindowTitle($handle)
            $rect = [DockProbe.Native]::WindowRect($handle)
            if ([DockProbe.Native]::IsTopmost($handle) -and $title -ne 'AgentDock' -and $rect[2] -gt 150 -and $rect[2] -le 900 -and $rect[3] -gt 30) { return $handle }
        }
        Start-Sleep -Milliseconds 150
    } while ($watch.ElapsedMilliseconds -lt $TimeoutMs)
    return [IntPtr]::Zero
}

function Wait-MainHidden([int]$ProcessId, [int]$TimeoutMs = 8000) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        if (-not (Find-Window $ProcessId 'AgentDock' 0)) { return $true }
        Start-Sleep -Milliseconds 150
    } while ($watch.ElapsedMilliseconds -lt $TimeoutMs)
    return $false
}

function Hide-MainWindow($MainWindow) {
    $method = 'none'
    $pattern = $null
    try {
        if ($MainWindow.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$pattern)) {
            try { $pattern.Close(); $method = 'UIA WindowPattern.Close' } catch { $method = 'WindowPattern.Failed: ' + $_.Exception.Message }
        }
    } catch { $method = 'WindowPattern.Unavailable: ' + $_.Exception.Message }
    if ($method -ne 'UIA WindowPattern.Close') {
        $handle = Get-WindowHandle $MainWindow
        if ($handle -ne [IntPtr]::Zero) {
            [void][DockProbe.Native]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
            $method = 'PostMessage WM_CLOSE'
        }
    }
    return $method
}

# =====================================================================
# 主流程
# =====================================================================

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw '本探针仅支持 Windows。' }

$script:InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$script:Output = [IO.Path]::GetFullPath($Output)
if (-not [IO.Directory]::Exists($script:InstallRoot)) { throw ('InstallRoot 不存在：' + $script:InstallRoot) }
$installPrefix = $script:InstallRoot.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
$outputPrefix = $script:Output.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
if ($script:InstallRoot -eq $script:Output -or
    $script:Output.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $script:InstallRoot.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output 必须是安装根之外的独立全新目录。'
}
if ([IO.Directory]::Exists($script:Output)) {
    if (@(Get-ChildItem -LiteralPath $script:Output -Force).Count -gt 0) {
        throw ('Output 必须是全新（不存在或为空）目录：' + $script:Output)
    }
} else {
    [IO.Directory]::CreateDirectory($script:Output) | Out-Null
}

$mainHome = Join-Path $script:Output 'data'
$mainHost = $null
$mainDesktop = $null

$caseSeed = New-CaseResult 'seed-paused-task-and-host-start' $true
$caseUia = New-CaseResult 'settings-uia-metadata' $true
$caseExit = New-CaseResult 'exit-cancel-then-confirm' $true
$caseNoHost = New-CaseResult 'exit-without-host' $false
$caseToast = New-CaseResult 'toast-failure-notification' $false

try {
    Initialize-Uia
    $script:Manifest = Read-InstallManifest $script:InstallRoot
    $hostExe = [IO.Path]::GetFullPath([string]$script:Manifest.host)
    $desktopExe = [IO.Path]::GetFullPath([string]$script:Manifest.desktop)
    $script:SyntheticSettings = Get-EngineSettings $script:Manifest

    Write-Host ('安装版本：' + $script:Manifest.version + '；Host=' + $hostExe)
    Write-Host ('隔离数据目录：' + $mainHome)

    # ---------------- Case 1：合成 Paused 任务 + Host/Desktop 启动 ----------------
    $seedWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $seedTaskId = $null
    $seedRequestId = $null
    $seedSentinel = $null
    $mainWindow = $null
    $prerequisiteError = $null
    try {
        $fixtureDir = Join-Path $mainHome 'fixture-project'
        $seed = New-SyntheticState $fixtureDir 1 'main'
        $seedTaskId = [string]$seed.taskIds[0]
        $seedRequestId = [string]$seed.requestIds[0]
        $seedSentinel = [string]$seed.sentinels[0]
        $tasksPath = Write-SyntheticState $mainHome $seed

        $rawBytes = [IO.File]::ReadAllBytes($tasksPath)
        $hasBom = $rawBytes.Length -ge 3 -and $rawBytes[0] -eq 0xEF -and $rawBytes[1] -eq 0xBB -and $rawBytes[2] -eq 0xBF
        Add-Assertion $caseSeed 'tasks-json-no-bom' (-not $hasBom) '写入使用 UTF8 无 BOM。'
        $roundtrip = Read-JsonFile $tasksPath
        $roundTask = $roundtrip.tasks.PSObject.Properties[$seedTaskId].Value
        Add-Assertion $caseSeed 'tasks-json-roundtrip-paused' ($roundtrip.schema -eq 1 -and $null -ne $roundTask -and $roundTask.status -eq 'Paused') ('Get-Content -Encoding UTF8 复读 status=' + $roundTask.status)
        Add-Assertion $caseSeed 'fixture-project-and-sentinel' (Test-Path -LiteralPath $seedSentinel -PathType Leaf) $seedSentinel

        $hostWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $mainHost = Start-App $hostExe @('--host', '--home', $mainHome) $true
        $status = Wait-Host $mainHome $mainHost 25000
        $hostReadyMs = $hostWatch.ElapsedMilliseconds
        $caseSeed.timingsMs['hostReadyMs'] = $hostReadyMs
        $caseSeed.evidence['hostPid'] = $mainHost.Id
        $caseSeed.evidence['hostReadyMs'] = $hostReadyMs
        Add-Assertion $caseSeed 'host-status-pid' ($status.processId -eq $mainHost.Id) ('status.processId=' + $status.processId)
        Add-Assertion $caseSeed 'unfinished-tasks-1' ($status.unfinishedTasks -eq 1) ('unfinishedTasks=' + $status.unfinishedTasks)

        $taskList = @(Invoke-Dock $mainHome 'list' @{})
        $seedSummary = $taskList | Where-Object { $_.id -eq $seedTaskId } | Select-Object -First 1
        Add-Assertion $caseSeed 'api-task-paused' ($null -ne $seedSummary -and $seedSummary.status -eq 'Paused') ('api status=' + $seedSummary.status)
        $caseSeed.evidence['syntheticSettings'] = [ordered]@{
            mode = $(if (@($script:SyntheticSettings.Keys).Count -gt 0) { 'installed-engine-paths' } else { 'empty (engine files missing)' })
        }

        $desktopWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $mainDesktop = Start-App $desktopExe @('--home', $mainHome) $false
        $mainWindow = Find-Window $mainDesktop.Id 'AgentDock' 45000
        $desktopReadyMs = $desktopWatch.ElapsedMilliseconds
        $caseSeed.timingsMs['desktopWindowReadyMs'] = $desktopReadyMs
        $caseSeed.evidence['desktopPid'] = $mainDesktop.Id
        Add-Assertion $caseSeed 'desktop-main-window' ($null -ne $mainWindow) ('desktopWindowReadyMs=' + $desktopReadyMs)
        $caseSeed.status = 'Passed'
    } catch {
        $caseSeed.status = 'Failed'
        $caseSeed.error = $_.Exception.Message
        Add-Assertion $caseSeed 'case-exception' $false $_.Exception.Message
        $prerequisiteError = $_.Exception.Message
    }
    $seedWatch.Stop()
    $caseSeed.timingsMs['caseTotalMs'] = $seedWatch.ElapsedMilliseconds
    Finalize-Case $caseSeed '合成任务/Host/Desktop 启动用例失败。'

    # ---------------- Case 2：设置页 UIA 元信息 ----------------
    if ($prerequisiteError) {
        $caseUia.status = 'NotCovered'
        $caseUia.error = '前置用例失败，无法执行：' + $prerequisiteError
        Finalize-Case $caseUia ''
    } else {
        $uiaWatch = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $mainWindow = Find-Window $mainDesktop.Id 'AgentDock' 15000
            if (-not $mainWindow) { throw '主面板窗口不存在。' }

            $selectMethod = Open-SettingsTab $mainWindow
            $caseUia.evidence['settingsTabSelectMethod'] = $selectMethod
            Add-Assertion $caseUia 'settings-page-open' $true ('选择方式：' + $selectMethod)

            $metadata = [ordered]@{}
            $controls = @(
                @{ key = 'OpenCode Go 凭据'; type = 'ControlType.Edit' },
                @{ key = '保存凭据'; type = 'ControlType.Button' },
                @{ key = '完成通知开关'; type = 'ControlType.CheckBox' },
                @{ key = '登录自启开关'; type = 'ControlType.CheckBox' },
                @{ key = '悬浮窗置顶'; type = 'ControlType.CheckBox' },
                @{ key = '悬浮窗透明度'; type = 'ControlType.Slider' },
                @{ key = '清理范围'; type = 'ControlType.ComboBox' },
                @{ key = '完全退出'; type = 'ControlType.Button' }
            )
            foreach ($control in $controls) {
                $element = Find-Child $mainWindow $control.key 8000 $control.type
                $found = $null -ne $element
                Add-Assertion $caseUia ('control:' + $control.key) $found ('期望类型 ' + $control.type)
                if ($found) { $metadata[$control.key] = Get-ControlMeta $element }
            }

            $expandMethod = Expand-SettingsSection $mainWindow '角色提示词与超时（五个固定角色）'
            Add-Assertion $caseUia 'roles-expander-expanded' ($expandMethod -in @('ExpandCollapsePattern.Expand','TogglePattern.On')) $expandMethod
            Start-Sleep -Milliseconds 500
            $caseUia.evidence['treeMetadata'] = @($mainWindow.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object { Get-ControlMeta $_ })
            $unnamedEdits = @($caseUia.evidence['treeMetadata'] | Where-Object { $_.controlType -eq 'ControlType.Edit' -and [string]::IsNullOrWhiteSpace($_.name) })
            Add-Assertion $caseUia 'all-edit-controls-have-accessible-names' ($unnamedEdits.Count -eq 0) ('无名称输入框数量=' + $unnamedEdits.Count)
            foreach ($role in @('default', 'explorer', 'worker', 'reviewer', 'verifier')) {
                $promptName = '角色提示词 ' + $role
                $element = Find-Child $mainWindow $promptName 8000 'ControlType.Edit'
                $found = $null -ne $element
                Add-Assertion $caseUia ('control:' + $promptName) $found
                if ($found) { $metadata[$promptName] = Get-ControlMeta $element }
                $timeoutName = '角色超时秒数 ' + $role
                $element = Find-Child $mainWindow $timeoutName 4000
                $found = $null -ne $element
                Add-Assertion $caseUia ('control:' + $timeoutName) $found
                if ($found) { $metadata[$timeoutName] = Get-ControlMeta $element }
            }

            $mainHandle = Get-WindowHandle $mainWindow
            $dpi = 0
            try { if ($mainHandle -ne [IntPtr]::Zero) { $dpi = [int][DockProbe.Native]::GetDpiForWindow($mainHandle) } } catch { $dpi = 0 }
            Add-Assertion $caseUia 'dpi-metadata-read' ($dpi -gt 0) ('GetDpiForWindow=' + $dpi)
            $script:DpiEvidence = [ordered]@{
                method        = 'user32.GetDpiForWindow(主面板 HWND)'
                windowDpi     = $dpi
                scaleEstimate = [Math]::Round($dpi / 96.0, 3)
                note          = '仅窗口 DPI 元信息；未做 DPI 感知、多显示器缩放或渲染适配验证。'
            }
            $caseUia.evidence['dpi'] = $script:DpiEvidence
            $caseUia.evidence['controls'] = $metadata

            if ($mainHandle -ne [IntPtr]::Zero) {
                $script:ResponsivenessEvidence = Measure-NullMessage $mainHandle 3 2000
                $caseUia.evidence['wmNull'] = $script:ResponsivenessEvidence
                Add-Assertion $caseUia 'wm-null-responsive' ([bool]$script:ResponsivenessEvidence.allResponded) ($script:ResponsivenessEvidence.samples | ConvertTo-Json -Compress)
            } else {
                Add-Assertion $caseUia 'wm-null-responsive' $false '无法取得主面板 HWND。'
            }
            $caseUia.evidence['scope'] = '仅 UIA 元信息（名称/类型/可键盘聚焦/窗口 DPI）；未读取任何输入框值；不构成屏幕阅读器、完整键盘导航或 DPI 适配验收。'
            Add-Assertion $caseUia 'inputs-not-read' $true '未调用 Value/Text 模式读取输入框内容。'
            $caseUia.status = 'Passed'
        } catch {
            $caseUia.status = 'Failed'
            $caseUia.error = $_.Exception.Message
            Add-Assertion $caseUia 'case-exception' $false $_.Exception.Message
        }
        $uiaWatch.Stop()
        $caseUia.timingsMs['caseTotalMs'] = $uiaWatch.ElapsedMilliseconds
        Finalize-Case $caseUia '设置页 UIA 元信息用例失败。'
    }

    # ---------------- Case 3：退出取消 / 退出确认 ----------------
    if ($prerequisiteError) {
        $caseExit.status = 'NotCovered'
        $caseExit.error = '前置用例失败，无法执行：' + $prerequisiteError
        Finalize-Case $caseExit ''
    } else {
        $exitWatch = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $mainWindow = Find-Window $mainDesktop.Id 'AgentDock' 10000
            if (-not $mainWindow) { throw '主面板窗口不存在，无法执行退出用例。' }
            [void](Open-SettingsTab $mainWindow 8000)

            # --- 第一次：点击退出 → 取消 ---
            $exitButton = Find-ExitAction $mainWindow
            if (-not $exitButton) { throw '未找到“完全退出”按钮。' }
            $dialogWatch = [System.Diagnostics.Stopwatch]::StartNew()
            [void](Invoke-Action $exitButton '点击完全退出')
            $dialog = Find-Window $mainDesktop.Id '完全退出' 15000
            $dialogWatch.Stop()
            $caseExit.timingsMs['firstDialogMs'] = $dialogWatch.ElapsedMilliseconds
            Add-Assertion $caseExit 'exit-dialog-appeared' ($null -ne $dialog) ('出现耗时 ' + $dialogWatch.ElapsedMilliseconds + ' ms')
            if (-not $dialog) { throw '未出现“完全退出”确认窗口。' }
            $dialogBounds=$dialog.Current.BoundingRectangle
            $dialogArea=[System.Windows.Forms.Screen]::FromHandle([IntPtr]$dialog.Current.NativeWindowHandle).WorkingArea
            Add-Assertion $caseExit 'exit-dialog-within-work-area' ($dialogBounds.Left -ge $dialogArea.Left -and $dialogBounds.Top -ge $dialogArea.Top -and $dialogBounds.Right -le $dialogArea.Right -and $dialogBounds.Bottom -le $dialogArea.Bottom)
            $dialogText = Find-ChildText $dialog '未完成任务' 5000
            $dialogDetail = ''
            if ($dialogText) {
                $dialogDetail = $dialogText.Replace("`n", ' ')
                if ($dialogDetail.Length -gt 160) { $dialogDetail = $dialogDetail.Substring(0, 160) }
            }
            Add-Assertion $caseExit 'dialog-warns-unfinished' ($null -ne $dialogText) $dialogDetail

            $cancel = Find-Child $dialog '取消' 8000 'ControlType.Button'
            if (-not $cancel) { throw '确认窗口缺少“取消”按钮。' }
            [void](Invoke-Action $cancel '点击取消')
            $goneWatch = [System.Diagnostics.Stopwatch]::StartNew()
            $gone = Wait-WindowGone $mainDesktop.Id '完全退出' 10000
            $goneWatch.Stop()
            $caseExit.timingsMs['cancelCloseMs'] = $goneWatch.ElapsedMilliseconds
            Add-Assertion $caseExit 'dialog-closed-by-cancel' $gone ('关闭耗时 ' + $goneWatch.ElapsedMilliseconds + ' ms')

            Start-Sleep -Milliseconds 400
            $statusAfterCancel = Invoke-Dock $mainHome 'status' @{}
            Add-Assertion $caseExit 'host-same-pid-after-cancel' ($statusAfterCancel.processId -eq $mainHost.Id) ('PID=' + $statusAfterCancel.processId)
            $listAfterCancel = @(Invoke-Dock $mainHome 'list' @{})
            $taskAfterCancel = $listAfterCancel | Where-Object { $_.id -eq $seedTaskId } | Select-Object -First 1
            Add-Assertion $caseExit 'task-still-paused-after-cancel' ($null -ne $taskAfterCancel -and $taskAfterCancel.status -eq 'Paused') ('status=' + $taskAfterCancel.status)
            Add-Assertion $caseExit 'unfinished-still-1-after-cancel' ($statusAfterCancel.unfinishedTasks -eq 1) ('unfinishedTasks=' + $statusAfterCancel.unfinishedTasks)

            # --- 第二次：点击退出 → 确认 ---
            $mainWindow = Find-Window $mainDesktop.Id 'AgentDock' 8000
            if (-not $mainWindow) { throw '取消后主面板窗口不存在。' }
            $exitButton = Find-ExitAction $mainWindow
            if (-not $exitButton) { throw '取消后未找到“完全退出”按钮。' }
            [void](Invoke-Action $exitButton '再次点击完全退出')
            $dialog2 = Find-Window $mainDesktop.Id '完全退出' 15000
            Add-Assertion $caseExit 'second-dialog-appeared' ($null -ne $dialog2)
            if (-not $dialog2) { throw '第二次未出现确认窗口。' }
            $confirm = Find-Child $dialog2 '确认' 8000 'ControlType.Button'
            if (-not $confirm) { throw '确认窗口缺少“确认”按钮。' }
            $confirmWatch = [System.Diagnostics.Stopwatch]::StartNew()
            [void](Invoke-Action $confirm '点击确认')
            $desktopExited = $mainDesktop.WaitForExit(30000)
            $desktopExitMs = $confirmWatch.ElapsedMilliseconds
            $hostExited = $mainHost.WaitForExit(30000)
            $confirmWatch.Stop()
            $caseExit.timingsMs['desktopExitMs'] = $desktopExitMs
            $caseExit.timingsMs['hostExitMs'] = $confirmWatch.ElapsedMilliseconds
            $desktopExitCode = 'timeout'
            if ($desktopExited) { $desktopExitCode = $mainDesktop.ExitCode }
            $hostExitCode = 'timeout'
            if ($hostExited) { $hostExitCode = $mainHost.ExitCode }
            Add-Assertion $caseExit 'desktop-exits' $desktopExited ('ExitCode=' + $desktopExitCode)
            Add-Assertion $caseExit 'host-exits' $hostExited ('ExitCode=' + $hostExitCode)
            Add-Assertion $caseExit 'desktop-exits-cleanly' ($desktopExited -and $desktopExitCode -eq 0)
            Add-Assertion $caseExit 'host-exits-cleanly' ($hostExited -and $hostExitCode -eq 0)

            $finalState = Read-JsonFile (Join-Path $mainHome 'tasks.json')
            $finalTask = $finalState.tasks.PSObject.Properties[$seedTaskId].Value
            $finalRequest = $finalState.requests.PSObject.Properties[$seedRequestId].Value
            Add-Assertion $caseExit 'task-cancelled-on-full-exit' ($null -ne $finalTask -and $finalTask.status -eq 'Cancelled') ('status=' + $finalTask.status + '（TaskService.ShutdownAsync 对 Paused 任务的契约）')
            Add-Assertion $caseExit 'request-tombstone-cancelled' ($null -ne $finalRequest -and $finalRequest.status -eq 'Cancelled') ('request status=' + $finalRequest.status)
            Add-Assertion $caseExit 'sentinel-file-preserved' (Test-Path -LiteralPath $seedSentinel -PathType Leaf) $seedSentinel
            $caseExit.evidence['taskFinalStatus'] = $finalTask.status
            $caseExit.evidence['exitButton'] = '自动化名称“完全退出”（MainWindow.cs 可见内容为“完全退出 AgentDock…”）；确认窗口标题为“完全退出”'
            $caseExit.evidence['desktopExitCode'] = $desktopExitCode
            $caseExit.evidence['hostExitCode'] = $hostExitCode
            $caseExit.evidence['dataHome'] = $mainHome
            $caseExit.evidence['sentinel'] = $seedSentinel
            $caseExit.status = 'Passed'
        } catch {
            $caseExit.status = 'Failed'
            $caseExit.error = $_.Exception.Message
            Add-Assertion $caseExit 'case-exception' $false $_.Exception.Message
        }
        $exitWatch.Stop()
        $caseExit.timingsMs['caseTotalMs'] = $exitWatch.ElapsedMilliseconds
        Finalize-Case $caseExit '退出确认用例失败。'
    }

    # ---------------- Case 4（可选）：没有后台时的“仅退出界面”确认 ----------------
    $noHostWatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $home4 = Join-Path $script:Output 'no-host-data'
        [IO.Directory]::CreateDirectory($home4) | Out-Null
        $desktop4 = Start-App $desktopExe @('--home', $home4) $false
        $main4 = Find-Window $desktop4.Id 'AgentDock' 45000
        Add-Assertion $caseNoHost 'desktop-window-without-host' ($null -ne $main4)
        if (-not $main4) { throw '无后台场景下未出现主面板窗口。' }
        [void](Open-SettingsTab $main4 15000)
        $exit4 = Find-ExitAction $main4
        if (-not $exit4) { throw '无后台场景下未找到“完全退出”按钮。' }
        [void](Invoke-Action $exit4 '点击完全退出')
        $dialog4 = Find-Window $desktop4.Id '完全退出' 20000
        Add-Assertion $caseNoHost 'offline-exit-dialog' ($null -ne $dialog4)
        if (-not $dialog4) { throw '无后台时未出现退出确认窗口。' }
        $text4 = Find-ChildText $dialog4 '仅退出界面' 8000
        $detail4 = ''
        if ($text4) {
            $detail4 = $text4.Replace("`n", ' ')
            if ($detail4.Length -gt 160) { $detail4 = $detail4.Substring(0, 160) }
        }
        Add-Assertion $caseNoHost 'offline-dialog-text' ($null -ne $text4) $detail4
        $cancel4 = Find-Child $dialog4 '取消' 8000 'ControlType.Button'
        if (-not $cancel4) { throw '无后台确认窗口缺少“取消”按钮。' }
        [void](Invoke-Action $cancel4 '点击取消')
        Add-Assertion $caseNoHost 'dialog-closed-by-cancel' (Wait-WindowGone $desktop4.Id '完全退出' 10000)
        $desktop4.Refresh()
        Add-Assertion $caseNoHost 'desktop-alive-after-cancel' (-not $desktop4.HasExited)
        $caseNoHost.evidence['note'] = '该场景自始至终没有为 no-host-data 启动任何 Host 进程；未验证“退出界面”会停止后台。'
        $caseNoHost.status = 'Passed'
    } catch {
        $caseNoHost.status = 'Failed'
        $caseNoHost.error = $_.Exception.Message
        Add-Assertion $caseNoHost 'case-exception' $false $_.Exception.Message
    }
    $noHostWatch.Stop()
    $caseNoHost.timingsMs['caseTotalMs'] = $noHostWatch.ElapsedMilliseconds
    Finalize-Case $caseNoHost '无后台退出确认用例失败。'

    # ---------------- Case 5（可选）：失败任务 Toast 通知 ----------------
    $toastWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $toastEvidence = [ordered]@{}
    try {
        $home5 = Join-Path $script:Output 'toast-data'
        $fixture5 = Join-Path $home5 'fixture-project'
        $seed5 = New-SyntheticState $fixture5 3 'toast'
        $tasksPath5 = Write-SyntheticState $home5 $seed5
        $toastEvidence['tasksPath'] = $tasksPath5
        $toastEvidence['credentialConfigured'] = $false
        $toastEvidence['credentialSetCalled'] = $false

        $host5 = Start-App $hostExe @('--host', '--home', $home5) $true
        $status5 = Wait-Host $home5 $host5 25000
        Add-Assertion $caseToast 'fixture-host-started' ($status5.unfinishedTasks -eq 3) ('unfinishedTasks=' + $status5.unfinishedTasks)

        $desktop5 = Start-App $desktopExe @('--home', $home5) $false
        $main5 = Find-Window $desktop5.Id 'AgentDock' 45000
        Add-Assertion $caseToast 'fixture-desktop-window' ($null -ne $main5)
        if (-not $main5) { throw 'Toast fixture 桌面窗口未出现。' }
        $pausedRow = Find-Child $main5 '暂停' 20000
        Add-Assertion $caseToast 'paused-task-visible-in-ui' ($null -ne $pausedRow)

        # 等待至少两轮轮询（1s 周期）建立状态基线，然后隐藏主面板。
        Start-Sleep -Milliseconds 1500
        $hideMethod = Hide-MainWindow $main5
        $toastEvidence['hideMethod'] = $hideMethod
        $hidden = Wait-MainHidden $desktop5.Id 8000
        Add-Assertion $caseToast 'main-hidden-before-toast' $hidden ('方式：' + $hideMethod)

        # --- Toast A：失败通知出现 + 置顶 + 自动消失 ---
        $taskA = [string]$seed5.taskIds[0]
        $resumeWatch = [System.Diagnostics.Stopwatch]::StartNew()
        [void](Invoke-Dock $home5 'resume' @{ id = $taskA })
        $toastA = Find-ToastWindow $desktop5.Id 12000
        $resumeWatch.Stop()
        $toastFound = $toastA -ne [IntPtr]::Zero
        Add-Assertion $caseToast 'toast-appeared' $toastFound ('出现耗时 ' + $resumeWatch.ElapsedMilliseconds + ' ms')
        if ($toastFound) {
            Start-Sleep -Milliseconds 150
            $toastBounds=[DockProbe.Native]::WindowRect($toastA)
            $toastArea=[System.Windows.Forms.Screen]::FromHandle($toastA).WorkingArea
            Add-Assertion $caseToast 'toast-within-work-area' ($toastBounds[0] -ge $toastArea.Left -and $toastBounds[1] -ge $toastArea.Top -and ($toastBounds[0]+$toastBounds[2]) -le $toastArea.Right -and ($toastBounds[1]+$toastBounds[3]) -le $toastArea.Bottom)
            $toastEvidence['toastA'] = [ordered]@{
                title     = [DockProbe.Native]::WindowTitle($toastA)
                topmost   = [DockProbe.Native]::IsTopmost($toastA)
                rect      = [DockProbe.Native]::WindowRect($toastA)
                clientSize = [DockProbe.Native]::ClientSize($toastA)
            }
            Add-Assertion $caseToast 'toast-topmost' ([bool][DockProbe.Native]::IsTopmost($toastA))
            $dismissWatch = [System.Diagnostics.Stopwatch]::StartNew()
            $dismissed = $false
            while ($dismissWatch.ElapsedMilliseconds -lt 12000) {
                $visible = @([DockProbe.Native]::VisibleTopLevelWindows([uint32]$desktop5.Id))
                if (-not ($visible -contains $toastA)) { $dismissed = $true; break }
                Start-Sleep -Milliseconds 200
            }
            $dismissMs = $dismissWatch.ElapsedMilliseconds
            $toastEvidence['toastA'].dismissMs = $dismissMs
            Add-Assertion $caseToast 'toast-auto-dismissed' $dismissed ('消失耗时 ' + $dismissMs + ' ms')
            Add-Assertion $caseToast 'toast-dismiss-window-timer' ($dismissed -and $dismissMs -ge 2500 -and $dismissMs -le 11000) ('计时器约 5s；实测 ' + $dismissMs + ' ms')
        }

        # --- Toast B：点击动作（Toast.onClick = ShowMain） ---
        $taskB = [string]$seed5.taskIds[1]
        [void](Invoke-Dock $home5 'resume' @{ id = $taskB })
        $toastB = Find-ToastWindow $desktop5.Id 12000
        Add-Assertion $caseToast 'toastB-appeared' ($toastB -ne [IntPtr]::Zero)
        $clickEvidence = [ordered]@{ attempted = $false }
        $clickConfirmed = $false
        if ($toastB -ne [IntPtr]::Zero) {
            $clickEvidence.attempted = $true
            $clickEvidence.title = [DockProbe.Native]::WindowTitle($toastB)
            $client = [DockProbe.Native]::ClientSize($toastB)
            $x = [int]($client[0] / 2)
            $y = [int]($client[1] / 2)
            $lParam = [IntPtr]((($y -shl 16) -bor ($x -band 0xFFFF)))
            [void][DockProbe.Native]::PostMessage($toastB, 0x0200, [IntPtr]::Zero, $lParam)
            Start-Sleep -Milliseconds 60
            [void][DockProbe.Native]::PostMessage($toastB, 0x0201, [IntPtr]1, $lParam)
            Start-Sleep -Milliseconds 60
            [void][DockProbe.Native]::PostMessage($toastB, 0x0202, [IntPtr]::Zero, $lParam)
            $clickEvidence.method = 'PostMessage WM_LBUTTONDOWN/UP'
            $shownAgain = Find-Window $desktop5.Id 'AgentDock' 5000
            if (-not $shownAgain) {
                # 回退：真实指针点击（Toast 置顶且为探针自己启动的窗口）。
                try {
                    $element = [System.Windows.Automation.AutomationElement]::FromHandle($toastB)
                    Invoke-PointerClick $element
                    $clickEvidence.method = 'PostMessage + PointerClick fallback'
                    $shownAgain = Find-Window $desktop5.Id 'AgentDock' 5000
                } catch { $clickEvidence.fallbackError = $_.Exception.Message }
            }
            $clickConfirmed = $null -ne $shownAgain
            $clickEvidence.mainShownAfterClick = $clickConfirmed
        } else {
            $clickEvidence.error = '第二个 Toast 未出现，无法执行点击动作。'
        }
        $toastEvidence['click'] = $clickEvidence
        $desktop5.Refresh()
        Add-Assertion $caseToast 'desktop-alive-after-click' (-not $desktop5.HasExited)
        Add-Assertion $caseToast 'toast-click-shows-main' $clickConfirmed

        # --- 任务确实在本地失败（凭据为空；不得联网、不得设置密钥） ---
        $list5 = @(Invoke-Dock $home5 'list' @{})
        $failedTasks = @($list5 | Where-Object { $_.id -eq $taskA -or $_.id -eq $taskB })
        $failedA = $failedTasks | Where-Object { $_.id -eq $taskA } | Select-Object -First 1
        $failedB = $failedTasks | Where-Object { $_.id -eq $taskB } | Select-Object -First 1
        $toastEvidence['taskErrors'] = [ordered]@{
            taskA = $(if ($failedA) { $failedA.error } else { 'missing' })
            taskB = $(if ($failedB) { $failedB.error } else { 'missing' })
        }
        $localCodes = @('credential-missing', 'engine-missing', 'sandbox-missing', 'shell-missing')
        $errorA = if ($failedA) { [string]$failedA.error } else { '' }
        $errorB = if ($failedB) { [string]$failedB.error } else { '' }
        $failedLocally = ($failedA -and $failedA.status -eq 'Failed' -and $errorA.StartsWith('credential-missing')) -and
            ($failedB -and $failedB.status -eq 'Failed' -and $errorB.StartsWith('credential-missing'))
        $failedLocalOther = ($failedA -and $failedA.status -eq 'Failed' -and @($localCodes | Where-Object { $errorA.StartsWith($_) }).Count -gt 0) -or
            ($failedB -and $failedB.status -eq 'Failed' -and @($localCodes | Where-Object { $errorB.StartsWith($_) }).Count -gt 0)
        Add-Assertion $caseToast 'task-failed-locally' $failedLocally ('A=' + $errorA + '；B=' + $errorB)
        $toastEvidence['failureCause'] = if ($failedLocally) { 'credential-missing' } elseif ($failedLocalOther) { 'engine-files-missing (local pre-flight)' } else { 'unexpected' }

        # 全部通过才 Passed；缺少通知、桌面退出、点击未确认均保留为失败。
        $failedAssertions = @($caseToast.assertions | Where-Object { -not $_.passed })
        $failedNames = @($failedAssertions | ForEach-Object { $_.name })
        if ($failedAssertions.Count -eq 0) {
            $caseToast.status = 'Passed'
        } else {
            $caseToast.status = 'Failed'
            $caseToast.error = '失败通知用例断言未通过：' + ($failedNames -join ', ')
        }
        $caseToast.evidence = $toastEvidence
    } catch {
        $caseToast.status = 'Failed'
        $caseToast.error = $_.Exception.Message
        Add-Assertion $caseToast 'case-exception' $false $_.Exception.Message
        $caseToast.evidence = $toastEvidence
    }
    $toastWatch.Stop()
    $caseToast.timingsMs['caseTotalMs'] = $toastWatch.ElapsedMilliseconds
    Finalize-Case $caseToast '失败通知用例失败。'
} catch {
    $script:TopError = $_.Exception.Message
} finally {
    Stop-OwnedProcesses

    if ($script:TopError -and $script:Results.Count -eq 0) {
        $environmentCase = New-CaseResult 'environment-install-pointer' $true
        Add-Assertion $environmentCase 'install-pointer' $false $script:TopError
        $environmentCase.status = 'Failed'
        $environmentCase.error = $script:TopError
        [void]$script:Results.Add($environmentCase)
    }

    $passed = @($script:Results | Where-Object { $_.status -eq 'Passed' }).Count
    $failed = @($script:Results | Where-Object { $_.status -eq 'Failed' }).Count
    $notCovered = @($script:Results | Where-Object { $_.status -eq 'NotCovered' }).Count
    $requiredUnmet = @(@($caseSeed, $caseUia, $caseExit) | Where-Object { $_.status -ne 'Passed' })
    $success = ($failed -eq 0 -and $requiredUnmet.Count -eq 0 -and -not $script:TopError)

    $installInfo = $null
    if ($script:Manifest) {
        $installInfo = [ordered]@{
            version  = [string]$script:Manifest.version
            host     = [string]$script:Manifest.host
            desktop  = [string]$script:Manifest.desktop
        }
    }
    $result = [ordered]@{
        at             = [DateTimeOffset]::Now.ToString('O')
        os             = [Environment]::OSVersion.VersionString
        powershell     = $PSVersionTable.PSVersion.ToString()
        installRoot    = $script:InstallRoot
        output         = $script:Output
        dataHomes      = [ordered]@{ main = $mainHome; noHost = (Join-Path $script:Output 'no-host-data'); toast = (Join-Path $script:Output 'toast-data') }
        install        = $installInfo
        disclaimer     = '本探针只做 UIA 元信息读取与真实控件交互，并采集窗口 DPI 与 WM_NULL 响应辅助数据；不构成屏幕阅读器验收、完整键盘导航验收或 DPI/渲染适配验收，也未执行登录自启与任何模型调用。'
        cases          = $script:Results
        summary        = [ordered]@{ total = $script:Results.Count; passed = $passed; failed = $failed; notCovered = $notCovered }
        timings        = [ordered]@{
            totalMs = $script:ScriptWatch.ElapsedMilliseconds
            phases  = [ordered]@{
                hostReadyMs         = $caseSeed.timingsMs['hostReadyMs']
                desktopWindowReadyMs = $caseSeed.timingsMs['desktopWindowReadyMs']
                exitFirstDialogMs   = $caseExit.timingsMs['firstDialogMs']
                exitFinalDesktopMs  = $caseExit.timingsMs['desktopExitMs']
                exitFinalHostMs     = $caseExit.timingsMs['hostExitMs']
            }
        }
        dpi            = $script:DpiEvidence
        responsiveness = $script:ResponsivenessEvidence
        topError       = $script:TopError
        success        = $success
    }
    $resultPath = Join-Path $script:Output 'result.json'
    try {
        Write-JsonFile $resultPath $result 14
        if ($caseUia.evidence['controls']) {
            Write-JsonFile (Join-Path $script:Output 'uia-metadata.json') ([ordered]@{
                case          = 'settings-uia-metadata'
                controls      = $caseUia.evidence['controls']
                dpi           = $script:DpiEvidence
                responsiveness = $script:ResponsivenessEvidence
                scope         = $caseUia.evidence['scope']
            }) 14
        }
        if ($caseToast.evidence.Count -gt 0) {
            Write-JsonFile (Join-Path $script:Output 'toast-evidence.json') ([ordered]@{
                case     = 'toast-failure-notification'
                evidence = $caseToast.evidence
            }) 14
        }
    } catch { $success = $false; Write-Warning ('证据写入失败：' + $_.Exception.Message) }

    Write-Host ''
    Write-Host ('AgentDock Windows 交互探针：success=' + $success)
    foreach ($item in $script:Results) {
        $marker = ''
        if ($item.required) { $marker = ' (required)' }
        Write-Host ('  [' + $item.status + '] ' + $item.name + $marker)
        if ($item.error) { Write-Host ('          错误：' + $item.error) }
    }
    Write-Host ('结果文件：' + $resultPath)
    if ($success) { exit 0 } else { exit 1 }
}
