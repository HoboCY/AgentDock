#requires -Version 7.0
<#
.SYNOPSIS
  AgentDock Codex 插件接入探针：在隔离 CODEX_HOME 中真实调用一次 MCP 工具。

.DESCRIPTION
  流程（Codex 0.162.0-alpha.17.2 已验证）：
    1. 在隔离 CODEX_HOME 中注册本地 marketplace 并安装插件（codex plugin ...）；
    2. codex app-server initialize -> initialized -> thread/start；
    3. mcpServerStatus/list { detail: "toolsAndAuthOnly" } 断言插件 MCP 已连接且工具可见；
    4. mcpServer/tool/call 真实调用 status 工具：先取 structuredContent.result（Host 0.1.0 起返回），
       兼容仅有 content 文本的旧形态，并断言返回的是真实后台状态
       （version 非空、processId > 0、unfinishedTasks >= 0、canUpdate 为布尔）。

  不使用登录态、不调用模型、不读取用户既有配置；-SelfTest 用合成插件（仅 dev/CI）验证探针自身链路。

.PARAMETER MarketplaceDir
  真实模式：包含 .agents/plugins/marketplace.json 的目录（通常是安装包的 plugin/ 或已安装的 %LOCALAPPDATA%\AgentDock\plugin）。

.PARAMETER SelfTest
  合成自检模式：临时生成带 status 工具的合成插件（需要 node），验证探针链路。

.PARAMETER CodexExe
  codex 可执行文件；默认从 PATH 查找。必须是 Codex 0.162 或兼容版本。

.PARAMETER IsolatedHome
  隔离 CODEX_HOME；默认在临时目录新建。探测完成后删除（除非 -KeepTemp）。

.PARAMETER ExpectedHostVersion
  可选：断言 status 返回的后台版本（例如已安装 Host 的 0.1.0）。仅真实模式使用。
#>
[CmdletBinding()]
param(
    [Parameter(ParameterSetName = 'Real', Mandatory)][string]$MarketplaceDir,
    [Parameter(ParameterSetName = 'SelfTest', Mandatory)][switch]$SelfTest,
    [string]$CodexExe,
    [string]$IsolatedHome,
    [string]$ServerName = 'agentdock',
    [string]$ToolName = 'status',
    [int]$ToolTimeoutMs = 120000,
    [string]$ExpectedHostVersion,
    [string]$OutFile,
    # Explicit opt-in: five small real model tasks; requires credentials already saved in the panel.
    [string]$LiveTasksRoot,
    [switch]$KeepTemp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-Codex {
    if ($CodexExe) {
        if (-not (Test-Path -LiteralPath $CodexExe)) { throw "找不到 Codex：$CodexExe" }
        return (Resolve-Path -LiteralPath $CodexExe).Path
    }
    $command = Get-Command codex -ErrorAction SilentlyContinue
    if (-not $command) {
        throw '未找到 codex 命令。插件探针需要 Codex CLI（0.162+）；请安装 Codex 或指定 -CodexExe。'
    }
    return $command.Source
}

function Parse-JsonLines {
    param([string[]]$Lines)
    $result = @()
    foreach ($line in $Lines) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or -not $trimmed.StartsWith('{')) { continue }
        try { $result += ($trimmed | ConvertFrom-Json) } catch { }
    }
    return $result
}

function Start-CodexAppServer {
    param([string]$Exe, [string]$CodexHome)
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Exe
    $psi.ArgumentList.Add('app-server')
    $psi.ArgumentList.Add('--listen')
    $psi.ArgumentList.Add('stdio://')
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    $psi.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $psi.UseShellExecute = $false
    $psi.Environment['CODEX_HOME'] = $CodexHome
    return [System.Diagnostics.Process]::Start($psi)
}

function Send-Json($process, $obj) {
    $process.StandardInput.WriteLine(($obj | ConvertTo-Json -Compress -Depth 16))
    $process.StandardInput.Flush()
}

