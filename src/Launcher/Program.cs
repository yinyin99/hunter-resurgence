using System.Runtime.InteropServices;

namespace Resurgence.Launcher;

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] static extern nint GetStdHandle(int which);

    [STAThread]
    static int Main(string[] a)
    {
        if (a is ["--selftest"])
        {
            // a WinExe has no console of its own: use the redirected output of whoever started us, else the console they run in
            if (GetStdHandle(-11) is 0 or -1) AttachConsole(-1);
            return SelfTest.Run();
        }
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
