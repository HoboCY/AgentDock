using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentDock.Engine;

/// <summary>
/// Windows child process that is created suspended, assigned to a
/// KILL_ON_JOB_CLOSE job object before it can run, and only then resumed. All
/// descendants stay in the job, including processes spawned later, so a crash,
/// cancellation or disposal of this object kills the whole tree. There is no
/// unsandboxed fallback: if the job cannot be created or assigned, starting
/// throws and nothing runs.
///
/// Only stdio handles are inherited (whitelisted through
/// PROC_THREAD_ATTRIBUTE_HANDLE_LIST); no other inheritable handle leaks into
/// the child.
/// </summary>
internal sealed class JobProcess : IJobProcess
{
    public const int MaxLineBytes = 4 * 1024 * 1024;

    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint StartfUseShowWindow = 0x00000001;
    private const short SwHide = 0;
    private const uint HandleFlagInherit = 0x00000001;
    private const int JobExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint Infinite = 0xFFFFFFFF;
    private static readonly IntPtr ProcThreadAttributeHandleList = new(0x00020002);

    private readonly IntPtr _processHandle;
    private readonly IntPtr _jobHandle;
    private bool _disposed;
    private bool _terminated;

    private JobProcess(IntPtr processHandle, IntPtr jobHandle, FileStream stdin, FileStream stdout, FileStream stderr)
    {
        _processHandle = processHandle;
        _jobHandle = jobHandle;
        Stdin = stdin;
        Stdout = stdout;
        Stderr = stderr;
    }

    public Stream Stdin { get; }

    public Stream Stdout { get; }

    public Stream Stderr { get; }

    public bool HasExited => WaitForSingleObject(_processHandle, 0) == WaitObject0;

