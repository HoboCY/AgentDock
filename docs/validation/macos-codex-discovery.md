# macOS client discovery repair (2026-10-18)

Tracking: [Issue #16](https://github.com/HoboCY/AgentDock/issues/16). Actual macOS acceptance remains tracked in [Issue #12](https://github.com/HoboCY/AgentDock/issues/12).

## Cause and scope

Onboarding searched only `/Applications/Codex.app/Contents/Resources/codex`, PATH, and Homebrew locations. It omitted the renamed `ChatGPT.app` and per-user Applications directory. Manual selection copied an `.app` directory into the executable field; `File.Exists` then reported it as missing without inspecting the bundled CLI.

Discovery now accepts both application names under system/user Applications and the user-local CLI directory. ChatGPT.app takes precedence over the legacy application name across those directories. Manual selection resolves an app to `Contents/Resources/codex` or `Contents/MacOS/codex`; it does not use the GUI entry point when resolving a bundle. A bundle without the CLI gets a specific message when no usable installation is found. Both rechecking and registration retry discovery after a missing/invalid path, while preserving a valid manual selection. Direct CLI selection and Windows discovery retain their existing behavior.

The macOS picker labels and file types allow selecting the application bundle, executable, or script. The selected command tool is used for the existing plugin registration/status commands; credentials, authentication and model routing are unchanged.

## Evidence and limits

- Before the fix, both selected-bundle regression cases failed at the actual onboarding property assignment (`ChatGPT.app` and `Codex.app`).
- After the initial fix: solution build passed with zero warnings/errors; full regression passed with 166 passed, 29 environment-dependent skips, zero failures. The initial 12 discovery cases cover both names/locations, manual selection, paths with spaces/trailing separators, CLI selection, and a GUI-only bundle with fallback to a usable legacy installation.
- AgentDock read-only review `2f139599e6c343a5be0b93cc9becc344` reached Completed (cursor 190, no remaining event pages). Its actionable stale-path recovery finding was addressed in recheck and registration; five additional cases cover missing/invalid/moved paths, preserving a valid manual choice, and preferring the renamed app across directories. The review also identified native macOS picker/runtime coverage limits; those remain open. Attempts to stop the extended review were rejected by automatic approval; it completed naturally and was not restarted.
- Final affected-project regression: 89 passed, 4 skipped, zero failures (`final/discovery-final.trx`). The unchanged engine's preceding 82 passed/25 skipped result remains applicable: 171 passed and 29 skipped in total, including 17 discovery cases. The final solution build also passed with zero warnings/errors.
- The existing Windows onboarding smoke passed all 19 checks with an isolated Codex home and synthetic credentials/tasks; the existing user Codex configuration hash was unchanged. Evidence: `artifacts/validation/macos-codex-discovery/windows-ui/result.json`. This checks shared-flow regressions, not the macOS native picker.
- Initial sandboxed test execution could not connect to testhost; it was an environment abort, not a product result. The failing reproduction and final tests used normal permissions with isolated temporary directories.
- Evidence: `artifacts/validation/macos-codex-discovery/discovery-before-elevated.trx` and `artifacts/validation/macos-codex-discovery/full/`.
- These checks ran on Windows with application-bundle directory fixtures. They do not establish that the user's installed Mac bundle has the same layout, nor validate the native file picker, executable permissions, or installed-client plugin registration. No new macOS installer was built in this repair.

## User follow-up: nvm CLI and desktop App integration

The user reported `codex-classic.wav` without `codex` in the suggested application directory. Their subsequent terminal lookup located the CLI at `~/.nvm/versions/node/v25.0.0/bin/codex`. This confirms an installed CLI location omitted by the earlier discovery logic; it does not establish that their App has no other bundled runtime. They primarily use Codex in the ChatGPT desktop App.

Discovery now falls back to `NVM_DIR` (default `~/.nvm`), scans only immediate Node version directories, and chooses the highest numeric version containing `bin/codex`. Existing PATH/app/local/Homebrew candidates and valid manual selections retain precedence. No shell startup files are evaluated. npm-style launchers with a sibling `node` receive that bin directory at the front of the child process PATH, preserving the selected symlink path and the parent's environment. A Node lookup failure was not observed on the user's Mac; the child PATH handling prevents the known environment gap when a GUI process lacks the nvm PATH.

Before this follow-up fix, three isolated regression cases failed: default/custom nvm discovery and child Node search path. After the fix, the affected test project passed 95 tests with five environment-dependent skips, including 23 discovery cases passing and one native Mac launch case skipped. The solution build passed with zero warnings/errors. Evidence: `artifacts/validation/macos-codex-discovery/nvm/nvm-before.trx` and `nvm-regression.trx`. Synthetic filesystem roots isolate discovery from the test machine's actual CLI installs. A Mac-only test exercises an npm-style symlink and `/usr/bin/env node` with a synthetic Node script; it has not run on this Windows host.

The onboarding text now distinguishes the CLI used for plugin configuration from the desktop App used for daily work. The official [local plugin documentation](https://developers.openai.com/plugins/build/plugins) describes CLI marketplace registration and ChatGPT desktop App installation, with local plugin settings in `~/.codex/config.toml`. Registration and App use must target the same Codex home. Actual acceptance still requires restarting the user's desktop App, opening a new chat, and invoking AgentDock there. This follow-up has not produced a new Mac installer or established native App acceptance.
