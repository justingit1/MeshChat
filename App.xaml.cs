using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MeshChat.Logging;
using MeshChat.Views;
using Microsoft.Extensions.Logging;

namespace MeshChat;

public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One application-wide factory owns the logging pipeline. Typed ILogger<T>
        // instances are created from it and passed into windows, view models, and services.
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            builder.AddProvider(new DailyFileLoggerProvider());
        });

        // Performance optimizations for low-end computers

        // Set max frame rate to 30fps to reduce CPU/GPU usage
        // This prevents high CPU usage when animating
        Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(Timeline),
            new FrameworkPropertyMetadata(30));

        // Use Display mode for faster text rendering on low-end hardware
        // (Ideal mode is too expensive for school laptops)
        TextOptions.TextFormattingModeProperty.OverrideMetadata(
            typeof(Window),
            new FrameworkPropertyMetadata(TextFormattingMode.Display));

        var mainWindow = new MainWindow(_loggerFactory);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }
}