function Read-Message {
    param($Process, [int]$TimeoutMs)
    $task = $Process.StandardOutput.ReadLineAsync()
    if (-not $task.Wait([Math]::Max($TimeoutMs, 1000))) { throw "等待 app-server 输出超时（$TimeoutMs ms）。" }
    $line = $task.Result
    if ($null -eq $line) { throw 'app-server stdout 已关闭。' }
    if ($line.Trim().Length -eq 0) { return $null }
    try { return ($line | ConvertFrom-Json) } catch { return $null }
}

function Read-Until {
    param($Process, [int]$Id, [int]$TimeoutMs)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $message = Read-Message -Process $Process -TimeoutMs ([int](($deadline - (Get-Date)).TotalMilliseconds))
        if ($null -eq $message) { continue }
        if ($message.PSObject.Properties.Name -contains 'id' -and $message.id -eq $Id) { return $message }
    }
    throw "没有收到 id=$Id 的响应（$TimeoutMs ms）。"
}

function Assert-HostStatus {
    param($Value, [string]$Context)
    if ($null -eq $Value) { throw "$Context：没有可校验的后台状态对象。" }
    $names = @($Value.PSObject.Properties.Name)
    foreach ($required in @('version', 'processId', 'unfinishedTasks', 'canUpdate')) {
        if (-not ($names -contains $required)) {
            throw "$Context：后台状态 JSON 缺少字段 $required（实际字段：$($names -join ', ')）。"
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$Value.version)) { throw "$Context：后台 version 为空。" }
    if ([int64]$Value.processId -le 0) { throw "$Context：后台 processId 必须为正整数（实际：$($Value.processId)）。" }
    if ([int64]$Value.unfinishedTasks -lt 0) { throw "$Context：unfinishedTasks 不能为负（实际：$($Value.unfinishedTasks)）。" }
    if ($Value.canUpdate -isnot [bool]) { throw "$Context：canUpdate 必须是布尔值。" }
}

function Get-ToolCallStatus {
    param($CallResult, [string]$Context)
    $texts = @(@($CallResult.content) | Where-Object { $_.type -eq 'text' } | ForEach-Object { [string]$_.text })
    $status = $null
    $source = $null
    $structured = $CallResult.structuredContent
    if ($structured -and ($structured.PSObject.Properties.Name -contains 'result')) {
        $raw = $structured.result
        if ($raw -is [string]) {
            if (-not [string]::IsNullOrWhiteSpace($raw)) {
                try { $status = $raw | ConvertFrom-Json; $source = 'structuredContent.result(text)' } catch { }
            }
        } else {
            $status = $raw
            $source = 'structuredContent.result'
        }
    }
    if ($null -eq $status) {
        foreach ($text in $texts) {
            if ([string]::IsNullOrWhiteSpace($text)) { continue }
            try { $status = $text | ConvertFrom-Json; $source = 'content.text'; break } catch { }
        }
    }
    return [pscustomobject]@{ Status = $status; Source = $source; Texts = $texts; Structured = [bool]$structured }
}

function New-SelfTestPlugin {
    param([string]$Root)
    $node = (Get-Command node -ErrorAction SilentlyContinue)?.Source
    if (-not $node) { throw 'SelfTest 需要 node（仅开发/CI 环境，不随产品分发）。' }
    $market = Join-Path $Root 'market'
    $plugin = Join-Path $market 'plugins/agentdock'
    New-Item -ItemType Directory -Path (Join-Path $market '.agents/plugins') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $plugin '.codex-plugin') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $plugin 'mcp') -Force | Out-Null

    @'
{
  "name": "agentdock-selftest",
  "interface": { "displayName": "AgentDock Self Test" },
  "plugins": [
    {
      "name": "agentdock",
      "source": { "source": "local", "path": "./plugins/agentdock" },
      "policy": { "installation": "AVAILABLE", "authentication": "ON_INSTALL" },
      "category": "Productivity"
    }
  ]
}


'@ | Set-Content -LiteralPath (Join-Path $market '.agents/plugins/marketplace.json') -Encoding utf8

    @'
{
  "name": "agentdock",
  "version": "0.0.0-selftest",
  "description": "AgentDock probe self test",
  "mcpServers": "./.mcp.json"
}
'@ | Set-Content -LiteralPath (Join-Path $plugin '.codex-plugin/plugin.json') -Encoding utf8

    $stub = @'
