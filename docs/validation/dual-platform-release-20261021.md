# 双平台发版流水线验证（2026-10-21）

维护者要求以后每次发版同时提供 Windows 与 Mac 安装包。实现提交 `a88bf3e89a45c517dc13d9d03059e4281970e120`：统一 `package` 工作流确定版本，Windows 与可复用的原生 Mac 工作流并行构建；两边成功后才汇总附件或创建 Release。

## 原生云端演练

[Actions 39570470939](https://github.com/HoboCY/AgentDock/actions/runs/39570470939) 已成功。输入为 `version=0.1.1-ci.20261021`、`release=false`，不创建新标签或 Release。

- Windows：源码回归 180 通过 / 32 条件跳过，发布检查 25 通过，安装门禁 20/20；生成内置完整程序包的 EXE。
- Mac ARM64：Host 97 通过 / 7 条件跳过，Engine 88 通过 / 20 条件跳过，另有 1 项启动预检；附件校验 4/4（含符号链接拒绝、14 个无效清单/附件子场景）、运行时获取校验 3/3、安装门禁 21/21；完整 APP/DMG 构建成功。
- 汇总 job 下载两边产物，对 EXE、DMG、两份原始清单重新执行 SHA-256 校验，四项均为 OK；清单按平台重命名，原始内容不变。
- [统一下载包](https://github.com/HoboCY/AgentDock/actions/runs/39570470939/artifacts/12332582150) 共 6 个文件：EXE、DMG、两份清单、统一 `SHA256SUMS.txt`、发行说明。Actions artifact ZIP 为 911716337 字节，SHA-256 `945bfcca2460f2f3c3012ece8294ca4bd20cb36c97b112860abfccc53ac2e491`；按工作流保留 14 天，通常需要 GitHub 登录下载。

正式执行预发布时只上传上述前五个文件，发行说明作为 Release 正文。先创建草稿并完成两个安装包的上传，上传命令成功后才允许公开；任何平台构建失败都会阻止汇总，附件上传失败保留草稿，不覆盖已有 Release。

## 验证边界

实际发布脚本的 5 项模拟回归覆盖双平台附件、默认保留草稿、全部上传后公开、上传失败不公开、标签移动与文件损坏拒绝发布（两个拒绝条件分别为独立用例）。本轮真实 GitHub 的“创建 Release”步骤按 `release=false` 跳过；演练前后 beta.1/beta.2 的公开状态及发布时间不变。没有把模拟发布测试计为一次实际公开发版，也没有重新下载安装本轮云端附件到用户机器。

Windows 签名继续为可选项。Mac 目前仅 Apple Silicon arm64，未签名、未公证，真实下载后的 Gatekeeper 安装路径未验收；Intel Mac、人工 UI/可访问性、真实模型负载以及其他既有验收缺口未因流水线通过而完成。两平台 `releaseReady=false`，稳定标签仍拒绝。

本地证据位于 `artifacts/validation/dual-platform-release-20261021/`，包括平台与汇总日志、run/artifact 元数据、演练前后 Release 快照。外部实现子任务在产出文件和测试结果后停止，最终代码由主代理复核、精简并在上述 CI 重新验证，未计为独立审查通过。
