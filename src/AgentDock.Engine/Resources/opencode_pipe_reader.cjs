// AgentDock: copied unmodified from the private deepseek-agents reference
// implementation (opencode_pipe_reader.cjs) as permitted by the AgentDock v1
// design; attribution retained. Blocking Win32 reads run only on this worker,
// never on the harness thread.
const { parentPort, workerData } = require('node:worker_threads');
const { dlopen, ptr } = require('bun:ffi');
const native = dlopen('kernel32.dll', {
  ReadFile: { args: ['u64', 'ptr', 'u32', 'ptr', 'ptr'], returns: 'bool' },
  GetLastError: { args: [], returns: 'u32' },
});
const handle = BigInt(workerData.handle);
let ended = false;
parentPort.on('message', message => {
  if (message !== 'read' || ended) return;
  const buffer = new Uint8Array(65536), count = new Uint32Array(1);
  const ok = native.symbols.ReadFile(handle, ptr(buffer), buffer.length, ptr(count), null);
  if (!ok) {
    const code = native.symbols.GetLastError();
    ended = true;
    parentPort.postMessage(code === 109 || code === 232 ? { end: true } : { error: code });
    parentPort.close();
    return;
  }
  if (!count[0]) {
    ended = true;
    parentPort.postMessage({ end: true });
    parentPort.close();
    return;
  }
  parentPort.postMessage({ data: buffer, length: count[0] }, [buffer.buffer]);
});
