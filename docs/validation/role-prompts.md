# English role prompt defaults (2026-10-18)

Tracking: [Issue #15](https://github.com/HoboCY/AgentDock/issues/15).

## Behavior

The five built-in role prompts are defined in `src/AgentDock.Core/RolePrompts.cs`. Each has its own workflow and completion criteria, followed by a shared agreement covering task scope, file ownership, independent execution, proportional checks, evidence, and stopping conditions. The requested response language and exact output format take precedence over the fallback report format.

- `default`: execute a bounded general task and validate its deliverable.
- `explorer`: trace the relevant implementation and evidence without changing project files.
- `worker`: implement an assigned change within file ownership, preserve other work, and verify behavior.
- `reviewer`: assess a specified change, prioritize actionable defects, and report severity, location, trigger, and impact.
- `verifier`: execute acceptance checks with a read-only project and outputs redirected to the allowed artifact directory.

Host startup upgrades only prompts that exactly match the former generic default for the same role. The upgrade copies the configuration rather than mutating existing role dictionaries, preserving custom text, timeouts, other settings, and accepted-task snapshots. It is idempotent. Queued tasks retain their original prompt when resumed; new tasks receive the upgraded prompt.

Runtime guidance now distinguishes filesystem write capability from assignment authorization. The .NET test example redirects both build and test artifacts. OS permissions, models, credentials, and concurrency settings are unchanged.

## Validation

- Solution build: zero warnings and errors.
- Full regression: 154 passed, 29 skipped, zero failed. Skips require macOS or explicitly enabled sandbox/pipeline environments.
- Targeted checks cover English defaults, exact per-role matching, custom text and timeout preservation, persistence, accepted-task snapshots, resumed execution, and repeated startup.
- AgentDock read-only review task `9c267a23c56b4dc186484e209e10a7a2` completed. Its actionable wording concern about project write capability was addressed. Its conditional concern about read-only roles receiving write access was checked against `IsProjectWritable`, which permits only default/worker. This review did not exercise OS enforcement.
- The current local host configuration was updated through `config.set` and read back through `config.get`: five exact old defaults were replaced, other settings and timeouts matched, and the host process did not restart. Existing accepted tasks retain their snapshots. This verifies configuration delivery, not a guarantee of model behavior on future tasks.

Evidence: `artifacts/validation/role-prompts-20261018/`, including `english-defaults.json`, `roles-at-apply.json`, `live-result.json`, and the targeted/full TRX results. No real model task was submitted to benchmark the new prompts; the review task was accepted before the configuration update.

The source defaults and this machine's saved role configuration are updated. Earlier `.1` and `.2` installation packages have not been rebuilt with these defaults. This validation did not publish a release.
