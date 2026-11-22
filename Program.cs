using System.Runtime.InteropServices;
using CCMixStudio.Windows.Core;

namespace CCMixStudio.Windows;

public static class Program
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint FileTypeUnknown = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr hFile);

    [STAThread]
    public static int Main(string[] args)
    {
        if (MixCli.ShouldRunCli(args))
        {
            AttachParentConsoleIfNeeded();
            return MixCli.Run(args);
        }

        AppLaunchConfiguration.Shared = MixCli.ParseGuiLaunchConfiguration(args);
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static void AttachParentConsoleIfNeeded()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var stdOut = GetStdHandle(StdOutputHandle);
        var stdErr = GetStdHandle(StdErrorHandle);
        if (GetFileType(stdOut) == FileTypeUnknown || GetFileType(stdErr) == FileTypeUnknown)
        {
            AttachConsole(AttachParentProcess);
        }
    }
}
