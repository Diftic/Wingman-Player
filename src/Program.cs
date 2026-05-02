namespace wingman_player;

using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

public static class Program
{
    // Re-attach to the console that launched this WinExe process (e.g. dotnet run
    // or a terminal) so that the console logger is visible there.
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static void Main(string[] args)
    {
        AttachConsole(-1); // ATTACH_PARENT_PROCESS

        using var mutex = new Mutex(true, Constants.MutexId, out var isNewInstance);
        if (!isNewInstance)
        {
            // TODO: send WM message to bring existing instance to foreground
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