import readline from "node:readline";
const rl = readline.createInterface({ input: process.stdin });
function send(o) { process.stdout.write(JSON.stringify(o) + "\n"); }
rl.on("line", (line) => {
  let m; try { m = JSON.parse(line); } catch { return; }
  const { id, method, params } = m;
  if (method === "initialize") {
    send({ jsonrpc: "2.0", id, result: { protocolVersion: params?.protocolVersion ?? "2025-06-18", capabilities: { tools: {} }, serverInfo: { name: "agentdock", version: "0.0.0-selftest" } } });
  } else if (typeof method === "string" && method.startsWith("notifications/")) {
  } else if (method === "tools/list") {
    send({ jsonrpc: "2.0", id, result: { tools: [{ name: "status", description: "AgentDock self test status", inputSchema: { type: "object", properties: {} } }] } });
  } else if (method === "tools/call") {
    const status = { version: "0.0.0-selftest", processId: process.pid, unfinishedTasks: 0, canUpdate: true };
    send({ jsonrpc: "2.0", id, result: { content: [{ type: "text", text: JSON.stringify(status) }], structuredContent: { result: status }, isError: false } });
  } else if (id !== undefined) {
    send({ jsonrpc: "2.0", id, result: {} });
  }
});
'@
    Set-Content -LiteralPath (Join-Path $plugin 'mcp/stub-server.mjs') -Value $stub -Encoding utf8

    $mcp = [ordered]@{
        mcpServers = [ordered]@{
            agentdock = [ordered]@{
                command = $node
                args    = @((Join-Path $plugin 'mcp/stub-server.mjs'))
                cwd     = './'
            }
        }
    }
    $mcp | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $plugin '.mcp.json') -Encoding utf8
    return $market
}

