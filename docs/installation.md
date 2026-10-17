# AgentDock 安装与接入指南

本指南对应 Issue #10（打包安装、Codex 插件分发与空闲升级）的 Windows 流程。当前安装包尚未代码签名；干净 Windows 虚机的普通用户安装、桌面、安装门禁、Codex 插件状态调用及独立账户真实最小任务已通过，见 [Windows 记录](validation/windows-clean-hyperv.md)。Mac 完整安装、真实账户、升级及卸载已实测，步骤见 [Mac 安装说明](../packaging/mac/README.md)，证据见 [Mac 验收](validation/macos.md)；两平台**尚未达到正式发行条件**。

## 一、准备工作

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809+ / Windows 11，x64 |
| 权限 | 每用户安装，无需管理员权限 |
| Git | **无需预装**：安装包内置官方 MinGit（Git for Windows 最小分发，含 GNU Bash），Host 默认使用 `engines/git/usr/bin/sh.exe` 作为 shell |
| 开发环境 | **不需要**用户自行安装 Python、Node、.NET 或 Git；所需运行时与依赖均随包分发 |

安装包由两个文件组成，请放在同一目录：

- `AgentDockSetup-<版本>-win-x64.exe`：安装程序（图形界面 + 命令行）。
- `AgentDock-<版本>-win-x64.zip`：payload（桌面程序、后台 Host、执行引擎、固定运行时、插件源）。

## 二、安装

1. 双击 `AgentDockSetup-<版本>-win-x64.exe`（未签名版本可能触发 SmartScreen 提示）。
2. 安装程序自动完成：校验 payload 每个文件的 SHA-256 → 解包到版本目录 → 写入当前版本指针 → 刷新插件源 → 创建稳定启动器、开始菜单快捷方式与“应用和功能”卸载入口。
3. 安装位置（每用户）：

```
%LOCALAPPDATA%\AgentDock\
  AgentDock.exe          稳定启动器（跨版本有效）
  install\
    current.json         当前版本指针
    <版本>\              固定版本组合的程序文件（旧版本保留）
    machine.json         只读检测结果（内置 Git/sh 路径与哈希、旧 deepseek_agents 提示）
  plugin\                Codex 本地 marketplace（跨版本稳定路径）
  data\                  用户数据（安装/更新/卸载都不删除）
```

4. 首次启动后，若顶部显示后台未连接，先点击「启动后台」，等待显示「后台运行中」，再到「设置」保存 OpenCode Go 凭据和角色。**AgentDock 全新配置，不迁移旧凭据或角色**；旧 MCP 配置仅用于只读共存提示，系统登录自启默认关闭。后台未运行时保存凭据会超时；启动后台后可重试保存。

命令行方式（自动化/脚本）：

```powershell
# 安装（静默；payload 可省略，会自动寻找同目录的 zip）
AgentDockSetup-<版本>-win-x64.exe --install --payload "<zip 路径>" --silent

# 查看安装状态（JSON；WinExe 不便管道捕获，用 --out 写文件）
AgentDockSetup-<版本>-win-x64.exe --status --out status.json

# 启动
AgentDockSetup-<版本>-win-x64.exe --launch
```

安装、同版本修复、更新与卸载共用同一套门禁（fail closed）：操作前写入 `install\update.lock` 更新标记，
后台在线且空闲时先请求正常退出，之后独占 `data\launch.lock` 并持有到文件切换结束；
后台离线时直接读取 `data\tasks.json`，只要存在排队/运行/暂停任务或状态文件损坏就拒绝操作。

PowerShell 自动化应使用 `Start-Process -Wait -PassThru` 并检查 ExitCode；直接启动 WinExe 不保证脚本等待安装结束。快捷方式及可选登录自启通过稳定启动器读取当前版本，更新后不固定到旧版本程序。

## 三、Codex 插件接入

先完成 AgentDock 安装（安装程序会生成 `%LOCALAPPDATA%\AgentDock\plugin` 与插件所需的绝对路径清单 `runtime/install.json`），再在 Codex 中接入：

```powershell
codex plugin marketplace add "$env:LOCALAPPDATA\AgentDock\plugin"
codex plugin add agentdock@agentdock-local
```

- **需要新建对话或重启 Codex**：MCP 服务器在对话/进程启动时加载，已打开的对话不会获得新工具。
- 验证接入（在 Codex 对话中）：让 Codex 调用 `agentdock` 的 `status` 工具；返回的 JSON 应包含版本与 `CanUpdate` 等字段。
- 也可以在开发机上运行仓库内探针做无登录验证：

```powershell
pwsh scripts/Probe-Plugin.ps1 -MarketplaceDir "$env:LOCALAPPDATA\AgentDock\plugin" -ExpectedHostVersion 0.1.0
```

探针使用隔离 `CODEX_HOME`，依次执行 `initialize → thread/start → mcpServerStatus/list → mcpServer/tool/call`；
工具调用优先读取 `structuredContent.result`（Host 0.1.0 起返回），兼容仅含文本的旧形态，并断言返回的是真实后台状态
（`version` 非空、`processId > 0`、`unfinishedTasks >= 0`、`canUpdate` 为布尔）；不依赖登录和模型调用。

- 移除插件：`codex plugin remove agentdock@agentdock-local`。
- 旧入口提示：如果旧 deepseek_agents MCP 仍在启用，安装程序只做只读检测并提示“可能重复派发任务”，不会自动停用或改动旧配置；请自行决定何时停用旧入口。

## 四、更新

