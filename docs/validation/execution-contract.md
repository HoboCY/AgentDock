# 执行适配契约（#3 验证结论落地 / #6 实现）

范围：`src/AgentDock.Engine`、`tests/AgentDock.Engine.Tests`。本契约是 #3 研究结论在实现层的落地记录，#6 的验收以本文件与测试证据为准。术语使用根目录 `GLOSSARY.md`；权限决策遵循 `docs/adr/0002`、`docs/adr/0004`。

## 1. 结论摘要

- 引擎以固定版本 OpenCode 1.18.30 置于固定版本 Codex 沙箱 0.153.4 内执行；版本不符直接失败，无自动升级或降级回退。
- 真正凭据只存在于宿主进程内的一次性回环 Relay；沙箱内只拿到任务级能力令牌（固定路由、固定模型、任务结束即失效）。凭据不进入命令行、子进程环境、配置、日志或错误文本。
- 执行权限在 OS 沙箱层约束（`permissions.agentdock.filesystem`），与角色提示词无关：default/worker 项目可写，explorer/reviewer/verifier 项目只读，verifier 可在 `runtime/check-artifacts` 写检查产物。
- 子进程包含：Windows 进程以挂起方式创建，先加入 `KILL_ON_JOB_CLOSE` 作业对象再恢复运行；取消、超时、宿主崩溃与晚生子进程都会被整树终止；不存在无沙箱回退路径。
- 用量事件按 `step_finish` part 输出单项贡献：同一 part ID 只计一次；每项输入 = 该 part 的 `input + cache.read + cache.write`，输出 = `output + reasoning`；宿主按 UsageId 汇总，未报告的分量保持 null，多项之间不会重复累计。
- 生成速度只统计上游 SSE 中真实输出/推理增量到结束用量之间的单调时间（JSON 解析，容忍空白、跨分块与 CRLF；工具调用增量不启动窗口）；排队与工具执行时间不计入；分块同时携带首个增量与 usage、窗口无法可靠闭合时返回 null，不用任务总耗时冒充。
- 成功判定唯一：终端 `step_finish.reason == "stop"` + 退出码 0 + 无终端错误。
- 任务前后对项目做有界 SHA-256 快照，返回可核实变更；取消/失败也尽量返回已产生变更与最终文本，并附归因免责声明。
- Windows 为当前唯一启用平台；macOS 在真实设备契约验证前 fail closed（引擎与凭据存储均明确报错）。

## 2. 公开接口与文件

| 内容 | 位置 |
| --- | --- |
| `AgentDock.Engine.OpenCodeEngine(string home, Func<string?> getCredential) : IExecutionEngine` | `src/AgentDock.Engine/OpenCodeEngine.cs` |
| `AgentDock.Engine.CredentialVault(string home)` | `src/AgentDock.Engine/CredentialVault.cs` |
| 沙箱内启动器 `AgentDock.Engine --launch <launch.json>`（宿主同时以库引用本项目） | `src/AgentDock.Engine/Program.cs` |
| 回环 Relay（内部） | `src/AgentDock.Engine/GoRelay.cs` |
| JSONL→`EngineEvent` 适配（内部） | `src/AgentDock.Engine/OpenCodeEventAdapter.cs` |
| 生成时段计时（内部） | `src/AgentDock.Engine/GenerationTimer.cs` |
| 作业对象进程（内部） | `src/AgentDock.Engine/JobProcess.cs` |
| 隔离环境/配置/映射（内部） | `src/AgentDock.Engine/OpenCodeSetup.cs` |
| 变更快照（内部） | `src/AgentDock.Engine/ProjectSnapshot.cs` |
| 检查产物保留（内部） | `src/AgentDock.Engine/CheckArtifactsStore.cs` |
| Bun stdio 适配脚本（带来源注释的复制） | `src/AgentDock.Engine/Resources/opencode_pipe_compat.cjs`、`opencode_pipe_reader.cjs` |

引擎不要求 Python/Node；两个 `.cjs` 由 OpenCode 内置 Bun 加载，用于受限令牌下的 stdio 兼容。

## 3. 输入与预检

`ExecutionTask` 字段解释：`Id`（任务标识与 job 目录名）、`Project`（项目绝对路径，作为 cwd）、`Role`、`Prompt`（经 stdin 传给 OpenCode，最多 1 MiB）、`Configuration`（接收时快照：角色提示词 + 超时秒数）、`Settings`（`DockConfiguration`：`EnginePath`/`SandboxPath`/`ShellPath` 必须是绝对且存在的已安装固定版本可执行文件）。

