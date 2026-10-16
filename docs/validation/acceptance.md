# AgentDock V1 验收记录

基线：[父任务 #1](https://github.com/HoboCY/AgentDock/issues/1)，2026-10-16 读取的 Q1–Q26 与子任务 #2–#12。执行环境为 Windows 11（内核 10.0.26200）、x64、24 逻辑处理器、.NET SDK 10.0.401 / Runtime 10.0.12。验收任务均使用隔离目录内的合成项目；模型分别标明本地脚本化响应和独立真实 OpenCode Go 账户；既有 MCP 配置和凭据不迁移。

本文件区分源代码实现、本机实测与发布验收。真实 OpenCode CLI + 本地脚本化模型可以验证工具、权限、用量适配和停止链路，不能证明真实 OpenCode Go 账户可用或其当前用量字段完全一致。

Windows 本地实现与自包含包已经完成。完整回归 113 项通过、0 失败、0 跳过；最终包通过 16 项安装/修复/升级/卸载检查，以及 Codex app-server 真实插件调用。Standards 与 Spec 独立审查及处理记录见 [review.md](review.md)。用户已独立配置 Go 凭据，五角色真实最小任务及重试幂等已通过；Mac SSH 接入准备中，尚未通过 Mac 验收。

## 覆盖矩阵

| 决策 | 实现与可复现证据 | 尚需的外部验收 |
| --- | --- | --- |
| Q1、Q19 | Setup、自包含打包脚本、固定运行时锁文件、真实 Codex app-server 插件探针 | 无开发工具的干净 Windows 安装与独立账户最小调用 |
| Q2、Q8、Q14 | Engine 固定 OpenCode Go / deepseek-v4.1-flash；五角色；系统写权限与 verifier 专属产物目录；独立沙箱测试 | macOS 权限；更多用户项目的工具链兼容 |
| Q3 | Core / Desktop 共享 .NET；Windows 执行后端；macOS 构建配方明确未验收 | macOS 后端、凭据和安装发行流程须在真机完成，不声明支持 |
| Q4、Q6、Q10 | Host 单实例、客户端按需启动、真实桌面显示/隐藏/重开；MCP EOF 不停后台 | 登录自启在干净安装的交互验收 |
| Q5、Q7 | 全局配置、项目筛选、范围用量汇总、悬浮窗活跃任务 | 大量项目的人机可读性复核 |
| Q9、Q16、Q23 | SchedulingAndRetentionTests：跨项目容量、父子目录串行、排队配置快照；不创建 worktree | 真实模型并发 4 的资源样本 |
| Q11、Q24 | 可控时间测试覆盖从 FinishedAt 起算的 7/30 天边界；手动清理预览及结果；未完成任务受保护 | 无额外平台特有门槛 |
| Q12、Q13 | 分阶段 usage 去重/修订；缺失字段为 null；SSE 生成计时；单独显示 AcceptedAt 总耗时 | 抽验真实上游 input/output/reasoning/cache 字段 |
| Q15、Q21 | 真实进程崩溃恢复、取消、超时、退出确认；文件哈希观察不宣称因果归属 | 真实账户的停止/超时抽验 |
| Q17、Q18 | 真实 Avalonia 主面板/托盘/340×190 悬浮窗；透明度、置顶、失败与可选完成通知 | 屏幕阅读器和不同缩放/DPI 的人工检查 |
| Q20、Q26 | 旧 MCP 只读提示；探针前后 Codex 配置哈希一致；新数据目录无旧任务/角色/凭据 | 干净机与多配置位置共存抽验 |
| Q22 | 手动版本检查，只提示；安装器拒绝有运行/排队/暂停任务的更新 | 已签名发行包的真实更新演练 |
| Q25 | 独立后台/桌面资源探针与执行进程释放测试 | 干净机长期空闲、四任务并发、真实模型资源样本 |

## 本机桌面观测

命令：`pwsh -File scripts/Probe-Desktop.ps1`。脚本创建独立数据目录，实际启动后台和桌面，记录窗口显示/隐藏/重开、托盘创建、后台在桌面退出后仍存活，以及原 Codex 配置前后哈希比较。图像是实际运行控件的渲染捕获，不是设计稿。

2026-10-16 13:50 的开发构建样本：后台可连接约 359 ms，主面板 Opened 事件约 1.80 s；后台工作集 41.3 MiB、私有内存 12.7 MiB；桌面空闲工作集 146.8 MiB、私有内存 121.0 MiB。3 秒采样按 24 个逻辑处理器归一的 CPU 分别约 0.19% / 0.22%。此样本包含界面轮询，不含执行任务；短采样不能代表长期稳定上限。

初步预算建议：相同环境下空闲后台工作集目标 80 MiB 以内、桌面 200 MiB 以内、合计 CPU 1% 以内；需以自包含 Release 和至少 60 秒样本确认，再将目标升级为发行门槛。执行引擎和安装包另行计量，不能把桌面壳占用当作整个产品占用。

最终安装版 Release 60 秒样本（14:34）：后台工作集 **45.0 MiB**、私有内存 14.4 MiB、CPU 0.030%；桌面工作集 **192.1 MiB**、私有内存 157.5 MiB、CPU 0.154%；合计工作集约 237 MiB、CPU 0.184%。Host 可连接约 140 ms，主面板 Opened 事件约 1.12 s。这是当前机器单次启动观测，未清空 OS 文件缓存，不称为冷启动保证。主面板、悬浮窗、隐藏/重开、托盘创建均通过；桌面退出后 Host 存活，原 Codex 配置哈希未变。

此样本满足上述暂定目标，干净机和执行负载仍需另测。payload ZIP **409.8 MiB**，Setup **110.8 MiB**，两者合计约 520.6 MiB；主要包括固定 OpenCode、Codex 沙箱、MinGit 和自包含 .NET。当前安装体积不能称为小型安装包；后续精简方向是减少重复 runtime/Host 分发，须保持普通用户无需开发环境和插件缓存可独立启动的契约。

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

速度是各任务最后一轮的观测值，不是整项任务平均，不构成性能保证。提供方账单、未覆盖的复杂 compaction、并发资源峰值仍需另外抽验。

```powershell
dotnet test AgentDock.slnx -m:1
pwsh -File scripts/Probe-Desktop.ps1
Get-Help scripts/Probe-Plugin.ps1 -Detailed
```

OS 权限测试必须从普通 Windows 会话执行；嵌套在受限令牌中的进程不能替代真实权限测试。`HostIntegrationTests.RestrictedTaskCannotCallTheManagementPipeEvenWithTheConnectionToken` 已证明受限程序读到令牌后仍被 Windows 拒绝连接管理管道。Host 同时检查连接进程的受限令牌，作为额外防护。

## 发布门槛

用户已安排 Mac 接入；相应实机结果仍待取得。尚未授权公开发版或推送。本地构建和 Git 提交不等于 GitHub 分发。代码签名、干净 Windows 环境、独立真实 Go 凭据和真实 Mac 环境分别记录，缺少任何对应证据时不关闭相关平台验收任务。Mac 不可用不阻塞已验证 Windows 工作的本地交付。
