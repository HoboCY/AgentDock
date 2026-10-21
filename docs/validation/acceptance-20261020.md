# 2026-10-20 ticket 验收复核

随后用户授权复制当前源码，紧凑窗口的完整 Mac 安装包验收及新发现的路径修复见 [后续记录](mac-compact-20261020.md)。以下保留本日较早一轮的版本与证据边界。

本轮按用户要求核对可结项任务，并启动剩余验收。Mac 使用机器上已有、干净的 `ab5fd4225f0c626fefc069275033fb3c234e8a5a` 副本；Codex 发现实现、对应测试及 OpenCodeEngine 的 SHA-256 与 Windows 当前文件一致。在 Mac 本机创建独立测试目录，没有替换已有安装或修改用户 Codex 配置。

Windows 尚未提交的紧凑窗口改动不包含在本轮 Mac 结果中。其 Windows 验证仍见 [shadui.md](shadui.md)。

## #16：Codex 发现修复

- MacBookPro18,3 / macOS 26.6.2 / arm64；复用既有隔离 SDK 10.0.401。
- Core/Host/Desktop 关联测试：94 通过、5 项 Windows 专属跳过、0 失败。其中 24 项 Codex 发现测试全部通过，包括真正运行 macOS npm 风格符号链接与同目录 Node 的用例。
- 实机 `/Applications/ChatGPT.app` 存在，两个约定位置均无 bundled `codex`；可用 CLI 位于 `~/.nvm/versions/node/v25.0.0/bin/codex`。
- 在显式补入同目录 Node 的子进程 PATH 下，实际 CLI `0.159.2` 的版本检查和插件列表成功。AgentDock 插件已安装、启用并指向当前安装源；检查前后用户 `config.toml` 的 SHA-256 相同。没有重新注册或修改认证。
- 修复实现与自动化回归可以结项。原生文件选择器实际操作、新安装包的引导及重启桌面 App 后新对话调用，继续作为 #12 的明确未完成项；本轮没有生成或安装新 Mac 包。

## #12：当前引擎实机回归

- 当前提交 Engine 测试：88 通过、20 项 Windows 专属或未启用条件测试跳过、0 失败。
- 其中真实 Mac 执行测试 15 项、Keychain 测试 6 项全部通过，覆盖五角色写权限、取消、超时、脱离子进程和宿主退出后的清理。使用固定 OpenCode/Codex sandbox 与合成模型回复，无真实模型调用或用户密钥读取。
- 基础回归合计 182 通过、25 跳过，不将跳过计为通过。
- 额外复用真实 Mac 执行用例，在同一进程内并发运行 default/worker/explorer/verifier 四项引擎任务，使用合成模型回复：4 项完成，48 个心跳样本，其中 46 个样本四项均未完成，最大间隔 322.48 ms。该探针验证当前引擎并发与响应性，不等同于安装版 Host/MCP 全链路负载；后者仍列为待验收。
- 人工托盘菜单、通知、缩放、屏幕阅读器，以及原生文件选择器和新对话接入仍待验证。签名身份查询为 0；Developer ID、公证、真实下载后的 Gatekeeper 与干净系统发行路径未完成，`releaseReady=false`。

## #10：安装失败诊断

- 用既有完整 Windows 候选 `0.1.1-acceptance.20261017.1` 的 Setup，在全新隔离目录运行合成 payload 静默门禁：14 通过、0 失败。本轮没有重跑真实 payload 校验及在线 Host 停机两项，不替代此前 16 项完整安装验证。
- 单独重现损坏更新：退出码 1，stderr 明确报告 `payload 文件体积不符：host/AgentDock.Host.exe`；旧版本指针与合成配置哈希保持不变。
- **发现未满足项：** `install/setup.log` 没有保存这条失败原因。程序异常只写 stderr，GUI 错误只显示在窗口中，关闭后缺少持久诊断。已有门禁不检查失败原因落盘。
- 2026-10-17 首次失败的原始 stderr 没有保存，原因仍未知；本次损坏更新复现不能用于倒推该历史失败。
- GitHub Release 列表为空；实际分发、签名和真实下载后的 SmartScreen 路径未完成。本票保持打开。

## #4、#9 与父地图

- #4：Hyper-V 查询被当前 Windows 权限拒绝；未启动 VMConnect、未提权弹窗、未重启宿主或清理宿主缓存。严格冷缓存启动缺口仍保留，客体重启首次启动样本不升级为严格冷缓存结果。
- #9：已有 Windows 交互证据保留；Mac 人工交互与可访问性门槛仍待完成。
- #1：保留开放；源码修复完成不等于签名、下载和跨平台发行完成。

