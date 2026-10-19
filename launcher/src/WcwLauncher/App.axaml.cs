using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace WcwLauncher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Fluent's own accent (combo highlights, list selection, focus) follows the launcher's
        // AccentColor, so the accent file stays the only place the colour is set.
        if (TryGetResource("AccentColor", ThemeVariant.Dark, out object? accent) && accent is Color color &&
            Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
        {
            fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = color };
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(Program.CaptureFolder);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
