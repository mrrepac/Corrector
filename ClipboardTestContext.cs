using System.Text.Json;

namespace Corrector;

// Runs clipboard checks on the same STA message loop as the tray app, without a window or input.
internal sealed class ClipboardTestContext : ApplicationContext
{
    readonly Control dispatcher=new();
    public ClipboardTestContext()
    {
        _=dispatcher.Handle;
        dispatcher.BeginInvoke((Action)(async()=>
        {
            Directory.CreateDirectory(Paths.State);
            object report;
            try{report=await SelectedText.ClipboardRegression();}
            catch(Exception ex){report=new{error=ex.ToString()};}
            File.WriteAllText(Path.Combine(Paths.State,"clipboard-test.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            var json=JsonSerializer.SerializeToElement(report);
            Environment.ExitCode=json.TryGetProperty("error",out _) || json.EnumerateObject().Any(p=>p.Value.ValueKind==JsonValueKind.False)?1:0;
            dispatcher.Dispose();ExitThread();
        }));
    }
}
