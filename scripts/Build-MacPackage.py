#!/usr/bin/env python3
# ruff: noqa: E501
"""AgentDock macOS (arm64) 构件构建脚本（只在真实 macOS 上运行）。

构建固定版本组合的安装包：
  AgentDockSetup-<version>-macos-arm64.app        安装器 .app（自包含 MacSetup + payload.zip）
  AgentDockSetup-<version>-macos-arm64.app.zip    安装器 .app 压缩包（ditto 打包）
  AgentDock-<version>-macos-arm64.zip             payload（desktop/ host/ engine/ engines/ plugin/）
  [AgentDockSetup-<version>-macos-arm64.dmg]      hdiutil 可用时生成的本地候选
  package-manifest.json                           构件清单（哈希、provenance、发布阻塞项）
  executables.json                                可执行文件 SHA-256/权限位清单
  sbom.json                                       组件清单（releaseReady=false）

契约（fail closed）：
  - 绝不下载任何运行时。OpenCode 1.18.30 / Codex 0.153.4 / Git 独立分发都由 main 预先验证并以
    本地目录传入；Git 必须附带 provenance.json（verified=true、version、sha256），否则构建失败。
  - 每个运行时输入目录与 payload 都不允许符号链接；可执行文件权限位写入 payload-manifest.json（mode），
    由 MacSetup 在解包/复制时应用并校验。
  - 自包含发布固定 RuntimeFrameworkVersion=<lock.dotnet.runtime>；SDK 必须以 <lock.dotnet.sdk> 开头。
  - 未签名、未公证、未在真实 Mac 完成验收前 releaseReady 恒为 false；可生成明确披露限制的预发布，不能作为稳定版。

用法示例：
  python3 scripts/Build-MacPackage.py --version 0.1.0 \\
    --opencode-root /path/to/opencode-darwin-arm64 \\
    --codex-root /path/to/vendor-aarch64-apple-darwin \\
    --opencode-archive /path/to/opencode.tgz --codex-archive /path/to/codex.tgz \\
    --git-root /path/to/git-standalone
"""

from __future__ import annotations

import argparse
import base64
import datetime
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import zipfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_LOCK = os.path.join(REPO_ROOT, "packaging", "mac", "runtime-lock.json")
DEFAULT_TEMPLATE = os.path.join(REPO_ROOT, "packaging", "mac", "Info.plist.template")
PLATFORM = "osx-arm64"
EXECUTABLE_NAME = "AgentDock"
BUNDLE_ID = "org.agentdock.app"
VERSION_PATTERN = re.compile(r"^[0-9A-Za-z][0-9A-Za-z._-]*$")


class BuildError(RuntimeError):
    pass


def fail(message: str) -> None:
    raise BuildError(message)


def run(command: list[str], cwd: str | None = None) -> None:
    print("  $", " ".join(command), flush=True)
    result = subprocess.run(command, cwd=cwd)
    if result.returncode != 0:
        fail(f"命令失败（exit {result.returncode}）：{' '.join(command)}")


