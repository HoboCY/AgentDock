# GitHub 预发布

`package` 是统一发版入口：一个版本标签同时构建 Windows x64 EXE 和 Apple Silicon macOS arm64 DMG，两边测试、完整性检查及安装门禁全部通过后，才创建同一个 GitHub Release。任一平台失败，都不会只发布另一平台。

Mac 构建复用 `package-macos` 原生工作流，通过 [workflow_call](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows) 从同一提交调用，标签不会再触发独立的重复 Mac 构建。当前仅支持带测试后缀的版本，例如 `v0.1.1-beta.3`；Mac 包仍未签名/公证，作为明确标注限制的预发布附件，不表示稳定版验收通过。暂不支持稳定标签或 Intel Mac 安装包。

## 创建测试版

1. 先将准备发布的源码和发布工作流提交并推送。GitHub 只构建标签指向的提交，本地未提交的修复不会进入安装包。
2. 给已确认的提交打测试标签并推送，例如：

   ```powershell
   git tag -a v0.1.1-beta.3 <已确认的提交SHA> -m 'AgentDock 0.1.1 beta 3'
   git push origin v0.1.1-beta.3
   ```

3. 标签推送会并行构建两个平台，全部通过后创建 **Draft + Pre-release**，草稿同时包含 EXE 和 DMG。核对附件与说明后，在 GitHub Releases 编辑该草稿，保持 Pre-release 并发布。
4. 如标签尚无 Release，且需要由 Action 直接公开发布，使用 GitHub CLI 的 `--ref` 指定已有测试标签（网页 Run workflow 的选择器主要用于分支）：

   ```powershell
   gh workflow run package.yml --repo HoboCY/AgentDock --ref v0.1.1-beta.3 -f release=true -f draft=false
   ```

   `version` 留空，或填写标签去掉 `v` 后的版本。自动公开也会先创建草稿，所有附件上传成功后才公开；上传失败保留草稿。已有 Release 时创建操作会失败，不覆盖附件；标签推送通常已生成草稿，此时直接发布该草稿。标签选择语义见 [gh workflow run 文档](https://cli.github.com/manual/gh_workflow_run)。

普通 main/PR 只做 Windows 检查；手动运行 `package` 会构建两个平台。未勾选 `release` 时只保留 Actions artifacts，不创建或修改 Release，可用于双平台演练：

```powershell
gh workflow run package.yml --repo HoboCY/AgentDock --ref main -f version=0.1.1-ci.20261021
```

在该次运行的 `agentdock-release-bundle-<run_id>` 下载两个安装包、清单和校验文件。`package-macos` 仍可单独手动运行做 Mac 检查，但不创建 Release。创建 Release 的 job 单独获得 `contents: write`，无需添加 PAT、模型凭据或签名密钥。使用 `--verify-tag` 防止自动从默认分支创建标签；预发布不标为 Latest。参数语义见 [GitHub CLI 文档](https://cli.github.com/manual/gh_release_create)。

## 附件与门禁

- Windows 用户下载 `AgentDockSetup-<版本>-win-x64.exe`，Apple Silicon Mac 用户下载 `AgentDockSetup-<版本>-macos-arm64.dmg`；两个安装器均内置完整程序包，不需要另外下载或复制 payload ZIP。
- 两份原始构建清单分别发布为 `package-manifest-win-x64.json` 和 `package-manifest-macos-arm64.json`，避免重名。保留组件信息、哈希及 `releaseReady`/`releaseBlocked`，不把构建成功改写为稳定发行就绪。
- JSON 清单和 `SHA256SUMS.txt` 是可选维护资料，普通安装无需下载或编辑。安装器内的 `payload-manifest.json` 仍用于逐文件完整性校验。
- `SHA256SUMS.txt` 覆盖两个安装包与两份清单；汇总 job 在下载构建产物后再次校验。公开附件固定为这五个文件，构建中间产物不进入 Release。

发布前运行 .NET 回归、完整包构建、内置 Git 哈希检查及隔离安装门禁。额外检查平台、构建版本、Host 版本、自包含状态、必需组件、文件名、体积与 SHA-256；partial 包、缺少安装器、版本不匹配和损坏附件均拒绝发布。仅上传固定附件，不递归发布整个工作区或构建目录。

发布逻辑本地回归：`pwsh -NoProfile -File scripts/Test-ReleasePackage.ps1`、`python scripts/Test-MacRelease.py` 和 `python scripts/Test-ReleaseWorkflow.py`（需 Bash）。使用合成小文件验证附件门禁、实际版本脚本以及模拟上传失败/标签移动时拒绝公开，不替代真实安装包测试。

2026-10-21 的 [双平台云端演练](validation/dual-platform-release-20261021.md) 已通过，验证两个安装包、原生安装门禁和统一附件汇总；本次演练没有公开新版本。

## 当前发行边界

当前生成双平台预发布。Windows 未签名可能有 SmartScreen 提示；Mac 未经 Developer ID 签名、公证，真实下载后可能被 Gatekeeper 拦截，下载运行路径尚未验收。此前 Windows 干净系统、Mac 本地候选及真实任务的验收证据见 [验收矩阵](validation/acceptance.md)，这些结果属于各自的版本，不能自动继承为新标签的验收。

按维护者 2026-10-21 的决定，Windows 代码签名为可选增强项，不是稳定版发布的必需条件。未签名状态保留在构建清单 `warnings` 和发行说明中，不列入 Windows `releaseBlocked`；哈希、完整性及安装门禁继续执行。

稳定版仍需完成对应平台的验收：[#4](https://github.com/HoboCY/AgentDock/issues/4)、[#10](https://github.com/HoboCY/AgentDock/issues/10) 的严格冷启动、当前候选干净系统安装/接入/任务及真实下载后的 SmartScreen 路径；Mac 的 [#9](https://github.com/HoboCY/AgentDock/issues/9)、[#12](https://github.com/HoboCY/AgentDock/issues/12) 原生交互/可访问性、当前安装版真实负载和 Developer ID/公证/Gatekeeper 要求保持不变。Windows 下载验收需如实记录系统提示及可执行安装路径，不要求因签名消除全部信誉提示。当前构建器仍因未完成验收标记 `releaseReady=false`，稳定标签暂不开放。

测试版须从 Releases 页面手动获取；应用内更新查询使用 GitHub 的稳定版 `releases/latest`，不会提示预发布更新。

可选的 Windows 签名准备与 macOS 必需的发行身份见 [发行签名准备](signing.md)。只有对应平台同一候选的必需验收完成后，才能开放稳定标签或修改 `releaseReady`。
