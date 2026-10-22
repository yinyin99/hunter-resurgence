namespace Backup;

static class Program
{
    [STAThread]
    static int Main(string[] a)
    {
        if (a is ["--selftest"]) return SelfTest.Run();
        var app = new App();
        app.InitializeComponent();
        return app.Run(new MainWindow());
    }
}
