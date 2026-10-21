# 2026-10-21 单文件安装与原生 Mac 候选验证

候选版本 `0.1.1-beta.2`。本轮包含此前工作区的紧凑界面、Mac 路径发现和安装失败日志修复；起点为 `c14d2bc`，不把 beta.1 或历史验收自动视为本候选的验收。签名准备见 [发行签名准备](../signing.md)。

## 完成的交付变化

- Windows 安装 EXE 内置完整程序包，首次安装、修复和空闲升级不再要求单独下载 ZIP。安装后使用包内精简启动器，保留原来的逐文件哈希和路径检查。
- GitHub Windows 预发布附件为 EXE、维护清单和 SHA256SUMS；后两者不参与用户安装。维护 ZIP 仍在构建目录生成，旧版显式 `--payload` 用法保留。
- 安装/更新失败原因写入 `install/setup.log`；失败更新保留旧版本和数据。
- Windows owner.lock 仅对短暂共享冲突做有界等待，持续占用仍拒绝第二个 owner。命名管道客户端提前断开不再终止接入循环。
- 新增 Mac ARM64 原生 Actions：固定运行时下载校验、源码回归、APP/DMG 构建与原生安装门禁。产物继续为未签名候选 artifact，不自动公开 Mac 稳定版本。

## Windows 本地结果

- Core/Host/Desktop 相关测试 98 通过、7 条件跳过；Engine 82 通过、25 条件跳过；合计 180 通过、32 跳过、0 失败。
- 完整单文件候选的安装门禁 20/20，覆盖单 EXE 首次安装、相邻损坏 ZIP 不影响修复、未完成任务阻止升级、空闲升级和卸载保留数据。MacSetup 在 Windows 的隔离逻辑门禁 26/26。
- 发布附件与版本门禁 22/22，两个 workflow 的 actionlint 和 git diff --check 通过。
- 隔离安装版界面：在线引导 21 项、离线 13 项通过，5 个合成任务，桌面退出后 Host 仍运行。不读取日常凭据、不更换用户安装。
- 本地 EXE SHA-256：`6a463537ce5d0d4028852add2a63af086d6a07c9144654f792d58834d336b6ff`；这是本地构建，不是后续 GitHub 构建附件的哈希。

保留的失败证据：首次完整回归暴露管道提前断开故障，修复后复验通过；首次单文件升级检查发现 CLI 仍强制要求 --payload，修复并重新打包后 20 项全过。owner.lock 用例先复现短暂占用失败再验证修复；历史 CI 文件占用者仍未知。旧 MacSetup 二进制测试失败后，显式构建最终源码并通过 26 项，未用旧二进制结果代替新源码验证。

## 原生 Mac 结果

环境为现有 Apple Silicon Mac、macOS 26.6.2、SDK 10.0.401。隔离目录 `/Users/hobo/Projects/AgentDock-release-20261021`，未替换日常安装。源码归档 SHA-256：`eb1e4b4fd172fa58c5a5227f05c24ae9ca9fa8e0bac858d312984ce0992caa44`；复制后逐文件核对。归档之后继续修改 CI 配置、文档及下述测试清理；产品源码未变，GitHub 精确提交构建另记。

- 运行时下载/归档安全测试 3/3；Core/Host 97 通过、7 跳过；Engine 基础回归 67 通过、36 跳过。
- 开启真实 Mac 执行及 Keychain 条件后，Engine 84 通过、4 失败、20 跳过。4 个失败均为 SSH 会话不能完成 Keychain 写入；切换到已有图形登录会话，独立重跑 Keychain 6/6 通过。真实执行器的 15 项已通过；没有将两次结果伪写为一次全绿运行。
- 完整 APP/DMG 构建、21 项原生安装门禁和完整安装通过，安装后 Host 版本为 `0.1.1-beta.2`。
- 安装版界面在线 21 项、离线 13 项通过；193 次 Host 查询全部成功，最慢 412.87 ms；关闭桌面不退出 Host，合成凭据已删除，日常配置哈希不变。
- 隔离 Codex app-server 新会话发现七个 AgentDock 工具并读取到正确 Host 版本。仅验证连接和 status，不等同用户桌面 App 重启后的实际模型任务。

## 证据和未完成项

原始本地证据保存在 `artifacts/validation/release-readiness-20261021/`，包含成功/失败日志、TRX、安装门禁 JSON、界面摘要和源码清单；Mac 对应目录的 results 保留原生输出。旧工作区变更备份为 `preexisting.patch`。这些隔离证据不随安装包分发。

