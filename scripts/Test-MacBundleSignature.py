#!/usr/bin/env python3
"""Verify distributed and installed app signatures using a read-only DMG and isolated install."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dmg', required=True, type=Path)
    parser.add_argument('--work', required=True, type=Path)
    args = parser.parse_args()
    if sys.platform != 'darwin':
        parser.error('This check requires native macOS codesign and hdiutil.')
    work = args.work.resolve()
    work.mkdir(parents=True, exist_ok=False)
    mount = work / 'mount'
    mount.mkdir()
    results = []
    attached = False

    def run(command, succeeds=True):
        result = subprocess.run([str(item) for item in command], capture_output=True, text=True, timeout=180)
        results.append(dict(command=[str(item) for item in command], exit=result.returncode,
                            output=result.stdout + result.stderr))
        print(result.stdout + result.stderr, end='', flush=True)
        assert (result.returncode == 0) == succeeds, results[-1]

    def verify(app):
        run(['codesign', '--verify', '--deep', '--strict', '--verbose=2', app])

    try:
        run(['hdiutil', 'verify', args.dmg.resolve()])
        run(['hdiutil', 'attach', '-readonly', '-nobrowse', '-mountpoint', mount, args.dmg.resolve()])
        attached = True
        installers = list(mount.glob('AgentDockSetup-*-macos-arm64.app'))
        assert len(installers) == 1, installers
        installer = installers[0]
        verify(installer)
        verify(installer / 'Contents/Resources/launcher/AgentDock.app')
        installed = work / 'installed'
        run([installer / 'Contents/MacOS/AgentDock', '--install', '--root', installed,
             '--silent', '--no-shell-integration'])
        verify(installed / 'AgentDock.app')
        current = json.loads((installed / 'install/current.json').read_text())
        verify(Path(current['desktop']).parents[2])

        # The gate must detect resource tampering after the installer's ordinary file copy.
        tampered = work / 'tampered.app'
        shutil.copytree(installed / 'AgentDock.app', tampered)
        with (tampered / 'Contents/Info.plist').open('a') as stream:
            stream.write('\n<!-- tampered -->\n')
        run(['codesign', '--verify', '--deep', '--strict', '--verbose=2', tampered], succeeds=False)
        print('Distributed and installed bundle signatures verified; tampered bundle rejected.', flush=True)
    finally:
        if attached:
            run(['hdiutil', 'detach', mount])
        (work / 'results.json').write_text(json.dumps(results, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
