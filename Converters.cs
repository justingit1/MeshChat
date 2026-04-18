using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using MeshChat.Models;

namespace MeshChat.Converters;

// Sent messages align right, received align left
public class MessageAlignmentConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is string senderId && values[1] is string localId)
            return senderId == localId ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        return HorizontalAlignment.Left;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// Modern gradient bubbles - Fresh iMessage-style design
public class BubbleColorConverter : IMultiValueConverter
{
    // Sent = Modern blue gradient (outgoing - user's messages)
    private static readonly LinearGradientBrush Sent = new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops = new GradientStopCollection
        {
            new GradientStop(Color.FromRgb(10, 132, 255), 0),    // Bright blue
            new GradientStop(Color.FromRgb(0, 122, 255), 0.5),   // Blue
            new GradientStop(Color.FromRgb(88, 86, 214), 1)      // Purple tint
        }
    };

    // Received = Modern dark slate (incoming - from others)
    private static readonly LinearGradientBrush Received = new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops = new GradientStopCollection
        {
            new GradientStop(Color.FromRgb(58, 64, 72), 0),     // Dark gray
            new GradientStop(Color.FromRgb(44, 46, 50), 1)      // Darker
        }
    };

    // System messages = Subtle translucent
    private static readonly SolidColorBrush System = new(Color.FromArgb(40, 100, 116, 139));

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        // values[2] = MessageType (optional)
        if (values.Length >= 3 && values[2] is MessageType mt && mt == MessageType.System)
            return System;

        if (values.Length >= 2 && values[0] is string senderId && values[1] is string localId)
            return senderId == localId ? Sent : Received;

        return Received;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// Sent text is white, received is white (both have white text now with 2026 design)
