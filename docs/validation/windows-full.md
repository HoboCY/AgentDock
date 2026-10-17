# Windows 扩展验收与历史崩溃排查（2026-10-16）

本文保留此前候选的验收快照；后续修复与用户指定的 12 小时稳定性验收见 [剩余验收记录](windows-remaining.md)。

本轮继续 [干净 Hyper-V Windows 验收](windows-clean-hyperv.md)，核对 [设计基线 #1](https://github.com/HoboCY/AgentDock/issues/1) 的 Q1–Q26。**Windows 尚不能判为全部通过，也不具备发行条件。** 安装、真实任务、登录自启、退出确认和实际工具执行中的停止/超时已有通过证据；新增发现数字输入框的无障碍名称缺失，以及一次负载期间的 MCP 通信失败。

环境沿用 `AgentDock-CleanWin11-20261016`：Windows 11 Pro 25H2、4 vCPU、固定 6 GiB RAM，普通用户 `DockTest`。正式候选仍为 `final-ui-20261016/win-x64` 的 0.1.0；本轮没有修改产品源码或替换已安装候选，没有提交、推送或发布。所有模型任务都在新建合成目录执行，使用用户在 VM 独立保存的 OpenCode Go 凭据；未复制宿主凭据。

## 本轮结果

| 检查 | 结论与证据边界 |
| --- | --- |
| 干净安装与卸载/修复/重装 | 沿用首轮 8/8 和安装门禁 16/16，通过；未因相同构件重复执行 |
| 登录自启 | 实际界面开启，检查 HKCU Run，再重启 Windows 登录；恰好一个 Desktop 以 `--tray` 启动，通过。验证后恢复默认关闭，Run 项移除 |
| 凭据持久化 | Windows 重启后 `credentialConfigured=true`；只回读布尔状态，不导出密钥 |
| 完全退出：取消 | 带一个合成 Paused 任务，真实确认窗出现；取消后原 Host PID、Paused 状态和未完成数量保持，通过 |
| 完全退出：确认 | 真实确认后 Desktop、Host 均退出码 0；暂停任务及请求墓碑均为 Cancelled；项目哨兵文件保留，通过 |
| 后台不可达时退出 | 显示“仅退出界面”提示，取消后桌面仍在，通过 |
| 失败通知 | 无密钥的隔离 fixture 触发本地 `credential-missing`；主面板隐藏后出现置顶 Toast，约 5 秒消失，点击重新打开主面板，通过 |
| 可选完成通知 | 实际界面保存开启后，真实任务完成出现匹配该标题的 Toast；界面保存关闭后，下一真实任务 Completed 且静默，通过；配置已还原 |
| UIA 可访问性 | **不通过**：凭据、按钮、复选框、滑块等名称存在；并发数及五个角色超时框在 Windows UIA 树中是空名称 Edit，详见下文 |
| 键盘基本操作 | 方向键切换到设置、连续 22 次 Tab、空格开/关通知复选框均通过，焦点保持在被测窗口并自动滚动；六个可聚焦数字输入框无名称，因此可访问性整体仍失败 |
| 多 DPI | 当前窗口为 96 DPI；增强会话的 Windows 显示设置明确禁止修改缩放/分辨率，150%/200% 未覆盖 |
| 五角色真实任务 | 20:16:55–20:17:46 的复测全部 Completed，输入/写权限/verifier 专属产物/用量/重复请求身份均通过；首次负载通信失败单独保留，不被复测通过覆盖 |
| 真实工具执行中停止 | 写入 marker 后手动停止，约 418 ms 到 Cancelled；PowerShell 子进程消失，marker 保留，后续写入未发生，通过 |
| 真实工具执行中超时 | 写入 marker 后等待，接收时固定的 45 秒时限生效，约 45.37 s 到 TimedOut；子进程消失、marker 保留，通过 |
| 真实用量字段抽验 | 一个两次工具调用的真实 explorer 任务捕获 3 个 OpenCode step 的数值字段，逐项与 Host 相等，输入总和 14,195、输出总和 972，通过；不等同提供方账单审计 |
| 配置还原与残留进程 | 停止/超时临时配置已还原，真实任务结束后无残留 Engine/OpenCode/Codex 执行进程 |
| 重启后的首次启动 | Windows Restart 后测量前无 AgentDock 进程，首次 Host/Desktop 探针通过；宿主磁盘缓存未清空，见下文 |
| 15 分钟空闲 | 180 个样本无未完成任务、执行进程、窗口心跳超时或进程替换；合计平均 CPU 约 0.15%。仅为有界样本，不代表全天稳定性 |
| 发行签名 | **未通过**：最终 Setup 的 Authenticode 状态为 NotSigned；本机个人证书库中未发现代码签名证书 |
| 真实下载 SmartScreen / 已签名发行升级 | **未覆盖**：没有本轮可用的签名发行包和真实下载链路；本地复制到 VM 不能替代此验收 |

交互脚本 `scripts/Probe-WindowsInteraction.ps1` 在新的隔离数据目录运行，无真实凭据或模型调用。独立只读复核后，补强第二个 Toast 出现与进程存活、无名称 Edit、退出码、异常不应通过等判定；最终有效交互记录为 `interaction-05`，5 个用例中 4 个通过、UIA 用例失败。前几次探针自身的变量名、模态窗口枚举及默认 Toast 标题假设已纠正；不把这些探针错误记作产品故障。另有独立真实键盘探针通过基本操作，但未作完整讲述人语音或多 DPI 验收。

## 历史“未响应”的证据

用户将时间范围限定为另一个完成 #1 的会话宣布实现结束后、安装 Hyper-V 前。只读查询宿主 Application 日志，保留本轮复现之前的截止快照，发现两组 AgentDock APPCRASH：

| 时间（北京时间） | 原可执行文件 |
| --- | --- |
| 17:16:10 起 | `artifacts/mac-validation/windows-final-installed/install/0.1.0/desktop/AgentDock.Desktop.exe` |
| 18:08:45 起 | `artifacts/validation/installed-final/install/0.1.0/desktop/AgentDock.Desktop.exe` |

两次均为 `NullReferenceException`，堆栈指向 `MainWindow.BuildTaskRow(TaskSummary)`，经任务列表刷新和 Avalonia 模板回收触发。对应查询中没有 AgentDock Application Hang 1002 记录；这不排除未写入事件日志的其他卡顿，也不能仅凭时间把用户看到的每次“未响应”都等同这两次崩溃。

补查 17:00–18:30 的所有应用 Hang 1002，以及 System 中资源耗尽、常见磁盘/存储重试、显示驱动恢复、意外断电等选定事件 ID，均无匹配记录。宿主物理内存约 31.8 GiB；没有当时的连续内存/CPU 样本，不能倒推整机卡顿不存在，也不能归因于后来安装的 Hyper-V。

本轮差分复现使用同一个 `Probe-Desktop.ps1 -SeedTasks` 和全新隔离数据目录：旧安装包退出 `-532462766`（`0xe0434352`），重现同一空引用；当前最终包退出 0，带任务列表显示/回收、隐藏/重开、后台存活均通过。当前源码已经对回收时的 null 项作保护，本轮没有重复修改此修复。

通过包的 Desktop SHA-256 为 `615AA8824ADBC01B2AE533ED0A864F574DC15424144CCB6B5785778B348FDF24`，与最终候选 ZIP 内 Desktop 字节一致。旧测试目录全部保留，没有因版本号都为 0.1.0 而误把旧二进制当作当前包。现有证据支持“旧版列表回收崩溃已在候选中修复”，不支持“所有未响应原因已排除”。

## 新发现的可访问性缺口

`MainWindow.BuildRolesEditor` 将 `AutomationProperties.Name` 设置在 `NumericUpDown` 外层；实际 Windows UIA 中可聚焦的内部 Edit 没有继承该名称。五个角色提示词 Edit 有名称，而其后各有一个空名称 Edit；并发数也为空名称。主面板 96 DPI 的控件元信息、未读取输入值的完整树片段均已保存。

因此屏幕阅读器无法依靠输入框的名称区分角色超时。需要把标签传递到实际可聚焦的数字编辑控件，并通过真实 UIA/键盘/讲述人再验收；本轮保留为产品缺陷，没有将源码上的 SetName 当作已生效的证据。

键盘探针在独立无后台窗口内使用真实方向键、Tab 和空格。六个空名称 Edit 都确实取得键盘焦点，焦点未离开被测进程；通知开关由 Off → On → Off，未保存临时更改。可见的键盘操作成功，无法弥补名称缺失。

## 并发、停止与用量

19:52 首次五角色探针：explorer 完成，随后 default/worker/reviewer/verifier 四项均 Running；MCP 返回通用 `Unavailable`，探针 finally 显式取消四项。后台和桌面存活，30 个资源样本中有四并发，最低剩余物理内存约 1,892 MiB，WM_NULL 心跳无失败。未发现 OOM 的直接证据。

空闲旧任务的 241 次 status/wait RPC 全通过。增加逐次 RPC 追踪并移除采样器额外的 650 ms `list` 调用后，五角色复测通过：

| 角色 | 实际输入 Token | 实际输出 Token（含推理） |
| --- | ---: | ---: |
| explorer | 8,663 | 192 |
| default | 15,808 | 563 |
| worker | 15,223 | 279 |
| reviewer | 8,671 | 199 |
| verifier | 17,757 | 2,690 |

80 个 OS 资源样本无采样错误、无桌面 WM_NULL 超时。Host 工作集峰值约 118.29 MiB，Desktop 93.12 MiB，OpenCode 工作集合计峰值约 2,801.82 MiB，最低剩余物理内存约 736.99 MiB。工作集合计可能重复计算共享页，不能解释为独占物理内存；瞬时峰值也可能被采样遗漏。本轮是 6 GiB VM 的有界负载样本，不是低内存设备的性能保证。

代码只读调查将候选原因缩小到 MCP 到 Host 的连接/响应路径：连接等待为 700 ms，业务调用没有连接重试，非 DockException 被统一显示为 Unavailable。高负载调度延迟与现象相容，但第一轮没有内部异常类型，**尚不能把它写成已确定根因，也未盲目修改超时参数**。

20:44 的诊断复测恢复了 650 ms `list` 采样，用独立构建的 MCP 客户端副本记录调用阶段和异常类型，安装版 Host/Desktop 未替换。五角色再次完成，80 次 IPC 调用均完成、480 条阶段记录中无异常；连接阶段最大观测约 671 ms，status 完整调用最大约 4,623 ms。该样本说明负载下确有明显延迟，连接耗时接近 700 ms 等待窗口，但没有捕获首次失败的异常，因此仍未闭合根因。诊断副本只记录方法、阶段、耗时、PID 和异常类型，不记录请求参数、正文、凭据或响应内容；它的通过不替代正式 MCP 二进制的验收。

真实停止/超时使用合成 `hold.ps1`，先写入标记和自身 PID，再等待 120 秒。第一次手动停止在尚未生成标记时发生，只证明执行前取消；后续新请求在标记出现后停止，补齐进程终止与文件保留证据。被中止任务用量尚未上报时维持 null，没有改成 0。

真实用量抽验在该任务运行期间，以只读 SQLite 连接查询它自己的 OpenCode 数据库，只输出 `step-finish` 的 partId 和五个数值分量；不导出任务正文、密钥或数据库。3 个 part 全部捕获，和 Host 持久化的 3 个 usage part 一一对应：

| step | input | cache read | cache write | output | reasoning | Host 输入 / 输出 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 4,256 | 0 | 0 | 44 | 557 | 4,256 / 601 |
| 2 | 648 | 4,224 | 0 | 100 | 81 | 4,872 / 181 |
| 3 | 203 | 4,864 | 0 | 124 | 66 | 5,067 / 190 |

实际合计符合既定口径：输入为 input + cache read + cache write，输出为 output + reasoning，分别为 14,195 和 972。此次有非零 cache read 和 reasoning，cache write 均为 0；没有用该样本宣称非零 cache write 或所有 compaction 形态均已实测。生成阶段速度的边界仍结合原有执行适配测试，本轮不把含工具等待的整项耗时当作生成计时。

该探针同时尝试通知开关时，最初直接修改后台配置，已打开 Desktop 的本地配置快照未刷新，因此“开启后无 Toast”不能作为用户界面路径的失败结论。随后新建受控 Desktop 窗口，通过真实设置控件开/关并点击保存，后台回读和窗口本地状态同时生效，再隐藏主面板提交新的真实任务：开启时出现包含对应任务标题的完成 Toast，关闭时 Completed 且无 Toast，两项通过。原始无效前提的记录与有效 UI 复测分别保留，最终全局配置还原。

## 首次启动与空闲资源

第二次正常重启的 Windows 启动时间为 20:21:20；首次 AgentDock 进程测量从 20:21:27 开始，之前无 AgentDock 进程。Host 可连接约 1,018 ms；Desktop Opened 约 3,816 ms（探针内的 Opened 计时口径）；首次 `main-shown` 记录距进程启动约 5,809 ms。窗口、托盘、悬浮窗显示/隐藏/重开与 Host 在 Desktop 退出后存活均通过。

随后的 60 秒样本中，Host/桌面 CPU 按 4 vCPU 归一约 0.221%/0.410%。这是单次客体 Windows Restart 后的首次应用启动；没有清空宿主的物理磁盘缓存，不承诺每次机器冷启动都达到此数值。

随后用正常安装数据启动主面板与 Host，在无执行任务状态下采样 905 秒（20:22:44–20:37:49），每约 5 秒读取资源与窗口心跳，共 180 个样本。全过程 Host/Desktop PID 不变，进程数始终为 2，未发现执行进程、未完成任务、采样错误或 WM_NULL 超时。

| 进程 | 平均 CPU（4 vCPU 归一） | 工作集峰值 | 私有内存起点 → 终点 |
| --- | ---: | ---: | ---: |
| Desktop | 0.123% | 152.58 MiB | 73.26 → 72.41 MiB |
| Host | 0.027% | 48.46 MiB | 14.67 → 27.91 MiB |

该区间满足此前的参考目标：Host 工作集 80 MiB 内、Desktop 200 MiB 内、合计 CPU 1% 内。Host 私有内存增加约 13.24 MiB，后两个五分钟段仍各增长约 1.2 MiB；未做 GC/堆归因，不能据此判定泄漏，也不能宣称长期无增长。24 小时稳定性及复杂项目负载未覆盖；6 GiB 四并发样本不升级为承诺的最低配置或资源上限。

## 证据位置与交付边界

本轮原始证据均在 `artifacts/validation/windows-full-20261016/`，不入 Git：

- `host-original-crash-events.json`、`hang-differential.json`、`old-row-repro/`、`current-row-repro/`：历史记录与新旧包差分。
- `guest-evidence/login-enable.json`、`login-after-reboot.json`：实际登录自启、注册表还原及凭据持久化。
- `guest-evidence/interaction-result.json`：退出/通知/UIA 的逐项断言；最终有效运行输出目录是 `interaction-05`，此前 `interaction-04` 同样保存。
- `guest-evidence/plugin-five-roles.json`、`full-live-state.json`、`load-samples.jsonl`：首次失败保留证据。
- `guest-evidence/transport-result.json`、`transport-state.json`：只读 RPC 对照。
- `guest-evidence/plugin-five-roles3.json`、`full-live3-state.json`、`mcp-live-trace.jsonl`、`load3-samples.jsonl`：成功负载复测。
- `guest-evidence/stop-timeout-state.json`、`stop-recheck-state.json`：真实执行停止/超时、子进程与配置还原。
- `guest-evidence/display-inspect.json` 与 `.png`：远程会话禁止更改缩放的实证。
- `guest-evidence/cold-state.json`、`cold-desktop-result.json`：重启首次启动。
- `guest-evidence/idle-state.json`、`idle-samples.jsonl`：15 分钟资源及响应性。
- `guest-evidence/keyboard-state.json`、`keyboard-settings.png`：真实按键、焦点和六个无名称输入框。
- `guest-evidence/usage-notify-state.json`：3 个真实 step 数值分量与 Host 的逐项比较；其中首次通知设置方式不构成有效 UI 开关验收。
- `guest-evidence/notify-ui-state.json`：有效界面路径下的完成通知开/关和还原。
- `guest-evidence/full-live-diag-state.json`、`plugin-five-diag.json`、`diagnostic-ipc-trace.jsonl`、`diagnostic-mcp-trace.jsonl`：恢复高频采样的诊断复测。`diagnostic-src/`、`diagnostic-host/` 是仅在忽略目录内的诊断副本。
- `host-system-window.json`：历史时段的选定系统事件与所有应用挂起查询。

收尾回读：未完成任务为 0，仅保留正常安装的 Host 和 Desktop；默认并发 4、五角色超时均为 1,200 秒、登录自启和完成通知恢复关闭，独立凭据仍配置。已移除本轮专用的冷启动登录触发任务，没有运行中的验收辅助任务。VM 本轮时段未发现 AgentDock 崩溃/挂起事件。`guest-evidence/final-state.json`、`vm-final-cleanup.json` 和 `installed-candidate-comparison.json` 保存最终配置、进程与正式候选二进制对比。

产品源码未变，沿用同一候选此前的 114 项 Windows 回归和 19 项平台跳过记录。本轮新增探针经独立复核、PowerShell 语法检查及实际 VM 复测；`git diff --check` 通过。未执行提交、推送、GitHub 状态修改或公开发布。

本轮结论用于 Windows 验收，不扩大为 Mac 或发行验收。签名、真实下载系统拦截、多 DPI/讲述人、无障碍缺陷及未定位通信错误保持可见；`releaseReady=false`，不关闭父任务或宣称公开交付完成。