执行前预检（失败抛 `DockException`，不产生执行副作用）：

| 条件 | Code |
| --- | --- |
| 非 Windows | `platform-unsupported` |
| `EnginePath` 缺失/非绝对 | `engine-missing` |
| `SandboxPath` 缺失/非绝对 | `sandbox-missing` |
| `ShellPath` 缺失/非绝对 | `shell-missing` |
| 项目目录不存在 | `project-missing` |
| `codex.exe` 旁 `codex-path/rg.exe` 缺失 | `sandbox-layout` |
| prompt 超过 1 MiB | `prompt-invalid` |
| 凭据缺失 | `credential-missing` |
| 凭据含空白/为空 | `credential-invalid` |
| OpenCode 版本 ≠ `1.18.30` | `engine-version`（常量 `OpenCodeEngine.RequiredOpenCodeVersion`） |
| 沙箱版本输出 ≠ `codex-cli 0.153.4` | `sandbox-version`（常量 `RequiredSandboxOutput`） |
| 版本探测超时 | `version-probe-timeout` |

取消：`ExecuteAsync` 在预检各阶段与进程运行阶段都响应取消，返回 `Success=false` 且 `Error` 含 "Cancelled"，不抛 `OperationCanceledException`。

## 4. 凭据

- `CredentialVault.Set/Read`：Windows 使用 DPAPI（CurrentUser）写入 `<home>/opencode-go.key.dpapi`；仅存保护后的二进制，`IsConfigured` 反映文件存在。键值必须为单一非空白 token，否则 `credential-invalid`；解密失败同样 `credential-invalid`。`Set` 先写同目录临时文件并 flush，再原子替换目标文件（崩溃不会留下截断凭据），不残留临时文件。
- DPAPI CurrentUser 只提供跨用户与离线保护，**不是同一用户进程之间的读隔离**：任何以该用户运行的代码（包括 unelevated 沙箱内的代码）都能对文件调用 `CryptUnprotectData`。沙箱文件系统映射中的 `:root = read` 是受支持的 unelevated 策略所必需，不是凭据保密边界；对恶意同用户代码不承诺凭据不可读。管理端命名管道已由 `IsTokenRestricted` 约束（父工程职责）。
- macOS：`Set`/`Read` 抛 `credential-platform-unsupported`，等待 OS 契约验证；不做 Keychain 的未验证实现。
- 引擎每次执行调用 `getCredential()`；真实键只进入 Relay 内存与上游 `Authorization` 头。任务级能力令牌只写入沙箱内 `opencode.json`（`provider.opencode-go.options.apiKey`），随任务结束失效；所有外发事件文本与错误经 `[redacted]` 清洗。
- 不迁移、不读取旧 deepseek-agents 的凭据或配置；不自动接管旧 MCP。

## 5. 隔离环境与配置

沙箱外层环境（启动 `codex sandbox`）：仅 PATH/SystemRoot/ComSpec/Pathext/ProgramFiles/ProgramData/OS/Processor_Architecture 及真实用户 Profile 变量（沙箱初始化需要），`CODEX_HOME` 指向任务隔离目录（`<home>/engine/<id>/home`），另有 `DOTNET_ROOT`（可用时）。

沙箱内层环境（launch.json，由 `--launch` 应用）：HOME/USERPROFILE/APPDATA/LOCALAPPDATA/TEMP/TMP/XDG_* 全部重定向到 `<job>/runtime` 子目录；设置 `OPENCODE_DISABLE_PROJECT_CONFIG`、`OPENCODE_DISABLE_DEFAULT_PLUGINS`、`OPENCODE_DISABLE_EXTERNAL_SKILLS`、`OPENCODE_DISABLE_LSP_DOWNLOAD`、`OPENCODE_DISABLE_AUTOUPDATE`、`OPENCODE_DISABLE_MODELS_FETCH`、`OPENCODE_DISABLE_CLAUDE_CODE`、`OPENCODE_EXPERIMENTAL_OUTPUT_TOKEN_MAX=131072`、`BUN_INSPECT_PRELOAD`（pipe 适配）、`OPENCODE_CONFIG`、`OPENCODE_GIT_BASH_PATH`、`TERM=dumb`、`NO_COLOR=1`。

