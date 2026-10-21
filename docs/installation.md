# AgentDock 安装与接入指南

本指南对应 Issue #10（打包安装、Codex 插件分发与空闲升级）的 Windows 流程。当前安装包尚未代码签名；干净 Windows 虚机的普通用户安装、桌面、安装门禁、Codex 插件状态调用及独立账户真实最小任务已通过，见 [Windows 记录](validation/windows-clean-hyperv.md)。Mac 完整安装、真实账户、升级及卸载已实测，步骤见 [Mac 安装说明](../packaging/mac/README.md)，证据见 [Mac 验收](validation/macos.md)；两平台**尚未达到正式发行条件**。

## 一、准备工作

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809+ / Windows 11，x64 |
| 权限 | 每用户安装，无需管理员权限 |
| Git | **无需预装**：安装包内置官方 MinGit（Git for Windows 最小分发，含 GNU Bash），Host 默认使用 `engines/git/usr/bin/sh.exe` 作为 shell |
| 开发环境 | **不需要**用户自行安装 Python、Node、.NET 或 Git；所需运行时与依赖均随包分发 |

从新版 Release 下载一个文件即可：

- `AgentDockSetup-<版本>-win-x64.exe`：内置桌面程序、后台 Host、执行引擎、固定运行时与插件源的完整安装程序。

无需另行下载、解压或复制 payload，也不需要处理 `package-manifest.json` 或 `SHA256SUMS.txt`；它们是可选的构建与校验资料。旧版 `0.1.1-beta.1` 仍是 EXE + ZIP 分体包，需要按该版本的 Release 说明下载两个文件。

## 二、安装

1. 双击 `AgentDockSetup-<版本>-win-x64.exe`，点击「安装 / 应用更新」（未签名版本可能触发 SmartScreen 提示）。
2. 安装程序自动校验内置程序包、安装文件、刷新插件源并创建启动和卸载入口。日常启动使用精简启动器，不携带整份分发包。
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

4. 首次启动进入「开始使用」，依次完成：启动后台 → 保存 OpenCode Go API Key → 接入 Codex → 启用全局委派。可点击「稍后配置」进入任务页；未完成时下次启动继续显示引导，完成后默认进入任务页。可随时从「开始使用」或「设置 → 打开配置引导」重新进入。
5. 凭据已存在时可直接继续，输入新值才会替换。保存后立即刷新状态并清空输入；“已保存”仅确认本机存储，不代表 API Key 有效或未过期。是否可执行以真实任务结果为准。**AgentDock 全新配置，不迁移旧凭据或角色**；系统登录自启默认关闭。
6. 引导通过 Codex 命令工具注册本机插件，配置完成后仍可日常使用 ChatGPT / Codex 桌面 App。macOS 会查找 PATH、系统及用户“应用程序”目录中的 `ChatGPT.app`、`Codex.app`、用户本地 CLI 和 Homebrew 安装位置；这些位置没有命令工具时，还会查找 `NVM_DIR`（默认 `~/.nvm`）下 `versions/node/v*/bin/codex`，优先使用包含 Codex 的最新 Node 版本。手动选择 `.app` 仅在其中包含命令工具时有效，也可直接选择现有的 `codex` 文件。启动 nvm 命令工具时，AgentDock 为该子进程补充同目录 Node 的查找路径，不修改系统或终端配置。

   CLI 与桌面 App 需要使用同一份 Codex 配置目录。插件状态显示“已注册并启用”，并不代表桌面 App 当前对话已经载入工具；配置后重启桌面 App 并新建对话，在 App 中验证 AgentDock 工具。官方说明见 [本地插件与桌面 App](https://developers.openai.com/plugins/build/plugins#how-local-marketplaces-work)。

全局委派首次仅在点击「启用全局委派并完成」时启用：优先使用 `CODEX_HOME`（默认 `~/.codex`）中非空的 `AGENTS.override.md`，否则使用 `AGENTS.md`，不会新建覆盖文件。受控区块之外的内容保留；可从引导最后一步「关闭全局委派并恢复原规则」。恢复记录保存在 AgentDock 数据目录 `onboarding-routing-backup.json`，引导完成记录在 `onboarding.json`。

启动主面板或重新检查配置时，会更新已启用、内容完整且恢复记录匹配的旧 AgentDock 托管规则；未启用或已关闭的规则不会自动开启。升级保留原区块、文件编码和换行，并可在中断后继续恢复。新版派发说明通过后台 `status.executionProfile` 读取实际模型和推理强度，不套用 Codex 原生子代理 TOML。旧后台没有这些字段时，只有核实执行配置后才能填值，不能凭主模型猜测。

启用后，适合独立完成的五类子任务使用 AgentDock，主模型、认证及原生 architect 保持不变。规则影响所有项目，仍遵守各项目限制。已有未知委派规则、手动修改区块或切换生效文件时会提示冲突，不自动覆盖。只识别并可恢复既有的“等待用户选择外部集成”过渡区块，不迁移旧 DeepSeek MCP 配置。

角色默认提示词使用英文，分别明确 default、explorer、worker、reviewer、verifier 的职责、工作步骤、完成条件和交付要求；回复语言和格式遵循实际任务要求。内置文本集中在 `src/AgentDock.Core/RolePrompts.cs`。后台启动时只更新与 AgentDock 旧默认文本逐字一致的提示词，保留自定义内容、角色超时和其他设置。已接收的任务继续使用接收时的提示词快照；新接收任务使用更新后的配置。角色提示词不扩大执行权限。

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

推荐使用首次引导的「接入 Codex」。手动接入时，先完成 AgentDock 安装（安装程序会生成 `%LOCALAPPDATA%\AgentDock\plugin` 与插件所需的绝对路径清单 `runtime/install.json`），再执行：

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
- 若已启用全局委派，卸载前先在「开始使用」最后一步关闭并恢复原规则；卸载器不会改写 Codex 的全局规则文件。
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
