using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Markup;
using W = System.Windows;
using UI = System.Windows.Controls;
using M = System.Windows.Media;

namespace Corrector;

internal sealed class SettingsWindow : W.Window
{
    readonly UI.TextBox editor;
    readonly UI.CheckBox startup;
    AppSettings draft;
    PromptKind kind = PromptKind.Prose;
    internal AppSettings Saved => draft;

    internal SettingsWindow(AppSettings current, bool autostart)
    {
        draft = current;
        Title = "Настройки Corrector";
        WindowStartupLocation = W.WindowStartupLocation.CenterScreen;
        Width = 590; Height = 555;
        MaxHeight = W.SystemParameters.WorkArea.Height;
        MaxWidth = W.SystemParameters.WorkArea.Width;
        MinWidth = Math.Min(460, MaxWidth); MinHeight = Math.Min(380, MaxHeight);
        FontFamily = new M.FontFamily("Segoe UI"); FontSize = 14;
        UseLayoutRounding = true;
        CardTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "CardSurface");
        using var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream("Corrector.SettingsWindow.xaml")
            ?? throw new InvalidOperationException("Не найден макет настроек.");
        var panel = (UI.DockPanel)XamlReader.Load(stream);
        Content = panel;
        T Item<T>(string name) where T : W.FrameworkElement => (T)panel.FindName(name);
        editor = Item<UI.TextBox>("Prompt");
        startup = Item<UI.CheckBox>("Startup");
        startup.IsChecked = autostart;
        foreach (var language in Enum.GetValues<ProofLanguage>())
        {
            var option = Item<UI.RadioButton>(language.ToString());
            option.IsChecked = language == draft.Language;
            option.Checked += (_, _) => {
                RememberEditor();
                draft = draft with { Language = language };
                ShowPrompt();
            };
        }
        foreach (var task in Enum.GetValues<PromptKind>())
        {
            var option = Item<UI.RadioButton>(task.ToString());
            option.IsChecked = task == kind;
            option.Checked += (_, _) => { RememberEditor(); kind = task; ShowPrompt(); };
        }
        Item<UI.Button>("Reset").Click += (_, _) => { editor.Text = draft.DefaultPrompt(kind); editor.ScrollToHome(); };
        Item<UI.Button>("Save").Click += (_, _) => {
            RememberEditor();
            try { SettingsStore.Save(draft, startup.IsChecked == true); DialogResult = true; }
            catch (Exception ex) { W.MessageBox.Show(this, ex.Message, "Не удалось сохранить настройки"); }
        };
        ShowPrompt();
    }

    void RememberEditor() => draft = draft.WithPrompt(kind, editor.Text);
    void ShowPrompt() { editor.Text = draft.Prompt(kind); editor.Select(0, 0); editor.ScrollToHome(); }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        void Theme()
        {
            CardTheme.Apply(this);
            int dark = ((M.SolidColorBrush)Resources["CardSurface"]).Color.R < 128 ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        Theme();
        HwndSource.FromHwnd(handle).AddHook((nint hwnd, int msg, nint w, nint l, ref bool handled) => {
            if (msg is 0x1A or 0x31A or 0x15) Theme();
            return 0;
        });
    }
}