生成的 `opencode.json` 固定：`enabled_providers=["opencode-go"]`、`model`/`small_model=opencode-go/deepseek-v4.1-flash`、`variant=max`、`options.reasoningEffort=max`、`plugin=[]`、`mcp={}`、`skills` 空、`share=disabled`、`autoupdate=false`、`subagent_depth=0`、`lsp=false`、`formatter=false`、`snapshot=false`；唯一 provider 指向回环 Relay，白名单只含固定模型。

角色的 OpenCode 自身权限配置与 OS 映射共同生效，且提示词无法改变：见下表。

## 6. 权限矩阵（Windows）

| 角色 | 项目目录 | OpenCode `edit` | 其他可写位置 | 说明 |
| --- | --- | --- | --- | --- |
| default | write | allow | `<job>/runtime` | |
| worker | write | allow | `<job>/runtime` | |
| explorer | read | deny | `<job>/runtime` | 提示词要求写入也不能突破 |
| reviewer | read | deny | `<job>/runtime` | |
| verifier | read | deny | `<job>/runtime`、`<job>/runtime/check-artifacts` | 产物目录固定，终态后保留到 `<home>/artifacts/<taskId>`（有界）；提示词说明 `dotnet build --artifacts-path` / `dotnet test --results-directory` |

`permissions.agentdock.filesystem` 映射：`:root=read`、runtime=write、项目按角色 read/write、沙箱 home 与全部固定可执行文件目录 read（OpenCode、Codex、codex-path、shell、dotnet）。config.toml 的 profile 名为 `agentdock`。

## 7. 事件契约（`EngineEvent`）

| Kind | Text | 其他字段 | 说明 |
| --- | --- | --- | --- |
| `text` | 有界模型文本（≤48 000 字符 + `…`） | — | 完成态文本 part；`Result` 取最后一条非推理文本 |
| `reasoning` | 有界推理文本 | — | |
| `tool` | `"<tool> started/completed/failed: <detail>[ | <有界输出>]"` | — | 工具执行可见性；重复状态不重发 |
| `usage` | 空 | `UsageId`/`InputTokens`/`OutputTokens`/`GenerationSeconds` | 每次有有效分量的 `step_finish` 一条 |
| `error` | 有界终端错误 | — | 出现即禁止成功 |

`usage` 语义（与 `opencode_events.py` 适配语义一致）：

- 每个 `step_finish` part 只输出该 part 自身的贡献（不是会话累计）；`UsageId` 为原生 part ID（缺失时不计入，避免无法去重的数字冒充可靠用量）。
- `InputTokens = input + cache.read + cache.write`，`OutputTokens = output + reasoning`；分量仅在原生报告时计入，从未报告保持 null（不伪装为 0）；非法值（负数、布尔、NaN）忽略。
- 宿主（Core `TaskService`）按 `UsageId` 只保留最新贡献并求和：重放同一 `UsageId` 幂等，修订版本覆盖而非累加，多个 part 之间不会重复累计（`UsageIntegrationTests` 覆盖两轮总量与重放/修订）。
- `GenerationSeconds`：Relay 解析上游 SSE JSON（容忍空白、跨 chunk 分块与 CRLF），从首个非空输出/推理增量计时，到之后到达的携带 `usage` 的结束块（缺省为 `[DONE]`）为止；工具调用增量不启动窗口，排队与工具执行不在观测流内。同一分块同时携带首个增量与 usage 时窗口无法可靠闭合，返回 null 而不是伪造零时长；单调时钟，仅在该轮有完成信号时发布，否则 null。宿主用 `输出 Token / GenerationSeconds` 得生成速度。

成功门：`CanComplete = exitCode == 0 && 最后 step_finish.reason == "stop" && 无终端 error`。长度截断（`length`/`max_tokens`）、EOF、非零退出、错误事件一律不成功。

## 8. 执行结果、取消与超时

`ExecutionResult`：

- `Success`：唯一成功门。
- `Result`：最后非推理文本（有界），经凭据与能力令牌清洗后返回；当存在变更时追加一行归因免责声明（见 §9）。失败与取消时也返回已有部分文本。
- `Error`：失败原因（终端错误 / 沙箱 stderr 尾部 / 退出码 / "Timed out after N seconds; file changes were retained." / "Cancelled by the caller; file changes were retained."），≤8 000 字符，已清洗凭据与能力令牌。
- `Changes`：见 §9；取消、失败、超时同样返回已观测变更。

