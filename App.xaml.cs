using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MeshChat;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
    }
}
