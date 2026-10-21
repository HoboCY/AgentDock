# ShadUI 页面重构（2026-10-17，UI v2）

本记录描述本地开发构建，不替代安装包、macOS 实机或发行验收。

## 紧凑工具窗口（2026-10-20）

根据用户要求，将主面板从大型工作台收紧为桌面小工具：

- 默认尺寸从 1280×820 改为 760×560，窗口尺寸属性的最小值为 640×480；默认面积减少约 59%。
- 移除 200 宽的侧栏、品牌展示、大标题和统计卡片。任务、设置、开始使用改为顶部页签，后台状态及后台/悬浮窗操作移到底部。
- 任务统计使用一行摘要，保留未知/部分待更新用量语义。收紧列表行与留白，结果和停止操作固定在列表底部；用户放大至宽度 1000 以上时仍可查看原有右侧详情。
- 设置与首次配置引导同步收紧；设置在宽度 720 以下改为单列，保存与引导导航保持固定可见。
- 冒烟检查改为覆盖新的默认/最小尺寸，并覆盖从紧凑布局放大恢复详情。检查原有任务选择、项目筛选、设置编辑器、引导步骤及窗口生命周期。

本地验证：解决方案构建 0 警告、0 错误；回归测试 177 通过、30 按平台/显式环境条件跳过、0 失败；带 5 条合成任务的真实桌面冒烟通过 16 项页面检查，无后台空状态通过 13 项。已查看默认任务页、最小窗口、设置、角色展开和引导截图。测试数据隔离，UI 探针没有调用模型；原 Codex 配置哈希保持不变，关闭 GUI 后后台继续存活。

证据：`artifacts/validation/compact-tool-final/`（运行结果与截图），`artifacts/validation/compact-tool-offline/`（无后台空状态），`artifacts/validation/compact-tool-tests-desktop/`（回归 TRX）。默认任务页为 `desktop.json.selected.png`，设置页为 `desktop.json.settings.png`。

AgentDock explorer 已完成只读验证约束调查；据其建议补充引导主按钮的纵向边界和保存按钮的右边界检查。滚动正文中的设置仍需正常滚动访问；该调查没有运行桌面，不计为独立运行验收。

沙箱内的命名管道/测试进程通信失败，改在正常 Windows 桌面进程环境中验证通过；旧冒烟首次在隐藏详情检查处失败，修正检查顺序后通过。失败证据保留在 `compact-tool-01`、`compact-tool-02` 和 `compact-tool-tests`，不计入通过结果。本次未更新安装版、未发布，也未进行 macOS 或实际多显示器 DPI 验收。

## 设计与实现

根据用户对第一版“界面没变化”的反馈，先用内置 `image_gen` 生成完整桌面设计稿，再重写主面板布局。

- [生成的设计稿](../design/agentdock-ui-v2.png)、[完整提示词](../design/agentdock-ui-v2-prompt.md)。
- [实际任务页截图](../design/agentdock-ui-v2-implemented.png)、[实际设置页截图](../design/agentdock-ui-v2-settings.png)。截图来自运行中的 Avalonia 程序；任务及用量为隔离的合成测试数据，没有调用模型。
- 深墨绿侧栏替代横向页签。继续使用 TabControl / TabItem，保留“任务”“设置”的键盘和 UIA 语义。
- 八列表格改为虚拟化双行任务列表，右侧详情展示完整项目、角色、输入/输出 Token、生成速度、总耗时、任务 ID、错误及结果/停止操作。
- 宽度小于 1140 时收起详情栏，将操作放到列表底部；高度较小时折叠统计。任务摘要完整内容仍可通过提示与结果窗口查看。
- 设置按“接入与执行 / 桌面与通知 / 维护”分区。接入、并发卡片在窄窗口改为上下排列；保存栏固定在底部。
- 已知用量数值与“部分待更新”提示分行，未知值仍显示“待更新”；没有从字数或总耗时估算用量/生成速度。
- 任务派发、停止条件、凭据保存后清空、配置冻结、清理预览/确认、托盘、关窗只隐藏及完全退出确认行为保留。

主布局在 `MainWindow.Layout.cs`；状态与操作仍在 `MainWindow.cs`。`DesktopStyles.axaml` 管理主题与布局样式，`Ui.cs` 提供共享展示辅助。

## 依赖

