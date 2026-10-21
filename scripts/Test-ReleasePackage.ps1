#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$work = Join-Path ([IO.Path]::GetTempPath()) ('agentdock-release-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$version = '0.1.1-beta.1'
$package = Join-Path $work 'package'
New-Item -ItemType Directory -Path $package | Out-Null
$manifest = @{
    schema = 1; product = 'AgentDock'; runtime = 'win-x64'; configuration = 'Release'
    version = $version; hostVersion = $version; partial = $false; selfContained = $true
    frameworkDependent = $false; missingComponents = @(); releaseReady = $false
    releaseBlocked = @('clean-vm-acceptance-missing'); warnings = @('unsigned')
    components = @{}; artifacts = @{}
}
foreach ($name in 'host', 'desktop', 'engine', 'setup') { $manifest.components[$name] = @{ present = $true } }
foreach ($item in @(
    @{ Key = 'setup'; File = "AgentDockSetup-$version-win-x64.exe" },
    @{ Key = 'payloadZip'; File = "AgentDock-$version-win-x64.zip" }
)) {
    $path = Join-Path $package $item.File
    Set-Content -LiteralPath $path -Value 'synthetic package fixture'
    $manifest.artifacts[$item.Key] = @{
        file = $item.File; size = (Get-Item -LiteralPath $path).Length
        sha256 = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
    }
}
# 目录里的无关文件不能成为发布附件。
$manifest.artifacts.setup.embeddedPayload = $true
$manifest.artifacts.setup.payloadSha256 = $manifest.artifacts.payloadZip.sha256
Set-Content -LiteralPath (Join-Path $package 'private-fixture.txt') -Value 'do not publish'
$baseline = $manifest | ConvertTo-Json -Depth 8
$caseCount = 0

# 执行构建器实际门禁，避免合成清单绕过平台签名策略。
$builder = Get-Content -Raw -LiteralPath "$PSScriptRoot/Build-Package.ps1"
$policy = [regex]::Match($builder, '(?s)\$releaseBlocked = .*?(?=\r?\n\$packageManifest = )')
if (-not $policy.Success) { throw '未找到构建器发布门禁。' }
$checkPolicy = [scriptblock]::Create($policy.Value)
foreach ($windows in @($true, $false)) {
    & {
        $isWindowsTarget = $windows
        $warnings = [Collections.Generic.List[string]]::new()
        $missingComponents = @()
        $FrameworkDependent = $false
        $SkipSetup = $false
        . $checkPolicy
        $unsignedBlocked = @($releaseBlocked | Where-Object { $_ -like 'unsigned*' }).Count -gt 0
        $unsignedWarning = @($warnings | Where-Object { $_ -like 'unsigned*' }).Count -gt 0
        if ($unsignedBlocked -eq $windows -or $unsignedWarning -ne $windows) { throw 'Windows 签名须为警告，Mac 签名仍须为门禁。' }
        if ($releaseReady -or -not ($releaseBlocked -like 'clean-vm-acceptance-missing*')) { throw '签名可选不能清除其他验收门禁。' }
        if (-not $windows -and -not ($releaseBlocked -like 'macos-real-device-acceptance-missing*')) { throw 'Mac 实机门禁丢失。' }
    }
    $caseCount++
    Write-Host "PASS signing-policy-windows-$windows"
}

function Invoke-PackageCase([string]$Name, [scriptblock]$Mutate, [bool]$Reject) {
    $script:caseCount++
    $candidate = $baseline | ConvertFrom-Json -AsHashtable
    & $Mutate $candidate
    $candidate | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json')
    $output = Join-Path $work $Name
    $failure = $null
    try { & "$PSScriptRoot/Prepare-Release.ps1" -Version $version -PackageDir $package -OutputDir $output }
    catch { $failure = $_ }
    if ($Reject) {
        if (-not $failure) { throw "$Name 应拒绝发布。" }
        if (Test-Path -LiteralPath $output) { throw "$Name 在校验失败前已生成输出。" }
    } else {
        if ($failure) { throw $failure }
        $names = @(Get-ChildItem -LiteralPath $output -File | Select-Object -ExpandProperty Name)
        if ($names.Count -ne 4 -or $names -contains 'private-fixture.txt' -or $names -contains "AgentDock-$version-win-x64.zip") { throw '发布附件范围不正确。' }
        $sums = Get-Content -LiteralPath (Join-Path $output 'SHA256SUMS-win-x64.txt')
        if ($sums.Count -ne 2) { throw '校验清单缺少附件。' }
        foreach ($line in $sums) {
            $hash, $file = $line -split '  ', 2
            if ((Get-FileHash -LiteralPath (Join-Path $output $file)).Hash.ToLowerInvariant() -cne $hash) { throw '校验清单不正确。' }
        }
        if ([IO.File]::ReadAllText((Join-Path $output 'SHA256SUMS-win-x64.txt')).Contains("`r")) { throw '校验清单必须使用 LF。' }
        if ((Get-Content -Raw -LiteralPath (Join-Path $output 'release-notes-win-x64.md')) -notmatch 'releaseReady=false') { throw '发行限制丢失。' }
        if ((Get-FileHash -LiteralPath (Join-Path $output 'package-manifest-win-x64.json')).Hash -cne (Get-FileHash -LiteralPath (Join-Path $package 'package-manifest.json')).Hash) { throw '发布清单原始字节被改写。' }
        $publishedManifest = Get-Content -Raw -LiteralPath (Join-Path $output 'package-manifest-win-x64.json') | ConvertFrom-Json
        if ($publishedManifest.warnings -notcontains 'unsigned' -or $publishedManifest.releaseBlocked -contains 'unsigned') { throw '发布清单必须保留未签名警告，不能将其改为 Windows 阻塞项。' }
        $retryFailed = $false
        try { & "$PSScriptRoot/Prepare-Release.ps1" -Version $version -PackageDir $package -OutputDir $output }
        catch { $retryFailed = $true }
        if (-not $retryFailed) { throw '不得覆盖既有附件目录。' }
    }
    Write-Host "PASS $Name"
}

