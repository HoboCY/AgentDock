# 第一版沿用 OpenCode Go 接入 DeepSeek

AgentDock 第一版沿用现有 OpenCode Go 到 DeepSeek 的接入渠道，直接使用 DeepSeek 官方 API Key 的接入方式留待后续扩展。这样可在重构控制台和任务管理时复用已有接入经验，减少同时替换接入渠道带来的验证范围；相应地，第一版用户需要使用 OpenCode Go 的凭据，不能将 DeepSeek 官方 API Key 当作同一种凭据。

模型和接入渠道在产品概念上保持区分，后续增加其他模型或渠道时继续使用这一约定。
