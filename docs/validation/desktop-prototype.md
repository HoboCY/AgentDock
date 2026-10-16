# AgentDock 桌面原型记录（Issue #4 / #9）

本文件保留早期独立桌面冒烟结果；最终安装版、后台连接、60 秒 Release 资源和剩余验收以 [acceptance.md](acceptance.md) 为准。后续已补实际控件渲染捕获和 Host 退出隔离验证；登录自启现通过安装根目录的稳定 AgentDock.exe 读取当前版本。

本文只覆盖 `src/AgentDock.Desktop/**` 的实现与验证。后台宿主、执行引擎、打包分发由其他任务负责；本文不宣称整链路验收。

## 范围与边界

- 主面板：项目筛选的任务列表（状态/标题/项目/角色/输入输出 Token/生成速度/耗时），停止所选、查看结果；不提供聊天或新建任务入口。
- 设置：全局凭据（PasswordChar，仅通过 `credential.set` 发送）、五角色固定提示词与超时、全局并发、完成通知（默认关闭）、登录自启（默认关闭）、悬浮窗置顶与透明度、历史清理（先预览实际数量再确认）、完全退出。
- 悬浮窗：约 340×190、半透明、置顶可配、数值字号 12–14pt，只展示活跃任务与真实用量。
- 托盘：主面板 / 显示隐藏悬浮窗 / 启动后台 / 完全退出。
- 生命周期：`ShutdownMode.OnExplicitShutdown`；关窗只隐藏，后台独立进程继续工作；完全退出有未完成任务时先确认再 `shutdown {confirm:true}`。
- 连接：单请求轮询（1s，轮询期间不重叠）、失败自动重试重连；只有显式「启动后台」调用 `EnsureHostAsync`。
- 通知：失败/中断/凭据问题在窗口隐藏时也弹出置顶提示；普通完成默认静默，可在设置中开启。
- 数据：全部来自 `TaskSummary/TaskDetail`；未知用量显示“待更新”，不使用演示数据或估算值。
- 图标：代码绘制（圆角矩形 + A），不拉取外部资源。

## 环境

- Windows 10.0.26200（win32），.NET SDK 10.0.401，运行时 .NET 10.0.12。
- Avalonia 11.3.22（本地 packages 目录：`D:/Projects/AgentDock/artifacts/dotnet-home/.nuget/packages`）。
- 构建使用 `--no-restore`，`DOTNET_CLI_HOME=artifacts/dotnet-home`（保持本地运行状态）。
- 运行时不启动/不连接后台；本机开发输出下无同级 `host/AgentDock.Host.exe`，属于预期。

## 构建验证

```
dotnet build src/AgentDock.Desktop/AgentDock.Desktop.csproj --no-restore
```

结果：成功，0 警告 0 错误（`TreatWarningsAsErrors=true`）。

## UI 冒烟测试（真实窗口）

```
dotnet src/AgentDock.Desktop/bin/Debug/net10.0/AgentDock.Desktop.dll --smoke-test artifacts/validation/desktop-smoke.json
```

结果：退出码 0；`ok=true`。真实显示/隐藏主面板与悬浮窗，不提交任务、不依赖后台：

| 断言 | 实测 |
| --- | --- |
| 主面板真实显示/隐藏 | true / true（1020×680） |
| 悬浮窗真实显示/隐藏 | true / true（340×190） |
| 悬浮窗置顶 | true |
| 悬浮窗透明度 | 0.88 |
| 托盘对象创建 | true |
| 无后台时不崩溃、状态为未连接 | hostReachable=false，未崩溃 |

Debug 构建实测资源（`artifacts/validation/desktop-smoke.json`，仅作现状记录，不构成资源预算）：

| 阶段 | 工作集 | 私有内存 | 线程 | 距进程启动 |
| --- | --- | --- | --- | --- |
| 主面板显示 | 135.9 MB | 108.4 MB | 90 | +1.84s |
| 主面板隐藏 | 137.0 MB | 108.8 MB | 90 | +2.15s |
| 悬浮窗显示 | 141.2 MB | 117.4 MB | 90 | +2.67s |
| 悬浮窗隐藏 | 141.8 MB | 118.4 MB | 90 | +2.98s |

## `--tray` 启动检查

```
dotnet src/AgentDock.Desktop/bin/Debug/net10.0/AgentDock.Desktop.dll --tray
```

结果：无后台环境下进程保持存活 ≥5s（主面板隐藏、托盘可用），确认 opt-in 登录自启入口不会因后台缺失而崩溃；随后由检查脚本结束进程。

## 未自动验证（需要真实后台/桌面交互或安装环境）

- 托盘四项菜单与左键打开主面板的可视交互（冒烟只断言托盘对象创建）。
- 关窗后任务继续：需要真实后台与运行中任务。
- 失败/中断/凭据失效通知在窗口隐藏时的实际弹出效果。
- `config.set` 仅影响新接收任务：需后台侧任务快照验证（UI 文案与调用路径已实现）。
- `credential.set` 后凭据状态与失效错误路径：需后台运行。
- 登录自启复选框写入注册表：需要安装版目录结构（`../desktop/AgentDock.Desktop.exe`）。
- 历史清理预览/确认的端到端计数：需后台数据。
- macOS：托盘、透明置顶、字号在真实设备上的表现未验证，不能以 Windows 结果替代。

## 已知缺口与风险

- 冒烟测试不断言像素级渲染或托盘图标可见性，只断言窗口对象与真实显示/隐藏状态。
- 开发输出无同级 host 时，「启动后台」会显示 `未找到配套后台程序` 的明确错误，而不是崩溃；打包后由 `../host/AgentDock.Host.exe` 或 `AGENTDOCK_HOST` 提供。
- 主面板列表每次轮询重建（任务量小），未做增量 diff；极端任务量下有待优化。
- 代码约 1400 行 / 10 个文件（略高于 600–1000 行的指南目标），取舍：无 MVVM/DI/响应式框架依赖，包含 smoke 测试工具与全部设置面板；未引入第三方组件。
