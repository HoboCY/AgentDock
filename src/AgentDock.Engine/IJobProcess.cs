namespace AgentDock.Engine;

/// <summary>One contained execution; termination returns only after descendants stop.</summary>
internal interface IJobProcess : IDisposable
{
    Stream Stdin { get; }
    Stream Stdout { get; }
    Stream Stderr { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    bool WaitForExit(int milliseconds);
    void Terminate();
}
