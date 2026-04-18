using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Media;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using MeshChat.Models;
using MeshChat.Services;
using Newtonsoft.Json;
using System.Runtime.InteropServices;

namespace MeshChat.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged
{
    private readonly WiFiService _wifi;
    private readonly BluetoothService _bluetooth;
    private readonly FileTransferService _fileTransfer;
    private readonly Dispatcher _dispatcher;
    private readonly MessageStore _messageStore;

    // ─── Observable State ───────────────────────────────────────────────────

    public ObservableCollection<Peer> Peers { get; } = [];
    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];

    private Peer? _selectedPeer;
    public Peer? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            _selectedPeer = value;
            OnPropertyChanged();
            // Use dispatcher to ensure thread safety
            _dispatcher.Invoke(() =>
            {
                LoadMessagesForPeer(value);
                UpdateUnreadCounts();
            });
        }
    }

    private string _messageInput = string.Empty;
    private DateTime _lastTypingSent = DateTime.MinValue;
    private const int TypingSendIntervalMs = 2000; // Send typing every 2 seconds

    public string MessageInput
    {
        get => _messageInput;
        set
        {
            _messageInput = value;
            OnPropertyChanged();
            // Send typing indicator when user starts typing
            if (!string.IsNullOrEmpty(value) && SelectedPeer != null)
            {
                var now = DateTime.Now;
                if ((now - _lastTypingSent).TotalMilliseconds > TypingSendIntervalMs)
                {
                    _lastTypingSent = now;
                    _ = SendTypingIndicatorAsync();
                }
            }
        }
    }

    private async Task SendTypingIndicatorAsync()
    {
        if (SelectedPeer == null) return;

        var packet = new NetworkPacket
        {
            Type = PacketType.Typing,
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            TargetId = SelectedPeer.Id
        };

        await SendToPeerViaTransportAsync(SelectedPeer.Id, packet);
    }

    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            _searchQuery = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilteredMessages));
        }
    }

    public IEnumerable<ChatMessage> FilteredMessages
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchQuery))
                return Messages;

            var query = SearchQuery.ToLowerInvariant();
            return Messages.Where(m =>
                m.Content.ToLowerInvariant().Contains(query) ||
                (m.SenderName?.ToLowerInvariant().Contains(query) ?? false) ||
                (m.FileName?.ToLowerInvariant().Contains(query) ?? false));
        }
    }

    // Log filter options
    public string[] LogFilterOptions { get; } = { "All", "WiFi", "Bluetooth", "Errors", "Messages" };

    private string _logFilter = "All";
    public string LogFilter
    {
        get => _logFilter;
        set
        {
            _logFilter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilteredLogs));
        }
    }

    public IEnumerable<LogEntry> FilteredLogs
    {
        get
        {
            if (LogFilter == "All")
                return Logs;

            return Logs.Where(l => LogFilter switch
            {
                "WiFi" => l.Tag.StartsWith("WiFi"),
                "Bluetooth" => l.Tag.StartsWith("Bluetooth"),
                "Errors" => l.Tag.Contains("Error") || l.Message.Contains("Error") || l.Message.Contains("failed"),
                "Messages" => l.Tag.Contains("SENT") || l.Tag.Contains("RECEIVED"),
                _ => true
            });
        }
    }

    private string _displayName = string.Empty;
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); }
    }

    private bool _encryptionEnabled = false;
    public bool EncryptionEnabled
    {
        get => _encryptionEnabled;
        set
        {
            _encryptionEnabled = value;
            OnPropertyChanged();
            // Log encryption status change
            if (value)
            {
                AddLog("🔒 Message encryption ENABLED", LogLevel.Success);
                if (!StatusText.Contains("Encrypted"))
                    StatusText += " · Encrypted";
            }
            else
            {
                AddLog("🔓 Message encryption DISABLED", LogLevel.Info);
                StatusText = StatusText.Replace(" · Encrypted", "");
            }
        }
    }

    // Expose the local peer ID so the UI can determine sent vs received
    public string LocalId => _wifi.LocalId;

    // Toast notification properties
    private string _toastMessage = string.Empty;
    public string ToastMessage
    {
        get => _toastMessage;
        set { _toastMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(ToastVisible)); }
    }

    public bool ToastVisible => !string.IsNullOrEmpty(_toastMessage);

    private bool _toastIsError;
    public bool ToastIsError
    {
        get => _toastIsError;
        set { _toastIsError = value; OnPropertyChanged(); }
    }

    public void ShowToast(string message, bool isError = false)
    {
        ToastMessage = message;
        ToastIsError = isError;
        _ = HideToastAfterDelay();
    }

    private async Task HideToastAfterDelay()
    {
        await Task.Delay(3000);
        ToastMessage = string.Empty;
    }

    private string _statusText = "Not connected";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private bool _isWifiConnected;
    public bool IsWifiConnected
    {
        get => _isWifiConnected;
        set { _isWifiConnected = value; OnPropertyChanged(); }
    }

    private bool _isBluetoothAvailable;
    public bool IsBluetoothAvailable
    {
        get => _isBluetoothAvailable;
        set { _isBluetoothAvailable = value; OnPropertyChanged(); }
    }

    private string _connectIp = string.Empty;
    public string ConnectIp
    {
        get => _connectIp;
        set { _connectIp = value; OnPropertyChanged(); }
    }

    private string _connectPort = "45678";
    public string ConnectPort
    {
        get => _connectPort;
        set { _connectPort = value; OnPropertyChanged(); }
    }

    // ─── Unread Badge for Title ─────────────────────────────────────────────

    private int _totalUnreadCount;
    public int TotalUnreadCount
    {
        get => _totalUnreadCount;
        private set
        {
            _totalUnreadCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TitleWithUnread));
        }
    }

    public string TitleWithUnread => TotalUnreadCount > 0
        ? $"MeshChat — Offline P2P Messenger ({TotalUnreadCount} unread)"
        : "MeshChat — Offline P2P Messenger";

    // ─── Typing Indicator ───────────────────────────────────────────────────

    private bool _isPeerTyping;
    public bool IsPeerTyping
    {
        get => _isPeerTyping;
        private set { _isPeerTyping = value; OnPropertyChanged(); }
    }

    private string _typingPeerName = string.Empty;
    public string TypingPeerName
    {
        get => _typingPeerName;
        private set { _typingPeerName = value; OnPropertyChanged(); }
    }

    private readonly Dictionary<string, DateTime> _typingTimers = [];
    private const int TypingIndicatorDurationMs = 3000;

    // ─── UI State ───────────────────────────────────────────────────────────
    private bool _isNetworkLogVisible = true; // Default to visible for debugging
    public bool IsNetworkLogVisible
    {
        get => _isNetworkLogVisible;
        set { _isNetworkLogVisible = value; OnPropertyChanged(); }
    }

    [RelayCommand]
    private void ToggleNetworkLog()
    {
        IsNetworkLogVisible = !IsNetworkLogVisible;
        OnNetworkLogToggled?.Invoke(IsNetworkLogVisible);
    }

    // Event for XAML to handle animations
    public event Action<bool>? OnNetworkLogToggled;

    // Per-peer message history
    private readonly Dictionary<string, List<ChatMessage>> _messageHistory = [];

    // ─── Transport Routing Helpers ──────────────────────────────────────────

    private async Task SendToPeerViaTransportAsync(string peerId, NetworkPacket packet)
    {
        var peer = Peers.FirstOrDefault(p => p.Id == peerId);
        if (peer == null)
        {
            // Default to WiFi if peer not found
            await _wifi.SendToPeerAsync(peerId, packet);
            return;
        }

        // Use appropriate transport based on peer's connection type
        if (peer.Transport == TransportType.Bluetooth)
            await _bluetooth.SendToPeerAsync(peerId, packet);
        else
            await _wifi.SendToPeerAsync(peerId, packet);
    }

    private async Task SendToAllViaTransportAsync(NetworkPacket packet)
    {
        // Broadcast via both transports to ensure all peers receive it
        await _wifi.SendToAllAsync(packet);
        await _bluetooth.SendToAllAsync(packet);
    }

    // ─── Constructor ────────────────────────────────────────────────────────

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        _wifi = new WiFiService();
        _bluetooth = new BluetoothService();
        _fileTransfer = new FileTransferService();
        _messageStore = new MessageStore();

        DisplayName = Environment.MachineName;

        // Load persisted messages
        LoadPersistedMessages();

        // Wire up events
        _wifi.PeerDiscovered += OnPeerDiscovered;
        _wifi.PeerLost += OnPeerLost;
        _wifi.PacketReceived += OnPacketReceived;
        _wifi.LogMessage += AddLog;

        _bluetooth.PeerDiscovered += OnPeerDiscovered;
        _bluetooth.PeerLost += OnPeerLost;
        _bluetooth.PacketReceived += OnPacketReceived;
        _bluetooth.LogMessage += AddLog;

        _fileTransfer.ProgressUpdated += OnFileProgress;
        _fileTransfer.FileReceived += OnFileReceived;
        _fileTransfer.LogMessage += AddLog;
    }

    // ─── Startup ────────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        // Add startup banner for presentation
        AddLog("══════════════════════════════════════", LogLevel.Info);
        AddLog("   MeshChat - Offline P2P Messenger", LogLevel.Info);
        AddLog("══════════════════════════════════════", LogLevel.Info);
        AddLog("Initializing network services...", LogLevel.Info);

        _wifi.LocalName = DisplayName;
        _bluetooth.LocalName = DisplayName;
        _bluetooth.LocalId = _wifi.LocalId;

        // WiFi startup
        AddLog("[WiFi] Starting TCP server...", LogLevel.WiFi);
        await _wifi.StartAsync();
        IsWifiConnected = _wifi.IsRunning;

        if (IsWifiConnected)
        {
            AddLog($"[WiFi] Listening on port {_wifi.ListenPort}", LogLevel.WiFi);
            AddLog("[WiFi] mDNS service discovery active", LogLevel.WiFi);
        }

        // Bluetooth startup
        AddLog("[Bluetooth] Scanning for devices...", LogLevel.Bluetooth);
        await _bluetooth.StartAsync();
        IsBluetoothAvailable = _bluetooth.IsAvailable;

        if (IsBluetoothAvailable)
        {
            AddLog("[Bluetooth] Service running - ready to connect", LogLevel.Bluetooth);
        }
        else
        {
            AddLog("[Bluetooth] Not available on this device", LogLevel.Warning);
        }

        // Final status
        StatusText = $"Online · Port {_wifi.ListenPort}" +
                     (IsBluetoothAvailable ? " · Bluetooth ready" : " · No Bluetooth");

        AddLog("══════════════════════════════════════", LogLevel.Info);
        AddLog($"Ready! Your ID: {_wifi.LocalId[..8]}...", LogLevel.Success);
        AddLog("Waiting for peers to connect...", LogLevel.Info);
        AddLog("══════════════════════════════════════", LogLevel.Info);

        // Add help text for presentation
        AddLog("Tips:", LogLevel.Info);
        AddLog("- Peers auto-discover via mDNS/Bonjour", LogLevel.Info);
        AddLog("- Use manual connect for IP addresses", LogLevel.Info);
        AddLog("- Files transfer in 32KB chunks", LogLevel.Info);
    }

    public void Stop()
    {
        _wifi.Stop();
        _bluetooth.Stop();
    }

    // ─── Manual Connect ─────────────────────────────────────────────────────

    public async Task ConnectManualAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectIp)) return;
        if (!int.TryParse(ConnectPort, out int port)) port = 45678;
        await _wifi.ConnectToPeerAsync(ConnectIp.Trim(), port);
    }

    // ─── Messaging ──────────────────────────────────────────────────────────

    public async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(MessageInput)) return;
        if (SelectedPeer == null)
        {
            ShowToast("Select a peer to send message", true);
            return;
        }

        // Determine transport based on selected peer or broadcast
        string transport = SelectedPeer?.Transport == TransportType.Bluetooth ? "Bluetooth" : "WiFi";

        var msg = new ChatMessage
        {
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            Content = MessageInput,
            Type = MessageType.Text,
            Status = MessageStatus.Sent,
            Transport = transport
        };

        MessageInput = string.Empty;

        // Add to local history and always show in current view
        AddMessageToHistory(SelectedPeer?.Id ?? "broadcast", msg);
        Messages.Add(msg);

        var jsonPayload = JsonConvert.SerializeObject(msg);
        if (EncryptionEnabled)
        {
            jsonPayload = Encrypt(jsonPayload);
        }

        var packet = new NetworkPacket
        {
            Type = PacketType.Message,
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            TargetId = SelectedPeer?.Id,
            Payload = jsonPayload
        };

        if (SelectedPeer != null)
        {
            await SendToPeerViaTransportAsync(SelectedPeer.Id, packet);
            // Log the send event with visual indicator
            AddLog($"[SENT \u2191] To: {SelectedPeer.DisplayName} via {transport}", LogLevel.Sent);
        }
        else
        {
            await SendToAllViaTransportAsync(packet);
            // Log broadcast
            AddLog($"[SENT \u2191] Broadcast to all peers via {transport}", LogLevel.Sent);
        }
    }

    public async Task SendFileAsync(string filePath)
    {
        if (SelectedPeer == null) return;

        var fileInfo = new System.IO.FileInfo(filePath);
        var msg = new ChatMessage
        {
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            Type = MessageType.File,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            FilePath = filePath,
            Status = MessageStatus.Sending,
            Transport = SelectedPeer.Transport == TransportType.Bluetooth ? "Bluetooth" : "WiFi"
        };

        AddMessageToHistory(SelectedPeer.Id, msg);
        Messages.Add(msg);

        await foreach (var packet in _fileTransfer.ChunkFileAsync(
            filePath, SelectedPeer.Id, _wifi.LocalId, DisplayName))
        {
            await SendToPeerViaTransportAsync(SelectedPeer.Id, packet);
        }

        msg.Status = MessageStatus.Sent;
        OnPropertyChanged(nameof(Messages));
    }

    // ─── Event Handlers ─────────────────────────────────────────────────────

    private void OnPeerDiscovered(Peer peer)
    {
        _dispatcher.Invoke(() =>
        {
            // Deduplicate by ID — also guard against discovering ourselves
            if (peer.Id == _wifi.LocalId) return;

            var existing = Peers.FirstOrDefault(p => p.Id == peer.Id);
            if (existing == null)
            {
                Peers.Add(peer);
                AddLog($"Peer joined: {peer.DisplayName} via {peer.Transport} ({peer.HopDescription})");

                var sysMsg = new ChatMessage
                {
                    Type = MessageType.System,
                    Content = $"{peer.DisplayName} joined the network"
                };
                AddMessageToHistory(peer.Id, sysMsg);
            }
            else
            {
                existing.Status = PeerStatus.Online;
                existing.LastSeen = DateTime.Now;
                // Update transport if it changed (e.g. WiFi → Both)
                if (existing.Transport != peer.Transport)
                    existing.Transport = peer.Transport;
            }
        });
    }

    private void OnPeerLost(string peerId)
    {
        _dispatcher.Invoke(() =>
        {
            var peer = Peers.FirstOrDefault(p => p.Id == peerId);
            if (peer != null)
            {
                peer.Status = PeerStatus.Offline;
                AddLog($"Peer left: {peer.DisplayName}");

                var sysMsg = new ChatMessage
                {
                    Type = MessageType.System,
                    Content = $"{peer.DisplayName} left the network"
                };
                AddMessageToHistory(peerId, sysMsg);

                if (SelectedPeer?.Id == peerId)
                    Messages.Add(sysMsg);
            }
        });
    }

    private void OnPacketReceived(NetworkPacket packet)
    {
        _dispatcher.Invoke(() =>
        {
            switch (packet.Type)
            {
                case PacketType.Message:
                    HandleIncomingMessage(packet);
                    break;

                case PacketType.MessageAck:
                    HandleDeliveryAck(packet);
                    break;

                case PacketType.ReadReceipt:
                    HandleReadReceipt(packet);
                    break;

                case PacketType.FileChunk:
                    _fileTransfer.HandleChunk(packet);
                    break;

                case PacketType.Typing:
                    // Show typing indicator for the peer
                    if (packet.SenderId != _wifi.LocalId)
                    {
                        SetPeerTyping(packet.SenderId, packet.SenderName ?? "Peer");
                    }
                    break;

                case PacketType.Reaction:
                    HandleIncomingReaction(packet);
                    break;
            }
        });
    }

    private async void HandleIncomingMessage(NetworkPacket packet)
    {
        if (packet.Payload == null) return;

        var payload = packet.Payload;
        if (EncryptionEnabled)
        {
            payload = Decrypt(payload);
        }

        var msg = JsonConvert.DeserializeObject<ChatMessage>(payload);
        if (msg == null) return;

        msg.Status = MessageStatus.Delivered;

        var peerId = packet.SenderId;
        AddMessageToHistory(peerId, msg);

        // Show message if viewing this peer OR in broadcast view (no peer selected)
        bool isViewingThisPeer = SelectedPeer?.Id == peerId;
        bool isInBroadcastView = SelectedPeer == null;

        if (isViewingThisPeer || isInBroadcastView)
        {
            Messages.Add(msg);

            if (isViewingThisPeer)
            {
                // Send read receipt only when directly viewing this peer
                var ack = new NetworkPacket
                {
                    Type = PacketType.ReadReceipt,
                    SenderId = _wifi.LocalId,
                    SenderName = DisplayName,
                    Payload = msg.Id
                };
                await SendToPeerViaTransportAsync(peerId, ack);
            }
        }
        else
        {
            // Viewing a different peer — increment unread badge
            var peer = Peers.FirstOrDefault(p => p.Id == peerId);
            if (peer != null) peer.UnreadCount++;
        }

        // Send delivery receipt
        var deliveryAck = new NetworkPacket
        {
            Type = PacketType.MessageAck,
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            Payload = msg.Id
        };
        await SendToPeerViaTransportAsync(peerId, deliveryAck);

        // Log the receive event with visual indicator
        var senderName = packet.SenderName ?? "Unknown";
        var transport = msg.Transport ?? "WiFi";
        AddLog($"[RECEIVED \u2193] From: {senderName} via {transport}", LogLevel.Received);
    }

    private void HandleDeliveryAck(NetworkPacket packet)
    {
        var msgId = packet.Payload;
        var msg = Messages.FirstOrDefault(m => m.Id == msgId);
        if (msg != null) msg.Status = MessageStatus.Delivered;
    }

    private void HandleReadReceipt(NetworkPacket packet)
    {
        var msgId = packet.Payload;
        var msg = Messages.FirstOrDefault(m => m.Id == msgId);
        if (msg != null) msg.Status = MessageStatus.Read;
    }

    public async Task AddReactionAsync(string messageId, string emoji)
    {
        // Find the message
        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg == null) return;

        // Initialize reactions dict if null
        if (msg.Reactions == null)
            msg.Reactions = new Dictionary<string, List<string>>();

        // Toggle reaction: add if not present, remove if present
        bool isAdding = true;
        if (msg.Reactions.ContainsKey(emoji) && msg.Reactions[emoji].Contains(_wifi.LocalId))
        {
            // User already reacted with this emoji - remove it
            msg.Reactions[emoji].Remove(_wifi.LocalId);
            if (msg.Reactions[emoji].Count == 0)
                msg.Reactions.Remove(emoji);
            isAdding = false;
        }
        else
        {
            // Add the reaction
            if (!msg.Reactions.ContainsKey(emoji))
                msg.Reactions[emoji] = new List<string>();
            msg.Reactions[emoji].Add(_wifi.LocalId);
        }

        // Notify property changed to update UI
        OnPropertyChanged(nameof(Messages));

        // Send reaction to network
        var reactionPayload = new ReactionPayload
        {
            MessageId = messageId,
            Emoji = emoji,
            UserId = _wifi.LocalId,
            UserName = DisplayName,
            IsAdded = isAdding
        };

        var jsonPayload = JsonConvert.SerializeObject(reactionPayload);
        var packet = new NetworkPacket
        {
            Type = PacketType.Reaction,
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            TargetId = msg.SenderId,
            Payload = jsonPayload
        };

        // Send to the message sender (for directed messages) or broadcast for group
        if (!string.IsNullOrEmpty(msg.SenderId) && msg.SenderId != _wifi.LocalId)
        {
            await SendToPeerViaTransportAsync(msg.SenderId, packet);
        }
        else
        {
            await SendToAllViaTransportAsync(packet);
        }

        AddLog(isAdding ? $"[REACTION] You reacted {emoji} to a message" : $"[REACTION] You removed {emoji} reaction");
    }

    private void HandleIncomingReaction(NetworkPacket packet)
    {
        if (packet.Payload == null) return;

        try
        {
            var reaction = JsonConvert.DeserializeObject<ReactionPayload>(packet.Payload);
            if (reaction == null) return;

            // Find the message in history
            ChatMessage? msg = null;

            // Search through all message histories
            foreach (var history in _messageHistory.Values)
            {
                msg = history.FirstOrDefault(m => m.Id == reaction.MessageId);
                if (msg != null) break;
            }

            if (msg == null) return;

            // Initialize reactions dict if null
            if (msg.Reactions == null)
                msg.Reactions = new Dictionary<string, List<string>>();

            if (reaction.IsAdded)
            {
                // Add the reaction
                if (!msg.Reactions.ContainsKey(reaction.Emoji))
                    msg.Reactions[reaction.Emoji] = new List<string>();
                if (!msg.Reactions[reaction.Emoji].Contains(reaction.UserId))
                    msg.Reactions[reaction.Emoji].Add(reaction.UserId);
            }
            else
            {
                // Remove the reaction
                if (msg.Reactions.ContainsKey(reaction.Emoji))
                {
                    msg.Reactions[reaction.Emoji].Remove(reaction.UserId);
                    if (msg.Reactions[reaction.Emoji].Count == 0)
                        msg.Reactions.Remove(reaction.Emoji);
                }
            }

            // Notify property changed
            OnPropertyChanged(nameof(Messages));

            // Log the reaction
            var senderName = packet.SenderName ?? "Someone";
            AddLog($"[REACTION] {senderName} reacted {reaction.Emoji}");
        }
        catch (Exception ex)
        {
            AddLog($"Error handling reaction: {ex.Message}", LogLevel.Error);
        }
    }

    private void OnFileProgress(string msgId, double progress)
    {
        _dispatcher.Invoke(() =>
        {
            var msg = Messages.FirstOrDefault(m => m.Id == msgId);
            if (msg != null) msg.FileProgress = progress;
        });
    }

    private void OnFileReceived(string msgId, string savedPath)
    {
        _dispatcher.Invoke(() =>
        {
            var msg = Messages.FirstOrDefault(m => m.Id == msgId);
            if (msg != null)
            {
                msg.FilePath = savedPath;
                msg.Status = MessageStatus.Delivered;
                msg.FileProgress = 1.0;
            }
            AddLog($"File received and saved to {savedPath}");
        });
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private void AddMessageToHistory(string peerId, ChatMessage msg)
    {
        if (!_messageHistory.ContainsKey(peerId))
            _messageHistory[peerId] = [];
        _messageHistory[peerId].Add(msg);
        SaveMessagesAsync();
    }

    private async void LoadPersistedMessages()
    {
        try
        {
            var messages = await Task.Run(() => _messageStore.Load());
            foreach (var msg in messages)
            {
                // For simplicity, store all messages under "broadcast" key
                // A more sophisticated approach would track recipient per message
                if (!_messageHistory.ContainsKey("broadcast"))
                    _messageHistory["broadcast"] = [];
                _messageHistory["broadcast"].Add(msg);
            }
            Logger.Info($"Loaded {messages.Count} persisted messages");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load persisted messages", ex);
        }
    }

    private async void SaveMessagesAsync()
    {
        try
        {
            var allMessages = _messageHistory.Values.SelectMany(m => m).ToList();
            await Task.Run(() => _messageStore.Save(allMessages));
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save messages", ex);
        }
    }

    private void LoadMessagesForPeer(Peer? peer)
    {
        Messages.Clear();

        if (peer == null)
        {
            // Load broadcast history with date separators
            if (_messageHistory.TryGetValue("broadcast", out var broadcast))
                LoadMessagesWithDateSeparators(broadcast);
            return;
        }

        peer.UnreadCount = 0;

        if (_messageHistory.TryGetValue(peer.Id, out var history))
            LoadMessagesWithDateSeparators(history);

        UpdateUnreadCounts();
    }

    private void LoadMessagesWithDateSeparators(List<ChatMessage> history)
    {
        DateTime? lastDate = null;

        foreach (var msg in history)
        {
            // Add date separator if date changed
            if (msg.Type != MessageType.System && msg.Type != MessageType.DateSeparator)
            {
                if (lastDate == null || msg.Timestamp.Date != lastDate.Value.Date)
                {
                    var separatorText = GetDateSeparatorText(msg.Timestamp);
                    Messages.Add(new ChatMessage
                    {
                        Type = MessageType.DateSeparator,
                        Content = separatorText,
                        IsDateSeparator = true,
                        DateSeparatorText = separatorText
                    });
                    lastDate = msg.Timestamp.Date;
                }
            }
            Messages.Add(msg);
        }
    }

    private string GetDateSeparatorText(DateTime date)
    {
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);

        if (date.Date == today)
            return "Today";
        if (date.Date == yesterday)
            return "Yesterday";
        if (date.Year == today.Year)
            return date.ToString("MMMM d, yyyy");
        return date.ToString("MMMM d, yyyy");
    }

    private void UpdateUnreadCounts()
    {
        int total = Peers.Sum(p => p.UnreadCount);
        if (_messageHistory.TryGetValue("broadcast", out var broadcast))
            total += broadcast.Count(m => m.SenderId != _wifi.LocalId && m.Type != MessageType.System);
        TotalUnreadCount = total;
    }

    // ─── Typing Indicator Helpers ───────────────────────────────────────────

    public void SetPeerTyping(string peerId, string peerName)
    {
        _typingTimers[peerId] = DateTime.Now.AddMilliseconds(TypingIndicatorDurationMs);

        if (SelectedPeer?.Id == peerId)
        {
            IsPeerTyping = true;
            TypingPeerName = peerName;
        }

        // Schedule hide
        Task.Delay(TypingIndicatorDurationMs).ContinueWith(_ =>
        {
            _dispatcher.Invoke(() =>
            {
                if (_typingTimers.TryGetValue(peerId, out var expiry) && expiry <= DateTime.Now)
                {
                    _typingTimers.Remove(peerId);
                    if (SelectedPeer?.Id == peerId)
                    {
                        IsPeerTyping = false;
                        TypingPeerName = string.Empty;
                    }
                }
            });
        });
    }

    private void AddLog(string msg)
    {
        _dispatcher.Invoke(() =>
        {
            var entry = CreateLogEntry(msg);
            Logs.Insert(0, entry);
            if (Logs.Count > 200) Logs.RemoveAt(Logs.Count - 1);
        });
    }

    private void AddLog(string msg, LogLevel level)
    {
        _dispatcher.Invoke(() =>
        {
            var entry = CreateLogEntry(msg, level);
            Logs.Insert(0, entry);
            if (Logs.Count > 200) Logs.RemoveAt(Logs.Count - 1);
        });
    }

    private LogEntry CreateLogEntry(string msg)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = new LogEntry
        {
            Timestamp = timestamp,
            FullText = $"[{timestamp}] {msg}"
        };

        // Determine log level from message content
        var level = LogLevel.Info;
        if (msg.StartsWith("[WiFi]")) level = LogLevel.WiFi;
        else if (msg.StartsWith("[Bluetooth]")) level = LogLevel.Bluetooth;
        else if (msg.StartsWith("[FileTransfer]")) level = LogLevel.FileTransfer;
        else if (msg.Contains("joined")) level = LogLevel.Peer;
        else if (msg.Contains("left")) level = LogLevel.Peer;
        else if (msg.Contains("Error") || msg.Contains("failed") || msg.Contains("Failed")) level = LogLevel.Error;
        else if (msg.Contains("warning") || msg.Contains("Warning")) level = LogLevel.Warning;
        else if (msg.StartsWith("[Started]") || msg.Contains("saved to") || msg.Contains("ready")) level = LogLevel.Success;
        else if (msg.Contains("[SENT")) level = LogLevel.Sent;
        else if (msg.Contains("[RECEIVED")) level = LogLevel.Received;

        var levelColor = GetLevelColor(level);

        return ApplyLogEntryStyles(entry, msg, levelColor);
    }

    private LogEntry CreateLogEntry(string msg, LogLevel level)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = new LogEntry
        {
            Timestamp = timestamp,
            FullText = $"[{timestamp}] {msg}"
        };

        var levelColor = GetLevelColor(level);
        return ApplyLogEntryStyles(entry, msg, levelColor);
    }

    private LogEntry ApplyLogEntryStyles(LogEntry entry, string msg, Color levelColor)
    {
        if (msg.StartsWith("["))
        {
            var bracketEnd = msg.IndexOf(']');
            if (bracketEnd > 0)
            {
                entry.Tag = msg[1..bracketEnd];
                entry.TagColor = levelColor;
                entry.Message = msg[(bracketEnd + 1)..].TrimStart();
            }
            else
            {
                entry.Message = msg;
            }
        }
        else
        {
            entry.Message = msg;
        }

        return entry;
    }

    private static Color GetLevelColor(LogLevel level) => level switch
    {
        LogLevel.WiFi => ColorFromHex("#0A84FF"),        // iOS Blue
        LogLevel.Bluetooth => ColorFromHex("#BF5AF2"),   // iOS Purple
        LogLevel.FileTransfer => ColorFromHex("#FFD60A"), // iOS Yellow
        LogLevel.Peer => ColorFromHex("#30D158"),        // iOS Green
        LogLevel.Success => ColorFromHex("#30D158"),     // iOS Green
        LogLevel.Warning => ColorFromHex("#FF9F0A"),     // iOS Orange
        LogLevel.Error => ColorFromHex("#FF453A"),       // iOS Red
        LogLevel.Sent => ColorFromHex("#64D2FF"),        // iOS Cyan - outgoing
        LogLevel.Received => ColorFromHex("#BF5AF2"),    // iOS Purple - incoming
        _ => ColorFromHex("#636366")                      // iOS Gray
    };

    private static Color ColorFromHex(string hex)
    {
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    // ─── Simple Encryption (XOR with shared key) ─────────────────────────────
    // Note: For production, use proper AES-256 with key exchange
    private const string EncryptionKey = "MeshChatSecretKey2024!";

    private string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText) || !EncryptionEnabled) return plainText;

        var keyBytes = System.Text.Encoding.UTF8.GetBytes(EncryptionKey);
        var textBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        var result = new byte[textBytes.Length];

        for (int i = 0; i < textBytes.Length; i++)
        {
            result[i] = (byte)(textBytes[i] ^ keyBytes[i % keyBytes.Length]);
        }

        return Convert.ToBase64String(result);
    }

    private string Decrypt(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText) || !EncryptionEnabled) return encryptedText;

        try
        {
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(EncryptionKey);
            var encryptedBytes = Convert.FromBase64String(encryptedText);
            var result = new byte[encryptedBytes.Length];

            for (int i = 0; i < encryptedBytes.Length; i++)
            {
                result[i] = (byte)(encryptedBytes[i] ^ keyBytes[i % keyBytes.Length]);
            }

            return System.Text.Encoding.UTF8.GetString(result);
        }
        catch
        {
            return encryptedText; // Return as-is if decryption fails (not encrypted)
        }
    }

    // ─── INotifyPropertyChanged ─────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
