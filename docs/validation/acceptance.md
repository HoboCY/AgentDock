# AgentDock V1 验收记录

基线：[父任务 #1](https://github.com/HoboCY/AgentDock/issues/1)，2026-10-16 读取的 Q1–Q26 与子任务 #2–#12。执行环境为 Windows 11（内核 10.0.26200）、x64、24 逻辑处理器、.NET SDK 10.0.401 / Runtime 10.0.12。验收任务均使用隔离目录内的合成项目；模型分别标明本地脚本化响应和独立真实 OpenCode Go 账户；既有 MCP 配置和凭据不迁移。

本文件区分源代码实现、本机实测与发布验收。真实 OpenCode CLI + 本地脚本化模型可以验证工具、权限、用量适配和停止链路，不能证明真实 OpenCode Go 账户可用或其当前用量字段完全一致。

Windows 本地实现与自包含包已经完成。2026-10-16 基线的完整回归 114 项通过（Engine 89、Core/Host 25），19 项 Mac 平台用例跳过；安装包通过 16 项安装/修复/升级/卸载检查，以及 Codex app-server 真实插件调用。Standards 与 Spec 独立审查及处理记录见 [review.md](review.md)。两平台均通过独立 Go 账户的五角色真实最小任务、重试幂等与四任务并发采样。真实 Mac 的后端、Keychain、GUI、进程停止、完整安装/升级/卸载和账户链路见 [Mac 记录](macos.md)，其中 Core/Host 28 项、Engine 87 项、安装门禁 21 项通过。

2026-10-16 后续干净 VM 的 Windows 扩展验收和宿主历史崩溃排查见 [windows-full.md](windows-full.md)：登录自启、退出确认、失败通知、五角色负载和真实工具执行中的停止/超时已补测；发现数字输入框无障碍名称缺失，首次负载有一次 MCP 通信失败，不能将 Windows 判为全部通过。

2026-10-17 更新：数字输入框名称、实际键盘/讲述人、显示缩放、停止/三次重连及用户指定的 **12 小时持续运行**已通过。随后修复执行引擎占用共享线程池的问题，完整 Windows 候选 `.20261017.1` 的十轮压力复验通过：50 个任务、797 次严格查询、416 次 MCP 任务调用、312 次桌面心跳均无失败，停止和三次重连通过，本地回归为 121 项不同 Windows 测试通过、19 项 Mac 用例跳过。详见 [压力复验](windows-pressure.md) 和 [其余记录](windows-remaining.md)。12 小时的 2,881 个成功样本属于旧候选 `.20261016.3`；新引擎没有重跑十二小时或 Mac 实机验收，不能扩大为所有发行门槛通过。

## 覆盖矩阵

2026-10-21 策略更新：Windows 代码签名为可选项，不再作为稳定发布必需条件；真实下载/系统提示及安装更新验收继续保留。macOS 签名、公证与 Gatekeeper 要求不变。下方历史记录保留当时结论，当前发布边界以 [发布说明](../releasing.md#当前发行边界) 为准。

| 决策 | 实现与可复现证据 | 尚需的外部验收 |
| --- | --- | --- |
| Q1、Q19 | Setup、自包含打包脚本、固定运行时锁文件、真实 Codex app-server 插件探针；干净 Hyper-V Windows 普通用户安装、插件调用及独立账户真实最小任务已通过，见 [记录](windows-clean-hyperv.md) | 当前候选真实下载后的系统提示与安装路径验收；Windows 签名可选 |
| Q2、Q8、Q14 | Engine 固定 OpenCode Go / deepseek-v4.1-flash；五角色；两平台 OS 写权限与 verifier 专属产物目录实测 | 更多用户项目的工具链兼容 |
| Q3 | 两平台完整本地包及真实五角色；macOS launchd、Keychain、沙箱、GUI、真实版本升级均实测 | macOS 签名、公证和系统分发拦截验收；其他 macOS 版本 |
| Q4、Q6、Q10 | Host 单实例、客户端按需启动、真实桌面显示/隐藏/重开；MCP EOF 不停后台；干净 VM 实际启用自启、重启登录及关闭还原通过；修复线程池阻塞后十轮压力与三次新客户端重连通过 | 更多客户端及长时间持续高并发样本；不把本次机制归因于历史每一次 Unavailable |
| Q5、Q7 | 全局配置、项目筛选、范围用量汇总、悬浮窗活跃任务 | 大量项目的人机可读性复核 |
| Q9、Q16、Q23 | SchedulingAndRetentionTests：跨项目容量、父子目录串行、排队配置快照；真实账户并发 4 已采样；不创建 worktree | 更多项目工具链的负载样本 |
| Q11、Q24 | 可控时间测试覆盖从 FinishedAt 起算的 7/30 天边界；手动清理预览及结果；未完成任务受保护 | 无额外平台特有门槛 |
| Q12、Q13 | 分阶段 usage 去重/修订；缺失字段为 null；SSE 生成计时；单独显示 AcceptedAt 总耗时；干净 VM 的 3 个真实 OpenCode step 五项数值分量与 Host 逐项一致 | 非零 cache write、更多 compaction 形态和提供方账单未由此样本覆盖 |
| Q15、Q21 | 真实进程崩溃恢复、取消、超时；干净 VM 的退出取消/确认及真实 Go 工具执行中的停止/超时通过；子进程退出且文件保留 | 文件哈希观察仍不宣称因果归属 |
| Q17、Q18 | 真实主面板/托盘/悬浮窗、透明度与置顶；通知开/关、六项数字输入名称、实际键盘/讲述人及 100%/150%/200% 主窗口检查通过，详见剩余验收记录 | 最终候选新增 Toast 延迟定位在 150% 复验；最终 200% Toast 位置未单独复验 |
| Q20、Q26 | 旧 MCP 只读提示；探针前后 Codex 配置哈希一致；新数据目录无旧任务/角色/凭据 | 干净机与多配置位置共存抽验 |
| Q22 | 手动版本检查，只提示；安装器拒绝有运行/排队/暂停任务的更新 | 最终发行包的真实更新演练；Windows 不要求签名 |
| Q25 | 独立资源与进程释放探针、两平台真实模型四并发、干净 VM 重启首次启动及 6 GiB 资源样本；旧候选 12 小时持续运行通过，2,881 样本无监测异常；新候选十轮严格通信压力通过 | 新引擎版本未重跑十二小时及 Mac 实机；内存增量的堆归因、更多负载及低内存设备预算 |

## 本机桌面观测

命令：`pwsh -File scripts/Probe-Desktop.ps1`。脚本创建独立数据目录，实际启动后台和桌面，记录窗口显示/隐藏/重开、托盘创建、后台在桌面退出后仍存活，以及原 Codex 配置前后哈希比较。图像是实际运行控件的渲染捕获，不是设计稿。

2026-10-16 13:50 的开发构建样本：后台可连接约 359 ms，主面板 Opened 事件约 1.80 s；后台工作集 41.3 MiB、私有内存 12.7 MiB；桌面空闲工作集 146.8 MiB、私有内存 121.0 MiB。3 秒采样按 24 个逻辑处理器归一的 CPU 分别约 0.19% / 0.22%。此样本包含界面轮询，不含执行任务；短采样不能代表长期稳定上限。

初步预算建议：相同环境下空闲后台工作集目标 80 MiB 以内、桌面 200 MiB 以内、合计 CPU 1% 以内；需以自包含 Release 和至少 60 秒样本确认，再将目标升级为发行门槛。执行引擎和安装包另行计量，不能把桌面壳占用当作整个产品占用。

最终安装版 Release 60 秒样本（14:34）：后台工作集 **45.0 MiB**、私有内存 14.4 MiB、CPU 0.030%；桌面工作集 **192.1 MiB**、私有内存 157.5 MiB、CPU 0.154%；合计工作集约 237 MiB、CPU 0.184%。Host 可连接约 140 ms，主面板 Opened 事件约 1.12 s。这是当前机器单次启动观测，未清空 OS 文件缓存，不称为冷启动保证。主面板、悬浮窗、隐藏/重开、托盘创建均通过；桌面退出后 Host 存活，原 Codex 配置哈希未变。

此空闲样本满足上述暂定目标，干净机仍需另测；执行负载见下文。payload ZIP 约 **409.8 MiB**，Setup 约 **110.8 MiB**，两者合计约 520.6 MiB；主要包括固定 OpenCode、Codex 沙箱、MinGit 和自包含 .NET。当前安装体积不能称为小型安装包；后续精简方向是减少重复 runtime/Host 分发，须保持普通用户无需开发环境和插件缓存可独立启动的契约。

证据路径：`artifacts/validation/final-tests/*.trx`、`final-setup-gates/results.json`、`plugin-final.json`、`desktop-release/result.json`。`version-update-preserves-data-and-rejects-unfinished` 与损坏更新场景使用合成 payload，只证明安装事务和版本指针；真实 payload 场景证明程序/运行时安装，不能代替两个真实发行版本间的升级验收。

## 可复现检查

真实 Go 账户验收（用户在主面板独立保存凭据后执行，2026-10-16）：Codex app-server → 安装版 MCP → Host → OpenCode Go → deepseek-v4.1-flash 五角色均 Completed。default/worker 仅生成预期 answer.txt；explorer/reviewer 不改项目；verifier 的 verification.txt 被保留到专属产物目录且项目未变。重复 request_id 返回同一已完成身份，无重新执行。证据 `artifacts/validation/plugin-live-v3.json` 仅使用合成输入 41→42，不导出密钥。

| 角色 | 实际输入 Token | 实际输出 Token（含推理） | 最后一轮生成 Token/s |
| --- | ---: | ---: | ---: |
| explorer | 8692 | 144 | 176.48 |
| default | 10272 | 341 | 106.63 |
| worker | 15671 | 496 | 146.14 |
| reviewer | 8678 | 177 | 243.10 |
| verifier | 11796 | 3282 | 122.84 |

速度是各任务最后一轮的观测值，不是整项任务平均，不构成性能保证。并发资源样本已补充；提供方账单与未覆盖的复杂 compaction 仍需另行抽验。

```powershell
dotnet test AgentDock.slnx -m:1
pwsh -File scripts/Probe-Desktop.ps1
Get-Help scripts/Probe-Plugin.ps1 -Detailed
```

OS 权限测试必须从普通 Windows 会话执行；嵌套在受限令牌中的进程不能替代真实权限测试。`HostIntegrationTests.RestrictedTaskCannotCallTheManagementPipeEvenWithTheConnectionToken` 已证明受限程序读到令牌后仍被 Windows 拒绝连接管理管道。Host 同时检查连接进程的受限令牌，作为额外防护。

## 发布门槛

Windows 后续已修复数字输入框名称、短连接预算和高 DPI 定位，最终候选及用户指定的 12 小时稳定性状态见 [Windows 剩余验收](windows-remaining.md)。该记录更新下方历史快照中的对应缺口，不代表发行门槛全部通过。

真实 Mac 完整安装与账户验收已通过，但新引擎调度路径尚未在 Mac 实机回归，Developer ID、公证和 Gatekeeper 发行检查未通过。Windows 未配置发行签名。2026-10-16 启用 Hyper-V 后，专用干净 Windows VM 的安装、插件接入与独立账户结果见 [干净 Windows 记录](windows-clean-hyperv.md)；后续人工交互、十二小时持续运行及通信压力结果以本文件顶部的 2026-10-17 更新和对应报告为准。

2026-10-17 已按用户授权更新 tickets：#11 的 Windows 验收七项全部完成并关闭；#4 保留严格冷启动测量，#9 保留 Mac 人工交互，#10 保留首个未解释安装失败及签名/下载分发门槛，#12 保留新引擎 Mac 回归及发行检查，父项 #1 继续开放。用户随后授权提交并推送源码；公开发版尚未授权，源码交付不代表安装包已满足发行条件。

## 真实 Windows 四任务负载（2026-10-16）

安装版、用户独立配置的 OpenCode Go 账户、合成项目：explorer 完成后同时提交 default、worker、reviewer、verifier，五角色全部 Completed，重试身份一致。52 个成功样本覆盖 52.25 秒，其中 13 个样本观察到 4 项同时 Running；无采样错误。采样只读取任务状态和进程资源，不导出凭据。

| 指标 | 观测峰值 |
| --- | ---: |
| Host 工作集 | 104.46 MiB |
| Desktop 工作集 | 162.10 MiB |
| 执行进程合计工作集 | 6894.42 MiB |
| 全部合计工作集 | 7158.08 MiB（约 7.0 GiB） |
| CPU（24 逻辑处理器归一） | 29.19%（采样均值 3.55%） |

主要占用来自四个 OpenCode 进程，各约 1.6–1.7 GiB；工作集相加可能重复计算共享页，不等于独占物理内存。一秒采样可能遗漏短命进程，不能作为最坏上限。完成后独立检查确认没有遗留执行中的 OpenCode/Codex/Engine 子进程。默认并发仍为冻结规格中的 4；该样本说明执行引擎占用显著高于空闲桌面，尚未制定低内存设备的发行预算。

证据：`artifacts/mac-validation/windows-load/{plugin-load.json,samples.jsonl,resource-summary.json}`。新增平台回归：`artifacts/mac-validation/windows-regression/*.trx`。

## 最终交付快照

最终完整回归在 `artifacts/mac-validation/windows-complete-final-enabled/`，包含实际固定 OpenCode/Codex 沙箱。第一次收尾检查暴露 Windows 进程测试读取尚未关闭的 PID 文件；测试改为关闭临时文件后再移动发布，并确保断言失败也释放 job，随后全套通过。该修复不修改产品进程行为。

随后带任务数据的真实 UI 检查发现两平台共有的列表模板空值崩溃。`BuildTaskRow` 现接受 Avalonia 回收项时传来的 null；`Probe-Desktop.ps1 -SeedTasks` 使用离线合成终态数据覆盖这个场景，不需凭据或模型调用。旧 Windows 包复现崩溃，最终包通过；Mac 以已有五任务及新一轮真实五角色通过，任务列表保持可见。修复后全套 Windows 测试再次为 114 通过、19 平台跳过（`windows-ui-final/`）。

最终本地包目录为 `artifacts/release-candidate/final-ui-20261016/{win-x64,osx-arm64}/`，保留先前候选和测试证据。Windows 安装器 SHA-256 与已通过 16 门禁的版本相同，最新插件与有任务 UI 证据在 `windows-ui-final-plugin.json`、`windows-row-after/result.json`；Mac 构件哈希、安装和负载明细见 [macos.md](macos.md)。这些目录不入 Git；代码仓库只包含源码、脚本、模板、锁文件与脱敏验收结论。
