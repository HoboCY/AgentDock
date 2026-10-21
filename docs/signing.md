# 发行签名准备

签名身份由维护者申请并完成实名验证；项目不生成自签名证书冒充公开发行身份。安装器和运行环境验证可以先完成，稳定发布仍需最终签名包的真实下载验收。

## Windows

1. 先确定发行者使用个人还是公司身份，以及真实所在地区。
2. 选择接受该身份/地区的公共代码签名服务，并在购买前确认支持无人值守 GitHub Actions；仅能插 USB Key 的方案不能直接用于 GitHub 托管 runner。
3. 服务确定后，配置专用签名权限与受保护的发布环境。云签名优先使用服务支持的短期身份授权；令牌只进入 GitHub Secrets，不写仓库或聊天。
4. 实际构建需要先签程序/精简启动器，再生成文件哈希与内置 ZIP，最后签外层安装 EXE。签名后重新核对清单和安装结果。

微软 Artifact Signing 的 Public Trust 有地区和主体限制：目前个人仅美国/加拿大；组织支持列表也不含中国大陆。申请前以[微软最新要求](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart)为准。

开源项目可以评估 [SignPath Foundation](https://signpath.org/terms)。AgentDock 使用 MIT，但申请仍需核对全部分发组件、维护和发布记录、隐私政策等条件，由其审核决定；尚未申请或获得批准。使用第三方认证服务的事实及数据用途需要在面向用户的隐私说明中写清楚。

签名不保证新版本立刻获得 SmartScreen 信誉，必须保留真实下载后的验收。参考[微软签名说明](https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide)。

## macOS

1. 使用自己的 Apple Account 加入 Apple Developer Program，并完成身份验证。
2. 在 Mac 上通过 Xcode 的账户/证书管理创建 **Developer ID Application** 证书；当前 APP/DMG 分发不使用 Mac App Store 证书。
3. 准备公证认证，在 Mac Keychain 或 GitHub 受保护环境中保存；不把 Apple 登录密码或私钥写进脚本。
4. 对内层可执行文件、应用 bundle 和外层 DMG 按顺序签名，再提交公证、staple，最后通过真实下载在干净系统验收 Gatekeeper。

当前以 APP/DMG 分发，无需上架 App Store；未来若改为 PKG 才另需考虑 Developer ID Installer。官方入口：[Developer ID 与公证](https://developer.apple.com/developer-id/)。

以上是准备步骤，不代表仓库已经接入任一签名服务。证书、签名身份和公证认证缺失时，产物继续标为未签名测试候选。