function Invoke-LiveAcceptance($Process, [string]$ThreadId, [string]$Root) {
    if ($SelfTest) { throw '真实任务不能使用合成插件。' }
    $Root = [IO.Path]::GetFullPath($Root)
    if (Test-Path -LiteralPath $Root) { throw "真实任务验收目录必须全新，避免覆盖文件：$Root" }
    New-Item -ItemType Directory -Path $Root | Out-Null
    $script:LiveRpcId = 10
    function Invoke-LiveTool([string]$Name, [hashtable]$Arguments) {
        $script:LiveRpcId++
        Send-Json $Process @{ jsonrpc='2.0'; id=$script:LiveRpcId; method='mcpServer/tool/call'; params=@{ server=$ServerName; threadId=$ThreadId; tool=$Name; arguments=$Arguments } }
        $reply = Read-Until -Process $Process -Id $script:LiveRpcId -TimeoutMs 60000
        if ($reply.PSObject.Properties.Name -contains 'error') { throw '真实任务 MCP 调用失败。' }
        if ($reply.result.isError) { throw ($reply.result.content[0].text) }
        return $reply.result.structuredContent.result
    }
    $accepted = [Collections.Generic.List[object]]::new()
    $acceptedPrompts = @{}
    $observed = [Collections.Generic.List[object]]::new()
    $result.liveTasks = $observed
    $result.liveTaskRoot = $Root
    try {
        foreach ($role in @('explorer','default','worker','reviewer','verifier')) {
            $project = Join-Path $Root $role
            New-Item -ItemType Directory -Path $project | Out-Null
            Set-Content -LiteralPath (Join-Path $project 'input.txt') -Value '41' -NoNewline -Encoding utf8
            $instruction = if ($role -in @('default','worker')) {
                'Read input.txt, add one, and write exactly 42 to answer.txt. You own answer.txt only.'
            } elseif ($role -eq 'verifier') {
                'Run a command that reads input.txt and checks that its integer plus one equals 42. Write a short verification.txt only in the allowed check-artifacts directory given in your system prompt. Do not modify project files.'
            } else {
                'Read input.txt using a tool and report its integer plus one. Do not write any files.'
            }
            $prompt = "Bounded AgentDock acceptance task in this isolated fixture only. $instruction Do not delegate, access credentials, inspect other directories, or run network commands. Use at most two tool calls. Finish with ROLE_OK $role 42 and a brief factual result."
            $arguments = @{ request_id=('acceptance-'+[Guid]::NewGuid().ToString('N')); cwd=$project; title="真实验收 $role"; role=$role; prompt=$prompt }
            Write-Host "SUBAGENT_DISPATCH`ntask_name: live_acceptance_$role`nagent_type: $role`nmodel: deepseek-v4.1-flash`nreasoning_effort: max"
            $task = Invoke-LiveTool 'start_task' $arguments
            $accepted.Add($task)
            $acceptedPrompts[$task.id] = $prompt
            Write-Host "真实任务已接收：$role / $($task.id)"
            # Verify the account with the first small read-only task before starting the other four.
            if ($role -eq 'explorer') {
                $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
                $cursor = 0
                do {
                    $detail = Invoke-LiveTool 'wait_task' @{ id=$task.id; after=$cursor; wait_seconds=15 }
                    $cursor = $detail.nextCursor
                } while ($detail.summary.status -in @('Queued','Running') -and [DateTimeOffset]::UtcNow -lt $deadline)
                if ($detail.summary.status -ne 'Completed') { $observed.Add($detail); throw "首个真实任务未完成：$($detail.summary.status) / $($detail.summary.error)" }
            }
        }
        foreach ($task in $accepted) {
            $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
            $cursor = 0
            do {
                $detail = Invoke-LiveTool 'wait_task' @{ id=$task.id; after=$cursor; wait_seconds=15 }
                $cursor = $detail.nextCursor
            } while ($detail.summary.status -in @('Queued','Running') -and [DateTimeOffset]::UtcNow -lt $deadline)
            $observed.Add($detail)
            if ($detail.summary.status -ne 'Completed') { throw "真实任务未完成：$($task.role) / $($detail.summary.status) / $($detail.summary.error)" }
            if ($null -eq $detail.summary.inputTokens -or $null -eq $detail.summary.outputTokens) { throw "真实任务用量缺失：$($task.role)" }
            if ($detail.result -notmatch "ROLE_OK $($task.role) 42") { throw "真实任务结果不符合测试契约：$($task.role)" }
            $project = Join-Path $Root $task.role
            if ((Get-Content -Raw -LiteralPath (Join-Path $project 'input.txt')) -ne '41') { throw '测试输入文件被修改。' }
            if ($task.role -in @('default','worker')) {
                if ((Get-Content -Raw -LiteralPath (Join-Path $project 'answer.txt')).Trim() -ne '42') { throw '写角色未产生预期文件。' }
            } elseif (@(Get-ChildItem -LiteralPath $project -Force).Count -ne 1) { throw '只读角色在项目中产生了额外文件。' }
            if ($task.role -eq 'verifier' -and $detail.result -notmatch '\[AgentDock\] Check artifacts:') { throw 'verifier 未留下可核实检查产物。' }
            $retry = Invoke-LiveTool 'start_task' @{ request_id=$task.requestId; cwd=$task.project; title=$task.title; role=$task.role; prompt=($acceptedPrompts[$task.id]) }
            if ($retry.id -ne $task.id -or $retry.status -ne 'Completed') { throw '重复请求未复用原任务。' }
        }
    } finally {
        foreach ($task in $accepted) {
            try {
                $detail = Invoke-LiveTool 'get_task' @{ id=$task.id }
                if ($detail.summary.status -in @('Queued','Running','Paused')) { $null = Invoke-LiveTool 'stop_task' @{ id=$task.id } }
            } catch { Write-Warning "请在主面板确认测试任务已停止：$($task.id)" }
        }
    }
}

# ---- 准备 ----
$codex = Resolve-Codex
$createdHome = $false
if (-not $IsolatedHome) {
    $IsolatedHome = Join-Path ([System.IO.Path]::GetTempPath()) ("agentdock-probe-" + [Guid]::NewGuid().ToString('N'))
    $createdHome = $true
}
New-Item -ItemType Directory -Path $IsolatedHome -Force | Out-Null

$selfTestRoot = $null
$market = $MarketplaceDir
if ($SelfTest) {
    $selfTestRoot = Join-Path $IsolatedHome 'selftest'
    $market = New-SelfTestPlugin -Root $selfTestRoot
}
$market = (Resolve-Path -LiteralPath $market).Path

