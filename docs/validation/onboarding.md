# 首次配置引导（2026-10-18）

需求与跟踪：[Issue #13](https://github.com/HoboCY/AgentDock/issues/13)。沿用 Avalonia 11.3.22 / ShadUI 0.1.6，没有更换 UI 框架或增加依赖。

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
