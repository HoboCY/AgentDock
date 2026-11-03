# 首次配置引导（2026-10-18）

需求与跟踪：[Issue #13](https://github.com/HoboCY/AgentDock/issues/13)。沿用 Avalonia 11.3.22 / ShadUI 0.1.6，没有更换 UI 框架或增加依赖。

## 启动任务授权（2026-11-03 / Issue #17）

跟踪：[Issue #17](https://github.com/HoboCY/AgentDock/issues/17)。用户报告后台已连通，但 Codex 在调用 `start_task` 前要求明确授权，Host 因此没有收到新任务。此前引导只注册插件、写入委派规则，未处理工具审批设置。

- 第三步提供「接入并授权」；已经注册的安装显示「授权派发并继续」。授权前说明外部模型数据发送、API 费用及实现类任务的项目写入能力。
- 可选择「仅接入，保留审批」；启动、注册、刷新和规则升级不会自动授权。授权后可在该步骤「恢复逐次询问」。连接状态分别显示已接入与已授权；完成引导时会提示尚未预授权的情况。
- 复用已发现/手动选择的 Codex CLI，通过 stdio app-server 的 `config/read`、带 `expectedVersion` 的 `config/batchWrite` 和再次回读，只修改 `plugins."agentdock@agentdock-local".mcp_servers.agentdock.tools.start_task.approval_mode`（`approve` / `prompt`）。保留其他工具、其他插件、全局审批、主模型、认证和配置注释。契约见 [官方插件工具审批配置](https://developers.openai.com/plugins/build/plugins#configure-installed-server-policy)。
- 工具禁用/允许列表保持原样；配置写入被覆盖、读取失败、取消或超时不会报告授权成功。界面不显示原始配置诊断，防止其中的私密配置值泄露。用户和组织/项目策略仍适用，保存后需重启 Codex、新建对话；本地回读不代表已有聊天已加载新设置。

验证使用 Codex CLI `0.166.0-alpha.5.1` 和独立 `CODEX_HOME`。解决方案构建 0 警告、0 错误；聚焦测试 91 通过、4 项 macOS 专用测试跳过。覆盖实际 Codex 配置授权/撤销、无授权的读取不修改文件、禁用工具不被重新开启、取消与损坏配置，以及既有接入/委派规则测试。

桌面回归先因缺少明确授权入口失败，修复后源码与安装构件均通过 26 项检查，包含先保留审批、随后明确授权、重开引导撤销、窄窗口及窗口/Host 生命周期。探针确认日常 Codex 配置哈希保持不变。测试使用合成凭据和已落盘的暂停/终态任务，没有派发真实模型任务。

Windows 测试包 `0.1.1-test.20261103.1` 已完整构建并在仓库内独立目录安装，安装器 SHA-256 和 Host 版本匹配。首轮离线构建未找到锁定的 OpenCode 本机路径；随后仅复制既有安装内与锁文件哈希/大小一致的公共运行时重新构建成功。未替换日常安装；包未签名、`releaseReady=false`，不代表 macOS 或真实模型账户验收通过。初次沙箱内 VSTest 因 testhost 连接超时中止；上述聚焦测试在正常本机权限下使用隔离配置完成。

证据：`artifacts/validation/approval-onboarding-20261103/` 下 `tests/approval-focused-final.trx`、`ui-red/desktop.json`、`ui-final/result.json`、`ui-installed/result.json`；构件在 `artifacts/package/0.1.1-test.20261103.1/win-x64/`。

## 规则加载 hook（2026-11-03 / Issue #18）

跟踪：[Issue #18](https://github.com/HoboCY/AgentDock/issues/18)。插件携带一个 `SessionStart` hook，覆盖 startup/resume/clear/compact/fork。Windows 使用系统 PowerShell，macOS 使用系统 sh；读取生效全局入口与两份用户维护的配套文档，不调用模型、派发任务、改写审批配置或自动保存信任。关闭入口后保留的文档不会继续被加载；缺失/空白文档只提示处理，不注入部分规则。

首次通过 Codex「设置 → Hooks」或 CLI `/hooks` 审阅并信任。真实 Codex CLI `0.166.0-alpha.5.1` 在隔离配置中的验证结果为：首次 untrusted，使用 `/hooks` 明确信任单个测试 hook 后为 trusted，重开独立进程仍为 trusted；仅改变插件版本号并重新安装即变为 modified。当前 Codex 校验插件包内容，固定脚本路径也不能保证跨版本免确认。引导已说明这一限制，未规避信任检查。

同时修复旧插件缓存：引导第 3 步即使已接入也重新运行官方插件注册流程。新增桌面回归先复现未刷新失败，修复后通过，确认新版本 hook 已进入 Codex 插件缓存。

验证：解决方案构建 0 警告/0 错误；hook、引导和任务授权关联回归 71 通过、2 项 macOS 专用测试跳过；其中 7 项 hook 测试运行了 PowerShell 和 Windows 上的 Git sh，覆盖启停、用户规则编辑、覆盖文件优先级、含空格/中文路径、UTF-8/16/32 BOM、缺失/空白文档及重复入口。源码与独立安装构件的桌面回归均为 27 项通过，安装后的 hook 文件与源码一致，日常 Codex 配置哈希保持不变。真实 Codex `SessionStart` 已执行测试 hook 并以 context 返回两份合成规则；使用临时空目录与本机假传输，不连接外部模型服务，不计为真实模型委派验收。macOS 脚本未在 Mac 实机运行。

证据：`artifacts/validation/hook-onboarding-20261103/` 中 `tests/hook-onboarding-final.trx`、`codex-trust-persistence.json`、`codex-hook-execution.json`、`ui-upgrade-red/desktop.json`、`ui-upgrade-final/result.json`、`ui-installed-final/result.json`、`installed-final.json`。Windows 测试包为 `0.1.1-test.20261103.2`，未签名且未公开发布。

## 行为

- 新数据目录自动进入「开始使用」，按后台、凭据、Codex 插件、全局委派四步操作；识别已有配置，允许稍后配置及从设置重开。完成记录保存在数据目录，升级不会重置它。
- 保存凭据后输入清空，设置页立即显示已配置，其他未保存的设置保留。界面区分本机已保存与实际任务验证；不会为验证而自动提交收费模型任务。
- 插件接入使用 Codex 自身的 `plugin marketplace add` / `plugin add`，回读 `plugin list --json`，检查插件身份、安装状态、启用状态和来源。Windows 支持桌面版自带 Codex 程序发现；也可手动选择程序。macOS 保留 PATH 与应用常见位置发现，尚未实机验收。
- 全局路由首次由点击启用写入生效的 AGENTS 文件；已启用且恢复记录匹配的旧托管区块会在主面板启动或重新检查时升级。保留原有区块备份、原编码和换行，重复执行无重复内容，升级中断可恢复。关闭只恢复 AgentDock 管理的区块，保留用户追加内容。重复/缺失标记、未知规则、丢失恢复记录及生效路径切换会提示处理，不静默覆盖。
- 配置完成提示重启 Codex 并新建对话。已注册不等于当前聊天已连接；不会修改主模型、认证或子代理 TOML。
- 首次检查复用正在运行的后台轮询，避免初次状态尚未返回时误判为未连接。

## 本地验证

- 解决方案构建：0 警告、0 错误。
- 回归：132 通过、29 跳过、0 失败。包含 21 项新增规则/状态测试；跳过项需要 macOS 或显式沙箱/引擎环境。
- Windows 桌面冒烟：18 项检查通过，包含首次显示、四步窄窗口导航、稍后配置、空凭据校验、凭据保存清空、真实 Codex CLI 注册、完成规则写入、界面关闭规则、设置页标签刷新及未保存输入保留。任务列表、结果页、托盘和窗口生命周期同时检查。
- 全部配置写入发生在隔离目录。界面使用合成凭据和静态任务，不调用模型。真实 Codex 插件注册使用隔离 marketplace 夹具，验证配置流程，不将其计为 MCP 执行或真实模型任务验收。
- 当前账户的两次外部子任务均收到渠道 `api_key_expired` / 401，未重试或换模型。它们只证明派发到达后台，没有形成独立调查或审查结论。

证据：`artifacts/validation/onboarding-tests/`（Engine）与 `artifacts/validation/onboarding-tests-final/`（最终 Desktop/Core/Host）。开发 UI 完整流程在 `artifacts/validation/onboarding-complete-ui/`；其 `result.json` 中 `existingCodexConfigUnchanged=true`，实际界面截图为 `desktop.json.onboarding*.png`。

## Windows 测试包

`artifacts/package/0.1.1-test.20261018.1/win-x64/` 包含配套 EXE 安装程序和 ZIP。完整自包含、无缺失组件，`releaseReady=false`（未签名，当前版本未做干净虚机验收）。包构建时沙箱未能获取 Host 版本；随后在隔离安装目录实际执行 `Host --version`，确认 `0.1.1-test.20261018.1`，没有把该构建警告删掉。

安装器以 `--root artifacts/validation/onboarding-installed --silent --no-shell-integration` 运行，退出码 0，安装指针指向新版本；未写系统快捷方式、卸载入口，也未替换原安装。安装后的程序再次通过全部 18 项界面检查，证据为 `artifacts/validation/onboarding-package-ui-final/result.json`，原 Codex 配置哈希保持不变。

无后台的首次启动另外通过 11 项页面/窗口检查（`artifacts/validation/onboarding-offline/desktop.json`，`hostReachable=false`、任务数 0）。安装程序及 ZIP 的最终 SHA-256 与构件清单一致。

```powershell
dotnet build AgentDock.slnx -m:1 --no-restore
dotnet test AgentDock.slnx -m:1 --no-build
pwsh -NoProfile -File scripts/Probe-Desktop.ps1 -SeedTasks -Onboarding -Output artifacts/validation/onboarding-new-run
```

引导探针需要可发现的 Codex CLI，输出目录须为新目录。测试不替代 macOS 实机、干净系统安装或真实凭据验收；没有替换用户正在运行的安装版。

## 派发信息与旧规则升级（Issue #14）

跟踪：[Issue #14](https://github.com/HoboCY/AgentDock/issues/14)。首次引导现在写入 managed-v2 规则，要求从 `status.executionProfile` 读取外部代理的提供方、模型和推理强度。后台与生成的执行配置及命令参数共用固定值，旧后台缺少该字段时反序列化为 null，避免套用原生 Codex TOML 或凭主模型猜测。

主面板初始化和引导重新检查调用规则升级；仅识别完整的 managed-v1、匹配的生效路径及恢复记录。升级前记录新旧区块，中断后可继续；保留升级前的原始区块及追加边界，使关闭仍能恢复最初内容。未启用、手动修改、缺失/损坏记录或生效文件切换均不自动改写。

本地验证：解决方案构建 0 警告、0 错误；完整回归 149 通过、29 跳过、0 失败。随后补充损坏恢复记录检查，最终规则专项 34 通过；完整回归中的其余结果仍适用。沙箱内首次规则测试因 testhost 本地连接超时而中止，在正常权限下的隔离目录复验通过。实际桌面引导 19 项检查通过，包含规则写入、实时后台执行配置、关闭恢复以及原 Codex 配置哈希不变。

证据：`artifacts/validation/dispatch-config-tests/routing-final.trx`、`artifacts/validation/dispatch-config-full/`、`artifacts/validation/dispatch-config-ui/result.json`。后台和引擎验证不发送模型请求。测试包版本为 `0.1.1-test.20261018.2`；安装与交付结果以对应构件清单及验证目录为准，未替换用户当前安装版。

测试包构建完整、无缺失组件，Host 实测版本正确，EXE/ZIP 哈希与构件清单一致。隔离安装后的 19 项界面检查再次通过（`dispatch-config-package-ui/result.json`）。另用安装版启动预置 managed-v1 的隔离目录，确认自动升级、用户前后文保留以及恢复记录完成（`dispatch-config-startup-upgrade/upgrade-result.json`）。构件仍为未签名本地测试包，未做该版本的干净虚机或 macOS 验收。

执行配置元数据由 AgentDock worker 任务 `4b2da9d58b1b425bad10c5bc9ab11a8a` 修改。其引擎专项 11 项通过，但后续测试收尾长时间未返回；主代理已完成代码检查、完整回归及安装版验证后结束该次派发，未重试。该任务不计为独立审查成功。
