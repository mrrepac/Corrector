using System.Diagnostics;

namespace Corrector;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--clipboard-test")) { Application.Run(new ClipboardTestContext()); return; }
        if (args.Contains("--input-fixture")) { Application.Run(SelfTests.InputFixture()); return; }
        if (args.Contains("--self-test")) { Environment.Exit(SelfTests.Run()); return; }
        if (args.Contains("--model-test")) { Environment.Exit(SelfTests.ModelTest().GetAwaiter().GetResult()); return; }
        using var singleton = new Mutex(true, @"Local\ABC.RussianCorrector", out bool first);
        if (!first) return;
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Corrector — ошибка");
        if(args.Contains("--editor"))Application.Run(new MainForm(false));
        else Application.Run(new TrayApp(args.Contains("--update-failed")));
    }
}

internal static class Paths
{
    public static string Root => AppContext.BaseDirectory;
    public static string Assets => Path.Combine(Root, "assets");
    public static string State => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Corrector");
}
