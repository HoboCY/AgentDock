#!/usr/bin/env python3
"""Developer acceptance probe: real Codex plugin calls in a fresh CODEX_HOME.

Requires Python only on the validation machine, never in the installed product.
--live-tasks-root explicitly opts into five synthetic real provider tasks; save
the independent credential in the AgentDock panel first. No key is read here.
"""
import argparse
import json
import os
import pathlib
import queue
import subprocess
import threading
import time
import uuid


class Rpc:
    def __init__(self, executable, environment, stderr_path):
        self.errors = open(stderr_path, 'w')
        self.process = subprocess.Popen([executable, 'app-server', '--listen', 'stdio://'],
            env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.errors,
            text=True, encoding='utf-8', bufsize=1)
        self.messages = queue.Queue()
        self.sequence = 0
        threading.Thread(target=self.read, daemon=True).start()

    def read(self):
        for line in self.process.stdout:
            try:
                self.messages.put(json.loads(line))
            except ValueError:
                continue
        self.messages.put(None)

    def send(self, message):
        self.process.stdin.write(json.dumps(message) + '\n')
        self.process.stdin.flush()

    def call(self, method, params, timeout=120):
        self.sequence += 1
        request_id = self.sequence
        self.send(dict(jsonrpc='2.0', id=request_id, method=method, params=params))
        deadline = time.monotonic() + timeout
        while True:
            message = self.messages.get(timeout=max(.001, deadline-time.monotonic()))
            if message is None:
                raise RuntimeError('Codex app-server stdout closed')
            if message.get('id') == request_id:
                if 'error' in message:
                    raise RuntimeError(str(message['error']))
                return message['result']

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
        self.errors.close()


