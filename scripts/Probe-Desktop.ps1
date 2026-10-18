param(
    [string]$HostPath = "$PSScriptRoot/../src/AgentDock.Host/bin/Debug/net10.0/AgentDock.Host.exe",
    [string]$DesktopPath = "$PSScriptRoot/../src/AgentDock.Desktop/bin/Debug/net10.0/AgentDock.Desktop.exe",
    [string]$Output = "$PSScriptRoot/../artifacts/validation/desktop-live",
    [ValidateRange(3,120)][int]$IdleSampleSeconds = 3,
    [switch]$SeedTasks,
    [switch]$Onboarding
)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory($Output) | Out-Null
$dataHome = Join-Path $Output ('data-' + [Guid]::NewGuid().ToString('N'))
if ($Onboarding) {
    # Plugin registration fixture only: no model, credentials, or installed-user paths.
    $fixturePlugin = Join-Path $Output 'plugin'
    if (Test-Path -LiteralPath $fixturePlugin) { throw 'Use a fresh output directory for onboarding verification.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../packaging/plugin') -Destination $fixturePlugin -Recurse -Force
}
$originalConfig = Join-Path $env:USERPROFILE '.codex/config.toml'
$beforeHash = if (Test-Path -LiteralPath $originalConfig) { (Get-FileHash -LiteralPath $originalConfig).Hash } else { $null }
$pipeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($dataHome.ToUpperInvariant()))).Substring(0,24)
function Invoke-Dock([string]$method, [hashtable]$parameters = @{}) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'agentdock-' + $pipeHash, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(500)
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 1024, $true)
        $reader = [IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $true, 1024, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine((@{ method=$method; parameters=$parameters; token=[IO.File]::ReadAllText((Join-Path $dataHome 'connection.token')) } | ConvertTo-Json -Compress))
        $response = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult() | ConvertFrom-Json
        if ($response.error) { throw ($response.error | ConvertTo-Json -Compress) }
        return $response.result
    } finally { $pipe.Dispose() }
}
function Start-App([string]$path, [string[]]$arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($path))
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.Environment['AGENTDOCK_SMOKE_SAMPLE_SECONDS'] = [string]$IdleSampleSeconds
    $info.Environment['CODEX_HOME'] = Join-Path $Output 'codex-home'
    $info.Environment['AGENTDOCK_SMOKE_ONBOARDING'] = $(if ($Onboarding) { '1' } else { '0' })
    foreach ($value in $arguments) { $info.ArgumentList.Add($value) }
    return [Diagnostics.Process]::Start($info)
}
$hostProcess = $null
$desktopProcess = $null
try {
    if ($SeedTasks) {
        [IO.Directory]::CreateDirectory($dataHome) | Out-Null
        $fixtureProject = Join-Path $Output 'row-fixture'
        [IO.Directory]::CreateDirectory($fixtureProject) | Out-Null
        # Synthetic persisted records only. Paused/terminal states never dispatch an engine.
        # Include two projects, known/unknown usage, long text and errors for layout checks.
        $now = [DateTimeOffset]::UtcNow
        $fixtureTasks = @{}
        $fixtureRequests = @{}
        $titles = @('重构任务工作台', '检查 IPC 连接状态', '验证 Windows 安装包', '整理角色配置与超时策略：验证长标题在较窄工作区中的显示', '更新使用文档')
        $states = @('Paused', 'Completed', 'Completed', 'Failed', 'Completed')
        $roles = @('worker', 'explorer', 'verifier', 'reviewer', 'default')
        for ($i = 0; $i -lt $titles.Count; $i++) {
            $id = 'desktop-design-fixture-' + $i
            $fixtureRequests[$id] = @{ id=$id; fingerprint='synthetic-ui-design-probe'; status=$states[$i] }
            $fixtureTasks[$id] = @{
                id=$id; requestId=$id; project=(Join-Path $fixtureProject $(if ($i -eq 4) { 'DesktopTools' } else { 'AgentDock' }))
                title=$titles[$i]; role=$roles[$i]; status=$states[$i]
                acceptedAt=$now.AddMinutes(-28 - $i * 10).ToString('O'); startedAt=$now.AddMinutes(-27 - $i * 10).ToString('O')
                finishedAt=$(if ($i -eq 0) { $null } else { $now.AddMinutes(-20 - $i * 10).ToString('O') })
                inputTokens=$(if ($i -eq 3) { $null } else { 62100 - $i * 9200 })
                outputTokens=$(if ($i -eq 3) { $null } else { 8400 - $i * 1300 })
                generationTokensPerSecond=$(if ($i -eq 3) { $null } else { 312 - $i * 18 })
                error=$(if ($i -eq 3) { '合成错误：用于验证错误提示与未知用量，不代表实际任务失败。' } else { $null })
                result="Synthetic UI fixture. No model was called. Task: $($titles[$i])"; bodyAvailable=$true
            }
        }
        @{ schema=1; tasks=$fixtureTasks; requests=$fixtureRequests } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dataHome 'tasks.json') -Encoding utf8
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $hostProcess = Start-App $HostPath @('--host','--home',$dataHome)
    $status = $null
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        try { $status = Invoke-Dock 'status'; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$status) { throw 'Host did not become reachable.' }
    $readyMs = $watch.ElapsedMilliseconds
    $smokePath = Join-Path $Output 'desktop.json'
    $desktopProcess = Start-App $DesktopPath @('--home',$dataHome,'--smoke-test',$smokePath)
    Start-Sleep -Seconds 2
    $hostProcess.Refresh()
    $cpuBefore = $hostProcess.TotalProcessorTime
    $interval = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $IdleSampleSeconds
    $hostProcess.Refresh()
    $hostSample = @{ readyMs=$readyMs; workingSetBytes=$hostProcess.WorkingSet64; privateBytes=$hostProcess.PrivateMemorySize64; idleCpuPercent=($hostProcess.TotalProcessorTime-$cpuBefore).TotalSeconds/$interval.Elapsed.TotalSeconds/[Environment]::ProcessorCount*100 }
    if (!$desktopProcess.WaitForExit(30000)) { throw 'Desktop smoke timed out.' }
    if ($desktopProcess.ExitCode -ne 0) { throw "Desktop smoke failed: $($desktopProcess.ExitCode)" }
    $smoke = Get-Content -LiteralPath $smokePath -Raw | ConvertFrom-Json
    if (!$smoke.ok -or !$smoke.hostReachable -or !$smoke.trayCreated) { throw 'Desktop windows, tray or host assertions failed.' }
    if ($SeedTasks -and $smoke.taskCount -lt 1) { throw 'Desktop row recycling was not exercised.' }
    $after = Invoke-Dock 'status'
    if ($after.processId -ne $status.processId) { throw 'Desktop close replaced the background host.' }
    $afterHash = if (Test-Path -LiteralPath $originalConfig) { (Get-FileHash -LiteralPath $originalConfig).Hash } else { $null }
    if ($beforeHash -ne $afterHash) { throw 'Existing Codex configuration changed during probe.' }
    @{ at=[DateTimeOffset]::Now; os=[Environment]::OSVersion.VersionString; host=$hostSample; desktop=$smoke; existingCodexConfigUnchanged=$true; taskSource=$(if ($SeedTasks) { 'Synthetic persisted paused and terminal tasks; no task execution or real model calls' } else { 'No real model calls or task submissions' }); hostSurvivedDesktopExit=$true } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Output 'result.json') -Encoding utf8
    Invoke-Dock 'shutdown' @{confirm=[bool]$SeedTasks} | Out-Null
    if (!$hostProcess.WaitForExit(10000)) { throw 'Host failed to stop.' }
    Get-Content -LiteralPath (Join-Path $Output 'result.json') -Raw
} finally {
    foreach ($process in @($desktopProcess,$hostProcess)) {
        if ($process -and !$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        if ($process) { $process.Dispose() }
    }
}
