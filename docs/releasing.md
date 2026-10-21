# GitHub 预发布

现有 `package` 工作流负责 Windows x64 构建、测试、安装门禁和 GitHub Release。当前只接受带测试后缀的版本，例如 `v0.1.1-beta.1`；稳定发行条件尚未满足，不支持 `v0.1.1` 稳定标签。macOS 的交叉构建配方不上传到 Release，原生打包流程见 [Mac 说明](../packaging/mac/README.md)。

## 创建测试版

1. 先将准备发布的源码和发布工作流提交并推送。GitHub 只构建标签指向的提交，本地未提交的修复不会进入安装包。
2. 给已确认的提交打测试标签并推送，例如：

   ```powershell
   git tag -a v0.1.1-beta.1 <已确认的提交SHA> -m 'AgentDock 0.1.1 beta 1'
   git push origin v0.1.1-beta.1
   ```

3. 标签推送会自动构建并创建 **Draft + Pre-release**。核对附件与说明后，在 GitHub Releases 编辑该草稿，保持 Pre-release 并发布。
4. 如需由 Action 直接公开发布，在 Actions → package → Run workflow 中选择已有测试标签，勾选 `release`、关闭 `draft`。`version` 留空，或填写标签去掉 `v` 后的版本。已有 Release 时创建操作会失败，不覆盖附件；若已生成草稿，直接发布该草稿。

普通 main/PR 构建和未勾选 `release` 的手动运行只保留 Actions artifacts。创建 Release 的 job 单独获得 `contents: write`，无需添加 PAT、模型凭据或签名密钥。使用 `--verify-tag` 防止自动从默认分支创建标签；预发布不标为 Latest。参数语义见 [GitHub CLI 文档](https://cli.github.com/manual/gh_release_create)。

## 附件与门禁

- `AgentDockSetup-<版本>-win-x64.exe` 与 `AgentDock-<版本>-win-x64.zip` 必须一起下载、放在同一目录。
- `package-manifest.json` 保留原始组件信息、哈希及 `releaseReady`/`releaseBlocked`，不把构建成功改写为稳定发行就绪。
- `SHA256SUMS.txt` 覆盖上述三项附件；发布 job 在下载构建产物后再次校验。

发布前运行 .NET 回归、完整包构建、内置 Git 哈希检查及隔离安装门禁。额外检查平台、构建版本、Host 版本、自包含状态、必需组件、文件名、体积与 SHA-256；partial 包、缺少安装器、版本不匹配和损坏附件均拒绝发布。仅上传固定附件，不递归发布整个工作区或构建目录。

发布逻辑本地回归：`pwsh -NoProfile -File scripts/Test-ReleasePackage.ps1`。它使用合成小文件验证门禁与实际工作流内联版本脚本，不替代真实安装包测试。

## 当前发行边界

适合先做 Windows 小范围未签名测试版。此前 Windows 干净系统、Mac 本地候选及真实任务的验收证据见 [验收矩阵](validation/acceptance.md)，这些结果属于各自的版本，不能自动继承为新标签的验收。

稳定版仍需完成 [#4](https://github.com/HoboCY/AgentDock/issues/4)、[#9](https://github.com/HoboCY/AgentDock/issues/9)、[#10](https://github.com/HoboCY/AgentDock/issues/10)、[#12](https://github.com/HoboCY/AgentDock/issues/12) 中的严格冷启动、Mac 原生交互/可访问性、当前安装版真实负载和发行交付检查；其中 Windows 签名及真实下载后的 SmartScreen、Mac Developer ID/公证/Gatekeeper 尚未完成。当前构建器始终保守标记 `releaseReady=false`。

测试版须从 Releases 页面手动获取；应用内更新查询使用 GitHub 的稳定版 `releases/latest`，不会提示预发布更新。
