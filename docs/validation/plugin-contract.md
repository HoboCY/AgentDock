# Codex 本地插件接入契约（P1 / #2）

2026-10-16，在 Windows 上通过独立 CODEX_HOME 实测。宿主 `codex-cli 0.162.0-alpha.17.2`。本节的插件探针使用合成 ping 工具；AgentDock 正式接口另列集成验收结果。

## 已验证链路

1. `codex plugin marketplace add <marketplace-root> --json`
2. `codex plugin add agentdock-probe@agentdock-local --json`
3. `codex app-server --listen stdio://`，依次发送 `initialize`、`initialized`、`thread/start`。
4. `mcpServerStatus/list`：工具已连接并发现 ping。
5. `mcpServer/tool/call`：实际返回 `PONG_FROM_AGENTDOCK_PROBE`。
6. `codex plugin remove`：删除插件缓存和对应启用条目；marketplace 登记仍在隔离配置中。

不需要模型调用、登录或复用任何现有凭据。只使用隔离配置；不接管旧 deepseek_agents 或 architect。

## 文件与路径

Marketplace 使用 `.agents/plugins/marketplace.json`；插件的兼容清单为 `.codex-plugin/plugin.json`，`mcpServers` 指向 `./.mcp.json`。清单内 stdio server 使用 `command: "./runtime/AgentDock.Host.exe"`、`args: ["--mcp"]`、`cwd: "./"`。安装后运行插件缓存副本。

**本机版本不展开 args 内的 `${PLUGIN_ROOT}`，也未注入同名环境变量。** 不依赖它们。相对 command 和 cwd 已实测可用。

安装包交付 self-contained .NET 程序及经过验证的引擎组合。普通用户安装时无需 Python、Node 或 .NET SDK。实际发布前还需要未安装开发环境的干净 Windows 验证。安装或更新后引导重启 Codex 或开启新对话；不承诺现有对话热加载。

参考：[官方插件打包说明](https://developers.openai.com/plugins/build/plugins)。上文路径行为以本机实际调用为准。

## 正式程序集成结果

最终 0.1.0 win-x64 自包含包已安装到隔离目录；同一 Codex 版本注册 `agentdock@agentdock-local` 后，`mcpServerStatus/list` 发现七项工具并显示 connected，真实 `status` 返回版本 0.1.0、独立后台 PID、unfinishedTasks=0、canUpdate=true。证据：`artifacts/validation/plugin-final.json`。

插件缓存内 Host 代理通过安装器生成的 `runtime/install.json` 找到稳定 `install/current.json`，再启动当前 Host。stdio 输入输出显式 UTF-8；Windows 默认控制台代码页曾导致 tools/list 超时，已修复并通过真实 app-server 重测。客户端断开后后台继续独立运行，桌面探针验证退出桌面不结束 Host。

旧配置识别仅覆盖当前用户 `.codex/config.toml` 中明确名为 `deepseek_agents` 且未被禁用的 MCP 段；不同 CODEX_HOME、改名入口、插件或包装脚本不能可靠识别。只做提示，不改配置，不读取旧服务的密钥文件。隔离探针前后原 Codex 配置哈希相同。