public class BubbleTextColorConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush White = new(Colors.White);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return White;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// Show delivery status icon text with proper checkmarks for read receipts
public class MessageStatusConverter : IValueConverter
{
    // Single checkmark (sent)
    private static readonly string SingleCheck = "\u2713";
    // Double checkmarks (delivered/read)
    private static readonly string DoubleCheck = "\u2713\u2713";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            MessageStatus.Sending => "\u23F3",  // Hourglass
            MessageStatus.Sent => SingleCheck,
            MessageStatus.Delivered => DoubleCheck,
            MessageStatus.Read => DoubleCheck,
            MessageStatus.Failed => "\u2717",   // X mark
            _ => ""
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Read receipts turn blue, others are gray
public class StatusColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Blue = new(Color.FromRgb(37, 99, 235));     // iOS Blue for read
    private static readonly SolidColorBrush Gray = new(Color.FromRgb(148, 163, 184));   // Gray for sent/delivered

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MessageStatus.Read ? Blue : Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Peer online dot color
public class PeerStatusColorConverter : IValueConverter
{
    private static readonly SolidColorBrush OnlineBrush = new(Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush AwayBrush = new(Color.FromRgb(245, 158, 11));
    private static readonly SolidColorBrush OfflineBrush = new(Color.FromRgb(148, 163, 184));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            PeerStatus.Online => OnlineBrush,
            PeerStatus.Away => AwayBrush,
            _ => OfflineBrush
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// File size formatter
public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
        return "0 B";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Show/hide unread badge
public class UnreadVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Boolean to visibility
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Null check → bool (for IsEnabled bindings)
public class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Double > 0 → Visible (for progress bars)
public class DoubleToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d && d > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Transport badge color
public class TransportColorConverter : IValueConverter
{
    private static readonly SolidColorBrush BluetoothBrush = new(Color.FromRgb(124, 58, 237));
    private static readonly SolidColorBrush BothBrush = new(Color.FromRgb(34, 211, 238));
    private static readonly SolidColorBrush WifiBrush = new(Color.FromRgb(56, 189, 248));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            TransportType.Bluetooth => BluetoothBrush,
            TransportType.Both => BothBrush,
            _ => WifiBrush
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// MessageType to Visibility (hide system and date separator message elements)
public class NotSystemVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Hide for System and DateSeparator types - only show regular messages
        if (value is MessageType mt)
            return mt == MessageType.System || mt == MessageType.DateSeparator ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Show ONLY system messages
public class IsSystemVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MessageType.System ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Show ONLY date separator messages
public class IsDateSeparatorVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MessageType.DateSeparator ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// ═════════════════════════════════════════════════════════════════════════════
// DATE SEPARATOR CONVERTER - Shows date headers between messages
// ═════════════════════════════════════════════════════════════════════════════

public class DateSeparatorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DateTime date)
        {
            var today = DateTime.Today;
            var yesterday = today.AddDays(-1);

            if (date.Date == today)
                return "Today";
            if (date.Date == yesterday)
                return "Yesterday";
            if (date.Year == today.Year)
                return date.ToString("MMMM d");
            return date.ToString("MMMM d, yyyy");
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Show date separator when date changes from previous message
public class DateSeparatorVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is DateTime currentDate && values[1] is DateTime previousDate)
        {
            return currentDate.Date != previousDate.Date ? Visibility.Visible : Visibility.Collapsed;
        }
        // First message always shows separator
        if (values.Length >= 1 && values[0] is DateTime)
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ═════════════════════════════════════════════════════════════════════════════
// AVATAR GRADIENT CONVERTER - Assigns gradient based on peer ID
// ═════════════════════════════════════════════════════════════════════════════

public class AvatarGradientConverter : IValueConverter
{
    private static readonly LinearGradientBrush[] Gradients;

    static AvatarGradientConverter()
    {
        // Mesh Gradient palette - Sky & Sunset tones for 2026
        Gradients =
        [
            CreateGradientBrush(Color.FromRgb(56, 189, 248), Color.FromRgb(129, 140, 248)),  // Sky to Indigo
            CreateGradientBrush(Color.FromRgb(244, 114, 182), Color.FromRgb(251, 191, 36)),  // Pink to Amber
            CreateGradientBrush(Color.FromRgb(52, 211, 153), Color.FromRgb(56, 189, 248)),   // Emerald to Sky
            CreateGradientBrush(Color.FromRgb(167, 139, 250), Color.FromRgb(244, 114, 182)), // Purple to Pink
            CreateGradientBrush(Color.FromRgb(251, 146, 60), Color.FromRgb(244, 114, 182)),  // Orange to Pink
            CreateGradientBrush(Color.FromRgb(34, 211, 238), Color.FromRgb(167, 139, 250)),  // Cyan to Purple
            CreateGradientBrush(Color.FromRgb(96, 165, 250), Color.FromRgb(34, 211, 238)),  // Blue to Cyan
            CreateGradientBrush(Color.FromRgb(252, 211, 77), Color.FromRgb(251, 146, 60))   // Yellow to Orange
        ];
    }

    private static LinearGradientBrush CreateGradientBrush(Color start, Color end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(start, 0));
        brush.GradientStops.Add(new GradientStop(end, 1));
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string id && !string.IsNullOrEmpty(id))
        {
            // Use hash of ID to consistently pick a gradient
            var hash = Math.Abs(id.GetHashCode());
            return Gradients[hash % Gradients.Length];
        }
        return Gradients[0];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// ═════════════════════════════════════════════════════════════════════════════
// TYPING INDICATOR CONVERTER
// ═════════════════════════════════════════════════════════════════════════════

public class TypingVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// ═════════════════════════════════════════════════════════════════════════════
// UNREAD COUNT TO TITLE BADGE CONVERTER
// ═════════════════════════════════════════════════════════════════════════════

public class UnreadTitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int count && count > 0)
            return $" ({count} unread)";
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isBold && isBold)
            return FontWeights.SemiBold;
        return FontWeights.Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public class LogEntryToInlinesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not LogEntry entry) return null!;

        var inlines = new List<Inline>();
        var mutedBrush = new SolidColorBrush(Color.FromRgb(75, 85, 99)); // #4B5563

        // Modern timestamp: dimmed
        inlines.Add(new Run(entry.Timestamp) { Foreground = mutedBrush, FontSize = 11 });
        inlines.Add(new Run(" | "));

        // Tag with colored background simulation using color
        if (!string.IsNullOrEmpty(entry.Tag))
        {
            var tagColor = new SolidColorBrush(entry.TagColor);
            inlines.Add(new Run($"[{entry.Tag}]") { Foreground = tagColor, FontWeight = FontWeights.SemiBold, FontSize = 11 });
            inlines.Add(new Run(" "));
        }

        // Format message for readability
        var message = entry.Message;

        // Add emoji indicators for common actions
        if (message.Contains("[SENT"))
            inlines.Add(new Run("\u2191 ") { Foreground = new SolidColorBrush(Color.FromRgb(34, 211, 238)), FontWeight = FontWeights.Bold }); // Cyan arrow up
        else if (message.Contains("[RECEIVED"))
            inlines.Add(new Run("\u2193 ") { Foreground = new SolidColorBrush(Color.FromRgb(167, 139, 250)), FontWeight = FontWeights.Bold }); // Purple arrow down
        else if (message.Contains("joined") || message.Contains("Joined"))
            inlines.Add(new Run("\u2795 ") { Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94)), FontWeight = FontWeights.Bold }); // Green plus
        else if (message.Contains("left") || message.Contains("Left"))
            inlines.Add(new Run("\u2796 ") { Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)), FontWeight = FontWeights.Bold }); // Red minus
        else if (message.Contains("Error") || message.Contains("error") || message.Contains("failed"))
            inlines.Add(new Run("\u26A0 ") { Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)), FontWeight = FontWeights.Bold }); // Red warning
        else if (message.Contains("saved to") || message.Contains("received"))
            inlines.Add(new Run("\u2705 ") { Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94)), FontWeight = FontWeights.Bold }); // Green check

        // Add the message with colored keywords
        var formattedMessage = FormatMessageWithColors(message);
        inlines.Add(new Run(formattedMessage) { Foreground = new SolidColorBrush(Color.FromRgb(229, 231, 235)), FontSize = 12 }); // #E5E7EB

        return inlines;
    }

    private static string FormatMessageWithColors(string message)
    {
        // Clean up the message for better readability
        var result = message;

        // Remove redundant prefixes that we handle with icons
        if (result.StartsWith("[SENT"))
            result = result.Substring(result.IndexOf(']') + 1).Trim();
        else if (result.StartsWith("[RECEIVED"))
            result = result.Substring(result.IndexOf(']') + 1).Trim();

        return result;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Connection quality to signal bars
public class SignalStrengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int strength) return "⚡";

        return strength switch
        {
            >= 80 => "⚡⚡⚡⚡",
            >= 60 => "⚡⚡⚡",
            >= 40 => "⚡⚡",
            > 0 => "⚡",
            _ => "⚡"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Connection quality color
public class QualityColorConverter : IValueConverter
{
    private static readonly SolidColorBrush ExcellentBrush = new(Color.FromRgb(48, 209, 88));
    private static readonly SolidColorBrush GoodBrush = new(Color.FromRgb(52, 199, 89));
    private static readonly SolidColorBrush FairBrush = new(Color.FromRgb(255, 159, 10));
    private static readonly SolidColorBrush PoorBrush = new(Color.FromRgb(255, 69, 58));
    private static readonly SolidColorBrush NoneBrush = new(Color.FromRgb(142, 142, 147));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int strength) return NoneBrush;

        return strength switch
        {
            >= 80 => ExcellentBrush,
            >= 60 => GoodBrush,
            >= 40 => FairBrush,
            > 0 => PoorBrush,
            _ => NoneBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// ═════════════════════════════════════════════════════════════════════════════
// SIGNAL BARS CONVERTER - Visual connection quality indicator
// ═════════════════════════════════════════════════════════════════════════════

public class SignalBarsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int strength) return 0;

        return strength switch
        {
            >= 80 => 4,
            >= 60 => 3,
            >= 40 => 2,
            > 0 => 1,
            _ => 0
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Signal bar color - returns color for each bar based on signal strength threshold
public class SignalBarColorConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveGreen = new(Color.FromRgb(48, 209, 88));
    private static readonly SolidColorBrush InactiveGray = new(Color.FromRgb(71, 85, 105));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int strength) return InactiveGray;
        if (parameter is not string barStr || !int.TryParse(barStr, out int barIndex)) return InactiveGray;

        // Bar thresholds: 1=25%, 2=50%, 3=75%, 4=90%
        var threshold = barIndex switch
        {
            1 => 25,
            2 => 50,
            3 => 75,
            4 => 90,
            _ => 0
        };

        return strength >= threshold ? ActiveGreen : InactiveGray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// ═════════════════════════════════════════════════════════════════════════════
// LAST SEEN CONVERTER - Presence indicators
// ═════════════════════════════════════════════════════════════════════════════

public class LastSeenTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime lastSeen) return "Unknown";

        var now = DateTime.Now;
        var diff = now - lastSeen;

        if (diff.TotalSeconds < 60) return "Just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 2) return "Yesterday";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";

        return lastSeen.ToString("MMM d");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Show last seen for offline/away peers, hide for online
