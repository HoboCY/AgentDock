param(
    [string]$HostPath = "$PSScriptRoot/../src/AgentDock.Host/bin/Debug/net10.0/AgentDock.Host.exe",
    [string]$DesktopPath = "$PSScriptRoot/../src/AgentDock.Desktop/bin/Debug/net10.0/AgentDock.Desktop.exe",
    [string]$Output = "$PSScriptRoot/../artifacts/validation/desktop-live",
    [ValidateRange(3,120)][int]$IdleSampleSeconds = 3,
    [switch]$SeedTasks
)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory($Output) | Out-Null
$dataHome = Join-Path $Output ('data-' + [Guid]::NewGuid().ToString('N'))
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
        $now = [DateTimeOffset]::UtcNow.ToString('O')
        @{ schema=1; tasks=@{ 'desktop-row-fixture'=@{ id='desktop-row-fixture'; requestId='desktop-row-regression'; project=$fixtureProject; title='列表刷新回归'; role='explorer'; status='Completed'; acceptedAt=$now; startedAt=$now; finishedAt=$now; result='Synthetic persisted UI fixture'; bodyAvailable=$true } }; requests=@{} } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $dataHome 'tasks.json') -Encoding utf8
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
    @{ at=[DateTimeOffset]::Now; os=[Environment]::OSVersion.VersionString; host=$hostSample; desktop=$smoke; existingCodexConfigUnchanged=$true; taskSource=$(if ($SeedTasks) { 'Synthetic persisted completed task; no task execution or real model calls' } else { 'No real model calls or task submissions' }); hostSurvivedDesktopExit=$true } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Output 'result.json') -Encoding utf8
    Invoke-Dock 'shutdown' @{confirm=$false} | Out-Null
    if (!$hostProcess.WaitForExit(10000)) { throw 'Host failed to stop.' }
    Get-Content -LiteralPath (Join-Path $Output 'result.json') -Raw
} finally {
    foreach ($process in @($desktopProcess,$hostProcess)) {
        if ($process -and !$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        if ($process) { $process.Dispose() }
    }
}
