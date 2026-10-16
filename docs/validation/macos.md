# macOS 实机验证（Issue #12）

2026-10-16，MacBookPro18,3、arm64、macOS 26.6.2 (25G83)、8 逻辑处理器。用户授权 SSH 接入和真实设备验收。构建 SDK 10.0.401 / Runtime 10.0.12 安装在本任务隔离目录，未替换用户 SDK、Codex 配置或旧 MCP。运行时固定 OpenCode 1.18.30、Codex sandbox 0.153.4。

这些结果来自真实 Mac。后端权限测试使用实际 OpenCode/Codex 二进制和本机脚本化上游；完整安装版另通过用户在 Mac 主面板独立配置的真实 OpenCode Go 账户验收。Windows 凭据未复制到 Mac。原有 Codex 配置前后哈希一致。

## 后端与权限

- Core/Host 最新回归 28 项通过：APFS 大小写与 `/var` 别名归一、父子目录串行、按需 GUI 会话 Host、单实例、100 次连续状态请求、MCP EOF 生命周期、动态 current 指针与旧客户端更新后的启动路径。
- Keychain 在 GUI launchd 会话测试 6 项通过，覆盖写入/读取/覆盖、数据目录隔离、大小写等价账户和安全删除。SSH 直接进程曾报 KeychainLocked，修复后由登录桌面会话启动 Host；仅属性检测不能等同成功读密钥。
- 受限客户端即使掌握测试连接令牌，也不能调用管理管道；Host 通过 socket peer audit token 检查沙箱身份。未以只比较 PID 替代稳定身份。
- 五角色真实沙箱测试验证项目修改、相邻目录/符号链接/Host 数据目录访问拒绝、verifier 专属产物、用量与无凭据继承。最新数据目录权限专项 6 项通过。
- 登录启动与按需 Host 独立：默认不写用户 LoginAgent；显式开启后使用按数据目录散列的注册项，关闭删除自己的注册项。测试未修改用户的其他启动项。

## 任务停止与崩溃

每项任务由独立 launchd job 建立进程 coalition，可信控制目录位于沙箱拒绝访问范围。执行启动前核验 PID、pidversion、coalition、开机会话 UUID 和许可文件；没有 PID 复用时的降级 kill 路径。Host 的 stdin lease、独立 watchdog、job runner 分离，Host 或 watchdog 死亡会收敛任务进程组。

15 项专项测试覆盖正常结束、取消、超时、Host 崩溃、watchdog 崩溃，以及 double-fork/setsid/关闭标准流后的逃逸子进程停止。审查发现的控制目录复用、旧启动周期恢复、同名 job 冲突、无限日志、错误吞掉、退出泵排空等问题已修复并回归。日志单流硬限制 64 MiB，限制在写盘之前执行；超限导致失败，不能冒充正常退出。恢复仅扫描当前数据目录，避免清理其他安装的数据。

Engine GUI 会话最终回归为 87 项通过、19 项 Windows 专属跳过、0 失败（`mac-engine-final.trx`）；Core/Host 28 项通过（`mac-core-host-final.trx`）。最终发布代码再次跑通 Core/Host 的 27 项常规用例及单独启用的 1 项真实沙箱管道拒绝用例（`mac-core-host-release-final.trx`、`mac-final-peer-denial-enabled.trx`）。纯接口模拟不替代上述实际进程测试。

## 桌面样本

Release 构建在登录桌面会话显示实际 Avalonia 控件，主窗 1020×680；悬浮窗 340×190、透明度 0.88、置顶。托盘创建、隐藏与重开通过，实际控件图像已检查中文显示。60 秒样本桌面工作集约 151.3 MiB，8 核归一 CPU 约 0.73%；主窗 Opened 约 1.55 秒，未清空文件缓存，不称为冷启动。

本次 UI 探针使用独立目录，未连接已安装 Host，所以不证明完整安装链路。Mac 不支持的 .NET 私有/峰值内存字段产生 0，应视为不可用而非实际零占用。

收尾时另以完整安装版连接已有五项真实验收任务，发现 Avalonia 清空回收列表容器时以 null 重建模板，旧行模板因此崩溃。已修复空值处理；Windows 用合成持久化任务复现相同故障并验证修复。Mac 最终安装版通过任务列表、主窗/悬浮窗显示隐藏重开、Host 可达与托盘对象断言，随后在任务列表保持可见时再次跑完五角色真实任务，面板持续存活。证据 `mac-installed-ui-final.json`、`plugin-ui-live-final.json` 和 `final-ui-install.json`。

## 完整包、插件与安装事务

构建输入为核对官方归档的 OpenCode/Codex 和从 Git 2.54.0 官方源码编译的 arm64 Git。Git 附源码与许可证，实际 init/add/commit/diff/clone 通过；Git 对系统动态库的依赖已检查。完整包在只含系统目录的 PATH、移除 SDK 环境变量后完成安装与真实任务；无 Homebrew、Node、Python、SDK 或 CLT 运行前置条件。验证脚本本身使用 Python。当前设备装有开发工具，因此这项环境隔离不等于干净系统验收。

