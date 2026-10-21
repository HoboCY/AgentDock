# packaging 契约（#2 / #10）

本目录是安装包与运行的固定版本组合来源。相关约束见 `docs/adr/0005-distribute-desktop-runtime-with-plugin-integration.md` 与 Issue #10。

## 目录

```
packaging/
  runtime-lock.json      固定运行时清单（官方来源、哈希、入包路径）
  Fetch-Runtimes.ps1     按清单获取运行时（缓存/本机已装位优先且必须过哈希；否则官方下载校验后解包）
  plugin/                Codex 插件源模板（本地 marketplace）
  .cache/                Fetch 本地缓存（不入库）
```

## 运行时固定与获取

- 只允许 `runtime-lock.json` 列出的官方来源（npm registry / 官方 Release）；下载先校验 sha512/sha256 与体积，
  再解包并逐文件校验 SHA-256。任何不符都直接失败，绝不静默降级。
- `kind = "npm"`：允许“使用本机已安装的公共程序位”（例如旧 deepseek_agents 的工具目录），但必须命中清单哈希；
  不匹配即失败（`-Force` 才改走官方 npm 下载）。
- `kind = "zip"`：整包归档（当前为 MinGit）。查找顺序：`-CacheDir` → `AGENTDOCK_RUNTIME_CACHE` →
  `<repo>/artifacts/runtime-downloads` → `packaging/.cache`，命中且哈希通过才使用；否则从官方 URL 下载。
  解包带路径越界防护，随后校验 `verify` 中列出的关键文件并写入 `runtime-manifest.json.archives`。
- 只复制/解包清单允许的内容；绝不打包任何设置、凭据、密钥、任务数据或旧 MCP 配置。
- **Git（MinGit）随包分发，不允许系统 Git 前置条件**：干净 Windows 无需预装 Git；Host `PackageDefaults`
  默认使用 `engines/git/usr/bin/sh.exe`（GNU Bash）。`Build-Package.ps1` 会再次核对归档哈希与 `sh.exe` 哈希，
  缺失或哈希不符即构建失败。安装程序把内置路径与哈希写入 `install/machine.json`（`shellPath`/`shellSource`/`shellSha256`），
  仅在完整包缺失内置 Git 时才回退系统 Git Bash 并给出警告。

## payload 布局（zip / 文件夹，根即 payload）

```
desktop/AgentDock.Desktop.exe
host/AgentDock.Host.exe
engine/AgentDock.Engine.exe
setup/AgentDock.Setup.exe              # Windows 精简启动/卸载器，不内嵌程序包
engines/opencode/opencode.exe          # OpenCode 1.18.30（固定）
engines/codex/...                      # Codex sandbox 0.153.4（固定）
engines/git/...                        # MinGit 2.56.0.2 整包解包（usr/bin/sh.exe 为 Host 默认 shell）
engines/git/git-runtime.json           # 归档哈希/来源与 sh.exe 哈希（构建时生成）
plugin/...                             # Codex 插件源（含 Host 副本 runtime/）
runtime-lock.json
runtime-manifest.json                  # Fetch 产出（文件/归档来源与哈希）
payload-manifest.json                  # 每文件 sha256 + size；安装/更新前逐文件校验
```

`payload-manifest.json` 由 `scripts/Build-Package.ps1` 生成；安装程序解包时逐文件校验，拒绝清单外文件。

Windows 分发 EXE 在上述 ZIP 完成后再次构建，将 ZIP 作为资源内嵌；安装时直接读取资源并复用相同校验。默认优先使用内置包，忽略下载目录的其他 ZIP；`--payload` 作为显式维护入口保留。安装后的稳定启动器复制自已校验的 `setup/AgentDock.Setup.exe`，避免日常启动携带整个分发包。`-PayloadFormat folder` 也会为完整 Windows 安装器生成所需 ZIP。

## 每用户安装布局（安装程序）

```
%LOCALAPPDATA%\AgentDock\
  AgentDock.exe               稳定启动器（安装程序自身副本，启动 current 版本）
  install\
    current.json              当前版本指针：version/root/host/desktop/engine/plugin/dataHome
    update.lock               更新窗口标记（fail closed）
    machine.json              只读检测结果：内置 Git（engines/git/usr/bin/sh.exe + 哈希，优先）或系统回退、
                              Codex CLI、旧 deepseek_agents 提示
    setup.log                 安装/更新日志（无凭据）
    <version>\...             固定版本组合的程序文件（旧版本保留，不原地覆盖）
  plugin\                     Codex 本地 marketplace 的稳定路径（跨版本稳定）
    plugins\agentdock\runtime\AgentDock.Host.exe
    plugins\agentdock\runtime\install.json   安装程序生成的绝对路径清单
  data\                       用户数据（安装/更新/卸载都不清理）
```