def live_tasks(tool, root, evidence):
    root.mkdir(parents=True, exist_ok=False)
    accepted = []
    evidence['tasks'] = []

    def wait(task):
        cursor, deadline = 0, time.monotonic() + 240
        while True:
            detail = tool('wait_task', dict(id=task['id'], after=cursor, wait_seconds=15))
            cursor = detail['nextCursor']
            if detail['summary']['status'] not in ('Queued', 'Running'):
                return detail
            if time.monotonic() > deadline:
                raise TimeoutError('Synthetic acceptance task deadline')

    try:
        for role in ('explorer', 'default', 'worker', 'reviewer', 'verifier'):
            project = root/role
            project.mkdir()
            (project/'input.txt').write_text('41')
            if role in ('default', 'worker'):
                instruction = 'Read input.txt, add one, and write exactly 42 to answer.txt. You own answer.txt only.'
            elif role == 'verifier':
                instruction = ('Run a command reading input.txt and checking integer plus one equals 42. '
                    'Write verification.txt only in the allowed check-artifacts directory in your system prompt. '
                    'Do not modify project files.')
            else:
                instruction = 'Read input.txt using a tool and report its integer plus one. Do not write files.'
            prompt = ('Bounded AgentDock acceptance task in this isolated fixture only. ' + instruction +
                ' Do not delegate, access credentials, inspect other directories, or run network commands. '
                'Use at most two tool calls. Finish with ROLE_OK ' + role + ' 42 and a brief factual result.')
            arguments = dict(request_id='mac-acceptance-'+uuid.uuid4().hex, cwd=str(project),
                title='Mac acceptance '+role, role=role, prompt=prompt)
            print('SUBAGENT_DISPATCH\ntask_name: mac_live_'+role+'\nagent_type: '+role+
                  '\nmodel: deepseek-v4.1-flash\nreasoning_effort: max', flush=True)
            task = tool('start_task', arguments)
            accepted.append((task, arguments))
            if role == 'explorer':
                detail = wait(task)
                if detail['summary']['status'] != 'Completed':
                    evidence['tasks'].append(detail)
                    raise RuntimeError('First read-only account task failed: '+str(detail['summary'].get('error')))
        for task, arguments in accepted:
            detail = wait(task)
            evidence['tasks'].append(detail)
            summary, role = detail['summary'], task['role']
            assert summary['status'] == 'Completed', summary.get('error')
            assert summary.get('inputTokens') is not None and summary.get('outputTokens') is not None
            assert 'ROLE_OK '+role+' 42' in detail['result']
            project = root/role
            assert (project/'input.txt').read_text() == '41'
            if role in ('default', 'worker'):
                assert (project/'answer.txt').read_text().strip() == '42'
            else:
                assert sorted(item.name for item in project.iterdir()) == ['input.txt']
            if role == 'verifier':
                assert '[AgentDock] Check artifacts:' in detail['result']
            retry = tool('start_task', arguments)
            assert retry['id'] == task['id'] and retry['status'] == 'Completed'
            print(role+': Completed; filesystem, usage, result and idempotency passed', flush=True)
    finally:
        for task, _ in accepted:
            try:
                if tool('get_task', dict(id=task['id']))['summary']['status'] in ('Queued','Running','Paused'):
                    tool('stop_task', dict(id=task['id']))
            except Exception as error:
                print('Check acceptance task '+task['id']+': '+type(error).__name__, flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--codex', required=True)
    parser.add_argument('--marketplace', required=True, type=pathlib.Path)
    parser.add_argument('--home', required=True, type=pathlib.Path, help='Must be a new isolated CODEX_HOME')
    parser.add_argument('--output', required=True, type=pathlib.Path)
    parser.add_argument('--expected-version', default='0.1.0')
    parser.add_argument('--live-tasks-root', type=pathlib.Path)
    parser.add_argument('--upgrade-setup', type=pathlib.Path, help='Explicitly apply a second real installer while this Codex session stays open')
    parser.add_argument('--upgrade-version')
    args = parser.parse_args()
    if bool(args.upgrade_setup) != bool(args.upgrade_version):
        parser.error('--upgrade-setup and --upgrade-version must be provided together')
    args.home = args.home.resolve()
    args.home.mkdir(parents=True, exist_ok=False)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    environment = os.environ.copy()
    environment['CODEX_HOME'] = str(args.home)
    evidence, rpc = dict(success=False, isolatedHome=str(args.home)), None
    try:
        marketplace = args.marketplace.resolve()
        manifest = json.loads((marketplace/'.agents/plugins/marketplace.json').read_text())
        plugin_id = 'agentdock@'+manifest['name']
        for command in (['plugin','marketplace','add',str(marketplace),'--json'],
                        ['plugin','add',plugin_id,'--json']):
            subprocess.run([args.codex,*command], env=environment, check=True, capture_output=True, text=True, timeout=90)
        config = args.home/'config.toml'
        header = '[plugins.'+json.dumps(plugin_id)+']'
        if not config.exists() or header not in config.read_text():
            with config.open('a') as output:
                output.write('\n'+header+'\nenabled = true\n')
        rpc = Rpc(args.codex, environment, args.output.with_suffix('.stderr.log'))
        evidence['initialize'] = rpc.call('initialize', dict(clientInfo=dict(name='agentdock-mac-probe',version='0.1.0')))
        rpc.send(dict(jsonrpc='2.0',method='initialized'))
        thread = rpc.call('thread/start', {})
        thread_id = thread.get('thread',{}).get('id') or thread.get('threadId')
        assert thread_id
        status = rpc.call('mcpServerStatus/list', dict(detail='toolsAndAuthOnly',threadId=thread_id,serverName='agentdock'))
        entry = next(item for item in status['data'] if item['name']=='agentdock')
        assert entry['runtimeStatus']=='connected', entry.get('toolsError')
        evidence['tools'] = sorted(entry['tools'])
        assert set(evidence['tools']) == {'status','start_task','list_tasks','get_task','wait_task','stop_task','resume_task'}

        def tool(name, arguments):
            result = rpc.call('mcpServer/tool/call',dict(server='agentdock',threadId=thread_id,tool=name,arguments=arguments))
            if result.get('isError'):
                raise RuntimeError(str(result.get('content')))
            return result['structuredContent']['result']

        host = tool('status', {})
        assert host['version']==args.expected_version and host['processId']>0
        assert host['unfinishedTasks']>=0 and isinstance(host['canUpdate'],bool)
        evidence['hostStatus'] = host
        print('Codex plugin connected; seven tools visible; real Host status passed', flush=True)
        if args.upgrade_setup:
            install = json.loads((marketplace/'plugins/agentdock/runtime/install.json').read_text())
            install_root = pathlib.Path(install['installRoot'])
            assert (install_root/'plugin').samefile(marketplace)
            upgrade = subprocess.run([str(args.upgrade_setup.resolve()),'--install','--root',str(install_root),
                '--no-shell-integration','--silent'],env=environment,capture_output=True,text=True,timeout=240)
            evidence['upgrade'] = dict(exitCode=upgrade.returncode,log=upgrade.stdout+upgrade.stderr)
            assert upgrade.returncode==0, upgrade.stdout+upgrade.stderr
            updated = tool('status', {})
            assert updated['version']==args.upgrade_version and updated['processId']!=host['processId']
            evidence['upgrade']['hostStatusFromExistingCodexSession']=updated
            print('Real-version update passed through the existing Codex/MCP session', flush=True)
        if args.live_tasks_root:
            live_tasks(tool, args.live_tasks_root.resolve(), evidence)
        evidence['success'] = True
    except Exception as error:
        evidence['error'] = str(error) or type(error).__name__
        raise
    finally:
        if rpc:
            rpc.close()
        args.output.write_text(json.dumps(evidence,ensure_ascii=False,indent=2))


if __name__ == '__main__':
    main()