- **只提示、不自动更新**：桌面程序只在用户主动检查时访问 GitHub 获取新版本；已安装的执行引擎绝不自动更新。
- 用户选择更新后，安装程序/更新器按固定顺序执行，任一步失败都保留旧版本可用（fail closed）：
  1. 先写入 `install\update.lock` 更新标记——更新窗口内 Host 启动、`start_task`/`resume_task` 都会被拒绝；
  2. 后台在线时要求 `status.CanUpdate == true` 且未完成任务为 0（排队/运行/暂停都不许），随后通过共享 DockClient
     请求正常退出（`shutdown { confirm: false }`，无对话框）；后台离线时直接读取 `data\tasks.json`，
     存在排队/运行/暂停任务或状态文件损坏时拒绝更新；
  3. 确认后台已退出后，安装程序独占获取 `data\launch.lock`（Host 生命周期锁）并持有到切换完成——
     期间竞争启动的 Host 会因标记而快速失败，不会接纳新任务；
  4. 更新完成后原子切换 `current.json` 并删除更新标记，旧版本目录保留，用户数据不动。
- 更新标记由存活进程号判定：持有进程仍在运行则拒绝；确认已退出才清理；无法解析的标记在 15 分钟内同样拒绝。
- 手动更新命令：

```powershell
AgentDockSetup-<新版本>-win-x64.exe --apply-update --payload "<新 zip 路径>"
```

失败时输出明确原因（有未完成任务 / 无法确认后台状态 / 更新标记冲突 / 启动锁被占用等），当前版本保持可用。

## 五、卸载

- 通过“设置 → 应用 → AgentDock”（或开始菜单）卸载；也可执行 `AgentDock.exe --uninstall`（稳定启动器内）。
- 卸载与安装/更新共用同一门禁：存在未完成任务、后台异常或更新标记由存活进程持有时拒绝；空闲后台会先正常退出。
- 卸载只删除程序文件、插件源目录、稳定启动器和快捷方式/注册表项；**保留 `%LOCALAPPDATA%\AgentDock\data` 用户数据**。
- 静默卸载带 `--silent`；自动化验证可运行 `pwsh scripts/Test-SetupGates.ps1 -SetupExe <安装程序> -Payload <payload>`。

## 六、固定版本组合

安装包内固定的运行时（同时写入包内 `runtime-lock.json`，可用其复核哈希）：

| 组件 | 版本 | 来源 | 校验 |
| --- | --- | --- | --- |
| OpenCode | 1.18.30 | npm `opencode-windows-x64`（官方 registry） | sha512 tarball + sha256 exe |
| Codex sandbox | 0.153.4 | npm `@openai/codex@0.153.4-win32-x64`（官方 registry） | sha512 tarball + 逐文件 sha256 |
| MinGit（内置 Git Bash） | 2.56.0.2（上游 v2.56.0.windows.2） | git-for-windows 官方 Release `MinGit-2.56.0.2-64-bit.zip` | 整包 sha256 `da35e72a…b3cd3be`，解包后校验 `usr/bin/sh.exe` sha256 `5490d0da…2d72e3`（GNU Bash 5.3.15） |

- MinGit 整包解包到 `engines/git`（含许可证与依赖 DLL），构建时由 `packaging/Fetch-Runtimes.ps1` 校验：
  优先使用本地缓存归档（`-CacheDir`、`AGENTDOCK_RUNTIME_CACHE`、`<repo>/artifacts/runtime-downloads`、`packaging/.cache`），
  缺失或哈希不符时才从官方 URL 下载；任何不符都直接失败，绝不静默降级。
- npm 运行时优先使用本机已安装的公共程序位，但必须通过清单哈希；否则从官方来源下载并在解包前校验。
  `runtime-manifest.json` 记录每个归档与关键文件的来源和 SHA-256，安装包与公开仓库不包含任何个人配置、凭据或任务数据。

## 七、故障排查

| 现象 | 处理 |
| --- | --- |
| 启动报“未找到配套后台程序” | 使用完整安装包重装；partial 构建不含 Host |
| 插件启动失败：找不到路径 / HostMissing | 先完成 AgentDock 安装，再重跑 `codex plugin marketplace add` + `codex plugin add`，让插件缓存包含 `runtime/AgentDock.Host.exe` 与 `runtime/install.json`；然后新建对话 |
| Codex 中看不到 agentdock 工具 | 确认 `codex plugin list` 已安装、已新建对话或重启 Codex |
| 安装程序提示缺少内置 Git | 该 payload 不含 `engines/git/usr/bin/sh.exe`（非完整包）；使用官方完整安装包重新安装，无需另装 Git |
| 更新/安装/卸载被拒绝 | 存在排队/运行/暂停任务、后台状态不明或更新标记冲突（fail closed）；等任务结束后重试，必要时按提示清理过期标记 |
| 安装包提示 partial / 缺少组件 | 该构建未包含 Host/Desktop/Engine，属于开发自检包，不能用于正式安装 |
| macOS 包 | 本地 arm64 候选；验收进度见 Mac 记录，签名、公证和正式分发尚未完成 |

## 八、发布门槛（未完成项）

- 代码签名（安装程序与程序文件）尚未配置；
- 干净 Windows 虚机已通过安装、插件接入、真实任务、人工交互和用户指定的 12 小时持续运行；新引擎候选另通过十轮压力复验。12 小时结果属于旧候选，严格冷启动和更广泛的资源预算仍有缺口，见 [最新验收状态](validation/acceptance.md)；
- macOS：完整包和真实账户链路已通过实机验证；Developer ID 签名、公证及下载后 Gatekeeper 验收仍未通过；
- 公开仓库与 Release 只能包含必要程序与模板；本仓库脚本已按“不夹带本机凭据/个人配置”实现，发行前仍需人工核验。
