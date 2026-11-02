#!/usr/bin/env python3
"""Run the workflow's actual publisher with a fake gh; no network or release writes."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest


WORKFLOW = (Path(__file__).resolve().parent.parent / '.github/workflows/package.yml').read_text(encoding='utf-8')
PUBLISH = textwrap.dedent(re.search(
    r'(?ms)^      - name: 创建 GitHub Release\n.*?^        run: \|\n(.*?)(?=^  [a-z]|\Z)', WORKFLOW
).group(1))
BASH = str(Path(shutil.which('git')).resolve().parents[1] / 'bin/bash.exe') if os.name == 'nt' else 'bash'


class PublishTests(unittest.TestCase):
    def test_mac_preflight_disables_sdk_patch_rollforward(self):
        repo = Path(__file__).resolve().parent.parent
        workflow = (repo / '.github/workflows/package-macos.yml').read_text(encoding='utf-8')
        script = textwrap.dedent(re.search(r"(?ms)^          python3 - <<'PY'\n(.*?)^          PY$", workflow).group(1))
        with tempfile.TemporaryDirectory(prefix='agentdock-sdk-') as directory:
            root = Path(directory)
            lock = root / 'packaging/mac/runtime-lock.json'
            lock.parent.mkdir(parents=True)
            shutil.copyfile(repo / 'packaging/mac/runtime-lock.json', lock)
            shutil.copyfile(repo / 'global.json', root / 'global.json')
            env = dict(os.environ, VERSION_INPUT='0.1.1-beta.4', GITHUB_REF_TYPE='branch',
                       GITHUB_ENV=str(root / 'env'), GITHUB_OUTPUT=str(root / 'output'))
            result = subprocess.run([sys.executable, '-c', script], cwd=root, env=env, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            expected = json.loads(lock.read_text())['dotnet']['sdk']
            self.assertEqual(json.loads((root / 'global.json').read_text())['sdk'],
                             {'version': expected, 'rollForward': 'disable'})
            self.assertIn('sdk=' + expected, (root / 'output').read_text())

    def run_case(self, *, draft='true', tag='expected', upload_exit='0', corrupt=False):
        with tempfile.TemporaryDirectory(prefix='agentdock-publish-') as directory:
            root = Path(directory)
            assets = ['AgentDockSetup-0.1.1-beta.3-win-x64.exe', 'AgentDockSetup-0.1.1-beta.3-macos-arm64.dmg',
                      'package-manifest-win-x64.json', 'package-manifest-macos-arm64.json']
            for name in assets:
                (root / name).write_bytes(b'fixture')
            checksum = hashlib.sha256(b'fixture').hexdigest()
            (root / 'SHA256SUMS.txt').write_text(''.join(f'{checksum}  {name}\n' for name in assets), encoding='utf-8', newline='\n')
            (root / 'release-notes.md').write_text('Preview', encoding='utf-8')
            if corrupt:
                (root / assets[1]).write_bytes(b'corrupt')
            (root / 'gh').write_text(textwrap.dedent('''\
                #!/usr/bin/env bash
                echo "$*" >> calls.txt
                if [[ "$1" == api ]]; then echo "$MOCK_TAG"; fi
                if [[ "$1 $2" == 'release create' ]]; then exit "$MOCK_UPLOAD_EXIT"; fi
                exit 0
                '''), encoding='utf-8', newline='\n')
            (root / 'gh').chmod(0o755)
            env = dict(os.environ, RELEASE_VERSION='0.1.1-beta.3', RELEASE_DRAFT=draft,
                       GITHUB_SHA='expected', GH_REPO='example/repo', MOCK_TAG=tag, MOCK_UPLOAD_EXIT=upload_exit)
            # Relative PATH works on both Git Bash and the Linux runner.
            result = subprocess.run([BASH, '-e', '-c', 'export PATH=".:$PATH"\n' + PUBLISH],
                                    cwd=root, env=env, capture_output=True, text=True)
            calls = (root / 'calls.txt').read_text().splitlines() if (root / 'calls.txt').exists() else []
            return result.returncode, calls, result.stderr

    def test_draft_contains_only_both_installers_and_stays_private(self):
        code, calls, error = self.run_case()
        self.assertEqual(code, 0, error)
        create = next(call for call in calls if call.startswith('release create '))
        for argument in ['--draft', '--verify-tag', '--prerelease', '--latest=false',
                         'AgentDockSetup-0.1.1-beta.3-win-x64.exe', 'AgentDockSetup-0.1.1-beta.3-macos-arm64.dmg']:
            self.assertIn(argument, create.split())
        arguments = create.split()
        self.assertEqual(arguments[3:arguments.index('--verify-tag')],
                         ['AgentDockSetup-0.1.1-beta.3-win-x64.exe', 'AgentDockSetup-0.1.1-beta.3-macos-arm64.dmg'])
        self.assertFalse(any(call.startswith('release edit ') for call in calls))

    def test_download_bundle_contains_only_installers(self):
        paths = re.search(r'(?ms)name: agentdock-release-bundle-.*?^          path: \|\n(.*?)^          if-no-files-found:',
                          WORKFLOW).group(1)
        self.assertEqual(paths.split(), ['release/*.exe', 'release/*.dmg'])

    def test_public_only_after_upload(self):
        code, calls, error = self.run_case(draft='false')
        self.assertEqual(code, 0, error)
        self.assertTrue(calls[-1].startswith('release edit '))
        self.assertIn('--draft=false', calls[-1])

    def test_failed_upload_never_publishes(self):
        code, calls, _ = self.run_case(draft='false', upload_exit='1')
        self.assertNotEqual(code, 0)
        self.assertTrue(any(call.startswith('release create ') for call in calls))
        self.assertFalse(any(call.startswith('release edit ') for call in calls))

    def test_moved_tag_never_creates_release(self):
        code, calls, _ = self.run_case(tag='changed')
        self.assertNotEqual(code, 0)
        self.assertEqual(len(calls), 1)
        self.assertTrue(calls[0].startswith('api '))
        self.assertFalse(any(call.startswith('release ') for call in calls))

    def test_corrupt_mac_never_contacts_github(self):
        code, calls, _ = self.run_case(corrupt=True)
        self.assertNotEqual(code, 0)
        self.assertEqual(calls, [])


if __name__ == '__main__':
    unittest.main()