Invoke-PackageCase 'valid-preview' {} $false
Invoke-PackageCase 'unbundled-installer' { param($m) $m.artifacts.setup.embeddedPayload = $false } $true
Invoke-PackageCase 'mismatched-bundle' { param($m) $m.artifacts.setup.payloadSha256 = '0' * 64 } $true
Invoke-PackageCase 'partial' { param($m) $m.partial = $true } $true
Invoke-PackageCase 'missing-component' { param($m) $m.components.engine.present = $false } $true
Invoke-PackageCase 'framework-dependent' { param($m) $m.selfContained = $false; $m.frameworkDependent = $true } $true
Invoke-PackageCase 'wrong-version' { param($m) $m.version = '0.1.2-beta.1' } $true
Invoke-PackageCase 'wrong-host' { param($m) $m.hostVersion = '' } $true
Invoke-PackageCase 'wrong-runtime' { param($m) $m.runtime = 'osx-arm64' } $true
Invoke-PackageCase 'bad-hash' { param($m) $m.artifacts.payloadZip.sha256 = '0' * 64 } $true
Invoke-PackageCase 'bad-size' { param($m) $m.artifacts.setup.size++ } $true
Invoke-PackageCase 'unsafe-name' { param($m) $m.artifacts.setup.file = '../private-fixture.txt' } $true
Invoke-PackageCase 'missing-field' { param($m) $m.Remove('partial') } $true
Invoke-PackageCase 'wrong-type' { param($m) $m.partial = 'False' } $true
$version = '0.1.1'
Invoke-PackageCase 'stable-version' {} $true
$version = '0.1.1-beta.1'
Remove-Item -LiteralPath (Join-Path $package $manifest.artifacts.setup.file)
Invoke-PackageCase 'missing-file' {} $true

# 执行工作流实际内联脚本，覆盖标签、手动分支误发和版本注入。
$workflow = Get-Content -Raw -LiteralPath "$PSScriptRoot/../.github/workflows/package.yml"
$match = [regex]::Match($workflow, '(?s)id: version.*?run: \|\r?\n(.*?)(?=\r?\n      -)')
if (-not $match.Success) { throw '未找到工作流版本步骤。' }
$resolveVersion = [scriptblock]::Create(($match.Groups[1].Value -replace '(?m)^          ', ''))
$envNames = @('CREATE_RELEASE', 'GITHUB_REF_TYPE', 'GITHUB_REF_NAME', 'INPUT_VERSION', 'VERSION', 'GITHUB_ENV', 'GITHUB_OUTPUT')
$saved = @{}
foreach ($name in $envNames) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    foreach ($case in @(
        @{ Name = 'branch-ci'; Release = 'false'; Type = 'branch'; Ref = 'main'; Input = ''; Version = '0.0.0-ci.1'; Expected = '0.0.0-ci.1'; Reject = $false },
        @{ Name = 'tag-release'; Release = 'true'; Type = 'tag'; Ref = 'v0.1.1-beta.1'; Input = ''; Version = '0.0.0-ci.1'; Expected = '0.1.1-beta.1'; Reject = $false },
        @{ Name = 'tag-build-only'; Release = 'false'; Type = 'tag'; Ref = 'v0.1.1-beta.1'; Input = ''; Version = '0.0.0-ci.1'; Expected = '0.1.1-beta.1'; Reject = $false },
        @{ Name = 'branch-release'; Release = 'true'; Type = 'branch'; Ref = 'main'; Input = ''; Version = '0.0.0-ci.1'; Reject = $true },
        @{ Name = 'stable-tag'; Release = 'true'; Type = 'tag'; Ref = 'v0.1.1'; Input = ''; Version = '0.0.0-ci.1'; Reject = $true },
        @{ Name = 'mismatched-input'; Release = 'true'; Type = 'tag'; Ref = 'v0.1.1-beta.1'; Input = '0.1.2-beta.1'; Version = '0.0.0-ci.1'; Reject = $true },
        @{ Name = 'newline-input'; Release = 'false'; Type = 'branch'; Ref = 'main'; Input = ''; Version = "0.1.1`n"; Reject = $true }
    )) {
        $caseCount++
        $env:CREATE_RELEASE = $case.Release; $env:GITHUB_REF_TYPE = $case.Type; $env:GITHUB_REF_NAME = $case.Ref
        $env:INPUT_VERSION = $case.Input; $env:VERSION = $case.Version
        $env:GITHUB_ENV = Join-Path $work ($case.Name + '.env')
        $env:GITHUB_OUTPUT = Join-Path $work ($case.Name + '.output')
        $failure = $null
        try { & $resolveVersion } catch { $failure = $_ }
        if ($case.Reject) {
            if (-not $failure -or (Test-Path -LiteralPath $env:GITHUB_ENV)) { throw "$($case.Name) 没有在写环境之前拒绝。" }
        } else {
            if ($failure) { throw $failure }
            if ((Get-Content -LiteralPath $env:GITHUB_OUTPUT) -notcontains "version=$($case.Expected)") { throw '构建版本输出不符。' }
        }
        Write-Host "PASS $($case.Name)"
    }
} finally {
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
Write-Host "$caseCount release checks passed; fixtures: $work"