    public static JobProcess Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Job objects require Windows.");
        }
        var fullPath = Path.GetFullPath(fileName);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Executable not found.", fullPath);
        }
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("The task working directory no longer exists.");

        var inherit = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        IntPtr stdinRead = IntPtr.Zero, stdinWrite = IntPtr.Zero;
        IntPtr stdoutRead = IntPtr.Zero, stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero, stderrWrite = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;
        IntPtr jobHandle = IntPtr.Zero;
        try
        {
            if (!CreatePipe(out stdinRead, out stdinWrite, ref inherit, 0) ||
                !CreatePipe(out stdoutRead, out stdoutWrite, ref inherit, 0) ||
                !CreatePipe(out stderrRead, out stderrWrite, ref inherit, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
            }
            if (!SetHandleInformation(stdinWrite, HandleFlagInherit, 0) ||
                !SetHandleInformation(stdoutRead, HandleFlagInherit, 0) ||
                !SetHandleInformation(stderrRead, HandleFlagInherit, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation failed");
            }

            jobHandle = CreateJobObjectW(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
            }
            var limits = new JobExtendedLimitInfo
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
            };
            var limitsSize = Marshal.SizeOf<JobExtendedLimitInfo>();
            var limitsPtr = Marshal.AllocHGlobal(limitsSize);
            try
            {
                Marshal.StructureToPtr(limits, limitsPtr, false);
                if (!SetInformationJobObject(jobHandle, JobExtendedLimitInformationClass, limitsPtr, (uint)limitsSize))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(limitsPtr);
            }

            attributeList = CreateHandleList(new[] { stdinRead, stdoutWrite, stderrWrite });

            environmentBlock = MarshalEnvironment(environment);
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles | StartfUseShowWindow,
                    ShowWindow = SwHide,
                    StdInput = stdinRead,
                    StdOutput = stdoutWrite,
                    StdError = stderrWrite,
                },
                AttributeList = attributeList,
            };
            var commandLine = BuildCommandLine(fullPath, arguments);
            var created = CreateProcessW(fullPath, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment, environmentBlock,
                Path.GetFullPath(workingDirectory), ref startup, out var processInformation);
            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
            }
            processHandle = processInformation.Process;
            threadHandle = processInformation.Thread;

            if (!AssignProcessToJobObject(jobHandle, processHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
            }
            if (ResumeThread(threadHandle) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed");
            }

            CloseHandle(threadHandle);
            threadHandle = IntPtr.Zero;
            CloseHandle(stdinRead);
            stdinRead = IntPtr.Zero;
            CloseHandle(stdoutWrite);
            stdoutWrite = IntPtr.Zero;
            CloseHandle(stderrWrite);
            stderrWrite = IntPtr.Zero;

            return new JobProcess(
                processHandle,
                jobHandle,
                new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(stdinWrite, true), FileAccess.Write),
                new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(stdoutRead, true), FileAccess.Read),
                new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(stderrRead, true), FileAccess.Read));
        }
        catch
        {
            if (threadHandle != IntPtr.Zero)
            {
                CloseHandle(threadHandle);
            }
            if (processHandle != IntPtr.Zero)
            {
                TerminateProcess(processHandle, 1);
                CloseHandle(processHandle);
            }
            if (jobHandle != IntPtr.Zero)
            {
                CloseHandle(jobHandle);
            }
            ClosePipe(stdinRead, stdinWrite, stdoutRead, stdoutWrite, stderrRead, stderrWrite);
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    public bool WaitForExit(int milliseconds)
    {
        var result = WaitForSingleObject(_processHandle, milliseconds < 0 ? Infinite : (uint)milliseconds);
        return result switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            _ => throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject failed"),
        };
    }

    public int ExitCode
    {
        get
        {
            if (!GetExitCodeProcess(_processHandle, out var code))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeProcess failed");
            }
            return unchecked((int)code);
        }
    }

    /// <summary>Kills every process in the job object (the tree, including late children).</summary>
    public void Terminate()
    {
        if (_disposed || _terminated)
        {
            return;
        }
        _terminated = true;
        if (!TerminateJobObject(_jobHandle, 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "TerminateJobObject failed");
        }
    }

    /// <summary>
    /// Reads UTF-8 lines from a stream with a hard per-line cap. Overlong lines
    /// are skipped instead of buffered. Drains until end of stream.
    /// </summary>
    public static IEnumerable<string> ReadLines(Stream stream, int maxLineBytes = MaxLineBytes)
    {
        var buffer = new byte[64 * 1024];
        var line = new List<byte>(4096);
        var overflow = false;
        while (true)
        {
            int read;
            try
            {
                read = stream.Read(buffer, 0, buffer.Length);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                yield break;
            }
            if (read == 0)
            {
                break;
            }
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    if (!overflow && line.Count > 0)
                    {
                        if (line[^1] == (byte)'\r')
                        {
                            line.RemoveAt(line.Count - 1);
                        }
                        yield return System.Text.Encoding.UTF8.GetString(
                            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(line));
                    }
                    line.Clear();
                    overflow = false;
                    continue;
                }
                if (overflow)
                {
                    continue;
                }
                line.Add(value);
                if (line.Count > maxLineBytes)
                {
                    line.Clear();
                    overflow = true;
                }
            }
        }
        if (!overflow && line.Count > 0)
        {
            if (line[^1] == (byte)'\r')
            {
                line.RemoveAt(line.Count - 1);
            }
            yield return System.Text.Encoding.UTF8.GetString(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(line));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // Closing the job handle is the hard guarantee: if anything at all is
        // still alive in the tree (including descendants spawned after start),
        // KILL_ON_JOB_CLOSE terminates it.
        CloseHandle(_jobHandle);
        CloseHandle(_processHandle);
        Stdin.Dispose();
        Stdout.Dispose();
        Stderr.Dispose();
    }

    private static IntPtr CreateHandleList(IntPtr[] handles)
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList sizing failed");
        }
        var list = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
        }
        var pinned = GCHandle.Alloc(handles, GCHandleType.Pinned);
        try
        {
            if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeHandleList, pinned.AddrOfPinnedObject(),
                    (IntPtr)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
            {
                DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");
            }
        }
        finally
        {
            pinned.Free();
        }
        return list;
    }

    private static IntPtr MarshalEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        var entries = environment
            .Where(pair => !string.IsNullOrEmpty(pair.Key) && !pair.Key.Contains('=') && pair.Value is not null)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value);
        var block = string.Join('\0', entries) + "\0\0";
        var chars = block.ToCharArray();
        var pointer = Marshal.AllocHGlobal(chars.Length * 2);
        Marshal.Copy(chars, 0, pointer, chars.Length);
        return pointer;
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new System.Text.StringBuilder(Quote(executable));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }
        return builder.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(static c => c is ' ' or '\t' or '\n' or '\v' or '"'))
        {
            return value;
        }
        var builder = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    private static void ClosePipe(params IntPtr[] handles)
    {
        foreach (var handle in handles)
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInfo
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? applicationName, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute,
        IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