保留 `Avalonia 11.3.22` / `.NET 10`，使用 `ShadUI 0.1.6`。官方 [0.1.6 项目](https://github.com/accntech/shad-ui/blob/0.1.6/src/ShadUI/ShadUI.csproj) 依赖 Avalonia 11.3.10；[0.2.4](https://github.com/accntech/shad-ui/blob/0.2.4/src/ShadUI/ShadUI.csproj) 要求 Avalonia 12.1.0，因此本次没有扩大框架版本迁移。

使用 ShadTheme、ShadUI Window/Card 及 Primary / Outline / Ghost / Destructive 控件样式。应用层覆盖配色与侧栏模板。MIT 许可证在 `docs/licenses/ShadUI-MIT.txt`，现有打包脚本包含该目录。

## 本地验证

| 检查 | 结果与边界 |
| --- | --- |
| 最终解决方案构建 | 0 警告、0 错误 |
| 现有回归测试 | 111 通过、29 跳过、0 失败；跳过项需要 macOS 或显式引擎/沙箱环境 |
| 页面冒烟 | 11 项页面断言通过：导航名称、任务卡片边界、详情随选择更新、项目筛选、窄窗口操作可见、短工作区列表可见、数字输入名称、折叠高度、保存栏、设置无重叠/输入框未裁切、结果正文高度 |
| 多任务数据 | 5 条暂停/终态任务、2 个项目，含长标题、错误、已知/未知用量；没有提交或执行模型任务 |
| 无后台 | 离线空列表、禁用任务操作、设置页面和窗口显隐检查通过 |
| Windows UIA | 5 组、64 项断言通过：合成暂停任务、设置元信息、退出取消后确认、无后台退出、失败通知 |
| 生命周期 | 主面板显示/隐藏/重开，悬浮窗、托盘，GUI 退出后 Host 继续存活；原 Codex 配置哈希未改变 |
| 视觉核对 | 已查看实际主界面、设置、角色展开、窄/短窗口、结果和离线截图 |
| 独立调查/审查 | DeepSeek explorer 完成旧布局/UIA 契约调查。最终 reviewer 在 180 秒时限内未给出结论，未计为审查通过 |

证据目录：

- `artifacts/validation/ui-v2-delivery/`：最终页面与窗口冒烟及截图。
- `artifacts/validation/ui-v2-final-interaction/result.json`：最终交互检查。随后仅修改“暂无任务”说明及冒烟截图选中项，交互处理器未再改动。
- `artifacts/validation/ui-v2-offline/`：无后台空状态；最后将零任务统计说明从“接入渠道已报告”改为“暂无任务”。
- `artifacts/validation/ui-v2-tests/`：回归测试 TRX。
- `artifacts/validation/ui-v2-final-stage/`：UIA 隔离开发二进制和已有固定引擎文件。没有替换用户安装目录。

中间失败截图与结果仍保留在 `ui-v2-multi`、`ui-v2-geometry`、`ui-v2-verified` 等目录，不作为通过证据。多任务夹具最初缺少 request tombstone，导致关闭隔离 Host 报错；补齐后关闭检查通过。设置卡片缩放时父容器保留旧高度的问题，通过在当前 arrange 完成后应用断点修复，并增加几何断言。

## ShadUI 接入细节

- C# 创建的主窗口显式设置 NameScope，满足数字输入行为的父作用域查找。
- 六个数字输入内部 `PART_TextBox` 保留可访问名称。
- 多行角色输入设置 `ControlAssist.Height`；结果正文内部高度随可用空间变化。
- ShadUI 按钮模板读取颜色资源；侧栏按钮单独覆盖资源，确保深色背景下可读。
- 侧栏和任务内容使用有界 Grid，保持滚动区域与底部操作可用。

## 复现与边界

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) 'artifacts/dotnet-home'
dotnet build AgentDock.slnx -m:1 --no-restore
dotnet test AgentDock.slnx -m:1 --no-build
pwsh -NoProfile -File scripts/Probe-Desktop.ps1 -SeedTasks -Output artifacts/validation/ui-v2-new-run
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Probe-WindowsInteraction.ps1 -InstallRoot artifacts/validation/ui-v2-final-stage -Output artifacts/validation/ui-v2-interaction-new
```

`Probe-Desktop.ps1` 创建全新隔离数据目录；UIA 探针的输出目录须不存在或为空。测试中凭据为空，失败通知用例在本地凭据检查处终止，没有调用模型。

开发版入口：`src/AgentDock.Desktop/bin/Debug/net10.0/AgentDock.Desktop.exe`。此次没有执行安装器或更新桌面快捷方式，因此现有安装版仍是原版本。

未据此声明 macOS、150%/200% 实际显示器 DPI、屏幕阅读器或长期性能验收通过。短窗口检查模拟较少的逻辑工作空间，不修改用户显示器设置。

## 2026-10-20 Mac 紧凑窗口实机补验

用户授权复制当前源码后，完整 Mac 候选 `0.1.1-acceptance.20261020.2` 在独立目录完成 21 项在线界面/引导和 13 项离线检查，默认 760×560、最小 640×480，真实安装版后台保持可用。接入过程发现的目录大小写/别名误判已作最小修复并重跑原流程。人工托盘、通知、原生选择器、系统缩放和 VoiceOver 仍待验收，详见 [Mac 紧凑窗口验收记录](mac-compact-20261020.md)。
