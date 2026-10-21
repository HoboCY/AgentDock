#!/usr/bin/env python3
"""Synthetic regression tests for Prepare-MacRelease.py (no real DMG needed)."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("Prepare-MacRelease.py")
VERSION = "0.1.1-beta.1"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def names(version):
    return (
        f"AgentDockSetup-{version}-macos-arm64.dmg",
        f"AgentDockSetup-{version}-macos-arm64.app.zip",
        f"AgentDock-{version}-macos-arm64.zip",
    )


class MacReleaseTests(unittest.TestCase):
    def fixture(self, root, version=VERSION):
        package = root / "package"
        package.mkdir()
        dmg, setup, payload = names(version)
        for name, content in (
            (dmg, b"synthetic dmg fixture\n"),
            (setup, b"synthetic setup app fixture\n"),
            (payload, b"synthetic payload fixture\n"),
            ("private-fixture.txt", b"do not publish\n"),
        ):
            (package / name).write_bytes(content)

        def artifact(name):
            path = package / name
            return {"file": name, "size": path.stat().st_size, "sha256": digest(path)}

        manifest = {
            "schema": 1,
            "product": "AgentDock",
            "version": version,
            "runtime": "osx-arm64",
            "configuration": "Release",
            "selfContained": True,
            "partial": False,
            "releaseReady": False,
            "signed": False,
            "notarized": False,
            "dotnet": {
                "sdk": "10.0.401",
                "expectedSdk": "10.0.401",
                "runtimeFramework": "10.0.12",
            },
            "components": {
                "host": "src/AgentDock.Host",
                "desktop": "src/AgentDock.Desktop",
                "engine": "src/AgentDock.Engine",
                "macSetup": "src/AgentDock.MacSetup",
            },
            "artifacts": {
                "dmg": artifact(dmg),
                "setupAppZip": artifact(setup),
                "payloadZip": artifact(payload),
            },
            "releaseBlocked": [
                "developer-id-signature-missing：未以 Developer ID 签署发行包",
                "notarization-missing：未做 Apple notarization/stapling",
            ],
        }
        raw = json.dumps(manifest, ensure_ascii=False, indent=2).encode("utf-8")
        (package / "package-manifest.json").write_bytes(raw)
        return package, manifest, raw

    def run_release(self, package, output, version=VERSION):
        environment = dict(os.environ, PYTHONIOENCODING="utf-8")
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--version", version,
             "--package-dir", str(package), "--output-dir", str(output)],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            env=environment, timeout=60,
        )

    def write_manifest(self, package, manifest):
        (package / "package-manifest.json").write_bytes(
            json.dumps(manifest, ensure_ascii=False, indent=2).encode("utf-8")
        )

    def assert_rejected(self, package, output, version=VERSION):
        result = self.run_release(package, output, version)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(output.exists(), "校验失败前不得创建输出目录")
        self.assertIn("错误：", result.stderr)

    def test_valid_release_has_exactly_four_verified_files(self):
        with tempfile.TemporaryDirectory() as work:
            root = Path(work)
            package, manifest, raw = self.fixture(root)
            output = root / "release"
            result = self.run_release(package, output)
            self.assertEqual(result.returncode, 0, result.stderr)

            dmg, _setup, _payload = names(VERSION)
            manifest_name = "package-manifest-macos-arm64.json"
            sums_name = "SHA256SUMS-macos-arm64.txt"
            notes_name = "release-notes-macos-arm64.md"
            expected = {dmg, manifest_name, sums_name, notes_name}
            self.assertEqual({item.name for item in output.iterdir()}, expected)
            self.assertFalse(any(item.is_dir() for item in output.iterdir()))
            self.assertFalse((output / "private-fixture.txt").exists())

            self.assertEqual((output / dmg).read_bytes(), (package / dmg).read_bytes())
            self.assertEqual((output / manifest_name).read_bytes(), raw)
            self.assertEqual((package / "package-manifest.json").read_bytes(), raw)

            sums = (output / sums_name).read_bytes()
            self.assertNotIn(b"\r", sums)
            lines = sums.decode("utf-8").splitlines()
            self.assertEqual(len(lines), 2)
            checksums = dict(reversed(line.split("  ", 1)) for line in lines)
            self.assertEqual(set(checksums), {dmg, manifest_name})
            for name, expected_digest in checksums.items():
                self.assertEqual(expected_digest, digest(output / name))

            notes_bytes = (output / notes_name).read_bytes()
            self.assertNotIn(b"\r", notes_bytes)
            notes = notes_bytes.decode("utf-8")
            for phrase in (
                "Apple Silicon arm64", "未签名", "未公证", "Gatekeeper",
                "真实下载验收", "不推荐关闭 Gatekeeper", "隔离标记",
                "DMG 内置完整 payload", "普通用户不需要 JSON/ZIP",
                "Mac preview", "非稳定发布", "releaseReady=false", "releaseBlocked:",
            ):
                self.assertIn(phrase, notes)
            for blocker in manifest["releaseBlocked"]:
                self.assertIn(blocker, notes)

    def test_rejections_leave_no_output(self):
        dmg, _setup, payload = names(VERSION)
        cases = {
            "stable-version": (lambda m: None, "0.1.1", None),
            "wrong-platform": (lambda m: m.__setitem__("runtime", "win-x64"), VERSION, None),
            "public-platform-is-not-source": (
                lambda m: m.__setitem__("runtime", "macos-arm64"), VERSION, None),
            "wrong-version": (
                lambda m: m.__setitem__("version", "0.1.2-beta.1"), VERSION, None),
            "partial-string-false": (lambda m: m.__setitem__("partial", "false"), VERSION, None),
            "self-contained-string-true": (
                lambda m: m.__setitem__("selfContained", "true"), VERSION, None),
            "missing-component": (
                lambda m: m["components"].pop("engine"), VERSION, None),
            "missing-dmg-record": (lambda m: m["artifacts"].pop("dmg"), VERSION, None),
            "missing-dmg-file": (lambda m: None, VERSION, dmg),
            "missing-payload-file": (lambda m: None, VERSION, payload),
            "wrong-hash": (
                lambda m: m["artifacts"]["dmg"].__setitem__("sha256", "0" * 64),
                VERSION, None),
            "wrong-size": (
                lambda m: m["artifacts"]["setupAppZip"].__setitem__(
                    "size", m["artifacts"]["setupAppZip"]["size"] + 1),
                VERSION, None),
            "malicious-name": (
                lambda m: m["artifacts"]["dmg"].__setitem__(
                    "file", "../private-fixture.txt"), VERSION, None),
            "sdk-mismatch": (
                lambda m: m["dotnet"].__setitem__("sdk", "10.0.500"), VERSION, None),
        }
        for name, (mutate, version, removed) in cases.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as work:
                root = Path(work)
                package, manifest, _raw = self.fixture(root, version)
                mutate(manifest)
                self.write_manifest(package, manifest)
                if removed:
                    (package / removed).unlink()
                self.assert_rejected(package, root / "release", version)

    def test_existing_output_is_untouched(self):
        with tempfile.TemporaryDirectory() as work:
            root = Path(work)
            package, _manifest, _raw = self.fixture(root)
            output = root / "release"
            output.mkdir()
            sentinel = output / "keep.txt"
            sentinel.write_text("existing evidence", encoding="utf-8")
            result = self.run_release(package, output)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual({item.name for item in output.iterdir()}, {"keep.txt"})
            self.assertEqual(sentinel.read_text(encoding="utf-8"), "existing evidence")

    def test_rejects_symlinked_attachment(self):
        with tempfile.TemporaryDirectory() as work:
            root = Path(work)
            package, _manifest, _raw = self.fixture(root)
            dmg = package / names(VERSION)[0]
            content = dmg.read_bytes()
            target = root / "elsewhere.dmg"
            target.write_bytes(content)
            dmg.unlink()
            try:
                os.symlink(target, dmg)
            except (OSError, NotImplementedError) as error:
                self.skipTest("文件符号链接不可用：" + str(error))
            self.assert_rejected(package, root / "release")


if __name__ == "__main__":
    unittest.main()
