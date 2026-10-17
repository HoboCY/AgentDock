# 干净 Windows Hyper-V 验收（2026-10-16）

对应 [#10](https://github.com/HoboCY/AgentDock/issues/10) 和 [#11](https://github.com/HoboCY/AgentDock/issues/11)。本轮已通过干净系统的普通用户安装、桌面冒烟、修复/卸载/重装、16 项安装门禁、真实 Codex 插件状态调用，以及虚拟机内独立 Go 凭据和一次真实模型最小任务。人工交互、长期资源预算与发行签名等门槛仍未完成，不能据此关闭完整验收或声明发行就绪。

## 环境与构件

- Hyper-V 服务 `vmms`、`vmcompute` 均为 Running；专用 VM：`AgentDock-CleanWin11-20261016`，Generation 2、Secure Boot、TPM、4 vCPU、6 GiB 固定内存、80 GiB 动态 VHDX。
- 系统来自用户提供的 Windows 11 25H2 中文消费版 ISO，选择 index 4 专业版，映像版本 `10.0.26200.9445`。ISO SHA-256：`9dc9cbfcc3bd3035e7396eb4a24241ae53e21597e8f86d2f4bf73b4b589a0f2d`。这是本次输入文件的指纹，没有声称与微软公布的散列比对通过。
- 在新 VHDX 应用系统映像后启动 OOBE；独立管理账户用于 VM 供给，`DockTest` 普通用户在交互桌面运行产品和验收。宿主机配置及凭据不复制到 VM。
- 产品为已有本地候选 `artifacts/release-candidate/final-ui-20261016/win-x64/`，源码基线 `6951f5b`；本轮没有修改产品代码或重打包。

| 构件 | SHA-256 |
| --- | --- |
| Setup | `cbf6eb1462f5aa482780025c5b8318abfa7c6701ad347087c14503b409158ad9` |
| payload ZIP | `3382f03a6c184c64027b58a9dd3c40f4e6cbef636087c88fcd87cbd8f1abf5b1` |

## 先验证干净系统，再加入测试工具

首轮使用系统自带 Windows PowerShell `5.1.26100.9444`。在产品安装前记录：账户不属于 Administrators、进程未提权；没有既有 AgentDock 和 Codex 配置；未安装共享 .NET 或 SDK；`dotnet`、`node`、`npm`、`git`、`pwsh`、`cl` 均不可发现。`python`/`python3` 仅为 WindowsApps 的商店执行别名，没有将别名当成已安装 Python。

系统初始断网。以下 8 项于北京时间 19:03:07–19:04:50 通过：

| 检查 | 结果 |
| --- | --- |
| 干净系统、普通用户基线 | 通过 |
| Setup / payload 哈希与候选一致 | 通过 |
| 默认位置安装及 Shell 集成 | 通过；稳定启动器、开始菜单、HKCU 卸载入口存在；登录自启默认关闭；内置 Git 生效；没有创建 Codex 配置 |
| 空任务桌面及 60 秒空闲采样 | 通过；主面板/悬浮窗显示、隐藏、重开，托盘创建；桌面退出后原 Host 存活 |
| 带合成终态任务的桌面 | 通过；任务列表显示与回收路径不崩溃 |
| 同版本修复及旧 MCP 共存 | 通过；使用无密钥的合成 `deepseek_agents` 配置，修复前后文件哈希一致 |
| 卸载与数据保留 | 通过；程序和插件源、快捷方式、卸载入口移除，合成用户数据哨兵保留 |
| 重新安装 | 通过；用户数据仍保留，供后续独立账户验证 |

首轮完成后，才向 VM 的单独测试工具目录复制公共 PowerShell `7.6.6` 和 Codex CLI `0.162.0-alpha.17.2` 程序文件、仓库验收脚本与运行时锁清单。没有复制宿主的 Codex 配置、登录态、MCP 凭据或任务记录。它们是后续自动化检查工具，首轮产品安装和启动并不依赖这些文件。

现有 `Test-SetupGates.ps1` 配合实际 Setup、payload、Host 和独立 WorkDir：**16/16 通过**。包括未完成任务阻止更新、活动锁保护、陈旧锁恢复、损坏状态拒绝、内置 Git 哈希、在线空闲 Host 停机、跨版本合成事务与损坏更新保护。

现有 `Probe-Plugin.ps1` 使用全新的隔离 `CODEX_HOME`，执行实际 marketplace 注册、插件安装、app-server 握手和 MCP `status`：**通过**，版本为 `0.1.0`。没有模型调用；这证明插件机制可用，不代表用户的日常 Codex 配置已接入，也不代表 Go 账户可用。

## 独立凭据与真实最小任务

用户随后在 VM 主面板独立输入 Go 凭据。首次保存复现「后台未运行或响应超时」：普通用户会话中仅有 Desktop，没有 Host，管理管道连接超时。通过界面「启动后台」后，Host 可连接；重试「保存凭据」，回读 `credentialConfigured=true`。安装指南已补充这个操作前置条件；未修改产品代码，后台离线时保存仍不会自动启动后台。

北京时间 19:30:41–19:31:02，经真实 Codex app-server → 安装版 MCP → Host → OpenCode Go → `deepseek-v4.1-flash`，一个 `explorer` 只读任务完成：读取全新合成项目的 `input.txt`（41），返回 `ROLE_OK explorer 42`。输入文件未变，目录内没有额外文件；相同 `request_id` 重试复用同一 Completed 任务。实际输入 8,704 Token、输出 308 Token（含推理）；任务耗时约 20.93 秒。完成后未发现遗留 Engine、OpenCode 或 Codex 执行进程。

该探针为仓库 `Probe-Plugin.ps1` 的本地副本，仅将五角色循环限制为 `explorer`。本次证明干净 VM 内独立凭据及真实最小任务可用，不扩展为此 6 GiB VM 的五角色或四并发负载通过。宿主凭据未复制，证据只记录配置布尔值和合成任务结果。

VMConnect 增强会话登录曾因 `DockTest` 不具备远程登录权限而拒绝；仅在测试 VM 内将其加入 Remote Desktop Users 后登录成功。该账户仍不属于 Administrators；修改发生在首轮干净基线验收之后。

## 空闲资源样本

| 指标 | 本轮观测 |
| --- | ---: |
| Host 工作集 | 43.79 MiB |
| Desktop 空闲工作集 | 126.59 MiB |
| Host CPU（按 4 vCPU 归一） | 0.267% |
| Desktop CPU（按 4 vCPU 归一） | 0.905% |
| 合计 CPU（按 4 vCPU 归一） | 1.172% |
| Host 可连接 | 3,063 ms |
| 主面板 Opened | 830.6 ms |

这是新安装 VM 的首次桌面 60 秒样本，没有清空文件缓存，不称为真正冷启动。工作集低于既有参考目标；CPU 合计高于暂定的 1% 数值。原参考样本使用 24 逻辑处理器，当前为 4 vCPU，不能把归一化比例直接当成相同机器的回归结论，也未据此宣称 CPU 性能门槛通过。长期空闲及低内存并发预算仍需单独定义和测量。

## 证据与剩余门槛

原始本地证据保存在 `artifacts/validation/hyperv-20261016/`，不进入 Git：

- `hyperv-inventory.json`、`windows-image.json`、`provision-state.json`：环境与供给记录。
- `guest-evidence/baseline.json`、`probe-finished.json`：干净基线及首轮 8 项结果。
- `guest-evidence/desktop-{empty,seeded}/result.json` 和 `desktop.json.{main,overlay}.png`：实际程序渲染与资源样本。
- `guest-evidence/coexistence.json`：合成旧配置前后哈希。
- `guest-evidence/setup-gates/results.json`、`setup-gates.log`：16 项安装门禁。
- `guest-evidence/plugin.json`、`secondary-finished.json`：真实 Codex 插件调用与第二阶段结果。
- `backend-inspect.json`、`backend-starthost.json`、`backend-savecredential.json`：后台未启动、界面启动及独立凭据保存成功的状态证据。
- `guest-evidence/plugin-live-minimal.json`、`live-minimal-state.json`：真实最小任务、用量、文件不变及请求幂等性；对应本地探针 `Probe-Plugin-Minimal.ps1`。
- `enhanced-login-permission.json`：测试 VM 增强会话登录权限及非管理员身份。
- `Run-CleanWindows.ps1` 是本轮兼容 5.1 的探针；合成持久化 JSON 明确使用无 BOM UTF-8。第二阶段使用仓库原有脚本。

供给阶段发生的宿主 BCDBoot 存储冲突单独保留日志；最终在本 VM 的 Windows 安装介质 WinPE 内完成引导初始化，返回码 0。该问题发生在产品安装前，不记作 AgentDock 缺陷。宿主 BCD 没有被重建。

以上是首轮验收的截止范围。后续登录自启、通知、完全退出确认、真实五角色/停止/超时、重启首次启动，以及发现的可访问性和通信问题，见 [Windows 扩展验收](windows-full.md)。Windows 发行签名及真实下载后的 SmartScreen 路径仍未通过；复制本地文件到 VM 没有覆盖下载标记。Mac 签名/公证/Gatekeeper 与公开发布不在本轮范围，候选 `releaseReady=false` 保持不变。
