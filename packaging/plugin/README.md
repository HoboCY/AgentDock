# AgentDock 插件源（打包模板）

此目录是打包用的插件源模板，Build-Package.ps1 会把它复制到安装包 payload 的 `plugin/` 目录，并把 `plugin.json` 的 `version` 改写为包版本。

```
plugin/
  .agents/plugins/marketplace.json          # 本地 marketplace（稳定路径由安装程序产出：%LOCALAPPDATA%\AgentDock\plugin）
  plugins/agentdock/.codex-plugin/plugin.json
  plugins/agentdock/.mcp.json
  plugins/agentdock/runtime/AgentDock.Host.exe   # 打包时注入 Host 副本（代理入口）
  plugins/agentdock/runtime/install.json         # 安装时生成的本机绝对路径清单（不随仓库分发）
```

## 已验证的 Codex 接入契约（Codex 0.162.0-alpha.17.2，隔离 CODEX_HOME）

- `.mcp.json` 中 `command: "./runtime/AgentDock.Host.exe"`、`cwd: "./"`、`args: ["--mcp"]` 是相对插件根解析的；
  插件被 Codex 复制到 `$CODEX_HOME/plugins/cache/<marketplace>/agentdock/<version>/` 后，相对路径仍然成立
  （原型已验证 `./bin/node.exe` + `cwd: "./"` 的真实 MCP 调用链路）。
- Codex 不会展开 `${PLUGIN_ROOT}`，也不会向 MCP 进程注入环境变量。因此本机安装路径不能依赖环境变量，
  只能由安装程序生成绝对路径清单文件。

## Host 代理解析顺序（供 Host/Engine 实现方遵守）

插件内的 `AgentDock.Host.exe --mcp` 是代理入口，必须按以下顺序解析“稳定安装的 Host”：

1. 显式参数（如 `--host <abs path>`，若提供）。
2. 用户自行设置的 `AGENTDOCK_HOST` 环境变量（安装程序不会静默修改用户环境）。
3. 插件内安装清单 `runtime/install.json`（由 AgentDock 安装程序写入，绝对路径、跨版本稳定）。
4. 默认安装位置：`%LOCALAPPDATA%\AgentDock\install\current.json`（或 `AGENTDOCK_HOME` 同级布局）。
5. 都找不到时快速失败，向 Codex 返回明确错误，不得静默改读旧 deepseek_agents 配置或凭据。

`runtime/install.json` 结构（安装程序生成）：

```json
{
  "schema": 1,
  "product": "AgentDock",
  "version": "0.1.0",
  "installRoot": "C:\\Users\\<user>\\AppData\\Local\\AgentDock",
  "hostManifest": "C:\\Users\\<user>\\AppData\\Local\\AgentDock\\install\\current.json",
  "launcher": "C:\\Users\\<user>\\AppData\\Local\\AgentDock\\AgentDock.exe",
  "dataHome": "C:\\Users\\<user>\\AppData\\Local\\AgentDock\\data"
}
```

`install.json` 与 `current.json` 都只包含路径与版本，不含凭据、令牌或任务数据。