超时来自 `RoleConfiguration.TimeoutSeconds`（≤0 表示不额外限时）；超时与宿主取消都会触发作业终止，不自动回滚文件修改，也不自动重跑。

## 9. 变更观测

- 任务开始前与结束后对 `Project` 做有界 SHA-256 全树快照：固定忽略 `.git/node_modules/bin/obj/.venv/target/dist/build/__pycache__` 等生成与依赖目录；上限 20 000 文件、单文件 64 MiB、总计 1 GiB。重解析点（junction/symlink）在递归**之前**跳过：项目根之外的文件不会进入哈希集合，目录环也不会发生；枚举与属性读取异常按跳过处理。
- `FileChange.Path` 为项目相对路径（`/` 分隔）；`BeforeSha256` 为 null 表示新建，`AfterSha256` 为 null 表示删除。
- 该观测不等于归属证明：并发进程可能同时修改项目。因此当变更非空时 `Result` 追加固定声明：“[AgentDock] Observed N changed project file(s) … This is an observation, not attribution …”，避免 Codex 误读为唯一来源。
- 引擎不自动创建 worktree、不回滚、不清理项目内产物（产物应写 check-artifacts）。

## 10. 进程与资源

- `JobProcess`：`CreateProcessW(CREATE_SUSPENDED)` → 加入 `KILL_ON_JOB_CLOSE` 作业对象 → `ResumeThread`；仅通过 `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` 继承 stdin/stdout/stderr 三个句柄；创建/分配失败直接抛错（无无沙箱回退）。
- 取消/超时：`TerminateJobObject` 终止整树（含工具启动的孙进程）；作业句柄关闭或宿主进程崩溃同样触发 kill-on-close。
- stdout 按行有界读取（单行上限 4 MiB，超长行跳过）；stderr 只保留尾部 8 000 字符用于错误诊断。任务结束删除 job 目录（尽力而为；被占用或 ACL 阻止时保留）。
- 沙箱内启动器不使用 `Assembly.Location`（single-file 发布为空、库引用时指向错误目录），按顺序解析：显式覆盖 → 宿主基目录旁 `../engine/AgentDock.Engine.exe`（打包布局）→ 同目录 `AgentDock.Engine.exe`（独立运行）→ 开发布局 `dotnet + AgentDock.Engine.dll`；全部缺失时以 `engine-runner-missing` 失败关闭，不再有 IL3000 发布警告。
- 删除 job 目录前，将非空且 ≤64 MiB 的 `runtime/check-artifacts` 移动到 `<home>/artifacts/<taskId>`（仅保留该检查产物子树，不保留沙箱配置、launch 文件等其他内容）；保留总量封顶 50 个任务目录 / 总计 256 MiB，超出时从最旧目录开始清理。Host 的自动及手动正文清理同时删除对应产物目录；含重解析点的产物树不予保留。

## 11. 回环 Relay 边界

- 绑定 `127.0.0.1` 随机端口，路由仅 `POST /v1/chat/completions`；其余路径 403。
- 校验：Host 必须为 `127.0.0.1:<port>`、`Authorization: Bearer <32 字节随机能力令牌>`（常量时间比较）、请求体 ≤12 MiB、`model` 必须等于 `deepseek-v4.1-flash`。
- 上游固定 `https://opencode.ai/zen/go/v1/chat/completions`；禁止重定向（3xx → 502）；连接失败 → 502；不透传用户凭据，只注入真实键并透传 `x-opencode-*` 会话头。
- 请求体、头与凭据不记录日志；响应逐块转发（先计时解析后转发，供 step_finish 关联；无法可靠关联时保留 null）。

## 12. 平台状态

| 平台 | 状态 |
| --- | --- |
| Windows x64 | 已在普通 Windows 会话执行真实 OS 权限、写入、取消和 verifier 构建探针 |
| macOS | fail closed：引擎 `platform-unsupported`、凭据 `credential-platform-unsupported`；未经真实设备验证不启用 |

## 13. 验证记录（2026-10-16，Windows，.NET SDK 10.0.401）