public class LastSeenVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PeerStatus status)
        {
            return status == PeerStatus.Online ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Show status text (Online/Away/Offline)
public class PeerStatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PeerStatus status)
        {
            return status switch
            {
                PeerStatus.Online => "Online",
                PeerStatus.Away => "Away",
                _ => "Offline"
            };
        }
        return "Offline";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Status color for peer list
public class PeerStatusTextColorConverter : IValueConverter
{
    private static readonly SolidColorBrush OnlineGreen = new(Color.FromRgb(48, 209, 88));
    private static readonly SolidColorBrush AwayOrange = new(Color.FromRgb(255, 159, 10));
    private static readonly SolidColorBrush OfflineGray = new(Color.FromRgb(142, 142, 147));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PeerStatus status)
        {
            return status switch
            {
                PeerStatus.Online => OnlineGreen,
                PeerStatus.Away => AwayOrange,
                _ => OfflineGray
            };
        }
        return OfflineGray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// ═════════════════════════════════════════════════════════════════════════════
// MESSAGE REACTIONS CONVERTERS
// ═════════════════════════════════════════════════════════════════════════════

// Show/hide reactions row based on whether any reactions exist
public class ReactionsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Dictionary<string, List<string>> reactions && reactions.Count > 0)
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Get reaction count for display
public class ReactionCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int count)
            return count > 0 ? count.ToString() : "";
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Convert reactions dictionary to items for display
public class ReactionsToCollectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Dictionary<string, List<string>> reactions)
        {
            return reactions.Select(kvp => new ReactionDisplayItem
            {
                Emoji = kvp.Key,
                Count = kvp.Value.Count,
                Users = kvp.Value
            }).ToList();
        }
        return new List<ReactionDisplayItem>();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Helper class for reaction display
public class ReactionDisplayItem
{
    public string Emoji { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<string> Users { get; set; } = new();
}

// Toast notification background color
public class ErrorToastColorConverter : IValueConverter
{
    private static readonly Color SuccessColor = (Color)ColorConverter.ConvertFromString("#FF34C759");
    private static readonly Color ErrorColor = (Color)ColorConverter.ConvertFromString("#FFFF3B30");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isError)
            return isError ? ErrorColor : SuccessColor;
        return SuccessColor;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
