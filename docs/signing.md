# 发行签名准备

Windows 代码签名按维护者 2026-10-21 的决定作为可选增强项，不作为稳定发布必需项；macOS 的 Developer ID、公证与 Gatekeeper 要求不变。两平台仍需最终发行包的真实下载验收。

需要签名时，身份由维护者申请并完成实名验证；项目不生成自签名证书冒充公开发行身份。

## Windows

以下步骤仅在决定接入签名时执行。目前无需购买或申请证书；未签名状态和可能的系统提示继续如实披露。

1. 先确定发行者使用个人还是公司身份，以及真实所在地区。
2. 选择接受该身份/地区的公共代码签名服务，并在购买前确认支持无人值守 GitHub Actions；仅能插 USB Key 的方案不能直接用于 GitHub 托管 runner。
3. 服务确定后，配置专用签名权限与受保护的发布环境。云签名优先使用服务支持的短期身份授权；令牌只进入 GitHub Secrets，不写仓库或聊天。
4. 实际构建需要先签程序/精简启动器，再生成文件哈希与内置 ZIP，最后签外层安装 EXE。签名后重新核对清单和安装结果。

微软 Artifact Signing 的 Public Trust 有地区和主体限制：目前个人仅美国/加拿大；组织支持列表也不含中国大陆。申请前以[微软最新要求](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart)为准。

开源项目可以评估 [SignPath Foundation](https://signpath.org/terms)。AgentDock 使用 MIT，但申请仍需核对全部分发组件、维护和发布记录、隐私政策等条件，由其审核决定；尚未申请或获得批准。使用第三方认证服务的事实及数据用途需要在面向用户的隐私说明中写清楚。

签名不保证新版本立刻获得 SmartScreen 信誉，必须保留真实下载后的验收。参考[微软签名说明](https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide)。

### 中国大陆个人开发者的准备路径

优先评估 SignPath Foundation 的开源项目申请，不先购买证书。它使用基金会名下的发行者身份，不是给个人签发证书；需要审核项目的可信记录，新项目不能保证获批，每次发行签名仍需指定成员批准。

申请前准备：

- 启用 GitHub 双重验证，确定维护者和签名批准人；申请账户、联系人和身份信息由本人填写。
- 提供仓库 `https://github.com/HoboCY/AgentDock`、MIT 许可证、已有 Windows Release 和对应 Actions 构建链接。
- 核对全部随包组件及许可证，说明 .NET、OpenCode、Codex、Git 等上游程序只是随包分发，不用项目证书给第三方二进制重签。
- 准备真实的隐私说明：AgentDock 按用户任务调用 OpenCode Go 服务，可能发送任务所需的代码和文档；不能声称软件从不联网或从不传输数据。签名政策还需列出维护/审核/批准角色；批准前不要写“已由 SignPath 提供签名”。

官方[申请入口](https://signpath.org/apply)及[项目条件](https://signpath.org/terms)。如果不获批，再评估支持中国大陆个人身份的付费服务，购买前由服务商确认身份材料、GitHub Actions 接入方式、交互审批要求及总费用。

[Certum 的开源云签名](https://shop.certum.eu/open-source-code-signing-on-simplysign.html)属于可询问的备选；2026-10-21 页面显示缺货，并要求移动应用和桌面连接程序。尚未确认中国大陆身份材料和无人值守 Actions 是否满足，不能把“云签名”直接理解为全自动 CI 签名。

## macOS

1. 使用自己的 Apple Account 加入 Apple Developer Program，并完成身份验证。
2. 在 Mac 上通过 Xcode 的账户/证书管理创建 **Developer ID Application** 证书；当前 APP/DMG 分发不使用 Mac App Store 证书。
3. 准备公证认证，在 Mac Keychain 或 GitHub 受保护环境中保存；不把 Apple 登录密码或私钥写进脚本。
4. 对内层可执行文件、应用 bundle 和外层 DMG 按顺序签名，再提交公证、staple，最后通过真实下载在干净系统验收 Gatekeeper。

当前以 APP/DMG 分发，无需上架 App Store；未来若改为 PKG 才另需考虑 Developer ID Installer。官方入口：[Developer ID 与公证](https://developer.apple.com/developer-id/)。

以上是准备步骤，不代表仓库已经接入任一签名服务。Windows 缺少证书不单独阻止稳定发布；macOS 缺少 Developer ID 或公证时仍为未签名测试候选。当前两平台另有未完成验收，仍保留 `releaseReady=false`，详见 [发布边界](releasing.md#当前发行边界)。
