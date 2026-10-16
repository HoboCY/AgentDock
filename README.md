# AgentDock

面向 Codex 外部子代理的 Windows 桌面控制台。任务由 Codex 派发；主面板、托盘和透明悬浮窗用于配置、监控、查看结果和停止任务。

实现基线：[Issue #1](https://github.com/HoboCY/AgentDock/issues/1)。当前交付状态及未通过的验收项见 [验收矩阵](docs/validation/acceptance.md)。macOS 尚未通过真实设备验收，不声明正式支持。

## 安装与使用

见 [安装说明](docs/installation.md)。Windows 包包含 .NET 运行时、固定版本 OpenCode、Codex 沙箱和 MinGit；最终用户不需要安装开发 SDK、Node 或 Python。OpenCode Go 凭据在主面板独立配置，不迁移既有 MCP 凭据或角色。

默认全局并发 4；同项目及父子项目的写任务串行。关闭窗口不会停止任务，重连不会重跑。完全退出是独立操作。后台异常退出后，已运行任务中断，队列暂停；Codex 需明确决定后续执行。

## 开发

需要 Windows 和 .NET 10 SDK（`global.json` 指定版本系列）。

```powershell
dotnet restore AgentDock.slnx --configfile NuGet.Config
dotnet build AgentDock.slnx -m:1 --no-restore
dotnet test AgentDock.slnx -m:1 --no-build
pwsh -File scripts/Build-Package.ps1 -Version 0.1.0 -Runtime win-x64
```

执行沙箱测试需要显式设置 `AGENTDOCK_TEST_OPENCODE`、`AGENTDOCK_TEST_SANDBOX`、`AGENTDOCK_TEST_SHELL`，指向打包的固定版本可执行文件；操作系统权限测试还需要 `AGENTDOCK_ENGINE_SANDBOX_TESTS=1`。这些测试使用合成凭据和本地脚本化模型，不产生真实模型费用，也不能替代 OpenCode Go 实际账户验收。

开发时可分别启动 `AgentDock.Host --host --home <独立数据目录>` 和 `AgentDock.Desktop --home <同一目录>`。桌面旁缺少打包的 Host 时，设置 `AGENTDOCK_HOST` 为开发 Host 可执行文件绝对路径。普通启动不自动保存凭据或派发任务。

## 结构

| 模块 | 职责 |
| --- | --- |
| Core | 持久化、调度、状态转换、保留策略、客户端协议 |
| Host | 单实例后台、本地管道、stdio MCP、全局配置 |
| Engine | OpenCode Go 适配、权限沙箱、进程树、用量与变更证据 |
| Desktop | Avalonia 主面板、托盘、悬浮窗、通知 |
| Setup | 每用户安装、版本切换、空闲更新、卸载 |

契约见 [架构](docs/architecture.md)、[插件](docs/validation/plugin-contract.md)、[执行引擎](docs/validation/execution-contract.md)。任务正文与结果从结束时保留 7 天，摘要保留 30 天；清理不会删除防止重复执行所需的请求身份。