- 最终候选 `package-candidate-4` 为 0.1.0 自包含 arm64：安装器 ZIP **363.65 MiB**、DMG **372.71 MiB**、payload ZIP **302.27 MiB**。安装器已内含 payload，不需额外下载 payload ZIP。
- 默认安装到 `~/Library/Application Support/AgentDock`，稳定入口为其 `AgentDock.app`；`~/Applications/AgentDock.app` 仅链接此入口。用户已经打开主面板并独立保存凭据。原有 Codex 配置未修改，登录自启默认关闭。
- 实际 Codex CLI **0.159.2** 在隔离 CODEX_HOME 注册稳定插件，发现七项工具并真实调用 Host；这里的宿主版本与包内 Codex sandbox **0.153.4** 分开记录。
- 最终 **21/21** 安装门禁通过：全新安装、同版本/文件损坏/权限损坏修复、稳定启动器修复、运行/排队/暂停拒绝安装更新卸载、损坏状态拒绝、8 种非法载荷、切换失败回滚、ZIP 更新保留数据、卸载保留无关目录。卸载无有效安装回执的任意目录会被拒绝，防止同名目录误删。
- 真实完整 payload **0.1.0 → 0.1.1-validation** 在隔离安装根升级；保持原 Codex app-server/MCP 会话，下一次调用返回新版本与新 Host PID，合成用户数据保留。登录启动 enable/disable 的配置与 0600 plist 同步通过；此测试没有注销或重启真实用户会话。
- 最终安装器修复用户默认安装后，原五个任务身份及状态保留，凭据仍显示已配置。候选 4 相比候选 3 只改变桌面与重编译的 Core DLL，Host/Engine 主程序及固定运行时未变；最终包仍重新通过五角色真实账户任务，面板已恢复打开。
- 使用最终安装器卸载上述隔离真实 payload，程序入口、插件、current 指针已移除，合成用户数据保留；默认用户安装继续可用。DMG 只读挂载后执行安装器 `--version` 成功，检查完成已卸载镜像。

| 构件 | SHA-256 |
| --- | --- |
| 安装器 `.app.zip` | `108296db575d4902d3f36dc2af015d2784d7e36b10ae4e7a29a5d7d2ce533f26` |
| 安装器 `.dmg` | `3e4d6390bf2a44af4dbd895848fbed1afac6f417282f4113f0aed39f039bd855` |
| payload `.zip` | `3b780d6b6d7de750adfc955f749595bf95f51088833d932f88a7192a1c4e219e` |

本地交付目录 `artifacts/release-candidate/final-ui-20261016/osx-arm64/` 包含安装器和清单，传输后重新校验大小与 SHA-256；Mac 的本任务 `package-candidate-4/` 保留全部构件。前述升级、真实卸载及 DMG 入口证据来自候选 3，其安装器实现未随 UI 修复改变；候选 4 再次通过全部 21 项安装门禁。

## 真实账户与四任务负载

用户于 2026-10-16 在 Mac 主面板独立保存 Go 凭据后执行：Codex → 安装版 MCP → Host → OpenCode Go → deepseek-v4.1-flash。五个合成项目输入均为 41；default/worker 写入 answer.txt=42，explorer/reviewer 不改项目，verifier 的检查产物留在专属目录。五角色全部 Completed，有真实非空用量和结果。重复 request_id 返回相同已完成任务，不再次执行。探针只读取凭据是否已配置，不读取或导出密钥。

| 角色 | 输入 Token | 输出 Token（含推理） | 最后一轮生成 Token/s |
| --- | ---: | ---: | ---: |
| explorer | 8292 | 179 | 138.23 |
| default | 14869 | 322 | 151.66 |
| worker | 14853 | 311 | 138.66 |
| reviewer | 8373 | 276 | 173.96 |
| verifier | 9231 | 1215 | 161.72 |

以下用量和资源表来自候选 2 的首次实机账户样本，未重新测量最终 UI 修复后的资源。explorer 完成后并发运行其余四角色。62 个一秒样本无采样错误，其中 13 个样本观察到四项同时 Running。

| 指标 | 观测峰值 |
| --- | ---: |
| Host RSS | 137.31 MiB |
| Desktop RSS | 149.67 MiB |
| 执行进程合计 RSS | 2630.45 MiB |
| 全部合计 RSS | 2883.23 MiB（约 2.82 GiB） |
| CPU（8 逻辑处理器归一） | 58.40%，采样均值 5.73% |

RSS 求和可能重复计算共享页，一秒采样可能漏掉短命进程；这些是样本而非性能上限。完成后独立检查未发现安装目录下遗留 OpenCode、Codex sandbox 或 Engine 任务进程。原有已登录桌面保持开启。

## 发行缺口

构建目标最低 macOS 14，但本次仅实测上述 macOS 26.6.2 arm64；不推广为其他 OS 版本或 Intel 已支持。未配置 Developer ID 和 notarization/stapling。只读 `codesign --display` 显示 apphost 的 ad-hoc 签名、无 TeamIdentifier、Info.plist 未绑定；`spctl --assess` 拒绝，报告 `code has no resources but signature indicates they must be present`。当前本地构件无下载 quarantine 标志，直接运行成功不能证明浏览器下载后的 Gatekeeper 能通过。未关闭 Gatekeeper 或删除隔离标志。

正式签名/公证、下载分发链路、干净系统、不同缩放及屏幕阅读器、系统托盘菜单和通知弹出的人工验收仍未完成，包保持 `releaseReady=false`。公开发布未授权，#12 不按发行通过关闭。

证据目录：本地 `artifacts/mac-validation/` 的 `plugin-live.json`、`samples.jsonl`、`resource-summary.json`、`mac-installer-gates-4.json`、`real-upgrade-and-login.json`、`plugin-real-upgrade-1.json`、`final-installed-checks.json`、`final-ui-install.json`、TRX 与窗口图像；Mac 本任务目录保留原始输出。测试项目均为合成数据；凭据、用户配置和任务正文不纳入代码仓库。
