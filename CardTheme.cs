using Microsoft.Win32;
using W = System.Windows;
using UI = System.Windows.Controls;
using M = System.Windows.Media;

namespace Corrector;

internal static class CardTheme
{
    internal static UI.Border Frame(W.UIElement child, W.Thickness padding)
    {
        var frame = new UI.Border { CornerRadius = new W.CornerRadius(17), BorderThickness = new W.Thickness(1),
            Padding = padding, Margin = new W.Thickness(16), Child = child,
            Effect = new System.Windows.Media.Effects.DropShadowEffect {
                Color = M.Colors.Black, BlurRadius = 16, ShadowDepth = 3, Opacity = 0.22 } };
        frame.SetResourceReference(UI.Border.BackgroundProperty, "CardSurface");
        frame.SetResourceReference(UI.Border.BorderBrushProperty, "CardEdge");
        return frame;
    }

    internal static void Apply(W.FrameworkElement root, bool warning = false) => Apply(root.Resources, warning);

    internal static void Apply(W.ResourceDictionary resources, bool warning = false)
    {
        bool dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0;
        void Set(string key, string night, string day, M.Brush contrast)
        {
            resources[key] = SystemInformation.HighContrast ? contrast :
                new M.SolidColorBrush((M.Color)M.ColorConverter.ConvertFromString(dark ? night : day));
        }
        Set("CardSurface", "#202724", "#FCFDFC", W.SystemColors.WindowBrush);
        Set("CardEdge", "#414B46", "#CED9D2", W.SystemColors.WindowTextBrush);
        Set("CardText", "#F4F7F4", "#18251E", W.SystemColors.WindowTextBrush);
        Set("CardSecondary", "#C0CCC4", "#4C5C52", W.SystemColors.WindowTextBrush);
        Set("CardTint", warning ? "#493724" : "#233F35", warning ? "#FFF0DA" : "#E0F1E7", W.SystemColors.HighlightBrush);
        Set("CardAccent", warning ? "#FFC680" : "#9AE1B9", warning ? "#865213" : "#246547", W.SystemColors.HighlightTextBrush);
    }
}
