# 实施前提交审查（Issue #1）

基线 `cfa619651ceafe55e3d00d3f922cbaace8107e00`，当前 main 的新增实现；DeepSeek 两个独立只读 reviewer 分别审查 Standards 与 Spec。初始审查针对暂存快照，之后的修复由主代理验证；不表示审查者复审过后续补丁。

## Standards

## 审查结论（staged 快照，基线 cfa6196）

**1.【高】暂存的聚焦测试无法通过，与“测试已通过”的验证声明不一致**
`tests/AgentDock.Tests/ConfigurationAndBoundaryTests.cs:47`：
`Assert.Equal(new CleanupPreview(0, 0, 2), service.Cleanup(false, "all"));`
但 `src/AgentDock.Core/TaskService.cs:227-228` 的 preview 始终返回计算出的 64 位十六进制 `Revision`；`CleanupPreview` 是 record，相等性包含 `Revision`，期望值默认 `null` 与实际值必然不等，断言必失败。说明该测试类不在所称“聚焦测试通过”的子集内，验证声明未覆盖此文件。修复：按字段断言（忽略 Revision），或确认代码与运行版本一致；待跑的全量测试应会在此处失败。建议提交前先验证。

**2.【中】暂存索引缺少工作区 4 个未暂存修复，提交会丢修复**
`git diff`（未暂存）显示 `src/AgentDock.Engine/OpenCodeSetup.cs`、`src/AgentDock.Host/Program.cs` 及两个测试文件有改动但未入索引。关键影响：staged 版 `OpenCodeSetup.BuildSandboxEnvironment` 未给自包含 runner 配置可写的 `TEMP`/`DOTNET_BUNDLE_EXTRACT_BASE_DIR`（父进程 TEMP 不在沙箱可写白名单），Host 也缺 UTF-8 控制台编码。若实现流程按当前索引提交，自包含包在真实沙箱下会回归失败。提交前需 `git add` 这些文件，或明确说明其不在本次交付范围。

**3.【低】测试辅助流虚报读取字节数**
`tests/AgentDock.Engine.Tests/GoRelayTests.cs:231-232`：`ChunkedStream.ReadAsync` 只复制 `Math.Min(bytes.Length, buffer.Length)` 字节却 `return bytes.Length`；chunk 大于调用方缓冲时会虚报长度、截断/污染 SSE 流。当前 chunk 小故未暴露，属潜在 flake。改为返回实际 `copy`。

**判断性说明**
- 未发现违反仓库文档/ADR 的硬性违规；沙箱权限、凭据隔离、路径与清理边界与 ADR-0001~0005 及 architecture.md 一致。
- 残余验证缺口：本审查环境无法启动 `dotnet test`（沙箱阻止子进程/用户目录写入），发现 1、3 为静态推断，应以待跑全量测试确认；真实账户、干净 VM、Mac 等外部验收按声明保持未完成，未视为缺陷。
- 未复核：未暂存文件的目标版本、licenses 目录（按指示排除）。

### 处理结果

1. CleanupPreview 相等性测试已改为断言清理范围和非空修订号；最终完整测试 113/113 通过。
2. 提交前重新暂存全部本任务修复。实际安装包已验证 UTF-8 MCP 和可写 single-file 解包目录。
3. ChunkedStream 现在保留未读偏移，返回实际复制字节数。

## Spec

**结论：未发现阻断性缺陷。以下为按严重度的发现。**

**(c) 疑似不正确实现**

1. [中] GoRelay 在未授权/超大/模型不符/重定向/上游失败等拒绝路径同样调用 `PublishMeasurement(null)` 并推进序号（`src/AgentDock.Engine/GoRelay.cs:136,148,182,214,256,351`），而宿主只在 `sequence==consumedMeasurement+1` 时采用时长（`src/AgentDock.Engine/OpenCodeEngine.cs:170-176`）。一次本机杂散请求或 502 重试即让该轮生成速度被丢弃、显示"待更新"。Q13 要求"展示当前或最近一轮生成阶段的平均输出速度（…排除排队和工具执行时间）"。

2. [中] 后台崩溃后 `<home>/engine/<id>` 作业目录无任何清理：只有引擎正常收尾才 `TryDelete(jobRoot)`（`OpenCodeEngine.cs:258-261`），Host 清理仅覆盖 `artifacts`（`src/AgentDock.Host/HostApplication.cs:13-19`），启动与小时任务都不扫描 `engine` 目录，可长期累积 NuGet/缓存体积。与仓库自述契约"任务结束删除 job 目录"（`docs/validation/execution-contract.md:129`）及 Q21 崩溃恢复路径不符。

3. [低] `JobProcess.cs:71` 的 `Directory.CreateDirectory(workingDirectory)` 会重建已被删除的项目目录，绕过刚做过的 project-missing 校验（`docs/validation/execution-contract.md:47`；`OpenCodeSetup.cs:58-61`），与 Q23"直接在指定项目目录执行"不符。

4. [低] `CheckArtifactsStore.cs:14-15` 注释称"core cleanup 尚未删除这些目录（已记录缺口）"，与 `HostApplication.CleanupArtifacts` 及 `execution-contract.md:131` 正好相反，源码内文档自相矛盾；另外手动清理"正文"会立即清空刚结束（未满 7 天）任务的正文（`TaskService.cs:213`），而 Q24 称正文"保留 7 天…供 Codex 重连取回结果"，界面（`MainWindow.cs:480`）未提示非过期正文也会被删。

**(a) 缺失/部分**：Q1–Q26 均有实现映射；干净虚机、真实 OpenCode Go 凭据、Mac、签名与真实上游字段已在 `acceptance.md`、`installation.md` §8、`execution-contract.md` §14 如实列为未完成，无隐性宣称。覆盖缺口：托盘/隐藏主窗时"完全退出"确认（`App.cs:96-150`）无自动化用例。

**(b) 越界**：未发现实质范围外实现；CI 打包、macOS 配方、探针脚本均属 Q19/Q3/T6/V1 授权范围。

### 处理结果

1. 未授权、参数错误、非成功上游响应和失败尝试不推进生成阶段。新增失败请求回归，相关测试通过。
2. Host 持有 launch.lock 且尚未接纳任务时回收崩溃残留，保留有界检查证据；新测试通过。
3. JobProcess 对不存在的工作目录抛错，不再创建项目目录；回归已复现旧行为并通过修复。
4. 删除过时注释；预览与确认均说明手动清理可包含未满七天的终态正文，Codex 随后无法取回。

托盘退出确认的专门 UI 自动化、真实账户、干净 Windows 与 Mac 仍按验收矩阵记录。另由主代理纠正登录自启与快捷方式走稳定启动器、卸载删除本安装的自启项，并加入跨版本更新、损坏更新保留指针和 partial 包拒绝场景。

Standards：3 项已处理，最高为旧测试断言失败；Spec：4 项已处理，最高为统计阶段及崩溃残留。两条轴的外部验收缺口继续保留。