def sha256_file(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def normalize_mode(mode: int) -> str:
    """把源文件权限归一为 0755（任何可执行位）或 0644，写入清单并实际 chmod。"""
    return "0755" if mode & 0o111 else "0644"


def check_no_symlinks(root: str, label: str) -> None:
    for current, directories, files in os.walk(root, followlinks=False):
        for name in list(directories) + files:
            path = os.path.join(current, name)
            if os.path.islink(path):
                fail(f"{label} 含符号链接：{path}（payload 不允许符号链接）")


def copy_tree(source: str, destination: str, label: str) -> None:
    check_no_symlinks(source, label)
    os.makedirs(destination, exist_ok=True)
    for current, directories, files in os.walk(source):
        relative = os.path.relpath(current, source)
        target_dir = destination if relative == "." else os.path.join(destination, relative)
        os.makedirs(target_dir, exist_ok=True)
        for name in files:
            source_file = os.path.join(current, name)
            target_file = os.path.join(target_dir, name)
            shutil.copy2(source_file, target_file)
            os.chmod(target_file, int(normalize_mode(os.stat(source_file).st_mode), 8))


def normalize_stage_modes(stage: str) -> None:
    for current, _directories, files in os.walk(stage):
        for name in files:
            path = os.path.join(current, name)
            os.chmod(path, int(normalize_mode(os.stat(path).st_mode), 8))


def json_load(path: str) -> object:
    with open(path, encoding="utf-8") as stream:
        return json.load(stream)


def read_version_file(root: str, candidates: list[str], key: str) -> str | None:
    for candidate in candidates:
        path = os.path.join(root, candidate)
        if os.path.isfile(path):
            try:
                data = json_load(path)
            except (ValueError, OSError) as error:
                fail(f"版本文件无法解析（{path}）：{error}")
            if isinstance(data, dict):
                value = data.get(key)
                if isinstance(value, str) and value:
                    return value
    return None


def version_matches(actual: str, expected: str) -> bool:
    return actual == expected or actual.startswith(expected + "-") or actual.startswith(expected + "+")


def load_provenance(path: str, name: str) -> dict:
    if not os.path.isfile(path):
        fail(f"缺少 {name} 的 provenance 文件：{path}（main 必须先验证运行时并提供来源/哈希元数据）")
    try:
        data = json_load(path)
    except (ValueError, OSError) as error:
        fail(f"{name} 的 provenance 无法解析（{path}）：{error}")
    if not isinstance(data, dict):
        fail(f"{name} 的 provenance 必须是 JSON 对象：{path}")
    if data.get("verified") is not True:
        fail(f"{name} 的 provenance 未标记 verified=true：{path}（禁止使用未验证运行时）")
    version = data.get("version")
    if not isinstance(version, str) or not version:
        fail(f"{name} 的 provenance 缺少 version：{path}")
    digest = data.get("sha256") or data.get("archiveSha256")
    if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest):
        fail(f"{name} 的 provenance 缺少 64 位十六进制 sha256/archiveSha256：{path}")
    return data