GitHub 已回写并回读确认：#16 以 completed 关闭，#10 为 ready-for-agent，#4/#9/#12 为 ready-for-human，父 #1 继续开放；共 5 个开放 tickets。未提交、推送或公开发布本轮修改。

本地原始证据：`artifacts/validation/acceptance-20261020/` 下的 `core-host.trx`、`engine-mac.trx`、`mac-cli.json`、`concurrency.json`、`setup-gates/results.json` 和 `setup-failure.json`。Mac 原始目录为 `~/Projects/AgentDock-acceptance-20261020/`。初始 Windows 源码归档没有上传；其 `source-manifest.json` 不是本轮 Mac 测试来源清单。

## #10 后续：安装失败诊断已修复并复验

Windows CLI 的安装/更新异常、Windows 图形安装入口和 Mac 安装入口，现在复用 `InstallLayout.Log` 保存操作与异常消息到 `install/setup.log`，保留原有 stderr、对话框及失败退出码。未新增依赖，不读取或转存凭据、配置正文。无法解析安装根或非 Mac 隔离保护拒绝时，不写默认安装根。

新增回归先在旧二进制上失败：Windows 12 通过 / 2 日志断言失败；MacSetup 的 Windows 隔离测试 24 通过 / 2 日志断言失败；原生 Mac 旧安装器 13 通过 / 8 日志断言失败。一次中间实现把卸载错误也记录到目标目录，原生 Mac 的“无关目录内容完全不变”用例发现了回归（20 通过 / 1 失败）；最终实现收紧为安装/更新诊断，未扩大卸载、清理或只读操作的写入范围。

最终源码验收：

| 检查 | 结果与范围 |
| --- | --- |
| Windows Setup 自包含 Release 发布 | 成功；版本 `0.1.1-acceptance.20261020.3` |
| Windows 安装门禁 | 14 通过 / 0 失败；本轮未重跑真实 payload 与在线 Host 停机两项 |
| MacSetup 在 Windows 上的隔离逻辑回归 | 26 通过 / 0 失败；Release 构建 0 警告、0 错误 |
| 原生 Mac 安装门禁 | 21 通过 / 0 失败；Mac 原生构建自包含安装器，使用合成 payload 和独立安装根 |

断言覆盖：进入安装门禁前的 partial 拒绝原因、损坏更新的具体 stderr/日志、旧版本指针与合成配置哈希不变、更新标记释放，以及无关目录不变。Mac 同时复验包哈希、路径、权限位、缺失组件和符号链接拒绝，以及稳定启动器/插件回滚。Windows GUI 的日志入口已编译检查，未打开窗口做交互复测，避免打断用户游戏。

本轮只构建安装器测试产物，未重新构建完整产品包、替换用户安装或签名发布。此前完整 Mac 包 `0.1.1-acceptance.20261020.2` 的 UI/插件验收仍见 [Mac 紧凑窗口记录](mac-compact-20261020.md)，不能把两种产物混称为同一个发行候选。2026-10-17 首次退出码 1 的历史原因仍未知。

最终 Mac `Cli.cs` SHA-256：`5EDBAD006735EA97B58B288EB4C874AD3A4C9345D06AC4E34B723F47B6DB566C`，与本地一致。Windows 最终 Setup SHA-256：`5AF12D94431395469159A4DEC83ACD91F423D38E37DFCA85B2748100051A11FC`。

本地证据：`artifacts/validation/setup-diagnostics-20261020/` 中的 `windows-red/results.json`、`windows-final/results.json`、`mac-local/old-run/results.json`、`mac-local/final-run/results.json`、`mac-native-red.json`、`mac-native-intermediate.json`、`mac-native-final.json` 和 `mac-native-summary.json`。Mac 最终原始目录为 `~/Projects/AgentDock-compact-20261020/setup-diagnostics-final/`，构建与运行脚本保留为本地 `run-mac-diagnostics.py`。

#10 的失败诊断与数据保留子项可以完成；GitHub 实际分发、Windows 签名和真实下载后的 SmartScreen 验收仍未完成。#4 的严格冷缓存、#9 的 Mac 原生交互及可访问性、#12 的当前安装版真实负载与发行验收均保持原有边界，父 #1 继续开放。未提交、推送或公开发布。
