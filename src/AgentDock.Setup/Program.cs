using System.Runtime.InteropServices;

namespace AgentDock.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0) NativeConsole.Prepare();
        try
        {
            return Cli.Run(args);
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine("AgentDock 安装程序错误：" + ex.Message);
            }
            catch
            {
                // 无控制台时静默退出。
            }
            return 1;
        }
    }
}

/// <summary>WinExe 从终端启动时附着父控制台，使 --status / 日志输出可见、可重定向。</summary>
internal static class NativeConsole
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    public static void Prepare()
    {
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            // 无控制台时忽略。
        }
        if (Console.IsOutputRedirected || Console.IsErrorRedirected) return;
        if (!AttachConsole(AttachParentProcess)) return;
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch
        {
            // 附着失败时保持静默。
        }
    }
}
