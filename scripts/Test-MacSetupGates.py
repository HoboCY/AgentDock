#!/usr/bin/env python3
"""Real Mac installer acceptance with synthetic payloads and isolated roots.

The supplied setup must be inside the built installer .app. The harness never
changes the user's default installation, Applications link or existing data.
It preserves its fixtures and JSON evidence for inspection.
"""
import argparse
import hashlib
import json
import os
import pathlib
import stat
import subprocess
import traceback
import zipfile


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def tree(path):
    return {str(file.relative_to(path)): digest(file) for file in path.rglob('*') if file.is_file()}


def payload(root, version):
    root.mkdir()
    files = {
        'host/AgentDock.Host': '#!/bin/sh\nexit 0\n',
        'engine/AgentDock.Engine': '#!/bin/sh\nexit 0\n',
        'desktop/AgentDock.app/Contents/MacOS/AgentDock.Desktop': '#!/bin/sh\nexit 0\n',
        'desktop/AgentDock.app/Contents/Info.plist': '<plist><dict/></plist>',
        'engines/opencode/opencode': '#!/bin/sh\nexit 0\n',
        'engines/codex/bin/codex': '#!/bin/sh\nexit 0\n',
        'engines/git/bin/git': '#!/bin/sh\nexit 0\n',
        'plugin/.agents/plugins/marketplace.json': '{"name":"agentdock-local"}',
        'plugin/plugins/agentdock/.codex-plugin/plugin.json': json.dumps(dict(name='agentdock',version=version)),
        'plugin/plugins/agentdock/.mcp.json': '{"mcpServers":{}}',
    }
    entries = []
    for relative, content in files.items():
        file = root/relative
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(content)
        mode = '0755' if content.startswith('#!') else '0644'
        file.chmod(int(mode, 8))
        entries.append(dict(path=relative, size=file.stat().st_size, sha256=digest(file), mode=mode))
    manifest = dict(schema=1, product='AgentDock',version=version,runtime='osx-arm64',partial=False,files=entries)
    (root/'payload-manifest.json').write_text(json.dumps(manifest))
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--setup', required=True, type=pathlib.Path)
    parser.add_argument('--work', required=True, type=pathlib.Path)
    args = parser.parse_args()
    assert os.uname().sysname == 'Darwin'
    args.work = args.work.resolve()
    args.work.mkdir(parents=True, exist_ok=False)
    setup = str(args.setup.resolve())
    root = args.work/'installed'
    first, second = args.work/'payload-a', args.work/'payload-b'
    payload(first, '0.1.0-gate-a')
    payload(second, '0.1.0-gate-b')
    results = []

    def run(arguments, succeeds=True, target=root):
        result = subprocess.run([setup,*arguments,'--root',str(target),'--no-shell-integration','--silent'],
            capture_output=True,text=True,timeout=120,env=dict(os.environ, PATH='/usr/bin:/bin:/usr/sbin:/sbin'))
        assert (result.returncode==0)==succeeds, result.stdout+result.stderr
        return result

    def case(name, body):
        try:
            body()
            results.append(dict(name=name,passed=True))
            print(name+': PASS', flush=True)
        except Exception as error:
            results.append(dict(name=name,passed=False,error=str(error),traceback=traceback.format_exc()))
            print(name+': FAIL '+str(error), flush=True)
        (args.work/'results.json').write_text(json.dumps(results,ensure_ascii=False,indent=2))

    def install():
        run(['--install','--payload',str(first)])
        current = json.loads((root/'install/current.json').read_text())
        assert current['desktop'].endswith('/desktop/AgentDock.app/Contents/MacOS/AgentDock.Desktop')
        launcher = root/'AgentDock.app/Contents/MacOS/AgentDock'
        assert launcher.is_file() and os.access(launcher,os.X_OK)
        mcp = json.loads((root/'plugin/plugins/agentdock/.mcp.json').read_text())['mcpServers']['agentdock']
        assert mcp['command']==str(launcher) and mcp['args']==['--mcp','--root',str(root)]
        status = subprocess.run([str(launcher),'--status'], capture_output=True,text=True,check=True)
        assert pathlib.Path(json.loads(status.stdout)['root']).samefile(root)
        assert not (root/'install/update.lock').exists()
    def unowned_uninstall():
        unrelated = args.work/'unowned-root'
        for relative in ('plugin/keep.txt','AgentDock.app/keep.txt'):
            file = unrelated/relative
            file.parent.mkdir(parents=True)
            file.write_text('unrelated')
        before = tree(unrelated)
        run(['--uninstall'],False,target=unrelated)
        assert tree(unrelated)==before
    case('uninstall-refuses-unowned-root-with-named-directories',unowned_uninstall)
    case('fresh-install-root-inference-modes-and-plugin',install)
    case('same-version-repair',lambda:run(['--install','--payload',str(first)]))

    def corrupt_repair():
        target = root/'install/0.1.0-gate-a/host/AgentDock.Host'
        target.write_text('damaged')
        target.chmod(0o644)
        run(['--install','--payload',str(first)])
        assert digest(target)==digest(first/'host/AgentDock.Host') and os.access(target,os.X_OK)
    case('damaged-file-and-mode-repair',corrupt_repair)
    def mode_repair():
        target = root/'install/0.1.0-gate-a/host/AgentDock.Host'
        target.chmod(0o644)
        run(['--install','--payload',str(first)])
        assert os.access(target,os.X_OK)
    case('mode-only-repair',mode_repair)
    def stable_repair():
        launcher = root/'AgentDock.app/Contents/MacOS/AgentDock'
        subprocess.run([str(launcher),'--install','--payload',str(first),'--no-shell-integration','--silent'],
                       check=True,capture_output=True,text=True,timeout=120)
    case('stable-launcher-can-repair-payload',stable_repair)
    data = root/'data'
    data.mkdir(exist_ok=True)
    (data/'preserve.txt').write_text('user data must survive')
    current_before = (root/'install/current.json').read_bytes()

    def blocked(status):
        (data/'tasks.json').write_text(json.dumps(dict(schema=1,tasks=dict(t=dict(id='t',status=status)),requests={})))
        try:
            for mode in ('--install','--apply-update','--uninstall'):
                arguments = [mode] if mode=='--uninstall' else [mode,'--payload',str(second)]
                run(arguments,False)
                assert (root/'install/current.json').read_bytes()==current_before
        finally:
            (data/'tasks.json').unlink()
    for status in ('Queued','Running','Paused'):
        case('unfinished-'+status+'-blocks-install-update-uninstall',lambda status=status:blocked(status))

    def corrupt_state():
        (data/'tasks.json').write_text('{invalid')
        try:
            run(['--apply-update','--payload',str(second)],False)
        finally:
            (data/'tasks.json').unlink()
    case('corrupt-state-fails-closed',corrupt_state)

    def malformed(kind):
        candidate = args.work/('bad-'+kind)
        manifest = payload(candidate,'0.1.0-bad-'+kind)
        if kind=='hash': manifest['files'][0]['sha256']='0'*64
        if kind=='path': manifest['files'][0]['path']='../escape'
        if kind=='partial': manifest['partial']=True
        if kind=='mode': manifest['files'][0]['mode']='0777'
        if kind=='missing-mode': del manifest['files'][0]['mode']
        if kind=='missing-component': manifest['files']=manifest['files'][1:]
        if kind=='runtime': manifest['runtime']='win-x64'
        if kind=='symlink':
            (candidate/'host/AgentDock.Host').unlink()
            (candidate/'host/AgentDock.Host').symlink_to('/bin/true')
        (candidate/'payload-manifest.json').write_text(json.dumps(manifest))
        run(['--apply-update','--payload',str(candidate)],False)
        assert (root/'install/current.json').read_bytes()==current_before
    for kind in ('hash','path','partial','mode','missing-mode','missing-component','runtime','symlink'):
        case('reject-'+kind,lambda kind=kind:malformed(kind))

    def rollback():
        before = {name:tree(root/name) for name in ('AgentDock.app','plugin')}
        machine = root/'install/machine.json'
        content = machine.read_bytes()
        machine.unlink()
        machine.mkdir()
        try:
            run(['--apply-update','--payload',str(second)],False)
            assert (root/'install/current.json').read_bytes()==current_before
            assert before=={name:tree(root/name) for name in before}
        finally:
            machine.rmdir()
            machine.write_bytes(content)
    case('failure-after-stable-swap-rolls-back',rollback)

    def update_zip():
        archive = args.work/'update.zip'
        with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as output:
            for file in second.rglob('*'):
                if file.is_file(): output.write(file,str(file.relative_to(second)))
        run(['--apply-update','--payload',str(archive)])
        current = json.loads((root/'install/current.json').read_text())
        assert current['version']=='0.1.0-gate-b'
        assert (data/'preserve.txt').read_text()=='user data must survive'
        assert stat.S_IMODE(pathlib.Path(current['host']).stat().st_mode)==0o755
    case('zip-update-retains-data-and-executable-mode',update_zip)

    def uninstall():
        unrelated = root/'install/user-files/host/keep.txt'
        unrelated.parent.mkdir(parents=True)
        unrelated.write_text('unrelated')
        (data/'tasks.json').write_text(json.dumps(dict(schema=1,configuration=dict(startAtLogin=True),tasks={},requests={})))
        run(['--uninstall'])
        assert not (root/'AgentDock.app').exists() and not (root/'plugin').exists()
        assert not (root/'install/current.json').exists()
        assert (data/'preserve.txt').read_text()=='user data must survive'
        assert unrelated.read_text()=='unrelated'
        assert json.loads((data/'tasks.json').read_text())['configuration']['startAtLogin'] is False
    case('uninstall-preserves-data-and-unrelated-host-directory',uninstall)
    if any(not item['passed'] for item in results):
        raise SystemExit(1)


if __name__=='__main__':
    main()