$marketJsonPath = Join-Path $market '.agents/plugins/marketplace.json'
if (-not (Test-Path -LiteralPath $marketJsonPath)) { throw "marketplace 缺少 .agents/plugins/marketplace.json：$market" }
$marketJson = Get-Content -Raw -LiteralPath $marketJsonPath | ConvertFrom-Json
$marketName = $marketJson.name
$pluginName = $marketJson.plugins[0].name
if ($pluginName -ne $ServerName) {
    Write-Host "提示：marketplace 插件名为 $pluginName，探针按 -ServerName $ServerName 查询。"
}

$previousCodexHome = $env:CODEX_HOME
$environment = [ordered]@{}
$result = [ordered]@{
    probe        = 'AgentDock.Probe-Plugin'
    selftest     = [bool]$SelfTest
    codexExe     = $codex
    isolatedHome = $IsolatedHome
    marketplace  = $market
    serverName   = $ServerName
    toolName     = $ToolName
}
$failure = $null

try {
    $env:CODEX_HOME = $IsolatedHome
    Write-Host "== 注册本地 marketplace（隔离 CODEX_HOME：$IsolatedHome）"
    $addMarketOut = & $codex plugin marketplace add $market --json 2>&1
    $result.marketplaceAdd = @($addMarketOut | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { throw "codex plugin marketplace add 失败（exit $LASTEXITCODE）：`n$($addMarketOut -join "`n")" }

    Write-Host "== 安装插件 $pluginName@$marketName"
    $addPluginOut = & $codex plugin add "$pluginName@$marketName" --json 2>&1
    $result.pluginAdd = @($addPluginOut | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { throw "codex plugin add 失败（exit $LASTEXITCODE）：`n$($addPluginOut -join "`n")" }

    # 确保隔离配置中插件为启用状态（不触碰用户既有 CODEX_HOME）。
    $configPath = Join-Path $IsolatedHome 'config.toml'
    $configText = if (Test-Path -LiteralPath $configPath) { Get-Content -Raw -LiteralPath $configPath } else { '' }
    $enableHeader = "[plugins.`"$pluginName@$marketName`"]"
    if ($configText -notmatch [regex]::Escape($enableHeader)) {
        Add-Content -LiteralPath $configPath -Value "`n$enableHeader`nenabled = true`n" -Encoding utf8
    }

    Write-Host "== 启动 codex app-server 并真实调用 MCP 工具"
    $process = Start-CodexAppServer -Exe $codex -CodexHome $IsolatedHome
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        Send-Json $process @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ clientInfo = @{ name = 'agentdock-probe'; version = '0.0.1' } } }
        $init = Read-Until -Process $process -Id 1 -TimeoutMs 60000
        $result.initialize = $init.result
        Send-Json $process @{ jsonrpc = '2.0'; method = 'initialized' }

        Send-Json $process @{ jsonrpc = '2.0'; id = 2; method = 'thread/start'; params = @{} }
        $thread = Read-Until -Process $process -Id 2 -TimeoutMs 90000
        if ($thread.PSObject.Properties.Name -contains 'error' -and $thread.error) { throw "thread/start 失败：$($thread.error | ConvertTo-Json -Compress)" }
        $threadId = $thread.result.thread.id
        if (-not $threadId) { $threadId = $thread.result.threadId }
        $result.threadId = $threadId

        Send-Json $process @{ jsonrpc = '2.0'; id = 3; method = 'mcpServerStatus/list'; params = @{ detail = 'toolsAndAuthOnly'; threadId = $threadId; serverName = $ServerName } }
        $status = Read-Until -Process $process -Id 3 -TimeoutMs $ToolTimeoutMs
        if ($status.PSObject.Properties.Name -contains 'error' -and $status.error) { throw "mcpServerStatus/list 失败：$($status.error | ConvertTo-Json -Compress)" }
        $result.mcpStatus = $status.result
        $entry = @($status.result.data) | Where-Object { $_.name -eq $ServerName } | Select-Object -First 1
        if (-not $entry) { throw "状态列表中没有 MCP server：$ServerName" }
        if ($entry.runtimeStatus -ne 'connected') { throw "MCP server $ServerName 未连接：$($entry.runtimeStatus)；$($entry.toolsError)" }
        if (-not ($entry.tools.PSObject.Properties.Name -contains $ToolName)) { throw "MCP server $ServerName 未暴露工具：$ToolName（已有：$($entry.tools.PSObject.Properties.Name -join ', ')）" }

        Send-Json $process @{ jsonrpc = '2.0'; id = 4; method = 'mcpServer/tool/call'; params = @{ server = $ServerName; threadId = $threadId; tool = $ToolName; arguments = @{} } }
        $call = Read-Until -Process $process -Id 4 -TimeoutMs $ToolTimeoutMs
        if ($call.PSObject.Properties.Name -contains 'error' -and $call.error) { throw "mcpServer/tool/call 失败：$($call.error | ConvertTo-Json -Compress)" }
        $result.toolCall = $call.result
        if ($call.result.PSObject.Properties.Name -contains 'isError' -and $call.result.isError) {
            $errorTexts = @(@($call.result.content) | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text })
            throw "工具 $ToolName 返回 isError=true：$($errorTexts -join "`n")"
        }
        $extracted = Get-ToolCallStatus -CallResult $call.result -Context "$ServerName.$ToolName"
        if (-not $extracted.Status) {
            throw '工具调用没有返回可解析的状态 JSON（structuredContent.result 与 content 文本均为空或不可解析）。'
        }
        if ($extracted.Texts.Count -gt 0) { $result.toolCallText = $extracted.Texts -join "`n" }
        if ($extracted.Structured) { $result.toolCallStructuredContent = $call.result.structuredContent }
        $result.toolCallJson = $extracted.Status
        $result.statusSource = $extracted.Source
        # 断言这是真实后台状态（status 工具会在需要时按需启动后台；不能只验证“有返回文本”）。
        Assert-HostStatus -Value $extracted.Status -Context "$ServerName.$ToolName"
        if (-not $SelfTest -and $ExpectedHostVersion -and ([string]$extracted.Status.version) -ne $ExpectedHostVersion) {
            throw "后台版本不符：$($extracted.Status.version) != $ExpectedHostVersion"
        }
        $result.assertions = [ordered]@{
            mcpConnected   = $true
            toolVisible    = $true
            realCall       = $true
            realHostStatus = $true
        }
        Write-Host "== 探针通过：$ServerName.$ToolName 真实返回（$($extracted.Source)）："
        $preview = if ($result.Contains('toolCallText')) { [string]$result.toolCallText } else { $result.toolCallJson | ConvertTo-Json -Compress -Depth 8 }
        Write-Host ($preview.Substring(0, [Math]::Min(400, $preview.Length)))
        if ($LiveTasksRoot) { Invoke-LiveAcceptance $process $threadId $LiveTasksRoot }
    } finally {
        try { $process.Kill() } catch { }
        try {
            $null = $stderrTask.Wait(5000)
            $stderrText = $stderrTask.Result
            if ($stderrText) { $result.appServerStderr = $stderrText.Substring(0, [Math]::Min(4000, $stderrText.Length)) }
        } catch { }
    }
} catch {
    $failure = $_.Exception.Message
    $result.probeError = $failure
} finally {
    if ($null -eq $previousCodexHome) { Remove-Item Env:\CODEX_HOME -ErrorAction SilentlyContinue } else { $env:CODEX_HOME = $previousCodexHome }
    $result.success = ($null -eq $failure)
    $json = $result | ConvertTo-Json -Depth 16
    if (-not $OutFile) { $OutFile = Join-Path $IsolatedHome 'probe-result.json' }
    try { Set-Content -LiteralPath $OutFile -Value $json -Encoding utf8 } catch { }
    Write-Host "探针结果：$OutFile"
    if (-not $KeepTemp) {
        if ($createdHome -and (Test-Path -LiteralPath $IsolatedHome)) {
            Remove-Item -LiteralPath $IsolatedHome -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($failure) { throw "插件探针失败：$failure" }
[pscustomobject]@{ Success = $true; Server = $ServerName; Tool = $ToolName; Evidence = $OutFile } | ConvertTo-Json -Compress
