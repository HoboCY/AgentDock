# macOS arm64 构建与安装

## 用户安装教程

适用于 **Apple Silicon（M 系列）Mac，macOS 14 或更高版本**。Intel Mac 暂不支持。安装包已内置运行时，无需自行安装 .NET、Node、Python 或 Git。

1. 在 [Releases 页面](https://github.com/HoboCY/AgentDock/releases) 下载 `AgentDockSetup-<版本>-macos-arm64.dmg`。只需这一个文件，Source code 是源码，不用于安装。
2. 双击下载的 DMG，Finder 会打开名为 `AgentDock <版本>` 的磁盘窗口。
3. 双击窗口内的 `AgentDockSetup-<版本>-macos-arm64.app` 安装器；Finder 隐藏扩展名时可能不显示 `.app`。由安装器自动完成文件安装和创建启动入口。
4. 出现“安装 AgentDock …？”对话框后，点击 **“安装”**，等待显示“安装完成”。
5. 在完成对话框中点击 **“启动”**，进入 AgentDock 主面板。
6. 在“开始使用”中依次完成：启动后台 → 保存你自己的 OpenCode Go API Key → 接入 Codex → 启用全局委派。接入完成后重启 Codex 桌面 App，并新建对话验证 AgentDock 工具。
7. 安装完成后可在 Finder 中推出 `AgentDock <版本>` 磁盘。以后从用户“应用程序”目录中的 `AgentDock.app` 启动；Finder 按 `Shift + Command + G`，输入 `~/Applications` 即可进入。

如果用户“应用程序”目录中没有入口，在 Finder 按 `Shift + Command + G`，输入 `~/Library/Application Support/AgentDock`，打开其中的 `AgentDock.app`。首次配置的详细说明见 [安装与接入指南](../../docs/installation.md#二安装)。

### 首次打开被 macOS 拦截

`0.1.0-beta.1` 的安装器存在资源签名缺失，可能显示“已损坏”。请改用 `0.1.0-beta.2` 或后续版本；重新下载旧版本不能修复此问题。

当前预发布包已做 ad-hoc 资源封装，但**没有 Developer ID 签名和 Apple 公证**，仍可能被 Gatekeeper 拦截。资源完整性校验通过不代表 Apple 信任此 App。如果提示“无法验证开发者”或“Apple 无法检查是否包含恶意软件”，并且你确认文件来自本仓库的 Release、信任其来源，可按 Apple 提供的方法处理：

1. 先尝试打开安装器一次，再进入 **系统设置 → 隐私与安全性**。
2. 找到对应 AgentDock 的拦截提示，点击 **“仍要打开”**（Open Anyway），按提示认证并确认打开，然后继续安装。

这是对该 App 的手动放行，不代表安装包已通过 Apple 公证。若没有放行按钮，或提示“已损坏”“会损坏电脑”，请停止安装并向维护者提供完整提示。不要关闭 Gatekeeper 或使用删除隔离标记的命令。参见 [Apple 官方说明](https://support.apple.com/zh-cn/102445)。

### 更新已有安装

先让未完成任务结束或明确取消，再运行新版 DMG 中的安装器，点击“安装”。安装器会更新已有安装，保留用户数据与 Keychain 凭据；有排队、运行或暂停任务时会拒绝更新。

## 开发者构建与验证

运行时锁文件 `runtime-lock.json` 固定 OpenCode 1.18.30、Codex sandbox 0.153.4、Git 2.54.0、.NET SDK 10.0.401 / Runtime 10.0.12。支持范围从 arm64、macOS 14 起设置，实测系统见 `docs/validation/macos.md`，不能把一个 OS 版本的结果推广为全部系统已验收。

构建在 Mac 上需要固定 SDK、Python 3 与 CLT；终端用户运行自包含安装包不需要这些开发工具。构建脚本不下载 OpenCode/Codex，输入必须为锁文件指定的官方 npm 归档及其原样展开目录。构建时核对归档 SHA-512、版本及每个展开文件，拒绝额外文件和符号链接。

GitHub 的 `package-macos` 工作流使用原生 ARM64 runner，按锁文件准备 SDK 和运行时、构建 Git，并执行原生回归和安装门禁。统一的 `package` 发版工作流调用它，将 DMG 与 Windows EXE 放进同一个 Release 草稿；任一平台失败都不创建 Release。Mac 工作流仍可独立手动运行，只输出 Actions artifact。完整操作见 [发布说明](../../docs/releasing.md)。`scripts/Fetch-MacRuntimes.py <全新目录>` 负责下载并在展开前校验固定归档；目录下 `opencode/`、`codex/` 可直接作为下方构建器的对应 root。

下载时选择 `AgentDockSetup-<版本>-macos-arm64.dmg`（Apple Silicon；不含 Intel 构建），其中安装器已内置完整程序包，普通用户无需另下 payload ZIP 或 JSON 清单。当前 Mac 包未签名/公证，真实下载后可能受 Gatekeeper 拦截；预发布附件可下载不代表已完成该安装路径或稳定发行验收。

```sh
# Git 构建：新目录，保留官方源码归档与完整性记录。
bash scripts/Build-MacGit.sh /absolute/path/git-build
python3 scripts/Prepare-MacGit.py /absolute/path/git-build

python3 scripts/Build-MacPackage.py --version 0.1.0 \
  --output-dir /absolute/path/new-candidate \
  --opencode-root /absolute/path/opencode/package \
  --opencode-archive /absolute/path/opencode.tgz \
  --codex-root /absolute/path/codex/package/vendor/aarch64-apple-darwin \
  --codex-archive /absolute/path/codex.tgz \
  --git-root /absolute/path/git-build/portable
```

Git 的 `Prepare-MacGit.py` 验证本地 init/add/commit/diff/clone，生成文件哈希 provenance；构建器再次核对源码归档和编译完成后的文件清单。Git 的 GPL 源码、构建脚本与许可证随包保留。输出目录必须全新，避免覆盖已有候选与证据。

产物包含 payload ZIP、安装器 `.app`/ZIP/DMG、逐文件可执行清单、组件清单和 SHA-256。payload 模式声明只接受 0644/0755；入口必须可执行。安装器嵌入独立的稳定启动器 bundle，安装时原样复制，不能复制安装器再删除签名覆盖的 payload。安装器和桌面程序采用 .NET 单文件发布，避免托管 DLL 签名依赖会在 ZIP/普通文件复制时丢失的扩展属性；三个 `.app` 在组装后做 ad-hoc 资源封装与严格验签。没有 Developer ID 签名、公证与完整验收时 `releaseReady=false`。ad-hoc 签名不等于发行签名。

安装布局：

```text
~/Library/Application Support/AgentDock/   默认安装根，--root 可覆盖
  AgentDock.app/                           稳定启动器，动态读取 current.json
  install/current.json                     当前版本指针
  install/<version>/
    host/                                  自包含 Host
    desktop/AgentDock.app/                  自包含 Avalonia 桌面
    engine/                                自包含任务监督与沙箱内启动器
    engines/{opencode,codex,git}/           固定运行时
    plugin/                                该版本插件模板
  plugin/                                  稳定插件源，MCP 含绝对启动器和 --root
  data/                                    用户配置和任务，修复/更新/卸载保留
~/Applications/AgentDock.app                可选链接，只指向自己的稳定 bundle
```

安装/修复/更新/卸载共用更新门禁：更新标记 → 停止空闲 Host → 独占 launch.lock → 再查持久化未完成任务。排队/运行/暂停和损坏状态均拒绝继续。Mac 额外移除已停止的旧 Host launchd 注册。稳定 app、插件与 machine 信息在 current 指针发布前保留回滚副本；登录启动默认关闭，面板配置使用按数据目录派生的独立 LoginAgent。

按需 Host 由 `MacHostLauncher` 在登录桌面 GUI 域启动，包含 SSH 发来的插件调用。它不注册登录自启。Git 仅注入产品子进程 PATH，不改用户 PATH。安装器 `--no-shell-integration` 不改 Applications 入口。

验收使用隔离目录：

```sh
python3 scripts/Test-MacSetupGates.py --setup '/path/AgentDockSetup.app/Contents/MacOS/AgentDock' --work /path/new-gates
python3 scripts/Test-MacBundleSignature.py --dmg /path/AgentDockSetup-macos-arm64.dmg --work /path/new-signature-gates
python3 scripts/Probe-MacPlugin.py --codex /path/to/installed/codex \
  --marketplace '/path/to/AgentDock/plugin' --home /path/new-codex-home \
  --output /path/plugin-result.json
```

`--live-tasks-root /path/new-fixtures` 显式启用五角色真实账户任务；必须先在 Mac 主面板独立保存 Go 凭据，不能复制 Windows/旧 MCP 密钥。探针不读取密钥，不改用户 Codex 配置。合成 payload 门禁不代替真实 payload 和升级版本的验收；公开发布需另行授权。

安装后将 `<root>/plugin` 添加为 Codex 本地 marketplace，再安装 `agentdock@agentdock-local`；启用后新建 Codex 对话。实测宿主为 Codex CLI 0.159.2，七项 MCP 工具均可发现并调用；插件内沙箱是独立固定版本 0.153.4。

```sh
codex plugin marketplace add "$HOME/Library/Application Support/AgentDock/plugin" --json
codex plugin add agentdock@agentdock-local --json
```

最终候选的 21 项安装门禁、现有 Codex 会话中的真实跨版本升级、五角色真实账户和 DMG 入口已验证，详见 [实机记录](../../docs/validation/macos.md)。稳定启动器可应用 payload 更新；升级启动器本身须运行新版本完整安装器。`--login enable|disable` 通过 Host 同步配置和本安装的 LoginAgent，默认关闭。卸载使用稳定入口的 `--uninstall`，保留 `data` 与 Keychain；目录缺少有效安装记录时拒绝卸载。
