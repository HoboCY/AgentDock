namespace AgentDock.MacSetup;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            // 终端/管道输出统一 UTF-8（安装日志与 --status JSON 可能含中文；macOS 本身即 UTF-8）。
            Console.InputEncoding = new System.Text.UTF8Encoding(false);
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch
        {
            // 无控制台时忽略。
        }
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
