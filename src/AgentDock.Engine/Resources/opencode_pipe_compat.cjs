// AgentDock: copied unmodified from the private deepseek-agents reference
// implementation (opencode_pipe_compat.cjs) as permitted by the AgentDock v1
// design; attribution retained. Object-local stdio repair for pinned Bun under
// a Windows restricted token. It changes neither tokens nor filesystem ACLs,
// and keeps native child PIDs.
const { isMainThread, Worker } = require('node:worker_threads');
if (isMainThread) {
  const cp = require('node:child_process');
  const fs = require('node:fs');
  const path = require('node:path');
  const { randomUUID } = require('node:crypto');
  const { Readable } = require('node:stream');
  const { dlopen, ptr } = require('bun:ffi');
  const sid = process.env.OPENCODE_PIPE_LOGON_SID;
  if (process.platform !== 'win32' || !/^S-1-5-5-\d+-\d+$/.test(sid || '')) {
    throw new Error('OpenCode pipe compatibility requires the existing Windows logon SID');
  }
  // Apply once to the harness. Project programs must not inherit a preload
  // that changes their own child_process API.
  delete process.env.BUN_INSPECT_PRELOAD;
  delete process.env.OPENCODE_PIPE_LOGON_SID;
  const kernel = dlopen('kernel32.dll', {
    CreateNamedPipeW: { args: ['ptr', 'u32', 'u32', 'u32', 'u32', 'u32', 'u32', 'ptr'], returns: 'u64' },
    ConnectNamedPipe: { args: ['u64', 'ptr'], returns: 'bool' },
    CloseHandle: { args: ['u64'], returns: 'bool' },
    CancelIoEx: { args: ['u64', 'ptr'], returns: 'bool' },
    LocalFree: { args: ['ptr'], returns: 'ptr' },
    GetLastError: { args: [], returns: 'u32' },
  }).symbols;
  const security = dlopen('advapi32.dll', {
    ConvertStringSecurityDescriptorToSecurityDescriptorW: { args: ['ptr', 'u32', 'ptr', 'ptr'], returns: 'bool' },
  }).symbols;
  const wide = text => Buffer.from(text + '\0', 'utf16le');
  const nativeError = (operation, code = kernel.GetLastError()) => Object.assign(new Error(operation + ' failed: WinError ' + code), { code: 'EIO', errno: code });

  function outputPipe() {
    const descriptor = new BigUint64Array(1);
    const sddl = wide('D:P(A;;GA;;;' + sid + ')');
    if (!security.ConvertStringSecurityDescriptorToSecurityDescriptorW(ptr(sddl), 1, ptr(descriptor), null)) {
      throw nativeError('Pipe security descriptor');
    }
    // SECURITY_ATTRIBUTES has 8-byte pointer alignment on this x64 runtime.
    const attributes = Buffer.alloc(24);
    attributes.writeUInt32LE(24, 0);
    attributes.writeBigUInt64LE(descriptor[0], 8);
    const name = '\\\\.\\pipe\\opencode-stdio-' + randomUUID();
    let handle;
    try {
      handle = BigInt(kernel.CreateNamedPipeW(ptr(wide(name)), 1 | 0x80000, 0, 1, 65536, 65536, 0, ptr(attributes)));
      if (handle === 0xffffffffffffffffn) throw nativeError('CreateNamedPipe');
    } finally {
      kernel.LocalFree(Number(descriptor[0]));
    }
    let fd = null, worker = null, closed = false, reading = false, ended = false;
    function closeHandle() {
      if (closed) return;
      closed = true;
      kernel.CancelIoEx(handle, null);
      kernel.CloseHandle(handle);
    }
    function closeClient() {
      if (fd === null) return;
      fs.closeSync(fd);
      fd = null;
    }
    try {
      // O_WRONLY without O_CREAT/O_TRUNC means OPEN_EXISTING for a named pipe.
      fd = fs.openSync(name, fs.constants.O_WRONLY);
      if (!kernel.ConnectNamedPipe(handle, null) && kernel.GetLastError() !== 535) {
        throw nativeError('ConnectNamedPipe');
      }
      const stream = new Readable({
        highWaterMark: 65536,
        read() {
          if (reading || ended || closed) return;
          reading = true;
          worker.postMessage('read');
        },
        destroy(error, callback) {
          closeHandle();
          if (worker) worker.terminate().catch(() => {});
          callback(error);
        },
      });
      worker = new Worker(path.join(__dirname, 'opencode_pipe_reader.cjs'), { workerData: { handle: handle.toString() } });
      worker.on('message', message => {
        reading = false;
        if (closed) return;
        if (message.end) {
          ended = true;
          stream.push(null);
        } else if (message.error) {
          stream.destroy(nativeError('ReadFile', message.error));
        } else {
          const accepted = stream.push(Buffer.from(message.data.buffer, 0, message.length));
          if (accepted) stream._read();
        }
      });
      worker.on('error', error => stream.destroy(error));
      worker.on('exit', code => {
        if (!closed && !ended) stream.destroy(nativeError('Pipe reader exited', code));
      });
      return { fd, stream, closeClient, abort() { closeClient(); stream.destroy(); } };
    } catch (error) {
      closeClient();
      closeHandle();
      if (worker) worker.terminate().catch(() => {});
      throw error;
    }
  }

  const original = cp.spawn;
  cp.spawn = function(file, args, options) {
    if (!Array.isArray(args)) { options = args || {}; args = []; }
    options = options || {};
    const spec = options.stdio === undefined ? ['pipe', 'pipe', 'pipe'] :
      typeof options.stdio === 'string' ? [options.stdio, options.stdio, options.stdio] : [...options.stdio];
    const captured = value => value === 'pipe' || value === 'overlapped' || value === null || value === undefined;
    if (captured(spec[0]) || spec.some(value => value === 'ipc') || spec.slice(3).some(captured)) {
      throw Object.assign(new Error('OpenCode stdio adapter supports captured stdout/stderr with ignored, inherited or numeric stdin only'), { code: 'ENOTSUP' });
    }
    if (!captured(spec[1]) && !captured(spec[2])) return original.call(this, file, args, options);
    const outputs = [];
    try {
      for (const index of [1, 2]) {
        if (captured(spec[index])) {
          const pipe = outputPipe();
          outputs.push({ index, ...pipe });
          spec[index] = pipe.fd;
        }
      }
      const child = original.call(this, file, args, { ...options, stdio: spec });
      for (const pipe of outputs) {
        Object.defineProperty(child, pipe.index === 1 ? 'stdout' : 'stderr', {
          value: pipe.stream, writable: true, configurable: true,
        });
        child.stdio[pipe.index] = pipe.stream;
        pipe.closeClient();
      }
      // Native spawn has numeric fds, so its own close counter cannot see our
      // readers. Preserve close-after-EOF, including inherited grandchild fds.
      const emit = child.emit;
      let pendingClose = null;
      const done = new Set();
      child.emit = function(event, ...values) {
        if (event === 'close' && done.size !== outputs.length) {
          pendingClose = values;
          return true;
        }
        return emit.call(this, event, ...values);
      };
      for (const pipe of outputs) {
        pipe.stream.once('close', () => {
          done.add(pipe.index);
          if (pendingClose && done.size === outputs.length) {
            const values = pendingClose;
            pendingClose = null;
            emit.call(child, 'close', ...values);
          }
        });
      }
      child.once('exit', () => {
        // Like native ChildProcess, drain an unconsumed stdio stream after
        // exit so close does not depend on attaching a listener to stderr.
        for (const pipe of outputs) {
          if (!pipe.stream.destroyed && pipe.stream.listenerCount('data') === 0 && pipe.stream.listenerCount('readable') === 0) {
            pipe.stream.resume();
          }
        }
      });
      return child;
    } catch (error) {
      for (const pipe of outputs) pipe.abort();
      throw error;
    }
  };
}
