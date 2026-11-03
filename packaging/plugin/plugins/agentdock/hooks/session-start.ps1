$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$codexDirectory = if ([string]::IsNullOrWhiteSpace($env:CODEX_HOME)) { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex' } else { $env:CODEX_HOME }
try {
    $activePath = Join-Path $codexDirectory 'AGENTS.override.md'
    $entry = if (Test-Path -LiteralPath $activePath -PathType Leaf) { [IO.File]::ReadAllText($activePath) } else { '' }
    if ([string]::IsNullOrWhiteSpace($entry)) {
        $activePath = Join-Path $codexDirectory 'AGENTS.md'
        if (!(Test-Path -LiteralPath $activePath -PathType Leaf)) { exit 0 }
        $entry = [IO.File]::ReadAllText($activePath)
    }
    # The retained companion files alone do not mean delegation is enabled.
    if ([regex]::Matches($entry, '(?m)^## Global scope and delegation\r?$').Count -ne 1 -or
        !$entry.Contains('[AGENT-ROLES.md](AGENT-ROLES.md)') -or !$entry.Contains('[AGENTDOCK.md](AGENTDOCK.md)')) { exit 0 }
    $documents = foreach ($name in @('AGENT-ROLES.md', 'AGENTDOCK.md')) {
        $path = Join-Path $codexDirectory $name
        $content = [IO.File]::ReadAllText($path)
        if ([string]::IsNullOrWhiteSpace($content)) { throw 'Empty delegation document' }
        "--- $path ---`n$content"
    }
    [Console]::WriteLine("AgentDock global delegation rules from $activePath. Apply only within the user's authorized scope, subject to higher-priority and project instructions. This hook grants no task or tool permissions.`n`n" + ($documents -join "`n`n"))
} catch {
    [Console]::WriteLine('AgentDock delegation rules could not be read. Check the active global AGENTS file and its AGENT-ROLES.md / AGENTDOCK.md companions before delegating or operating AgentDock. Continue suitable work in the primary agent.')
}