固定工具版本：OpenCode 1.18.30、Codex sandbox 0.153.4、MinGit 2.56.0.2。包内程序逐文件校验，工具版本由引擎预检。

最终全量回归：Engine 89 项、Core/Host/Desktop 24 项，共 113 项通过，0 失败、0 跳过。真实 OpenCode CLI 连接本地脚本化模型，验证工具落盘、usage 去重/修订、凭据回显脱敏、取消保留改动与子进程释放。此证据不代表真实 OpenCode Go 账户验收。

安装包布局的五项真实 Windows 沙箱测试通过：explorer 不能写项目或相邻目录；runtime 可写且无继承密钥；verifier 将检查产物重定向到专属目录；只读项目实际构建并执行；writer 文件哈希观察与取消进程树。沙箱内 single-file runner 使用专属 TEMP 与 DOTNET_BUNDLE_EXTRACT_BASE_DIR，避免向受保护系统临时目录解包。

```powershell
$env:AGENTDOCK_ENGINE_SANDBOX_TESTS='1'
$env:AGENTDOCK_TEST_OPENCODE='<安装目录>/engines/opencode/opencode.exe'
$env:AGENTDOCK_TEST_SANDBOX='<安装目录>/engines/codex/bin/codex.exe'
$env:AGENTDOCK_TEST_SHELL='<安装目录>/engines/git/usr/bin/sh.exe'
$env:AGENTDOCK_TEST_RUNNER='<安装目录>/engine/AgentDock.Engine.exe'
dotnet test AgentDock.slnx -m:1 --disable-build-servers --logger trx
```

操作系统测试须在普通 Windows 会话运行；嵌套受限令牌的 CreateRestrictedToken 失败不是无沙箱回退理由。项目测试需要 SDK；最终用户执行一般代理任务不需要 SDK，但用户要求编译的项目仍需其自己的构建工具链。

审查修复覆盖：未授权和失败的 Relay 请求不推进生成阶段；删除的项目目录不被进程启动器重建；启动持有 Host 锁时先回收崩溃残留的 job scratch，保留受容量约束的检查证据；清理预览带修订号，确认与执行期间范围变化则要求重新预览。

## 14. 外部验收与能力边界

1. **真实账户与字段**：用户已在主面板独立配置 OpenCode Go 凭据，五角色真实最小任务、实际 input/output 和可观测生成速度均通过（见 acceptance.md）。未读取或迁移旧 MCP 凭据；上游分量与账单逐项对照、复杂 compaction 的生成归属仍需更多样本。
2. **macOS**：执行与凭据后端未实现并保持 fail closed。需真实 Mac 上完成权限、进程清理、Keychain、桌面与发行验证（#12）。
3. **生成归属**：按连续成功模型响应关联 usage；请求序列跳跃或时段无法观测时返回 null，不用任务总耗时填补。
4. **检查产物**：宿主正文清理同时移除对应产物；另设单任务 64 MiB、全局 50 目录/256 MiB 上限，超限可提前清理。被占用的路径按尽力而为处理。
5. **凭据边界**：DPAPI CurrentUser 不构成对恶意同用户代码的读隔离（§4）；受限任务不能使用后台管理管道。此边界必须保留，不能表述为全面保密沙箱。
6. **资源**：UI/Host 空闲指标与执行引擎运行负载分别统计；尚无真实账户四任务并发资源样本。
## 15. 与 #3 验收标准的对应

| #3 验收点 | 落地 |
| --- | --- |
| 执行适配契约（输入、事件、结果、错误、停止、清理） | §3–§10 |
| 提示词不能突破只读；verifier 产物边界；Windows/macOS 状态 | §6、§12；集成测试 explorer 拒绝写入 |
| 用量不重复累计、未知保持未知、输出含推理、速度排除排队/工具 | §7；`OpenCodeEventAdapterTests`、`UsageIntegrationTests`、`GenerationTimerTests`、`GoRelayTests.GenerationTiming…` |
| 无生成时段时返回不可用 | `GenerationSeconds = null`（§7） |
| 仅测试项目与脱敏证据、无凭据输出、不改旧 MCP | 全部测试使用假键与假模型；`ExecutionSetupTests`、`CredentialVaultTests` 断言无明文键 |
| 角色操作矩阵与 verifier 产物位置、存在执行层约束点 | §6 矩阵；Windows OS 权限已实测，macOS 仍未通过 |
