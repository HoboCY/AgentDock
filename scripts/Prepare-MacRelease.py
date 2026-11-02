#!/usr/bin/env python3
"""Validate a native Mac build and stage its installer, verification data and notes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(chunk)
    return value.hexdigest()


def regular_file(path):
    require(not path.is_symlink() and path.is_file(), f'缺少普通文件或文件是符号链接：{path.name}')


def prepare_release(version, package, output):
    require(re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*', version),
            '只允许带测试后缀的版本，例如 0.1.1-beta.3。')
    require(not os.path.lexists(output), 'Release 输出目录必须全新。')
    source_manifest = package / 'package-manifest.json'
    regular_file(source_manifest)
    raw = source_manifest.read_bytes()
    manifest = json.loads(raw)
    require(type(manifest) is dict, '包清单必须是对象。')
    expected = dict(schema=1, product='AgentDock', version=version, runtime='osx-arm64', configuration='Release',
                    selfContained=True, partial=False, releaseReady=False, signed=False, notarized=False)
    for key, value in expected.items():
        require(type(manifest.get(key)) is type(value) and manifest[key] == value, f'包清单字段不符：{key}')
    components = manifest.get('components')
    require(type(components) is dict, '缺少组件清单。')
    for name in ('host', 'desktop', 'engine', 'macSetup'):
        require(type(components.get(name)) is str and components[name].strip(), f'缺少组件：{name}')
    dotnet = manifest.get('dotnet')
    require(type(dotnet) is dict, '缺少 .NET 清单。')
    for name in ('sdk', 'expectedSdk', 'runtimeFramework'):
        require(type(dotnet.get(name)) is str and dotnet[name].strip(), f'缺少 .NET 字段：{name}')
    require(dotnet['sdk'] == dotnet['expectedSdk'], '.NET SDK 与锁定版本不符。')
    blockers = manifest.get('releaseBlocked')
    require(type(blockers) is list and all(type(item) is str for item in blockers), '发布阻塞项必须是字符串数组。')
    artifacts = manifest.get('artifacts')
    require(type(artifacts) is dict, '缺少附件清单。')
    dmg = f'AgentDockSetup-{version}-macos-arm64.dmg'
    files = dict(dmg=dmg, setupAppZip=f'AgentDockSetup-{version}-macos-arm64.app.zip',
                 payloadZip=f'AgentDock-{version}-macos-arm64.zip')
    for key, name in files.items():
        entry = artifacts.get(key)
        require(type(entry) is dict and entry.get('file') == name, f'附件记录或文件名不符：{key}')
        path = package / name
        regular_file(path)
        require(type(entry.get('size')) is int and entry['size'] == path.stat().st_size, f'附件大小不符：{name}')
        require(type(entry.get('sha256')) is str and entry['sha256'] == digest(path), f'附件 SHA-256 不符：{name}')

    manifest_name = 'package-manifest-macos-arm64.json'
    notes = f'''\n## macOS arm64 — Mac preview（非稳定发布）

适用于 Apple Silicon arm64 Mac。下载并打开 `{dmg}`，挂载后运行其中的安装器。DMG 内置完整 payload；普通用户不需要 JSON/ZIP，也无需手工复制程序文件。

### DMG 安装步骤

要求 macOS 14 或更高版本，支持 M 系列芯片，暂不支持 Intel Mac。

1. 下载 `{dmg}`，双击打开，在 Finder 磁盘窗口中双击 `AgentDockSetup-{version}-macos-arm64.app` 安装器。
2. 在“安装 AgentDock …？”对话框中点击 **“安装”**，等待显示“安装完成”。
3. 点击 **“启动”**，按“开始使用”向导完成后台启动、OpenCode Go API Key、Codex 接入及全局委派配置；接入完成后重启 Codex 并新建对话。
4. 安装完成后可推出 DMG。以后从 `~/Applications/AgentDock.app` 启动；若没有该入口，在 Finder 按 `Shift + Command + G`，输入 `~/Library/Application Support/AgentDock`，打开其中的 `AgentDock.app`。

如果首次打开提示“无法验证开发者”或“Apple 无法检查是否包含恶意软件”，确认下载来自本仓库且信任来源后，可先尝试打开一次，再到 **系统设置 → 隐私与安全性 → 仍要打开**，按提示认证并确认打开。若提示“已损坏”“会损坏电脑”或没有放行按钮，请停止安装并反馈完整提示。参见 [Apple 官方说明](https://support.apple.com/zh-cn/102445)。

完整教程及更新说明：[Mac 安装教程](https://github.com/HoboCY/AgentDock/blob/main/packaging/mac/README.md#用户安装教程)。

本候选已做 ad-hoc 资源封装，但没有 Developer ID 签名和 Apple 公证，Gatekeeper 仍可能拦截，尚未完成真实下载验收。资源完整性校验通过不代表 Apple 信任此 App。不推荐关闭 Gatekeeper 或删除隔离标记来绕过检查；稳定发布仍保留签名、公证和真实下载验收要求。

原始构建清单 releaseReady=false，releaseBlocked:
{chr(10).join('- ' + item for item in blockers)}
'''
    output.mkdir(parents=True)
    try:
        shutil.copyfile(package / dmg, output / dmg)
        require(digest(output / dmg) == artifacts['dmg']['sha256'], '复制后的 DMG 哈希不符。')
        (output / manifest_name).write_bytes(raw)
        sums = f"{artifacts['dmg']['sha256']}  {dmg}\n{hashlib.sha256(raw).hexdigest()}  {manifest_name}\n"
        (output / 'SHA256SUMS-macos-arm64.txt').write_bytes(sums.encode('utf-8'))
        (output / 'release-notes-macos-arm64.md').write_bytes(notes.encode('utf-8'))
    except Exception:
        shutil.rmtree(output)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', required=True)
    parser.add_argument('--package-dir', required=True, type=Path)
    parser.add_argument('--output-dir', required=True, type=Path)
    args = parser.parse_args()
    prepare_release(args.version, args.package_dir, args.output_dir)
    print(f'Mac 发布附件已校验：{args.output_dir}')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError) as error:
        print(f'错误：{error}', file=sys.stderr)
        sys.exit(1)
