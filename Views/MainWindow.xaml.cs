using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using MeshChat.ViewModels;
using MeshChat.Models;
using System.Windows.Media.Animation;
using MeshChat.Services;
using Microsoft.Extensions.Logging;

namespace MeshChat.Views;

// Custom animation for GridLength (column width)
public class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register("From", typeof(GridLength), typeof(GridLengthAnimation));

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register("To", typeof(GridLength), typeof(GridLengthAnimation));

    public GridLength From
    {
        get => (GridLength)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction { get; set; }

    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var from = From.Value;
        var to = To.Value;
        var progress = animationClock.CurrentProgress ?? 0;

        if (EasingFunction != null)
            progress = EasingFunction.Ease(progress);

        var current = from + (to - from) * progress;
        return new GridLength(current, GridUnitType.Pixel);
    }
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ILogger<MainWindow> _logger;
    private readonly CancellationTokenSource _windowCts = new();
    private const double NetworkLogExpandedWidth = 280;
    private const double NetworkLogCollapsedWidth = 0;

    public MainWindow(ILoggerFactory loggerFactory)
    {
        InitializeComponent();
        _logger = loggerFactory.CreateLogger<MainWindow>();

        // Create typed loggers from the application factory and pass them into
        // each component that owns work. Use ILogger<T> placeholders for values
        // so the file provider receives structured properties.
        _vm = new MainViewModel(
            new WiFiService(loggerFactory.CreateLogger<WiFiService>()),
            new BluetoothService(loggerFactory.CreateLogger<BluetoothService>()),
            new FileTransferService(loggerFactory.CreateLogger<FileTransferService>()),
            new MessageStore(loggerFactory.CreateLogger<MessageStore>()),
            loggerFactory.CreateLogger<MainViewModel>());
        DataContext = _vm;

        // Auto-scroll the virtualized message list without forcing every item container to be created.
        _vm.Messages.CollectionChanged += Messages_CollectionChanged;

        // Handle network log toggle with animation
        _vm.OnNetworkLogToggled += AnimateNetworkLog;

        // Also subscribe to property changes to debug
        _vm.PropertyChanged += Vm_PropertyChanged;

        // Set initial state - network log visible for debugging
        Loaded += async (_, _) =>
        {
            // Ensure network log starts visible
            NetworkLogPanel.Width = NetworkLogExpandedWidth;

            // Play smooth modern animations on load
            _ = PlayStartupAnimationsAsync(_windowCts.Token);

            try
            {
                // Load persisted messages before network services can receive new packets.
                await _vm.InitializeAsync(_windowCts.Token);

                // Start services after window is fully rendered.
                await _vm.StartAsync(_windowCts.Token);
                UpdateTitle();
            }
            catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
            {
            }
        };
        Closing += async (_, _) =>
        {
            _vm.Messages.CollectionChanged -= Messages_CollectionChanged;
            _vm.OnNetworkLogToggled -= AnimateNetworkLog;
            _vm.PropertyChanged -= Vm_PropertyChanged;

            _windowCts.Cancel();
            try
            {
                await _vm.StopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shutdown failed");
            }
            finally
            {
                _windowCts.Dispose();
            }
        };
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "IsNetworkLogVisible")
        {
            System.Diagnostics.Debug.WriteLine($"Log visibility changed to: {_vm.IsNetworkLogVisible}");
        }
    }

    private async Task PlayStartupAnimationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Delays are tokenized so startup animations stop cleanly if the window closes mid-sequence.
            await Task.Delay(50, cancellationToken);

            // Fade in the entire window
            TryBeginStoryboard("WindowFadeIn", this);

            // Slide down the header
            await Task.Delay(100, cancellationToken);
            TryBeginStoryboard("HeaderSlideIn", HeaderBorder);

            // Slide in the sidebar
            await Task.Delay(120, cancellationToken);
            TryBeginStoryboard("SidebarSlideIn", SidebarBorder);

            // Fade in the chat area
            await Task.Delay(150, cancellationToken);
            TryBeginStoryboard("ChatAreaFadeIn", MainContentGrid);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Startup animation failed");
        }
    }

    private bool TryGetStoryboard(string key, out Storyboard storyboard)
    {
        if (TryFindResource(key) is Storyboard foundStoryboard)
        {
            storyboard = foundStoryboard;
            return true;
        }

        storyboard = null!;
        _logger.LogWarning("Storyboard resource '{ResourceKey}' is missing or not a Storyboard; skipping animation", key);
        return false;
    }

    private bool TryBeginStoryboard(string key, FrameworkElement target)
    {
        if (!TryGetStoryboard(key, out var storyboard))
            return false;

        storyboard.Begin(target);
        return true;
    }

    private void AnimateNetworkLog(bool show)
    {
        var targetWidth = show ? NetworkLogExpandedWidth : NetworkLogCollapsedWidth;

        // Animate the network log panel width with smooth cubic easing
        var animation = new GridLengthAnimation
        {
            From = NetworkLogColumn.Width,
            To = new GridLength(targetWidth),
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        NetworkLogColumn.BeginAnimation(ColumnDefinition.WidthProperty, animation);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+F - Focus search
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchTextBox.Focus();
            e.Handled = true;
        }
        // Escape - Clear search or close panels
        else if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(_vm.SearchQuery))
            {
                _vm.SearchQuery = string.Empty;
                MessageTextBox.Focus();
            }
            else if (ManualConnectPanel.Visibility == Visibility.Visible)
            {
                ManualConnectPanel.Visibility = Visibility.Collapsed;
            }
            e.Handled = true;
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_vm.FilteredMessages.Cast<ChatMessage>().LastOrDefault() is ChatMessage lastMessage)
                    MessagesList.ScrollIntoView(lastMessage);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
        => await _vm.SendMessageAsync(_windowCts.Token);

    private void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter or just Enter to send
        if (e.Key == Key.Return && (Keyboard.Modifiers == ModifierKeys.Control || !Keyboard.IsKeyDown(Key.LeftShift)))
        {
            e.Handled = true;
            _ = _vm.SendMessageAsync(_windowCts.Token);
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        => await _vm.ConnectManualAsync(_windowCts.Token);

    private void AddDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        // Animate button press
        if (TryGetStoryboard("ButtonPressAnim", out var pressAnim) &&
            TryGetStoryboard("ButtonReleaseAnim", out var releaseAnim))
        {
            pressAnim.Begin(PlusButtonBorder);
            pressAnim.Completed += (_, _) => releaseAnim.Begin(PlusButtonBorder);
        }

        // Toggle panel with fade animation
        bool isVisible = ManualConnectPanel.Visibility == Visibility.Visible;
        if (isVisible)
        {
            // Fade out
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (_, _) => ManualConnectPanel.Visibility = Visibility.Collapsed;
            ManualConnectPanel.BeginAnimation(OpacityProperty, fadeOut);
            ManualConnectPanel.IsHitTestVisible = false;
        }
        else
        {
            // Fade in
            ManualConnectPanel.Opacity = 0;
            ManualConnectPanel.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
            ManualConnectPanel.BeginAnimation(OpacityProperty, fadeIn);
            ManualConnectPanel.IsHitTestVisible = true;
        }
    }

    private async void SendFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a file to send",
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
            await _vm.SendFileAsync(dialog.FileName, _windowCts.Token);
    }

    private void UpdateTitle()
    {
        Title = _vm.TitleWithUnread;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LogToggleButton_Click(object sender, RoutedEventArgs e)
    {
        // Toggle and animate
        bool newState = !_vm.IsNetworkLogVisible;
        _vm.IsNetworkLogVisible = newState;
        AnimateNetworkLog(newState);
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        _vm.SearchQuery = string.Empty;
    }

    private void EncryptionToggle_Click(object sender, RoutedEventArgs e)
    {
        _vm.EncryptionEnabled = !_vm.EncryptionEnabled;
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu contextMenu)
        {
            var border = contextMenu.PlacementTarget as Border;
            if (border?.DataContext is ChatMessage msg)
            {
                try
                {
                    System.Windows.Clipboard.SetText(msg.Content);
                }
                catch (Exception ex) { _logger.LogError(ex, "Clipboard error while copying message content"); }
            }
        }
    }

    private void CopySender_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu contextMenu)
        {
            var border = contextMenu.PlacementTarget as Border;
            if (border?.DataContext is ChatMessage msg)
            {
                try
                {
                    System.Windows.Clipboard.SetText(msg.SenderName ?? "");
                }
                catch (Exception ex) { _logger.LogError(ex, "Clipboard error while copying sender name"); }
            }
        }
    }
}
