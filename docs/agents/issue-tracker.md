# Issue tracker: GitHub

仓库：https://github.com/HoboCY/AgentDock
需求、规格和任务统一存放在 GitHub Issues。

## 操作约定

使用 gh CLI；命令显式指定 --repo HoboCY/AgentDock。

- 创建：gh issue create --repo HoboCY/AgentDock --title "标题" --body-file <文件>
- 读取：gh issue view <编号> --repo HoboCY/AgentDock --json number,title,body,labels,comments
- 列表：gh issue list --repo HoboCY/AgentDock --state open --json number,title,body,labels
- 评论：gh issue comment <编号> --repo HoboCY/AgentDock --body-file <文件>
- 标签：gh issue edit <编号> --repo HoboCY/AgentDock --add-label <标签>
- 关闭：gh issue close <编号> --repo HoboCY/AgentDock

多行正文先保存到临时 UTF-8 文件，再通过 --body-file 提交。
技能要求“发布到任务追踪器”时创建 GitHub Issue；
要求“读取任务”时读取对应 Issue 及评论。

## Pull requests as a triage surface

PRs as a request surface: no.

## Wayfinding

- 用一个带 wayfinder:map 标签的 Issue 保存决策地图。
- 每个问题建立独立子 Issue，类型标签为
  wayfinder:research、wayfinder:prototype、wayfinder:grilling 或 wayfinder:task。
- 优先使用原生子 Issue 和依赖关系；不可用时在正文记录
  Part of #编号 和 Blocked by: #编号。
- 从地图顺序中选择尚未关闭、未认领且阻塞已解除的任务。
- 执行前认领；解决后追加答案、关闭任务，并更新地图中的决策及链接。