- 安装、更新不触碰旧 `deepseek_agents` 配置、凭据或 Codex 用户配置；仅只读检测并提示。
- 卸载只删除 `install\`、`plugin\`、稳定启动器、开始菜单快捷方式与 HKCU 卸载项；保留 `data\`。

## 更新/安装/卸载竞态门禁（#10，Host 与安装程序共同遵守）

**Host 侧（已实现）**

1. `--host` 启动时先独占持有 `data\launch.lock`（`FileShare.None`，持有整个生命周期），
   再检查 `parent(home)\install\update.lock`；标记存在则立即退出，绝不接纳任务。
2. `start_task` / `resume_task` 每次调用都复核更新标记，标记存在即拒绝（`UpdateInProgress`）。
3. 第二个 `--host` 因拿不到 `launch.lock` 而退出；后台崩溃时 OS 释放锁，不留死锁。

**安装程序侧（OperationGate，统一门禁）**

1. 版本检查只由用户在桌面程序中主动发起（GitHub）；已安装引擎不自动更新。
2. 门禁顺序必须是：清理/拒绝外部标记 → 写入本次 `install\update.lock` → 停后台（在线且空闲时 `shutdown { confirm: false }`）
   → 独占获取 `data\launch.lock` → 持有到文件切换结束 → 删除标记 → 释放锁。
   **绝不先拿 `launch.lock` 再停后台**，避免与启动中的 Host 互相等待。
3. 后台离线（命名管道不存在）时调用 `TaskService.CountPersistedUnfinished(home)`：
   排队/运行/暂停任一存在即拒绝；`tasks.json` 损坏或 schema 不识一律拒绝（fail closed）。
4. 陈旧标记按持有者进程判定：进程存活 → 拒绝；确认已退出 → 清理；无法解析 → 15 分钟内拒绝，超时才清理。
   更新标记可能先于 `launch.lock` 被缺席的 Host 看到——那是预期行为（让它快速失败）。
5. 安装、同版本修复、更新、卸载共用同一门禁；安装失败不切换 `current.json`，旧版本保持可用，用户数据不动。
6. `scripts/Test-SetupGates.ps1` 在真实 Windows 上覆盖以上场景（隔离 `--root`，不依赖命名管道；
   `-HostExe` 时额外验证在线停机场景）。

## 插件代理解析顺序（插件内 Host）

`plugin/plugins/agentdock/.mcp.json` 使用 `./runtime/AgentDock.Host.exe --mcp`（相对路径，Codex 不展开 `${PLUGIN_ROOT}`、不注入环境变量）。
插件内 Host `--mcp` 代理应依次解析：

1. 显式参数；
2. 用户自行设置的 `AGENTDOCK_HOST` 环境变量（安装程序不静默修改用户环境）；
3. `runtime/install.json`（安装程序在 `%LOCALAPPDATA%\AgentDock\plugin` 生成）；
4. 默认 `%LOCALAPPDATA%\AgentDock\install\current.json`；
5. 找不到即快速失败，不改读旧 deepseek_agents 配置。

## 版本与发布门槛

- `Build-Package.ps1` 默认 self-contained；`-FrameworkDependent` 仅供本地自检且必定 `releaseBlocked`。
- 组件项目缺失，或项目存在但发布产物缺少预期 exe，都视为 partial；未显式传 `-AllowPartial` 时直接构建失败。
- `package-manifest.json` 记录 `partial`、`missingComponents`、`releaseBlocked`、`releaseReady`、`hostVersion`
  与内置 Git 的归档/`sh.exe` 哈希；只有 `releaseReady == true` 才允许作为稳定发行候选。
- 未签名测试版可使用 [GitHub 预发布流程](../docs/releasing.md)：必须完整、自包含并通过自动化和安装门禁，保留 `releaseReady=false` 与全部阻塞项，明确标注 Pre-release，不成为 Latest。默认只创建草稿；公开测试版由维护者发布草稿或手动关闭 `draft`。
- Git（MinGit）缺失或哈希不符时构建必然失败——发行包不允许要求系统预装 Git。
- Windows 签名与干净虚机、Mac Developer ID 签名/公证与下载后的 Gatekeeper 验收分别作为发布门槛；公开发布需明确授权。真实 Mac 功能验收已完成，当前包保持 `releaseReady=false`，不能把本地可运行当成发行通过。

Mac arm64 使用 [独立锁文件和打包说明](mac/README.md)。其最终 payload、安装器 ZIP/DMG、运行时来源和实机结果见 [macos.md](../docs/validation/macos.md)。
