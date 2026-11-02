# 双平台安装包实机验收（2026-10-22）

按维护者要求删除既有 Release，并验收同一云端候选；Windows 产品检查均在既有 Hyper-V 虚拟机内执行。本轮尚未全部通过，`releaseReady=false` 和稳定标签门禁保持不变。

## 候选与 Release 状态

- 候选 `0.1.1-ci.20261021`，源码 `a88bf3e89a45c517dc13d9d03059e4281970e120`，来自 [双平台 Actions 39570470939](https://github.com/HoboCY/AgentDock/actions/runs/39570470939)。当前工作区基线 `5552b6c09b994c7ecdf2748a5bd54fd8b7d2b310` 相对该提交只有文档差异。
- 下载同一次运行的统一 artifact；EXE、DMG、两份清单均与 `SHA256SUMS.txt` 匹配。传入 VM/Mac 后再次确认安装包哈希。
- 已删除 `v0.1.1-beta.1`、`v0.1.1-beta.2` 的 Release，API 复读为空列表；两个 Git 标签保留。没有创建、公开或替换新 Release。
- 复用同一源码与安装包的 [云端结果](dual-platform-release-20261021.md)：Windows 180 通过 / 32 跳过，发布检查 25、安装门禁 20；Mac 185 通过 / 27 跳过，另有启动预检、附件校验 4、运行时检查 3、安装门禁 21。这些结果不代替下列实机检查。

| 安装包 | 字节数 | SHA-256 |
| --- | ---: | --- |
| Windows x64 EXE | 594027351 | `bdb49e99cf92bbc8e5ef6a74b757a141ef481e4e88c34d7d955c01120d4c02d1` |
| macOS arm64 DMG | 391681638 | `cedb15104d2e01faf6629ce7da6bb5dcd1ebe70c9852ddc3c0641a086e17fc99` |

## 当前完整候选：0.1.1-acceptance.20261023.1

2026-10-23 修复 Mac 无障碍树激活后的任务列表内存增长，再次构建完整 Windows EXE / Mac DMG。源码基线仍为 `5552b6c09b994c7ecdf2748a5bd54fd8b7d2b310` 加工作区修复；`partial=false / releaseReady=false`，没有提交、推送或新建 Release。下文 `.20261022.3` 的真实模型、人工桌面 App 和十二小时结果仍归属原候选，不追溯绑定新版。

| 当前本地安装包 | 字节数 | SHA-256 |
| --- | ---: | --- |
| Windows x64 EXE | 593888137 | `cb4e9e4d0c6bf21d7f2b3a8c5d6f5873092fef04369cacdc569b8b0ea4441aa4` |
| macOS arm64 DMG | 391693917 | `100a4445a0f3202ab6a70fc5e4beed28a7a5f74fead95f076cbe3a073229274f` |

### 内存修复与完整包回归

- 原生 AX 全树访问后，旧实现连续 150 次真实刷新使完整 GC 后的托管堆增长 **71.236 MiB**。仅打开窗口而未访问完整无障碍树的对照没有明显增长；单加模板 recycling 仍增长约 66.95 MiB，不能视作修复。
- 最终修改在每次轮询中复用任务行对象和控件，只更新绑定值、增删和排序；悬浮窗也复用已有任务行。选中任务操作始终读取最新 Summary。相同 AX 对照中，150 次刷新增长 **0.411 MiB**、600 次增长 **−0.394 MiB**，Host 身份不变。这些快速对照属于同逻辑诊断构建；下面的完整包另做安装和运行检查。
- 新增桌面断言覆盖八次真实刷新后的行/控件身份、选择、最新 Summary、标题/状态/输出/已结束耗时绑定，以及悬浮窗控件身份。Windows 本地构建 0 警告/0 错误；完整新包在 VM 通过 **20/20 安装门禁、24 项桌面检查、5/5 UIA 交互、两次 7 工具插件连接**，Host 不属于客户端 Job、客户端退出后存活且重连 PID 不变。
- Mac 完整新包通过 **21/21 安装门禁、在线 24 / 离线 14 项界面检查、189 次 Host 查询零错误**，最大查询 238.43 ms；Desktop 退出后 Host 存活。合成凭据已移除，日常配置/指针保护检查通过。两端窄窗口与 Mac 悬浮窗截图已复核。
- Mac 默认测试安装升级后，Host **70329** / Desktop **70334**，配置及任务历史哈希不变，凭据仍配置。完整安装版激活无障碍树后的 **1,200.024 秒 / 121 样本**检查通过：完整 GC 后托管堆 **7,619,800→7,918,896 字节（+0.285 MiB）**；Host/Desktop 平均 RSS **136.96 / 183.09 MiB**，8 核归一 CPU **0.0657% / 0.3151%**。这消除了短程复现，但不替代本轮连续六小时结论；Host RSS 仍高于早期 80 MiB 参考，不擅自改写预算。
- 独立 reviewer `9ebbf87cb1f04f28be0d20eb01e990d1` 已完成，cursor 24、`hasMore=false`，未发现确定的正确性缺陷；指出增删/重排专项测试仍可加强。该静态审查不代替上述原生测试。

### Windows 浏览器下载、图形升级和冷缓存检查

- VM 普通用户使用真实 Edge **155.0.4283.62**，从仅允许该 VM 访问的宿主 HTTP 测试端点下载单个 EXE。浏览器实际显示“通常不会下载”信誉警告；选择“保留”后下载完成，哈希匹配上表，Shell 启动前存在 **ZoneId=3**。没有关闭 SmartScreen、修改保护策略或调用 Unblock-File。启动后未观察到额外 SmartScreen 对话框；不能据此保证所有用户都不再遇到提示。
- 图形“安装 / 应用更新”直接使用内嵌程序包，把默认测试安装从 `0.1.1-ci.20261021` 升级至当前版本；**没有 payload 文件选择器**，Codex 配置与任务历史哈希保持不变。随后“启动 AgentDock”打开桌面，再点击桌面“启动后台”连接成功：Host **7128** / Desktop **12828**。安装器仅启动桌面、后台等待显式操作是现有契约；首轮助手错误要求自动启动后台，保留该失败并按实际流程复验，未改产品行为。
- 保留下载和完整 EXE，无需用户单独处理 ZIP 或 package manifest。下载警告、安装前后、启动结果和保护字段在 `windows-browser-download/`。这是**受控 HTTP 下载**，不是公开 GitHub Release 下载验收。临时服务已关闭，防火墙规则和监听端口均已复查不存在。
- VM 冷缓存检查使用签名有效的 Microsoft RAMMap **1.63**，临时停止 SysMain，清理系统工作集和 standby 缓存，再由普通用户启动隔离候选。系统缓存从约 161.86 MiB 降至 0.24 MiB，standby 从约 2395.70 MiB 降至 10.43 MiB；SysMain 原状态已恢复。Host 约 **445 ms** 可连接，主窗 Opened 约 **1013.35 ms**；60 秒归一 CPU Host/Desktop **0.2213% / 0.7031%**，界面检查和退出后后台存活通过。
- VHDX 位于宿主本地 NTFS。Microsoft 文档说明 [Hyper-V 虚拟磁盘 I/O 使用非缓冲方式](https://learn.microsoft.com/en-us/windows-server/failover-clustering/failover-cluster-manage-cluster-shared-volumes)，据配置推断其绕过普通宿主文件缓存；本轮未做逐 I/O 跟踪、宿主重启或硬件缓存清空。此结果限定为**明确清理来宾系统缓存后的启动**，不称物理磁盘完全冷启动。

### 当前未闭环项（2026-10-23 13:40，长测改为六小时）

- 维护者明确把当前候选长测要求从十二小时调整为**连续六小时**。保留同一产品进程和本轮已累计时长，不重启、不拼接旧候选：Mac 正式采样约 **11:27:28–17:27:28**，Windows **11:35:29–17:35:29**（2026-10-23，北京时间），随后进行有界收尾。两端收尾助手已进入 waiting；运行中不记通过。
- 原监测进程已固定为 43,200 秒，因此增加独立收尾助手：须先有 `elapsedSeconds >= 21600` 的连续原始样本且墙钟时间也满足，再结束原监测。原十二小时记录保留为有意中断/不完整；新六小时结果单独生成，不能改写为十二小时通过。Windows 核对序号、间隔、休眠、身份、IPC、窗口、崩溃事件和写入错误，并复用原探针完成结束清洁检查；Mac 核对进程启动身份、连续采样与空闲状态，再做完整 GC，沿用原先 GC 后保留堆增长小于 25 MiB 的回归阈值，结合内存趋势人工评估。六小时足以观察此前明显持续增长，不证明所有慢性泄漏都不存在。
- 独立 reviewer `9dbd382d5be445268de6ae2eef1051f2` 已 Completed，cursor 104、`hasMore=false`；其提出的末样本时长、取消后缺少结束检查、监测进程退出、Mac 终止前快照及身份保护均已纳入收尾助手。样本校验自检覆盖正常记录，以及短时长、间隔、忙碌、身份和 CPU 计数异常，Windows 另覆盖序号异常与 JSON 日期精度。
- Mac 首个 launchd 监测助手因辅助功能权限归属不同，在采样前失败；改为既有授权 SSH 上下文后正常采样，原记录保留。Windows 同步器错误与产品探针错误分别记录。Windows 六小时助手首个预检发现 PowerShell JSON 日期转字符串再解析会丢失精度和时区，未停止原监测或产品；改为直接 DateTime 转换并补充断言后，`six-hour-r2` 正常等待。原失败文件保留，宿主另有独立计划任务收集六小时结果。
- Mac 严格冷缓存启动尚待管理员动作：`sudo -n` 确认当前远程上下文必须输入本机密码。隔离测试脚本 `mac-cold-resource.py` 已部署并通过语法检查，已请用户在 Mac 终端运行；未修改 sudo 策略或请求密码内容。不会停止正在长测的默认安装。
- Apple Developer 身份尚未准备，按用户要求保留签名、公证和真实下载后的 Gatekeeper 检查；Windows 签名为可选。公开发行 URL、Mac 发行身份，以及长测后的最终资源评估仍未闭环，不关闭 #1、#4、#9、#10、#12。

新增证据位于 `package-full-resource-r2-win/`、`package-full-resource-mac/`、`windows-full-resource/`、`windows-browser-download/`、`windows-cold-resource/`、`mac-resource-results/`、`resource-soak-live/`。Mac 原始堆图仍留在实机，未发送给外部代理。旧临时包/门禁程序副本在确认无运行进程、验证完整包备份、把日志/JSON/清单保留到删除目标外后清理；用户数据及原始失败证据保留。

## 上一轮完整包：0.1.1-acceptance.20261022.3

在上述基线与本轮未提交修复上重建了同版本的 Windows x64 EXE、macOS arm64 DMG。两个包均为完整自包含包，`partial=false / releaseReady=false`，固定 SDK 10.0.401 / Runtime 10.0.12；没有发布 Release、提交或推送。`.1`、`.2` 中间包及失败证据分别保留，不作为最终候选。

| 最终本地安装包 | 字节数 | SHA-256 |
| --- | ---: | --- |
| Windows x64 EXE | 593884041 | `590ee222ad5fc549ce7d7a28d721629a3497edbc620c2083dfe6d8c8ff623043` |
| macOS arm64 DMG | 392154239 | `7ff02200004a45a6b2e8b2d9411c5bfc72d51bf50e9020f3cbef8ad75005a2a7` |

- Windows：在同一 VM 普通用户 Session 1、全新中文和空格安装路径中，单 EXE 安装、完整 **20/20** 安装门禁、桌面 smoke、**5/5** UIA 交互及两次隔离 Codex app-server 插件检查通过。默认安装指针、日常配置和长测 Host 9520 / Desktop 12168 的身份与启动时间不变。测试 Host 已正常停止。
- Mac：从新 DMG 安装到隔离目录，**21/21** 安装门禁、在线 **21** / 离线 **13** 界面检查通过；**162** 次 Host 查询均成功，最大 **100.81 ms**，Desktop 退出后 Host 存活。新插件识别 7 个工具、返回 `.3` 实际后台和原执行配置；三个附件大小/哈希全部校验，DMG 取回本地后再次校验。合成凭据已删除，三个中间测试后台及测试桌面已退出，默认 Host 91390 / Desktop 91463 和用户文件不变。
- 原生 Mac 显示切换完整回归属于 `.2`；`.3` 的 Desktop 业务代码与 `.2` 相同，仅增加 Windows 专用后台启动修复并重建版本。人工 VoiceOver、文件选择器的范围见下文，不把人工结果追溯绑定到 `.3` 的文件哈希。
- 下午打包收尾时，真实任务及长测仍只有 `0.1.1-ci.20261021` 的证据。维护者晚间明确要求继续验收后，`.3` 已补齐双平台真实五角色，并开始自己的 Windows 12 小时测试，详见下节；旧候选时长没有拼入新包。

### 晚间继续验收：最新包真实任务通过，独立长测已完成

维护者回复“继续一起验收了吧”后，沿用 `opencode-go / deepseek-v4.1-flash / max`，没有更换模型、重派此前失败任务或发布 Release。

- **Windows `.3`**：21:34:40–21:35:26 在 VM 普通账户 Session 1 的隔离安装中，五角色全部 Completed，实际并发 **4**，**35 次状态采样、0 错误**；结果 42、写入/只读权限、verifier 产物、提供方用量及相同请求幂等性通过。每项事件已读到终态且 `hasMore=false`。新 Host **9012** 在插件客户端退出后存活；旧长测 Host 9520 / Desktop 12168 身份保持不变，用户 Codex 配置未变。仅在 VM 内临时复用同用户的 DPAPI 加密凭据文件，完成后删除隔离副本；没有导出或记录明文 Key。
- **Mac `.3`**：21:34:46–21:34:55 从完整安装器升级默认验收安装，升级前后用户 Codex 配置及任务历史哈希一致，沿用原 Keychain 项。新 Host **37704** 完成五角色/实际四并发，**55 次状态采样、0 错误**；结果、权限、产物、用量和幂等性通过。五项事件均已读到 Completed / `hasMore=false`。这些是安装版与隔离 Codex app-server 的实测，尚不等于实际桌面 App 新对话通过。
- **Windows `.3` 独立长测**：Host 9012 / Desktop **2504** 的 60 秒预检完成，13 个含收尾样本无监测异常；**2026-10-22 21:40:46 +08** 开始新的 43,200 秒监测，预计 **2026-10-23 09:40:46 +08** 满时。状态文件为 `latest-soak-live/state.json`，后台证据同步器为 `latest-soak-watch.json`；运行中不计通过。旧长测继续独立运行，两组产品进程并存的环境已记录，未重启或拼接时长。
- **Windows 初始资源**：新主窗口可观测时间约 **1377 ms**，未清空缓存，不是严格冷启动。60 秒样本 Host/Desktop 工作集峰值约 **84.18 / 154.66 MiB**，按 4 vCPU 归一 CPU 约 **0.0325% / 1.1455%**。这是启动初段且同机有旧候选运行，不能把它当成稳定空闲基线；暂定 80/200 MiB、合计 1% 仍是待复核参考目标，不伪造为已满足。
- **Windows 后续空闲观察**：排除本次长测最初 60 秒，21:41:47–21:51:02 的 **555 秒 / 38 样本**中，Host/Desktop 平均工作集约 **88.06 / 85.96 MiB**，峰值 **89.83 / 137.61 MiB**，按 4 vCPU 归一 CPU **0.0373% / 0.2674%**，合计 **0.305%**。CPU 回到暂定参考值内，Host 工作集仍略高于 80 MiB 参考；私有内存从 30.04/70.49 增至 35.82/78.08 MiB，需结合完整长测，不据短段判断泄漏或通过全部资源项。原始样本和计算见 `latest-settled-resources.json`。
- **Mac 桌面补验初次受阻（后续解锁已通过）**：发现旧 Desktop 91463 没有随后台升级切换；关闭这一测试拥有的旧桌面后，新 `.3` 从 SSH 以及 gui launchd 启动均出现 Avalonia RenderTimer `-6661`，21:44 查询确认屏幕锁定。保留两次启动失败，等待解锁后对照复验，不直接认定锁屏是唯一原因，也不计桌面资源通过。后台 37704 和已完成任务不受影响。第一轮终态收集助手因 APFS 路径大小写筛选遗漏任务，后改按已知任务 ID 收齐；没有重派模型任务。

| 平台 | explorer | default | worker | reviewer | verifier |
| --- | --- | --- | --- | --- | --- |
| Windows `.3` | `d09e7be0159c457e8667d90d869e9a79` / 10 | `06fe8001cd4346688e213a4f25989088` / 11 | `8c741742a9934bbfae4e7655b4daf958` / 11 | `6859c4d6592b4efca5bdde586b9deb1d` / 9 | `316859efcb0b4f4b989e0a59102ea6a1` / 10 |
| Mac `.3` | `2944818d90f246c78fd272fc8344a8b5` / 10 | `6ffaeb281f64421787244b080f5b0078` / 13 | `c0369380dd67457d8d01f303b3890d21` / 13 | `15c2747df6734c31ae0b1f9ffd4d8548` / 9 | `1803c600e17c4415b868fbeda6e9bb9b` / 14 |

表内斜线后的数字为最终事件 cursor。证据目录：`windows-latest-live/`、`mac-latest-live/`、`latest-soak-short/`、`latest-soak-live/`。VM 当前未发现 Codex 桌面 App 包或窗口；Mac 的日常应用目录为 `/Applications/ChatGPT.app`，未发现约定位置的内置 `codex`，因此不能把该 `.app` 用作“带内置 CLI”的正向选择样本。

### 人工配合后的补验：150% 通知与 Mac 新对话通过

- 用户确认 Mac 完全退出/重开日常 Codex 桌面 App，并从新对话只调用 `status`，返回 **`.3 / processId 37704 / unfinishedTasks 0`**，与实测后台一致。记为用户人工验收；没有声称代理独立观察到该新对话。原始确认保存在 `manual-final-confirmation.json`。
- 用户将 VM 设置为 150% 后，22:04:31–22:05:06 实测 **144 DPI**；`.3` 的 **5/5** 交互组通过。通知位置 `[1359,855,525,117]` 位于工作区内且置顶，约 **4960 ms** 自动消失，点击后主面板恢复；两个合成任务在本地因无凭据失败，未调用模型。系统设置恢复 **100% / 96 DPI** 成功，两组长测的四个进程身份均不变。证据为 `windows-latest-toast-manual/`；此前设置窗口不可访问的记录保留，不混为产品失败。
- 用户解锁 Mac 后，未改产品二进制，以既有 gui LaunchAgent 启动 `.3` Desktop **40610** 成功，Host 37704 保持不变。原生 AX 读取主窗口 `[580,235,760,560]`，连续资源采样和 Host 查询通过；锁屏场景的 RenderTimer `-6661` 仍保留，解锁后同包未复现，不能扩大为锁屏启动也通过。
- Mac 初始 **63.36 秒 / 61 样本**：Host/Desktop 平均 RSS **73.47 / 179.98 MiB**，按 8 个逻辑处理器归一 CPU **0.118% / 2.129%**。后续稳定主窗口 **62.18 秒 / 61 样本**：平均 RSS **106.59 / 178.71 MiB**，峰值 **110.16 / 187.17 MiB**，CPU **0.1065% / 1.7328%**，合计 **1.839%**。资源测量完成但仍高于暂定参考，尚不能判资源项全绿；RSS 与 Windows 工作集口径分别记录，未作严格冷缓存测量。证据在 `mac-latest-live/finalized-unlocked-summary.json` 和 `settled-resource-summary.json`。

### 2026-10-23 上午：最新 Windows 长测通过，Mac 内存阻塞

- `.3` Windows 独立长测已完成 **43,200.335 秒 / 2,881 样本**，序号连续、最大间隔 15.069 秒；IPC 全部成功，监测异常、写入错误、睡眠事件均为 0，收尾未完成任务与执行进程均为 0。Host 9012 / Desktop 2504 的身份始终一致。同步器正常完成，82 次复制无错误，防睡眠已释放。证据冻结到 `latest-soak-final/`，主代理独立复核为 `latest-soak-final-verification.json`。
- 按 4 vCPU 归一的全程 CPU：Host **0.059%**、Desktop **0.3419%**，合计 **0.4009%**。私有内存 Host 28.941→32.281 MiB、Desktop 79.820→82.805 MiB，前期上升后持平/下降；同机旧候选并存仍按实记录，不把此样本扩大为所有负载均无泄漏。
- Mac `.3` Desktop 40610 自前晚 22:04:36 运行至今。RSS 约百余 MiB，但 `vmmap` 的实际 footprint 约 **15.0 GiB**（大部分已换出）；.NET 运行时计数器确认托管堆 gen2 约 **15.7 GB**，完整 GC 后仍约 **15.8 GB**。这是实际稳定性阻塞，不能以低 RSS 判通过。
- 已保存原生采样、GC 前后计数器和类型引用链。堆图工具达到 **10,000,005 对象**采集上限，只表示约 848 MB 的局部图，不宣称是完整 15 GB 堆。大量旧 TextBlock/Grid 经 `AvnAutomationPeer → NoneAutomationPeer` 引用链可达；继续通过原生 UI 对照验证刷新/无障碍对象的持有机制，尚未判定修复完成。证据位于 `mac-latest-live/overnight-*`、`runtime-counters-overnight.json`、`post-full-gc-counters.json`，原始堆图留在 Mac 验收目录。
- 用户明确回复 Apple Developer 身份尚未准备，先完成其余项；签名、公证和 Gatekeeper 发行门槛保留，Windows 签名继续可选。

该时点仍需 Mac 内存修复、资源与冷启动、浏览器下载和 Mac 发行身份；随后进展及当前边界见上方 `.20261023.1` 记录。两平台实际桌面 App 新对话均已由用户确认，Windows 详细结果见下节。

### Windows 实际桌面 App 环境

VM 中原先只有独立 CLI。官方 Store `winget` 安装退出 0 并报告成功，但普通账户、全机 Appx 与 winget 列表均未找到应用，Store 页面也未形成可操作控件；这次不计安装通过。随后按[官方离线部署说明](https://learn.chatgpt.com/docs/enterprise/windows-deployment)下载 x64 MSIX 和许可证：MSIX 签名状态 Valid，SHA256 为 `ef1fc9606261edc9c93d97b504d4c6ac3facf25ac64d7a8aa03496ef4733798d`，传入 VM 后哈希一致。22:28 完成系统预配及 DockTest 普通账户注册，实际包 `OpenAI.Codex_26.1016.8120.0_x64__2p2nqsd0c76g0`，无需重启。

使用新隔离 `actual-desktop-home` 注册插件，7 个工具可见，实际 status 返回 `.3 / Host 9012 / unfinishedTasks 0`，客户端结束后后台仍存活，默认 Codex 配置哈希不变。此项仍只是桌面登录前的插件预检，不替代实际 App 新对话验收。证据在 `windows-codex-app/`，保留首次 Store 未落地以及并发查询 Appx 忙的环境记录；没有关闭或重启两组长测进程。

22:30 以独立 CODEX_HOME 启动实际 `ChatGPT.exe` PID 9720，UIA 读到“登录 ChatGPT”“继续登录”，截图人工核对登录页已出现（部分被系统设置窗口遮挡）。默认配置哈希保持一致；隔离目录 ACL 已限制为 DockTest、SYSTEM 和管理员。已请用户自行登录并在新对话只查询 status，不读取或迁移其他设备登录信息，不启动验收模型任务；当时人工结果尚待回复，后续确认见下文。

2026-10-23 用户完成登录后，请求代为处理 DockAdmin 密码提示。核对实际 VM 安全桌面，提示来自已验证发布者 OpenAI OpCo, LLC 的 `codex-windows-sandbox-setup.exe`。首次提示在输入前已消失，App 显示“申请已取消”，身份保护检查未发送凭据。正常重开测试 App 后登录保留，选择并信任原定验收目录 `C:\AgentDockAcceptanceTools`。读取当前安装 CLI 生成的配置接口定义，再通过同一隔离 CODEX_HOME 的 `windowsSandbox/readiness` 确认 `notConfigured`，调用 `windowsSandbox/setupStart` 的 `elevated` 模式重新初始化；未启动模型任务、未切换为无沙箱或完整访问模式。

08:07:58 使用宿主已保存的测试管理员凭据完成新 UAC 授权，凭据仅在内存中传给已核对的 VM 密码框，未显示或记录。08:08 初始化完成通知 `success=true`；原配置连接紧接着仍返回 `notConfigured`，该记录保留。08:09:44 全新连接连续两次 readiness 均为 **`ready`**，独立复核通过。Host 9012 / Desktop 2504 与旧长测 Host 9520 / Desktop 12168 的身份保持不变。此时实际 App 新对话 status 尚待用户回报，不以沙箱初始化替代插件验收；后续人工结果见下一段。证据为 `windows-codex-uac-20261023/approval-result-r2.json`、`windows-codex-app/sandbox-initialize.json`、`sandbox-verify.json`；菜单探针的中文解码及证据遍历错误作为助手失败保留。

2026-10-23 08:57 收到用户从 VM 实际 Codex 桌面 App 新对话回报：**`version=0.1.1-acceptance.20261022.3 / processId=9012 / unfinishedTasks=0`**，与正在长测的安装版 Host 身份一致。本项记为**用户人工确认通过**，不声称代理独立观察了新对话，也不把只读 status 当作新一轮真实模型任务；五角色执行证据仍是上文独立列明的实测。原始字段与来源保存在 `windows-codex-app/manual-new-chat-confirmation.json`。

### Windows 新发现并修复的客户端生命周期问题

`.1`、`.2` 的安装、交互及插件调用本身通过，但收尾连接超时。进程探针复现：Codex app-server 按需创建的后台与客户端处于 Windows Job 中；终止 app-server 后，目标 Host 9716 在 3 秒内退出，exit code 0。既有独立 Host 9520 存活不能替代目标后台的验收。该问题违反窗口/连接退出不结束后台的契约，原失败不改写为成功。

新增 Windows 专用启动实现，通过已登录桌面的 shell 进程创建独立后台；核对同用户、同非零 Session、非提升的 shell 和 token 权限，拒绝受限 token、AppContainer、低完整性借用及 Job 内的 shell，不退回旧的客户端子进程启动。保留原 Host 单实例锁和就绪等待；不改变 Mac、认证、模型或任务重派行为。实现依据 [Microsoft 父进程属性文档](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)。自动按需启动现在要求可访问的登录桌面；无桌面或策略拒绝时返回启动错误。

`.3` VM 回归：目标 Host **5444**、Session 1，`IsProcessInJob=false`；结束 app-server 后等待 10 秒，再用全新隔离客户端连接，**PID 与启动时间均相同**，最后显式 shutdown 才退出。`Probe-Plugin.ps1` 增加真实后台在客户端退出后存活的断言。安装包内实际 MCP 在 Codex 受限沙箱中返回 **`UntrustedClient`**，没有创建后台，未调用模型。安全探针前两轮分别因遗漏 CODEX_HOME 目录、误把单文件安装包当松散 DLL 而失败，修正助手后第三轮通过，三轮证据均保留。

现有受限客户端源码测试已补充拒绝启动断言；当前源码编译零警告、零错误，新增源码测试未单独运行，实际行为由上述 VM 完整包探针验证。独立 architect 对原生结构、句柄、token、参数引用与创建标志复核未发现实质问题，运行结果由主代理验收。

## Windows：安装、桌面及修正凭据后的真实五角色通过

环境：`AgentDock-CleanWin11-20261016`，Windows 11 Pro 25H2 / build 26200，4 vCPU、6 GiB RAM；普通账户 `DockTest`。本轮复用了既有测试 VM，没有恢复为全新操作系统。测试工具目录中有独立 PowerShell/Codex CLI，不把它描述为从未装过工具的机器。

- 单个 EXE 在全新安装目录、仅系统 PATH 下静默安装成功，退出码 0，安装版 Host 返回 `0.1.1-ci.20261021`。普通安装不需手工复制 payload 或编辑清单。
- 默认安装也升级到当前候选；安装前后任务历史哈希一致，用户 Codex 配置哈希未变。
- VM 原生安装检查 **15/15** 通过，覆盖新装/修复、升级锁、未完成任务拒绝、损坏状态拒绝、卸载保留数据、在线 Host 停止后切换、partial/损坏包拒绝。本次使用合成 payload 与真实安装版 Host；完整内嵌 EXE 的安装另列，不把此结果写成完整 payload 的 20 项门禁。
- 新隔离 Codex app-server 识别 7 个工具，并实际读取当前安装版 Host 状态。它不是用户桌面 App 的新对话验收。
- 无桌面的 PowerShell Direct **Session 0** 中，第一个 explorer 真实任务失败：`2778cf06f60143a490ba709a45c08892`，错误 `OpenCode exited with code -1073741502.`，没有 token 用量。该任务事件已读到终态，cursor 3、`hasMore=false`；后四个角色未提交，未自动重试。测试拥有的 Host 已正常停止。
- 该退出值为 `0xC0000142`（DLL 初始化失败；[Microsoft NTSTATUS 定义](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/596a1078-e883-4972-9bbc-49e60bebca55)）。同一普通账户、同一 Session 0 直接执行安装版 `opencode.exe --version` 成功，返回 `1.18.30`。只能定位到任务沙箱启动链路，尚不能确认具体 DLL 或归因于 Session 0，也不能据此认定桌面运行通过或失败。
- 用户确认虚拟机窗口可见并明确授权重新验收后，使用本机保存的测试账户登录，Session 1 的当前安装版成功启动引擎并收到服务响应。首个 explorer 任务 `9d672b28b8154f9880fe12105964092c` 因 VM 独立 Key 过期返回 `api_key_expired / 401`，事件读到终态 cursor 4、`hasMore=false`；后四角色未提交，未自动重试。日常 Codex 配置未变，测试 Host 正常停止。
- 用户首次更新 Key 并明确请求重验后，任务 `6ba1ce5c2ada4e16b1f0294c40d5743c` 仍返回 `401 Invalid credential`；cursor 4、`hasMore=false`，其他角色未提交。仅核对加密凭据文件修改时间为 12:18:10，没有读取或导出 Key。源码确认每个新任务重新调用凭据存储读取，不需重启 Host；本次失败证据保留。
- 用户再次回复“已修正，重新验收”后，**12:44:35–12:45:12** 同一安装版、Session 1、同一 `opencode-go / deepseek-v4.1-flash / max` 完成五角色真实任务。检查结果 42、读写权限、verifier 产物、用量和相同请求幂等性；实际并发 **4**，**29 次采样、0 错误**。Host 9520 / Desktop 12168 的身份与启动时间保持不变，用户 Codex 配置未变，没有中断长测。

| 角色 | 最新成功任务 ID | 最终 cursor |
| --- | --- | ---: |
| explorer | `1dc068aa915847859a8ef99236c1dd49` | 9 |
| default | `cd98d41904574582b2277a23daafcff1` | 11 |
| worker | `7ffb63403c1b47779dc325d0ffec1b91` | 12 |
| reviewer | `63559bbfb3484ef38a53a81c7324bd5f` | 10 |
| verifier | `9a173a28ed3748cb95b1b450028ddf5d` | 10 |

五项均为 `Completed / hasMore=false`；此前失败与本次成功分开记录。

### 修正测试输出解码

`AgentDock.Setup` 输出 UTF-8，而 `Test-SetupGates.ps1` 的重定向读取器在此 VM 使用 gb2312，导致两个中文拒绝信息断言失败（13/15）。使用同一安装器的非法参数对照探针，默认解码产生乱码，显式 UTF-8 保留“未知参数”。测试助手统一设置 stdout/stderr 的 UTF-8 编码后，同一安装器复验 15/15；产品二进制未改动，不需要重打包。

保留了前置助手失败记录：首次未正确等待 GUI 子系统 EXE；后续错误地把没有 `payload-manifest.json` 的安装目录当作原始 payload。这些是测试调用问题，不计为产品缺陷或通过项。

### 桌面与可访问性

- 完整安装版界面/首次引导 **21 项通过**，包含凭据保存后的输入清空、Codex 注册和委派开关、紧凑窗口、任务显示、设置与悬浮窗；退出 Desktop 后 Host 保持运行，日常 Codex 配置不变。测试数据和凭据均为隔离合成夹具。
- **5/5 组 UIA 交互检查通过**：暂停任务载入、设置控件元信息、退出取消/确认、无 Host 时退出，以及通知弹出/置顶/工作区边界/自动消失/点击回到主面板。通知中的两个任务均在本地因没有凭据而失败，没有调用模型。
- 首轮引导超时是 VM 中现有 Codex CLI 位于非标准测试目录且不在 PATH；仅给测试进程加上该目录后完成注册。首轮通知测试仍假定首次打开就是任务页；在 `Probe-WindowsInteraction.ps1` 中先选择任务页，原失败的 `paused-task-visible-in-ui` 断言及整组检查通过。没有改动产品或系统 PATH。
- 实际键盘切换页签、Tab 导航、空格切换/恢复开关通过，焦点始终在自有窗口内，空名称输入框为 0。
- 讲述人语音回顾截图逐项显示“全局并发上限”及五个“角色超时秒数”的名称和数值（4、1200）。截图人工复核通过；自动 UIA 文本提取为空仍保留为失败，不将它改写为自动通过，也不宣称完整无障碍覆盖或音质验收。
- **1920×1080、100% / 96 DPI** 下主窗口、关键设置控件及悬浮窗均在可用工作区内。后续实际切换系统显示，**1366×768 / 100%、1024×768 / 100%、1920×1080 / 150%（144 DPI）** 三组均通过主窗、五个关键设置控件及悬浮窗边界检查，随后恢复 1920×1080 / 100%。Host 9520 和被监测 Desktop 持续存活。此矩阵不包含 150% Toast 的单独定位检查。
- 旧的键盘/讲述人/显示助手只支持 TogglePattern，当前可展开区域使用 ExpandCollapsePattern；复用现有交互探针的处理方式后重新验证。原失败证据保留；本轮创建的讲述人实例已按 PID/启动时间清理。

### 旧候选 12 小时持续运行通过（2026-10-23 收尾核验）

60 秒只读监测预检通过后，正式采样于 **2026-10-22 11:31:38（北京时间）**开始，预计 **23:31:38**满 12 小时，随后有界收尾。锁定当前安装版 Host PID 9520、Desktop PID 12168 及二进制哈希，每 15 秒检查进程身份、窗口响应、IPC、采样间隔及结束清理；任一异常保留为失败，不自动重启拼接时长。

VM 最终记录显示 **2026-10-22 23:31:39 +08** 完成，实际 **43,200.514 秒**，`completed / success=true / fullDurationElapsed=true`。2026-10-23 取回原始文件并重新核验：**2,880 个运行样本 + 1 个收尾样本，序号连续，最大间隔 15.506 秒，监测异常 0，IPC 2,881/2,881 成功，进程身份不变**；结束时 unfinishedTasks=0，候选执行进程=0。结果仅属于 `0.1.1-ci.20261021`，不计入 `.3` 时长。这是以空闲及周期查询为主的监测；8 个采样点存在已授权真实任务活动，不能描述为连续空闲或连续 12 小时四任务负载。

最终证据以 **`acceptance-20261022-soak-collected/`** 的三份原始文件及 `old-soak-final-verification.json` 为准。旧 `soak-live/state.json` 是同步助手失败后的过期快照：Windows PowerShell 5 默认编码读取含中文的最终 launch JSON 可稳定复现 `ArgumentException`，显式 UTF-8 读取成功（`soak-watcher-encoding-check.json`）。原日志只有异常类型，不能将全部 29 次同步错误逐一归因；原未完成状态保留，不混为产品监测异常。新候选的同步助手已补齐 UTF-8 并仅重开宿主同步进程，原状态保存在 `latest-soak-watch-before-utf8-fix.json`，替换助手 PID 13896 已成功复制；两轮产品及 VM 均未重启。

完整 12 小时 Host/Desktop CPU 按 4 vCPU 归一分别为 **0.246% / 0.689%**，合计 **0.935%**；工作集峰值 **94.406 / 158.492 MiB**，私有内存首尾变化 **-4.125 / -4.688 MiB**，没有触发持续增长提示。未触发提示不证明无泄漏；Host 句柄 329→604、峰值 783 的增长仍需结合用途解释，不能由本轮直接确认全部资源预算。初段参考值与原始记录 `initial-resource-sample.json` 保留。

独立 reviewer 任务 `17b6569d7c8d431b947c5ca0566e1891` 已 Completed，事件读至 cursor 88 / hasMore=false，同意旧候选达到探针的 12 小时通过条件。复核特别指出：旧候选最后约 1 小时 52 分钟与 `.3` 并存，系统空闲内存等指标不是单候选隔离基线；结束清洁只指无未完成任务/执行进程，Host/Desktop 继续存活符合监测设计；15 秒采样不覆盖所有短暂事件。原始审查保存在 `old-soak-independent-review.json`。审查仅有旧日志，因此未独立确认同步异常根因；主代理另用上述 PS5 默认编码/UTF-8 对照复现补齐。审查期间两个文档的变化来自主代理，不归为 reviewer 编辑。

长测期间后续任务验收须复用上述已监测 Host；不得重跑会停止 Host 的安装/后端包装脚本或通过重启拼接时长。

### Windows 待完成

最新 `.3` 的实际 Codex 桌面 App 新对话、150% Toast 位置/点击已通过，详见上方补验；旧候选 12 小时已完成。仍需 `.3` 自己的 12 小时最终核验、排除宿主磁盘缓存影响的严格冷启动与最终资源评估。额外 Toast 补验前两次显示助手的系统下拉框不可用及 UIA 空树记录保留；不把这类环境限制计为产品通过。真实下载后的 SmartScreen 与安装路径也未验收，文件复制不计作浏览器下载；Windows 签名仍是可选项。

## Mac：当前安装版五角色与并发通过

环境：真实 Apple Silicon MacBookPro18,3，macOS 26.6.2 / arm64，通过既有 `agentdock-mac` 连接。DMG 只读挂载于独立验收目录。

- 原生安装门禁 **21/21**。默认安装从 `0.1.0-test.20261018.2` 升级为当前候选，保留任务历史和日常 Codex 配置；独立 Keychain 凭据留在 Mac，没有导出。
- 完整安装版在线引导/界面 **21 项**、离线界面 **13 项**通过。合成任务 5 个，期间 **172 次 Host 查询全部成功**，最大 54.02 ms；退出桌面后 Host 继续运行。主面板与悬浮窗截图已检查。合成凭据已删除，隔离测试未改变默认安装指针和日常配置。
- 新隔离 Codex app-server 通过安装版插件连接当前 Host，使用既有独立账户，执行配置为 `opencode-go / deepseek-v4.1-flash / max`。五种角色真实最小任务全部 `Completed`，检查了结果、文件权限、用量和相同请求的幂等性。
- 实际并发达到 **4**；32 次状态/任务/进程采样成功，Host PID 始终为 91390，结束时无未完成任务。每次组合采样最大约 621 ms，不能解释为单次 Host API 延迟。
- 五个任务的事件均读到终态且 `hasMore=false`，没有将接受任务或运行中状态计为完成。

| 角色 | 任务 ID | 最终 cursor |
| --- | --- | ---: |
| explorer | `e3046309e3e146698b46a819b2ad861b` | 9 |
| default | `5ff4ec76ef03476c829c9b30a44e692a` | 11 |
| worker | `29e3607deede45c2b2b919d9d25eb30f` | 12 |
| reviewer | `a7b2eefe7b894429a48d4be7cc441598` | 9 |
| verifier | `4a31e11400a84a48b7795642cd1299c1` | 9 |

负载阶段曾存在两个桌面实例，进程 RSS 求和峰值约 3221 MiB，含共享页重复统计，不能作为正常单窗口常驻内存基线。已按准确 PID/路径关闭本轮重复实例，保留当前默认 Host 与一个 Desktop 供后续人工验收；不能据此关闭严格冷启动/常驻资源验收项。

初次原生助手在安装 21 项通过后，启动 Desktop 并提前等待尚未按需启动的 Host，因而中止；该次没有提交模型任务。随后通过真实 MCP 按需启动 Host，再完成上述五角色负载。原失败记录保留，不改写为整轮成功。

### Mac 原生交互与三处界面缺陷

用户开启 `/usr/libexec/sshd-keygen-wrapper` 的辅助功能权限后，实际查询 `UI elements enabled=true`、输入事件权限为 true。屏幕录制权限仍为 false，本段使用真实 AX 控件和鼠标事件，不声称取得系统截图。

- 原生托盘菜单、主面板关闭后隐藏及托盘重开、悬浮窗显示/隐藏通过；Host 始终存活。
- 实际文件选择器可打开及取消；选取 nvm 的 `bin/codex` 后，系统返回解析后的 `lib/node_modules/@openai/codex/bin/codex.js`，原候选检查退出 127。旧代码只在所选文件同目录找 Node，丢失了 nvm 前缀 `bin/node`。`.app` 选择后可返回主界面，但尚未完成带内置 CLI 的应用包正向验收。
- 原候选通知确实弹出并自动消失，但底部间距为 **568**，不在预期右下角；打开时 `SizeToContent` 尚未完成测量。原始失败记录保留。
- 本地最小修复：仅对精确的 npm Codex 入口补上同安装前缀的 Node 子进程 PATH；通知统一在 Loaded 布局阶段定位，Windows 原有执行时机不变。没有改变用户 PATH、认证或模型。
- 新回归先红后绿：Windows 定向检查 **27 通过 / 2 Mac 跳过**；真实 Mac 定向检查 **29/29 通过**。Mac 修复前真实 `env node` 场景退出 127，修复后成功。首次本地受限 testhost 和 Mac SDK 不匹配的环境失败分别保留，不计作产品通过。
- 在独立目录构建桌面版本 `0.1.1-local.20261022.1`，并未替换默认安装。其真实通知复验通过：工作区 `[0,33,1512,888]`，通知 `[1138,818,350,78]`，右距 **24**、底距 **25**（2 像素容差），自动消失和托盘恢复主面板均通过，Host 91390 存活。实际鼠标点击通知后，主面板在一秒内返回，后续排队通知也正常自动消失。
- 点击探针首轮错误地要求所有通知立即消失；“启动后台”实际产生“正在启动”和“已启动”两条通知。保留首轮失败，按真实队列契约检查后通过，并按 PID 核实窗口。两次均未启动模型任务。
- 修复版文件选择器远程按键未完成系统路径确认，人工选择后用户回复“已选择”。14:42 的按 PID AX 回读确认选择器已关闭，显示 `/Users/hobo/.nvm/versions/node/v25.0.0/bin/codex`，结果为“插件已注册并启用。重启 Codex 后，在新对话中使用。”。本次原生选择返回 `bin/codex`；解析成 `codex.js` 的分支由前述 Mac 29 项先红后绿回归验证，不能混为同一条实际选择路径。人工检查属于隔离桌面 `0.1.1-local.20261022.1`。
- VoiceOver 自动读取播报仍记为未成功；用户人工确认“六个输入框均能读出名称和数值”，覆盖全局并发 4 和五个角色超时 1200。已核对 VoiceOver 关闭。用户实际操作的精确 PID 未独立见证，不把人工结果绑定到某个安装附件哈希。

上述修复后已完成完整 EXE/DMG 重建及隔离安装验收，见 `.3` 记录；源码尚未提交/推送。原云端候选仍含这些缺陷。

后续真实 Retina 模式切换又发现悬浮窗位置问题：`.1` 从 1800×1169 切至 1024×665 后，隐藏重开的悬浮窗仍在 `[1432,878,340,190]`，完全落在屏幕外。修复改为每次显示及屏幕/缩放变化后检查边界，只有越界才重新定位，保留有效用户位置。

`.2` 在上述两档真实 Retina 2× / 120 Hz 下通过主窗口、四个关键设置控件、悬浮窗与通知边界检查；小屏悬浮窗回到 `[656,400,340,190]`，通知右/底间距均为 24。可见悬浮窗跨模式变化通过，手动位置 `[80,100,340,190]` 在隐藏重开后保持不变。原显示模式 1512×982 / 120 Hz 及两个主窗口位置均已恢复。此项是内置屏的系统模式切换，不代表多显示器热插拔验收。

首轮新包原生助手在主窗口尚未就绪时未找到窗口，保留失败；显示扩展助手首轮因 AX 引用寿命问题提前终止，立即恢复显示并修正引用保留后完成回归，保留恢复记录，不把助手失败当成产品通过。悬浮窗外部 reviewer 检查未形成结论，主代理停止后已读到 `Cancelled / cursor 225 / hasMore=false`，没有重派；不声称此项已有独立审查通过。

最终清理核对：日常 `config.toml`、默认 `current.json` 哈希未变，原 Host 91390 / Desktop 91463 存活，测试桌面与隔离 Host 已关闭；没有替换默认安装。

### Mac 待完成

- 修复版原生文件选择后实际 CLI 检查、真实系统缩放、VoiceOver 人工检查已按上述范围通过；`.app` 内置 CLI 正向选择及更多显示器/系统版本仍未验证。
- 本节初次检查时实际 Codex 桌面 App 新对话尚未验证；后续用户已回报 `.3 / Host 37704 / unfinishedTasks 0`，记为人工确认通过，详见上方补验。
- 本机可用签名身份为 0。当前包未经 Developer ID 签名、公证，`spctl` 评估拒绝；SCP 复制不带下载 quarantine，不能作为真实下载后的 Gatekeeper 验收。签名/公证、真实下载路径、干净系统发行仍未完成；未绕过 Gatekeeper。

## 证据

本地证据根目录为 `artifacts/validation/acceptance-20261022/`（忽略目录，不含于 Git 提交）：

- Release 前后快照、`download-checksums.json`、统一安装包 `bundle/`。
- Windows：`vm-inventory.json`、`vm-stage.json`、`windows-headless-summary.json`、`windows-native-gates.json`、`encoding-probe.json`、`windows-native-gates-utf8.json`、`windows-engine-diagnosis.json`、`windows-backend/`、`windows-interactive/`、`windows-desktop/`、`windows-accessibility-r2/`、`windows-live-new-key/`、`windows-live-key-corrected/`、`windows-display/`、`soak-live/`。`narrator-visual-review.json` 单独记录截图人工结论，不覆盖原始自动提取结果。
- Mac：`mac-results/` 中的安装/界面摘要、真实任务及采样、终态事件和截图；`mac-native-results/` 中的原生交互、修复前后 TRX 和隔离构建记录。远端目录 `~/Projects/AgentDock-acceptance-20261022/`，默认安装已升级且保留供人工验收。
- 本轮人工确认：`mac-manual-confirmation.json`；完整新包及摘要：`package-full-lifetime-win/`、`package-full-lifetime-mac/`、`full-lifetime-windows-summary.json`、`windows-full-lifetime/`、`mac-full-results/`。
- 新缺陷原始证据：`mac-full-results/full-fixed-display-matrix.json`、`windows-plugin-lifetime/`；修复后显示矩阵 `mac-full-results/full-final-display-matrix.json`；受限启动拒绝 `windows-launcher-security-r3/summary.json`。旧 VM 门禁临时目录在日志/JSON 归档、传回本地并校验哈希后清理，见 `old-vm-gates-archived.json`，保留安装包、默认数据和长测。

本轮更新不关闭 #4、#9、#10、#12；原云端候选五角色与并发通过，新 `.3` 完整包通过本文列明的安装、交互、插件及 Windows 生命周期回归。历史版本、新修复和人工检查分别记账，运行中的长测不计为已完成。

独立 reviewer 任务 `e3a71f1416f3425cbc120aa7dc0570be` 已完成并读到 cursor 116、`hasMore=false`：两处测试脚本修复及验收状态边界未发现 P1/P2 问题。范围为静态复核和指定证据核对，不替代产品实测、截图复核或 12 小时最终验收；结果保存在 `independent-review.json`。

两处 Mac 产品修复另经 reviewer `4cc57f1766be458a8db4a51b9436893b` 复核，Completed、cursor 28、`hasMore=false`，未发现新增 P1/P2；未代跑 GUI 或测试。详见 `mac-fixes-independent-review.json`。原 worker 在补充测试后被主代理取消，cursor 191、`hasMore=false`；主代理接手完成修复与验证，未重派失败/取消任务。