两平台均 `partial=false`、`releaseReady=false`。当前账号无法管理 Hyper-V，未重跑新候选的干净 Windows VM 或严格冷缓存测试；历史 Windows VM 通过记录不自动继承。仍缺 Windows 公共代码签名和真实下载 SmartScreen、Mac Developer ID/公证/Gatekeeper、Mac 原生选择器/托盘/Toast/缩放/VoiceOver、用户桌面 App 新对话和当前安装版真实模型负载验收。独立旧变更审查完成；新增打包逻辑的外部审查因服务 429 失败，未作为审查通过计入。

本记录证明本地候选验证；云端构建、附件核对及公开状态以对应 GitHub Actions / Release 链接和后续记录为准。

## 首次云端构建

提交 `10e50b2` 的 [Windows Actions](https://github.com/HoboCY/AgentDock/actions/runs/39538318019) 通过源码回归、完整打包、清单校验及 20 项安装门禁。[Mac 首次 Actions](https://github.com/HoboCY/AgentDock/actions/runs/39538319391) 原生运行时获取成功，但 HostIntegrationTests 两项在清理阶段失败：Host 创建的合成 Keychain 项目由不同代码身份的测试进程删除时返回 OSStatus -25244。该次运行计为失败，日志/TRX 已保留。

测试夹具改用系统 security 工具按唯一 service/account 删除，并断言项目不再存在；不改生产凭据权限，也不吞掉清理失败。原生 Mac 定向复验 6 通过、1 条件跳过。云端另使用临时测试钥匙串并在 always 步骤删除整个测试钥匙串，同时启用六项 Keychain 回归。

## 标签构建与公开测试版

最终标签 `v0.1.1-beta.2` 指向 `e4986c064df82c81fa2de564d0fc1a7fea86a879`。

- [Windows 标签 Actions](https://github.com/HoboCY/AgentDock/actions/runs/39540141926)：180 通过、32 条件跳过，22 项发布检查、20 项安装门禁通过。
- [Mac 标签 Actions](https://github.com/HoboCY/AgentDock/actions/runs/39540141902)：Host 97 通过/7 跳过；Engine 88 通过/20 跳过（含真实执行器和 6 项 Keychain）；另有 1 项启动预检、3 项下载安全测试，21 项安装门禁通过，完整 APP/DMG 构建成功。
- Mac 第二轮云端在 Host 全通过后，执行器连续启动超时，诊断运行已取消，未计为通过。源码测试 apphost 依赖 setup-dotnet 的 SDK，而 launchd 没有继承 shell 的 DOTNET_ROOT；为临时 runner 的 launchd 配置 SDK 路径后，最终标签测试全部通过。新增阶段/用例超时和启动预检；生产自包含运行时逻辑未改。重复分支构建已取消，以同提交标签构建为准。
- [Windows beta.2 已公开](https://github.com/HoboCY/AgentDock/releases/tag/v0.1.1-beta.2)，2026-10-21 03:52:51 UTC 发布，`draft=false`、`prerelease=true`、不设 Latest。Mac 未签名候选只保留为 Actions artifact。

草稿的三个实际附件已下载，EXE 与清单的本地 SHA-256 同时匹配 SHA256SUMS 和 GitHub asset digest。仅用下载的 EXE 在 `artifacts/validation/release-readiness-20261021/github-release-install/` 完整安装成功，Host 版本为 beta.2。公开后匿名下载清单哈希一致，EXE 匿名 HEAD 返回 200，体积 594023230 字节；没有将 HEAD 检查写成匿名完整下载。

公开 EXE SHA-256：`60ca7f6af037ce72efa192740e3c1a4195977ed6ce5d08f66a5d2587b0b7db8e`。
公开清单 SHA-256：`2e028d5f56f2bb8b44683f04f7a03fb82296458e500cabf7d167eeade7601037`。

详细证据：`github-release-verification.json`、`public-release-verification.json`、`windows-ci-final.log`、`mac-ci-final.log`。公开测试版没有解除本记录列出的稳定发行缺口。

## 后续策略调整：Windows 签名可选

维护者确认 Windows 代码签名不作为必需项。后续构建将未签名状态从 Windows `releaseBlocked` 移到 `warnings`，保留发行说明、哈希与安装校验；真实下载/SmartScreen 路径及其他未完成验收继续保留。macOS Developer ID、公证与 Gatekeeper 门槛不变。以上原始验收和 beta.2 附件仍保留当时的清单与哈希，不因策略调整改写为稳定发行通过。
