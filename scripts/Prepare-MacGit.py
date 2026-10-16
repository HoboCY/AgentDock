#!/usr/bin/env python3
"""Build-time normalization of Git's hardlinked builtin aliases for a ZIP payload."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('build_root')
args = parser.parse_args()
root = pathlib.Path(args.build_root).resolve()
source = root / 'stage/usr/local/agentdock-git'
target = root / 'portable'
if target.exists():
    raise SystemExit('Choose an unused portable output directory.')
main = source / 'bin/git'
archive = root/'git-2.54.0.tar.xz'
archive_sha256 = 'f689162364c10de79ef89aa8dbf48731eb057e34edbbd20aca510ce0154681a3'
if hashlib.sha256(archive.read_bytes()).hexdigest() != archive_sha256:
    raise SystemExit('Git source archive SHA-256 mismatch')
identity = (main.stat().st_dev, main.stat().st_ino)
for file in sorted(source.rglob('*')):
    relative = file.relative_to(source)
    destination = target / relative
    if file.is_symlink():
        raise SystemExit('Unexpected symlink in built Git: ' + str(relative))
    if file.is_dir():
        destination.mkdir(parents=True, exist_ok=True)
        continue
    destination.parent.mkdir(parents=True, exist_ok=True)
    if (file.stat().st_dev, file.stat().st_ino) == identity and relative != pathlib.Path('bin/git'):
        # Git installs builtins as hardlinks to git. A tiny shell wrapper preserves
        # their dispatch without expanding hundreds of duplicate binaries in ZIP.
        if relative.parts[0] == 'bin':
            executable = '$(dirname "$0")/git'
        elif relative.parts[:2] == ('libexec', 'git-core'):
            executable = '$(dirname "$0")/../../bin/git'
        else:
            raise SystemExit('Unexpected builtin alias: ' + str(relative))
        command = file.name.removeprefix('git-')
        suffix = '' if file.name == 'git' else ' ' + command
        if not all(c.isalnum() or c == '-' for c in command):
            raise SystemExit('Invalid alias name')
        destination.write_text('#!/bin/sh\nexec "' + executable + '"' + suffix + ' "$@"\n')
        destination.chmod(0o755)
    else:
        shutil.copy2(file, destination)
licenses = target / 'share/agentdock-source'
licenses.mkdir(parents=True)
shutil.copy2(root/'git-2.54.0.tar.xz', licenses/'git-2.54.0.tar.xz')
shutil.copy2(root/'provenance.txt', licenses/'provenance.txt')
shutil.copy2(pathlib.Path(__file__), licenses/'Prepare-MacGit.py')
build_script = pathlib.Path(__file__).with_name('Build-MacGit.sh')
if build_script.exists(): shutil.copy2(build_script, licenses/'Build-MacGit.sh')
fixture = root / 'portable-fixture'
fixture.mkdir()
env = dict(PATH=str(target/'bin')+':/usr/bin:/bin', HOME=str(fixture), GIT_CONFIG_NOSYSTEM='1')
def git(*arguments):
    return subprocess.check_output([str(target/'bin/git'), *arguments], cwd=fixture, env=env, text=True).strip()
git('init', '-q')
(fixture/'input.txt').write_text('fixture\n')
git('add', 'input.txt')
git('-c', 'user.name=AgentDock fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-qm', 'fixture')
git('diff', '--exit-code')
git('clone', '-q', '--no-local', str(fixture), str(root/'portable-clone'))
assert (root/'portable-clone/input.txt').read_text() == 'fixture\n'
(target/'provenance.json').write_text(json.dumps(dict(
    verified=True, version='2.54.0', source='https://www.kernel.org/pub/software/scm/git/git-2.54.0.tar.xz',
    sha256=archive_sha256, sourceIncluded='share/agentdock-source/git-2.54.0.tar.xz',
    build='Build-MacGit.sh with Apple CLT; runtime prefix; system libraries; normalized builtin aliases',
    validation=['git init','git add','git commit','git diff','git clone --no-local'],
    files={str(file.relative_to(target)):hashlib.sha256(file.read_bytes()).hexdigest()
           for file in sorted(target.rglob('*')) if file.is_file()}),indent=2))
print(json.dumps(dict(version=git('--version'), execPath=git('--exec-path'),
    files=sum(1 for file in target.rglob('*') if file.is_file()),
    logicalBytes=sum(file.stat().st_size for file in target.rglob('*') if file.is_file()),
    localClonePassed=(root/'portable-clone/input.txt').read_text()=='fixture\n')))