def verify_archive(root: str, archive_path: str, spec: dict) -> None:
    """Bind unpacked npm runtime bytes to the pinned official archive."""
    digest = hashlib.sha512()
    with open(archive_path, "rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    if base64.b64encode(digest.digest()).decode() != spec["archiveSha512"]:
        fail("运行时官方归档 SHA-512 不符：" + archive_path)
    prefix = spec["archivePrefix"]
    expected = set()
    with tarfile.open(archive_path, "r:gz") as archive:
        for entry in archive:
            if not entry.name.startswith(prefix) or entry.isdir():
                continue
            relative = entry.name[len(prefix):]
            if not entry.isfile() or not relative or ".." in relative.split("/"):
                fail("运行时归档包含非普通文件或无效路径：" + entry.name)
            expected.add(relative)
            target = os.path.join(root, *relative.split("/"))
            source = archive.extractfile(entry)
            digest = hashlib.sha256()
            for block in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(block)
            if not os.path.isfile(target) or sha256_file(target) != digest.hexdigest():
                fail("运行时文件与已固定归档不一致：" + target)
    actual = {os.path.relpath(os.path.join(folder, name), root).replace(os.sep, "/")
              for folder, _, files in os.walk(root) for name in files}
    if actual != expected:
        fail("运行时目录含归档外文件或缺失文件：" + repr(sorted(actual ^ expected)))


def verify_dotnet(lock: dict, allow_mismatch: bool) -> dict:
    expected_sdk = str(lock["dotnet"]["sdk"])
    try:
        result = subprocess.run(
            ["dotnet", "--version"], capture_output=True, text=True, check=True
        )
    except (OSError, subprocess.CalledProcessError) as error:
        fail(f"无法运行 dotnet --version：{error}")
    sdk = result.stdout.strip()
    if not sdk.startswith(expected_sdk) and not allow_mismatch:
        fail(f"dotnet SDK 版本不符：{sdk}（要求 {expected_sdk}*；仅本地排查可加 --allow-sdk-mismatch）")
    return {"sdk": sdk, "expectedSdk": expected_sdk}


def publish(project: str, destination: str, version: str, runtime: str, offline: bool) -> None:
    os.makedirs(destination, exist_ok=True)
    command = [
        "dotnet", "publish", project, "-c", "Release", "-r", PLATFORM,
        "--self-contained", "true", "-o", destination,
        "--nologo", "-m:1", "--disable-build-servers", "-nr:false", "-p:UseSharedCompilation=false",
        f"-p:Version={version}", f"-p:RuntimeFrameworkVersion={runtime}",
        "-p:DebugType=None", "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=false", "-p:PublishTrimmed=false", "-p:PublishSingleFile=false",
    ]
    if offline:
        command.append("-p:NuGetAudit=false")
    run(command)
    for current, _directories, files in os.walk(destination):
        for name in files:
            if name.endswith(".pdb"):
                os.remove(os.path.join(current, name))


def zip_tree(root: str, output: str) -> None:
    """确定性 ZIP：Unix 模式写入 external_attr，安装程序据此校验/应用权限位。"""
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for current, directories, files in os.walk(root):
            directories.sort()
            files.sort()
            for name in files:
                path = os.path.join(current, name)
                relative = os.path.relpath(path, root).replace(os.sep, "/")
                info = zipfile.ZipInfo(relative, date_time=(1980, 1, 1, 0, 0, 0))
                info.create_system = 3
                info.external_attr = (stat.S_IFREG | (os.stat(path).st_mode & 0o7777)) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                with archive.open(info, "w") as target, open(path, "rb") as source:
                    shutil.copyfileobj(source, target, 1024 * 1024)


def build_payload_manifest(stage: str, version: str) -> dict:
    files = []
    for current, directories, names in os.walk(stage):
        directories.sort()
        names.sort()
        for name in names:
            path = os.path.join(current, name)
            relative = os.path.relpath(path, stage).replace(os.sep, "/")
            if relative == "payload-manifest.json":
                continue
            status = os.stat(path, follow_symlinks=False)
            files.append({
                "path": relative,
                "size": status.st_size,
                "sha256": sha256_file(path),
                "mode": normalize_mode(status.st_mode),
            })
    files.sort(key=lambda item: item["path"])
    return {
        "schema": 1,
        "product": "AgentDock",
        "version": version,
        "runtime": PLATFORM,
        "partial": False,
        "files": files,
    }


def build_info_plist(template_path: str, version: str, destination: str,
                     executable: str = EXECUTABLE_NAME, bundle_id: str = BUNDLE_ID) -> None:
    with open(template_path, encoding="utf-8") as stream:
        text = stream.read()
    build = re.sub(r"[^0-9.]", ".", version).strip(".") or "0"
    text = text.replace("@VERSION@", version).replace("@BUILD@", build)
    text = text.replace("@EXECUTABLE@", executable).replace("@BUNDLE_ID@", bundle_id)
    with open(destination, "w", encoding="utf-8") as stream:
        stream.write(text)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="Build-MacPackage.py", description="构建 AgentDock macOS (arm64) 安装包（只在 macOS 上运行）"
    )
    parser.add_argument("--version", required=True, help="包版本（字母/数字/点/下划线/连字符）")
    parser.add_argument("--output-dir", default=None, help="输出目录，默认 artifacts/package/<version>/osx-arm64")
    parser.add_argument("--opencode-root", required=True, help="已验证的 OpenCode 1.18.30 解包目录")
    parser.add_argument("--codex-root", required=True, help="已验证的 Codex 0.153.4 vendor 树（其内容成为 engines/codex）")
    parser.add_argument("--git-root", required=True, help="main 提供的独立 Git 分发目录（必须附 provenance.json）")
    parser.add_argument("--opencode-archive", required=True, help="官方 OpenCode npm 归档；校验固定 SHA-512 与展开文件")
    parser.add_argument("--codex-archive", required=True, help="官方 Codex npm 归档；校验固定 SHA-512 与展开文件")
    parser.add_argument("--opencode-provenance", default=None, help="可选：OpenCode provenance JSON 路径")
    parser.add_argument("--codex-provenance", default=None, help="可选：Codex provenance JSON 路径")
    parser.add_argument("--git-provenance", default=None, help="Git provenance JSON 路径（默认 <git-root>/provenance.json）")
    parser.add_argument("--runtime-lock", default=DEFAULT_LOCK, help="运行时锁清单，默认 packaging/mac/runtime-lock.json")
    parser.add_argument("--payload-format", choices=["zip", "folder", "both"], default="zip",
                        help="payload.zip 始终生成（安装器 .app 需要）；folder/both 额外保留展开目录")
    parser.add_argument("--dmg", action="store_true", help="生成 DMG（hdiutil 可用时默认生成；--no-dmg 关闭）")
    parser.add_argument("--no-dmg", action="store_true", help="不生成 DMG")
    parser.add_argument("--allow-sdk-mismatch", action="store_true", help="仅本地排查：允许 SDK 补丁版本不符")
    parser.add_argument("--offline", action="store_true", help="离线构建（关闭 NuGet 审计）")
    args = parser.parse_args(argv)

    if sys.platform != "darwin":
        fail("Build-MacPackage.py 只在 macOS 上运行。Windows 侧请直接用 dotnet build/publish 做交叉编译检查。")
    if not VERSION_PATTERN.fullmatch(args.version):
        fail(f"版本号不合法：{args.version}")

    lock = json_load(args.runtime_lock)
    if lock.get("platform") != PLATFORM:
        fail(f"运行时锁清单平台不符：{lock.get('platform')}（要求 {PLATFORM}）")
    dotnet = verify_dotnet(lock, args.allow_sdk_mismatch)
    runtime_framework = str(lock["dotnet"]["runtime"])

    output_dir = args.output_dir or os.path.join(REPO_ROOT, "artifacts", "package", args.version, PLATFORM)
    output_dir = os.path.abspath(output_dir)
    if os.path.lexists(output_dir):
        fail("输出目录已存在；请使用新的候选目录，保留已有证据：" + output_dir)
    stage = os.path.join(output_dir, ".stage")
    setup_stage = os.path.join(output_dir, ".setup")
    os.makedirs(stage, exist_ok=True)

    try:
        # ---- 1. 校验 main 提供的运行时输入（只复制，绝不下载）----
        inputs = {
            "opencode": (os.path.abspath(args.opencode_root), args.opencode_provenance),
            "codex": (os.path.abspath(args.codex_root), args.codex_provenance),
            "git": (os.path.abspath(args.git_root), args.git_provenance),
        }
        runtime_records: dict[str, dict] = {}
        for name, spec in lock["runtimes"].items():
            root, provenance_arg = inputs[name]
            if not os.path.isdir(root):
                fail(f"{name} 输入目录不存在：{root}")
            check_no_symlinks(root, f"{name} 输入")
            if name in ("opencode", "codex"):
                verify_archive(root, getattr(args, name + "_archive"), spec)
            missing = [item for item in spec["required"] if not os.path.isfile(os.path.join(root, item))]
            if missing:
                fail(f"{name} 输入缺少必需文件：{', '.join(missing)}（根：{root}）")

            expected = spec.get("version")
            actual = read_version_file(root, spec.get("versionFiles") or [], spec.get("versionKey", "version"))
            if expected:
                if actual is None:
                    fail(f"{name} 输入无法读取版本（候选：{spec.get('versionFiles')}）；请提供含版本的文件")
                if not version_matches(actual, expected):
                    fail(f"{name} 版本不符：{actual}（要求 {expected}）")

            provenance_path = provenance_arg or os.path.join(root, spec.get("provenanceFile", "provenance.json"))
            provenance = None
            if name in ("opencode", "codex"):
                provenance = dict(verified=True, version=expected, source=spec["source"],
                                  sha256=sha256_file(getattr(args, name + "_archive")),
                                  integrity="sha512-" + spec["archiveSha512"])
                if provenance_arg and load_provenance(provenance_arg, name).get("sha256") != provenance["sha256"]:
                    fail(f"{name} 附加 provenance 与官方归档不一致")
            elif os.path.isfile(provenance_path):
                provenance = load_provenance(provenance_path, name)
                if expected and not version_matches(str(provenance["version"]), expected):
                    fail(f"{name} provenance 版本不符：{provenance['version']}（要求 {expected}）")
            elif spec.get("provenanceRequired"):
                fail(f"{name} 必须提供 provenance（{provenance_path}）；禁止使用未验证的 Git 分发")
            if name == "git":
                source_archive = os.path.join(root, "share", "agentdock-source", "git-2.54.0.tar.xz")
                if not os.path.isfile(source_archive) or sha256_file(source_archive) != spec["sourceSha256"]:
                    fail("Git 随包源码归档与锁定 SHA-256 不一致")
                if not provenance.get("files"):
                    fail("Git 缺少由 Prepare-MacGit.py 生成的构建文件清单")
                actual_files = {os.path.relpath(os.path.join(folder, item), root).replace(os.sep, "/"):
                                sha256_file(os.path.join(folder, item))
                                for folder, _, names in os.walk(root) for item in names
                                if os.path.join(folder, item) != os.path.join(root, "provenance.json")}
                if actual_files != provenance["files"]:
                    fail("Git 分发内容与构建完成时的文件清单不一致")

            file_count = 0
            total_size = 0
            for current, _directories, files in os.walk(root):
                for file_name in files:
                    file_count += 1
                    total_size += os.path.getsize(os.path.join(current, file_name))
            runtime_records[name] = {
                "input": os.path.basename(root),
                "version": expected or (provenance or {}).get("version"),
                "required": spec["required"],
                "fileCount": file_count,
                "totalSize": total_size,
                "provenance": {key: value for key, value in provenance.items() if key != "files"},
                "license": spec.get("license"),
                "homepage": spec.get("homepage"),
            }
            print(f"== {name}: {runtime_records[name]['version']}（{file_count} 个文件，{total_size} 字节）", flush=True)

        # ---- 2. 自包含发布组件 ----
        print("== 发布 Host / Desktop / Engine / MacSetup（osx-arm64 self-contained）", flush=True)
        publish(os.path.join(REPO_ROOT, "src", "AgentDock.Host", "AgentDock.Host.csproj"),
                os.path.join(stage, "host"), args.version, runtime_framework, args.offline)
        desktop_contents = os.path.join(stage, "desktop", "AgentDock.app", "Contents")
        publish(os.path.join(REPO_ROOT, "src", "AgentDock.Desktop", "AgentDock.Desktop.csproj"),
                os.path.join(desktop_contents, "MacOS"), args.version, runtime_framework, args.offline)
        build_info_plist(DEFAULT_TEMPLATE, args.version, os.path.join(desktop_contents, "Info.plist"),
                         "AgentDock.Desktop", "org.agentdock.desktop")
        publish(os.path.join(REPO_ROOT, "src", "AgentDock.Engine", "AgentDock.Engine.csproj"),
                os.path.join(stage, "engine"), args.version, runtime_framework, args.offline)
        setup_macos = os.path.join(setup_stage, "AgentDock.app", "Contents", "MacOS")
        publish(os.path.join(REPO_ROOT, "src", "AgentDock.MacSetup", "AgentDock.MacSetup.csproj"),
                setup_macos, args.version, runtime_framework, args.offline)

        # ---- 3. 组装 payload ----
        print("== 组装 payload", flush=True)
        for name, spec in lock["runtimes"].items():
            destination = os.path.join(stage, spec["to"])
            copy_tree(inputs[name][0], destination, f"{name} 输入")
            if name == "opencode":
                shutil.move(os.path.join(destination, "bin", "opencode"), os.path.join(destination, "opencode"))
            descriptor = {"schema": 1, "name": name, **{k: v for k, v in runtime_records[name].items() if k != "input"}}
            descriptor["input"] = runtime_records[name]["input"]
            with open(os.path.join(destination, f"{name}-runtime.json"), "w", encoding="utf-8") as stream:
                json.dump(descriptor, stream, ensure_ascii=False, indent=2)

        plugin_source = os.path.join(REPO_ROOT, "packaging", "plugin")
        copy_tree(plugin_source, os.path.join(stage, "plugin"), "plugin 模板")
        plugin_json_path = os.path.join(stage, "plugin", "plugins", "agentdock", ".codex-plugin", "plugin.json")
        plugin_json = json_load(plugin_json_path)
        plugin_json["version"] = args.version
        with open(plugin_json_path, "w", encoding="utf-8") as stream:
            json.dump(plugin_json, stream, ensure_ascii=False, indent=2)
        # .mcp.json 的 command 在安装时改写为绝对稳定启动器路径；占位符确保不会误用相对路径。
        mcp_placeholder = {"mcpServers": {"agentdock": {"command": "AGENTDOCK_LAUNCHER_PLACEHOLDER", "args": ["--mcp"]}}}
        with open(os.path.join(stage, "plugin", "plugins", "agentdock", ".mcp.json"), "w", encoding="utf-8") as stream:
            json.dump(mcp_placeholder, stream, ensure_ascii=False, indent=2)

        shutil.copy2(args.runtime_lock, os.path.join(stage, "runtime-lock.json"))
        copy_tree(os.path.join(REPO_ROOT, "docs", "licenses"), os.path.join(stage, "licenses"), "licenses")

        # ---- 4. payload 权限位规范化 + 清单 ----
        check_no_symlinks(stage, "payload staging")
        normalize_stage_modes(stage)
        for relative in ("host/AgentDock.Host", "desktop/AgentDock.app/Contents/MacOS/AgentDock.Desktop",
                         "engine/AgentDock.Engine", "engines/opencode/opencode", "engines/codex/bin/codex",
                         "engines/codex/codex-path/rg", "engines/git/bin/git"):
            executable = os.path.join(stage, relative)
            if not os.path.isfile(executable) or not os.access(executable, os.X_OK):
                fail("完整 payload 入口缺失或不可执行：" + relative)
        manifest = build_payload_manifest(stage, args.version)
        manifest_path = os.path.join(stage, "payload-manifest.json")
        with open(manifest_path, "w", encoding="utf-8") as stream:
            json.dump(manifest, stream, ensure_ascii=False, indent=2)

        # 安装器 .app 必须内嵌 payload.zip，因此始终生成；--payload-format folder/both 额外保留展开目录。
        payload_zip = os.path.join(output_dir, f"AgentDock-{args.version}-macos-arm64.zip")
        print(f"== 生成 payload 压缩包：{payload_zip}", flush=True)
        if os.path.exists(payload_zip):
            os.remove(payload_zip)
        zip_tree(stage, payload_zip)
        payload_folder = None
        if args.payload_format in ("folder", "both"):
            payload_folder = os.path.join(output_dir, f"payload-{PLATFORM}")
            shutil.rmtree(payload_folder, ignore_errors=True)
            shutil.copytree(stage, payload_folder, symlinks=False)

        # ---- 5. 安装器 .app ----
        print("== 组装安装器 .app", flush=True)
        app_dir = os.path.join(setup_stage, "AgentDock.app")
        build_info_plist(DEFAULT_TEMPLATE, args.version, os.path.join(app_dir, "Contents", "Info.plist"),
                         bundle_id="org.agentdock.setup")
        resources = os.path.join(app_dir, "Contents", "Resources")
        os.makedirs(resources, exist_ok=True)
        launcher = os.path.join(resources, "launcher", "AgentDock.app", "Contents")
        copy_tree(setup_macos, os.path.join(launcher, "MacOS"), "stable launcher")
        build_info_plist(DEFAULT_TEMPLATE, args.version, os.path.join(launcher, "Info.plist"))
        shutil.copy2(payload_zip, os.path.join(resources, "payload.zip"))
        for current, _directories, files in os.walk(setup_macos):
            for name in files:
                if name.endswith(".pdb"):
                    os.remove(os.path.join(current, name))
        setup_app = os.path.join(output_dir, f"AgentDockSetup-{args.version}-macos-arm64.app")
        shutil.rmtree(setup_app, ignore_errors=True)
        shutil.move(app_dir, setup_app)

        setup_zip = os.path.join(output_dir, f"AgentDockSetup-{args.version}-macos-arm64.app.zip")
        if os.path.exists(setup_zip):
            os.remove(setup_zip)
        if shutil.which("ditto"):
            run(["ditto", "-c", "-k", "--keepParent", setup_app, setup_zip])
        else:
            print("警告：未找到 ditto，使用 Python zip 打包 .app", flush=True)
            zip_tree(setup_app, setup_zip)

        dmg_path = None
        if not args.no_dmg:
            if shutil.which("hdiutil"):
                dmg_path = os.path.join(output_dir, f"AgentDockSetup-{args.version}-macos-arm64.dmg")
                if os.path.exists(dmg_path):
                    os.remove(dmg_path)
                run(["hdiutil", "create", "-volname", f"AgentDock {args.version}", "-srcfolder", setup_app,
                     "-ov", "-format", "UDZO", dmg_path])
            elif args.dmg:
                fail("--dmg 需要 hdiutil（仅在 macOS 上可用）")
            else:
                print("警告：未找到 hdiutil，跳过 DMG（zip 已足够作为本地候选）", flush=True)

        # ---- 6. 构件清单 / 可执行清单 / SBOM ----
        executables = [
            {"path": item["path"], "sha256": item["sha256"], "mode": item["mode"]}
            for item in manifest["files"] if item["mode"] == "0755"
        ]
        executables_path = os.path.join(output_dir, "executables.json")
        with open(executables_path, "w", encoding="utf-8") as stream:
            json.dump({"schema": 1, "product": "AgentDock", "version": args.version, "files": executables},
                      stream, ensure_ascii=False, indent=2)

        artifacts = {
            "payloadZip": {"file": os.path.basename(payload_zip), "size": os.path.getsize(payload_zip),
                           "sha256": sha256_file(payload_zip)},
            "payloadManifestSha256": sha256_file(manifest_path),
            "setupApp": {"dir": os.path.basename(setup_app)},
            "setupAppZip": {"file": os.path.basename(setup_zip), "size": os.path.getsize(setup_zip),
                            "sha256": sha256_file(setup_zip)},
            "executables": {"file": os.path.basename(executables_path), "count": len(executables)},
        }
        if payload_folder:
            artifacts["payloadFolder"] = {"dir": os.path.basename(payload_folder)}
        if dmg_path:
            artifacts["dmg"] = {"file": os.path.basename(dmg_path), "size": os.path.getsize(dmg_path),
                                "sha256": sha256_file(dmg_path)}

        release_blocked = [
            "developer-id-signature-missing：未以 Developer ID 签署发行包（内部二进制可能含工具链 ad-hoc 签名）",
            "notarization-missing：未做 Apple notarization/stapling",
            "macos-acceptance-evidence-not-attached：构建不会自动附带实机验收记录；发布前须核对对应构件的外部验收证据",
        ]
        if dotnet["sdk"] != dotnet["expectedSdk"]:
            release_blocked.append(f"dotnet-sdk-mismatch：{dotnet['sdk']} != {dotnet['expectedSdk']}")

        package_manifest = {
            "schema": 1,
            "product": "AgentDock",
            "version": args.version,
            "runtime": PLATFORM,
            "configuration": "Release",
            "selfContained": True,
            "partial": False,
            "releaseReady": False,
            "signed": False,
            "notarized": False,
            "createdAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "dotnet": {"sdk": dotnet["sdk"], "expectedSdk": dotnet["expectedSdk"], "runtimeFramework": runtime_framework},
            "runtimes": runtime_records,
            "components": {
                "host": "src/AgentDock.Host",
                "desktop": "src/AgentDock.Desktop",
                "engine": "src/AgentDock.Engine",
                "macSetup": "src/AgentDock.MacSetup",
            },
            "artifacts": artifacts,
            "releaseBlocked": release_blocked,
            "notes": [
                "安装布局：<root>=~/Library/Application Support/AgentDock；稳定 .app=<root>/AgentDock.app，~/Applications 为可选入口。",
                "payload 不含任何个人配置、凭据、Keychain 数据或任务数据；插件源不含用户密钥。",
                "稳定插件 .mcp.json 在安装时改写为绝对稳定启动器路径；启动器动态读取 install/current.json。",
                "随包 Git：engines/git（Host/引擎 PATH 注入）；宿主 shell 为系统 /bin/bash。",
            ],
        }
        package_manifest_path = os.path.join(output_dir, "package-manifest.json")
        with open(package_manifest_path, "w", encoding="utf-8") as stream:
            json.dump(package_manifest, stream, ensure_ascii=False, indent=2)

        sbom = {
            "schema": 1,
            "product": "AgentDock",
            "version": args.version,
            "platform": PLATFORM,
            "releaseReady": False,
            "components": [
                {"name": "AgentDock", "version": args.version, "kind": "application"},
                {"name": ".NET Runtime", "version": runtime_framework, "kind": "runtime", "license": "MIT"},
            ] + [
                {"name": name, "version": record["version"], "kind": "bundled-runtime",
                 "license": record["license"], "homepage": record["homepage"],
                 "provenance": record["provenance"]}
                for name, record in runtime_records.items()
            ],
            "artifacts": {key: value for key, value in artifacts.items()},
        }
        sbom_path = os.path.join(output_dir, "sbom.json")
        with open(sbom_path, "w", encoding="utf-8") as stream:
            json.dump(sbom, stream, ensure_ascii=False, indent=2)

        shutil.rmtree(stage, ignore_errors=True)
        shutil.rmtree(setup_stage, ignore_errors=True)
        summary = {
            "Version": args.version,
            "Platform": PLATFORM,
            "Output": output_dir,
            "ReleaseReady": False,
            "PayloadZip": artifacts["payloadZip"],
            "SetupApp": os.path.basename(setup_app),
            "SetupZip": artifacts["setupAppZip"]["file"],
            "Dmg": os.path.basename(dmg_path) if dmg_path else None,
            "Executables": len(executables),
        }
        print("构建完成：" + output_dir, flush=True)
        print(json.dumps(summary, ensure_ascii=False), flush=True)
        return 0
    except Exception:
        print("构建失败；.stage 保留用于排查：" + stage, file=sys.stderr, flush=True)
        raise


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except BuildError as error:
        print("错误：" + str(error), file=sys.stderr)
        sys.exit(1)
