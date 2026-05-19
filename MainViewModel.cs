using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshChat.Models;
using MeshChat.Services;
using MeshChat.Services.Crypto;
using Newtonsoft.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LogLevel = MeshChat.Models.LogLevel;

namespace MeshChat.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const string BroadcastConversationId = "broadcast";

    // MainViewModel talks to transports through INetworkService so WiFi and
    // Bluetooth can share send/receive/discovery handling without concrete coupling.
    private readonly INetworkService _wifi;
    private readonly INetworkService _bluetooth;
    private readonly IFileTransferService _fileTransfer;
    private readonly Dispatcher _dispatcher;
    private readonly MessageStore _messageStore;
    private readonly ILogger<MainViewModel> _logger;
    private readonly LocalIdentityStore _localIdentityStore;
    private readonly PeerTrustStore _peerTrustStore;
    private readonly bool _loadPeerTrustStoreOnInitialize;
    private LocalIdentity? _localIdentity;
    private SessionKeyManager? _sessionKeyManager;
    private bool _ownsLocalIdentity;
    private bool _peerTrustStoreLoaded;

    // ─── Observable State ───────────────────────────────────────────────────

    public ObservableCollection<Peer> Peers { get; } = [];
    public BulkObservableCollection<ChatMessage> Messages { get; } = [];
    public BulkObservableCollection<LogEntry> Logs { get; } = [];
    public ICollectionView FilteredMessages { get; }
    public ICollectionView FilteredLogs { get; }

    private Peer? _selectedPeer;
    public Peer? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (!SetProperty(ref _selectedPeer, value))
                return;

            // Use dispatcher to ensure thread safety
            _dispatcher.Invoke(() =>
            {
                LoadMessagesForPeer(value);
                UpdateUnreadCounts();
            });
        }
    }

    [ObservableProperty]
    private string _messageInput = string.Empty;

    private DateTime _lastTypingSent = DateTime.MinValue;
    private const int TypingSendIntervalMs = 2000; // Send typing every 2 seconds

    partial void OnMessageInputChanged(string value)
    {
        // Send typing indicator when user starts typing
        if (!string.IsNullOrEmpty(value) && SelectedPeer != null)
        {
            var now = DateTime.Now;
            if ((now - _lastTypingSent).TotalMilliseconds > TypingSendIntervalMs)
            {
                _lastTypingSent = now;
                _ = SendTypingIndicatorAsync(_lifetimeCts.Token);
            }
        }
    }

    private async Task SendTypingIndicatorAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedPeer == null) return;

        var packet = new NetworkPacket
        {
            Type = PacketType.Typing,
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            TargetId = SelectedPeer.Id
        };

        await SendToPeerViaTransportAsync(SelectedPeer.Id, packet, cancellationToken);
    }

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    partial void OnSearchQueryChanged(string value)
    {
        // CollectionView filtering refreshes the existing virtualized view instead
        // of rebuilding LINQ enumerables during scrolling and auto-scroll checks.
        FilteredMessages.Refresh();
    }

    // Log filter options
    public string[] LogFilterOptions { get; } = { "All", "WiFi", "Bluetooth", "Errors", "Messages" };

    [ObservableProperty]
    private string _logFilter = "All";

    partial void OnLogFilterChanged(string value)
    {
        FilteredLogs.Refresh();
    }

    private bool FilterMessage(object item)
    {
        if (item is not ChatMessage message)
            return false;

        if (string.IsNullOrWhiteSpace(SearchQuery))
            return true;

        var query = SearchQuery.Trim();
        return message.Content.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (message.SenderName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (message.FileName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private bool FilterLogEntry(object item)
    {
        if (item is not LogEntry log)
            return false;

        return LogFilter == "All" || LogFilter switch
        {
            "WiFi" => log.Tag.StartsWith("WiFi"),
            "Bluetooth" => log.Tag.StartsWith("Bluetooth"),
            "Errors" => log.Tag.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                        log.Message.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                        log.Message.Contains("failed", StringComparison.OrdinalIgnoreCase),
            "Messages" => log.Tag.Contains("SENT") || log.Tag.Contains("RECEIVED"),
            _ => true
        };
    }

    [ObservableProperty]
    private string _displayName = string.Empty;

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToastVisible))]
    private string _toastMessage = string.Empty;

    public bool ToastVisible => !string.IsNullOrEmpty(ToastMessage);

    [ObservableProperty]
    private bool _toastIsError;

    public void ShowToast(string message, bool isError = false)
    {
        ToastMessage = message;
        ToastIsError = isError;
        _ = HideToastAfterDelay();
    }

    private async Task HideToastAfterDelay()
    {
        try
        {
            await Task.Delay(3000, _lifetimeCts.Token);
            ToastMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [ObservableProperty]
    private string _statusText = "Not connected";

    [ObservableProperty]
    private bool _isWifiConnected;

    [ObservableProperty]
    private bool _isBluetoothAvailable;

    [ObservableProperty]
    private string _connectIp = string.Empty;

    [ObservableProperty]
    private string _connectPort = "45678";

    // ─── Unread Badge for Title ─────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleWithUnread))]
    private int _totalUnreadCount;

    public string TitleWithUnread => TotalUnreadCount > 0
        ? $"MeshChat — Offline P2P Messenger ({TotalUnreadCount} unread)"
        : "MeshChat — Offline P2P Messenger";

    // ─── Typing Indicator ───────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isPeerTyping;

    [ObservableProperty]
    private string _typingPeerName = string.Empty;

    private readonly Dictionary<string, DateTime> _typingTimers = [];
    private const int TypingIndicatorDurationMs = 3000;

    // ─── UI State ───────────────────────────────────────────────────────────
    [ObservableProperty]
    private bool _isNetworkLogVisible = true; // Default to visible for debugging

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
    private readonly Dictionary<string, Peer> _peerById = [];
    private readonly Dictionary<string, ChatMessage> _messageById = [];
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _reactionUsersByMessage = [];
    private readonly Dictionary<string, CancellationTokenSource> _ackRetryCancellations = [];
    private readonly HashSet<string> _keyExchangeInFlight = [];
    private readonly HashSet<string> _queuedSendInFlight = [];
    private readonly object _ackRetryLock = new();
    private readonly object _queuedSendLock = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _logBatchLock = new();
    private readonly List<LogEntry> _pendingLogEntries = [];
    private int _saveVersion;
    private bool _persistedMessagesLoaded;
    private bool _logFlushQueued;
    private const int SaveDebounceMs = 500;
    private const int MaxVisibleHistoryMessages = 500;
    private const int MaxVisibleLogs = 200;
    private const int AckRetryTimeoutMs = 250;
    private const int MaxAckRetries = 2;
    private const int MaxQueuedSendAttempts = 3;

    // ─── Transport Routing Helpers ──────────────────────────────────────────

    private async Task<bool> SendToPeerViaTransportAsync(string peerId, NetworkPacket packet, CancellationToken cancellationToken = default)
    {
        if (!_peerById.TryGetValue(peerId, out var peer))
        {
            // Default to WiFi if peer not found
            await _wifi.SendToPeerAsync(peerId, packet, cancellationToken);
            return true;
        }

        if (peer.Status == PeerStatus.Offline)
            return false;

        var sendPeerId = peerId;

        if (!peer.IsDirectlyConnected)
        {
            if (packet.Type != PacketType.Message ||
                string.IsNullOrWhiteSpace(peer.RelayPeerId) ||
                !_peerById.TryGetValue(peer.RelayPeerId, out var relayPeer) ||
                !relayPeer.IsDirectlyConnected ||
                relayPeer.Status == PeerStatus.Offline)
            {
                return false;
            }

            sendPeerId = relayPeer.Id;
            peer = relayPeer;
        }

        // Use appropriate transport based on peer's connection type
        if (peer.Transport == TransportType.Bluetooth)
            await _bluetooth.SendToPeerAsync(sendPeerId, packet, cancellationToken);
        else
            await _wifi.SendToPeerAsync(sendPeerId, packet, cancellationToken);

        return true;
    }

    private async Task SendToAllViaTransportAsync(NetworkPacket packet, CancellationToken cancellationToken = default)
    {
        // Broadcast via both transports to ensure all peers receive it
        await _wifi.SendToAllAsync(packet, cancellationToken);
        await _bluetooth.SendToAllAsync(packet, cancellationToken);
    }

    // ─── Constructor ────────────────────────────────────────────────────────

    private void TrackAckRetry(string peerId, ChatMessage msg, bool isEncrypted, string peerDisplayName)
    {
        if (_lifetimeCts.IsCancellationRequested)
            return;

        var retryCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);

        lock (_ackRetryLock)
        {
            CancelAckRetryLocked(msg.Id);
            _ackRetryCancellations[msg.Id] = retryCts;
        }

        _ = RetryUntilAckAsync(peerId, msg, isEncrypted, peerDisplayName, retryCts.Token);
    }

    private async Task RetryUntilAckAsync(
        string peerId,
        ChatMessage msg,
        bool isEncrypted,
        string peerDisplayName,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 1; attempt <= MaxAckRetries; attempt++)
            {
                await Task.Delay(AckRetryTimeoutMs, cancellationToken);

                if (!ShouldRetryForAck(msg.Id))
                    return;

                var packet = CreateRetryPacket(peerId, msg, isEncrypted);
                if (!await SendToPeerViaTransportAsync(peerId, packet, cancellationToken))
                {
                    QueueMessageAfterAckRetries(msg.Id, $"No route to {peerDisplayName}");
                    return;
                }

                AddLog($"[RETRY {attempt}/{MaxAckRetries}] To: {peerDisplayName}", LogLevel.Warning);
            }

            await Task.Delay(AckRetryTimeoutMs, cancellationToken);

            if (ShouldRetryForAck(msg.Id))
                QueueMessageAfterAckRetries(msg.Id, $"No delivery acknowledgement from {peerDisplayName}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ACK retry failed for message {MessageId}", msg.Id);
            QueueMessageAfterAckRetries(msg.Id, $"Retry failed for {peerDisplayName}: {ex.Message}");
        }
        finally
        {
            RemoveAckRetry(msg.Id);
        }
    }

    private NetworkPacket CreateRetryPacket(string peerId, ChatMessage msg, bool isEncrypted)
    {
        var payload = JsonConvert.SerializeObject(msg);
        if (isEncrypted)
            payload = Encrypt(payload);

        return new NetworkPacket
        {
            Type = PacketType.Message,
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            TargetId = peerId,
            Payload = payload,
            IsEncrypted = isEncrypted,
            CryptoVersion = isEncrypted ? CryptoVersionAesGcm1 : null
        };
    }

    private bool ShouldRetryForAck(string messageId)
    {
        if (!_messageById.TryGetValue(messageId, out var current))
            return false;

        return current.Status is MessageStatus.Sending or MessageStatus.Sent;
    }

    private void QueueMessageAfterAckRetries(string messageId, string reason)
    {
        InvokeOnUiThread(() =>
        {
            if (!_messageById.TryGetValue(messageId, out var current) || !ShouldRetryForAck(messageId))
                return;

            QueueOutgoingTextMessage(current, reason);
        });
    }

    private ChatMessage QueueOutgoingTextMessage(ChatMessage msg, string reason)
    {
        var queued = msg with
        {
            Status = MessageStatus.Failed,
            QueuedAt = msg.QueuedAt ?? DateTime.UtcNow
        };

        queued = ReplaceMessage(msg, queued);
        ShowToast(reason, true);
        AddLog($"[QUEUED] {reason}", LogLevel.Warning);
        return queued;
    }

    private bool IsQueuedOutgoingTextMessage(ChatMessage msg)
        => msg.Type == MessageType.Text &&
           msg.QueuedAt != null &&
           msg.Status is not (MessageStatus.Delivered or MessageStatus.Read) &&
           msg.SenderId == _wifi.LocalId &&
           !string.IsNullOrWhiteSpace(msg.TargetPeerId);

    private void TryResendQueuedTextMessages()
    {
        var queuedMessages = _messageHistory.Values
            .SelectMany(history => history)
            .Where(IsQueuedOutgoingTextMessage)
            .ToList();

        foreach (var msg in queuedMessages)
            _ = TryResendQueuedTextMessageAsync(msg.Id, _lifetimeCts.Token);
    }

    private async Task TryResendQueuedTextMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        lock (_queuedSendLock)
        {
            if (!_queuedSendInFlight.Add(messageId))
                return;
        }

        try
        {
            ChatMessage msg;
            string peerId;
            string peerDisplayName;
            bool isEncrypted;

            if (!_messageById.TryGetValue(messageId, out msg!) || !IsQueuedOutgoingTextMessage(msg))
                return;

            if (msg.QueueRetryCount >= MaxQueuedSendAttempts)
            {
                AddLog($"[QUEUED] Retry limit reached for message {messageId}", LogLevel.Warning);
                return;
            }

            peerId = msg.TargetPeerId!;
            if (!HasAvailableMessageRoute(peerId))
                return;

            peerDisplayName = _peerById.TryGetValue(peerId, out var peer)
                ? peer.DisplayName
                : peerId;
            isEncrypted = EncryptionEnabled;

            var attempting = ReplaceMessage(msg, msg with
            {
                Status = MessageStatus.Sending,
                QueueRetryCount = msg.QueueRetryCount + 1,
                LastQueueAttemptAt = DateTime.UtcNow
            });

            var packet = CreateRetryPacket(peerId, attempting, isEncrypted);

            try
            {
                if (await SendToPeerViaTransportAsync(peerId, packet, cancellationToken))
                {
                    var sent = ReplaceMessage(attempting, attempting with
                    {
                        Status = MessageStatus.Sent,
                        QueuedAt = null
                    });
                    TrackAckRetry(peerId, sent, isEncrypted, peerDisplayName);
                    AddLog($"[RESENT] To: {peerDisplayName}", LogLevel.Sent);
                    return;
                }

                QueueOutgoingTextMessage(attempting, $"No route to {peerDisplayName}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                QueueOutgoingTextMessage(attempting, $"Failed to send to {peerDisplayName}: {ex.Message}");
                _logger.LogError(ex, "Failed to resend queued message {MessageId} to {PeerId}", attempting.Id, peerId);
            }
        }
        finally
        {
            lock (_queuedSendLock)
            {
                _queuedSendInFlight.Remove(messageId);
            }
        }
    }

    private bool HasAvailableMessageRoute(string peerId)
    {
        if (!_peerById.TryGetValue(peerId, out var peer) || peer.Status == PeerStatus.Offline)
            return false;

        if (peer.IsDirectlyConnected)
            return true;

        return !string.IsNullOrWhiteSpace(peer.RelayPeerId) &&
               _peerById.TryGetValue(peer.RelayPeerId, out var relayPeer) &&
               relayPeer.IsDirectlyConnected &&
               relayPeer.Status != PeerStatus.Offline;
    }

    private void InvokeOnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess() || Application.Current == null)
            action();
        else
            _dispatcher.Invoke(action);
    }

    private void CancelAckRetry(string messageId)
    {
        lock (_ackRetryLock)
        {
            CancelAckRetryLocked(messageId);
        }
    }

    private void CancelAckRetryLocked(string messageId)
    {
        if (_ackRetryCancellations.Remove(messageId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void CancelAllAckRetries()
    {
        lock (_ackRetryLock)
        {
            foreach (var cts in _ackRetryCancellations.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _ackRetryCancellations.Clear();
        }
    }

    private void RemoveAckRetry(string messageId)
    {
        lock (_ackRetryLock)
        {
            if (_ackRetryCancellations.TryGetValue(messageId, out var cts) &&
                cts.IsCancellationRequested)
            {
                _ackRetryCancellations.Remove(messageId);
            }
        }
    }

    public MainViewModel()
        : this(
            new WiFiService(),
            new BluetoothService(),
            new FileTransferService(),
            new MessageStore(),
            NullLogger<MainViewModel>.Instance)
    {
    }

    public MainViewModel(INetworkService wifi, INetworkService bluetooth)
        : this(
            wifi,
            bluetooth,
            new FileTransferService(),
            new MessageStore(),
            NullLogger<MainViewModel>.Instance)
    {
    }

    public MainViewModel(
        INetworkService wifi,
        INetworkService bluetooth,
        IFileTransferService fileTransfer,
        MessageStore messageStore,
        ILogger<MainViewModel>? logger = null,
        LocalIdentityStore? localIdentityStore = null,
        PeerTrustStore? peerTrustStore = null,
        LocalIdentity? localIdentity = null,
        SessionKeyManager? sessionKeyManager = null)
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _wifi = wifi;
        _bluetooth = bluetooth;
        _fileTransfer = fileTransfer;
        _messageStore = messageStore;
        _logger = logger ?? NullLogger<MainViewModel>.Instance;
        _localIdentityStore = localIdentityStore ?? new LocalIdentityStore();
        _peerTrustStore = peerTrustStore ?? new PeerTrustStore();
        _loadPeerTrustStoreOnInitialize = peerTrustStore == null;
        _localIdentity = localIdentity;
        _sessionKeyManager = sessionKeyManager;

        FilteredMessages = CollectionViewSource.GetDefaultView(Messages);
        FilteredMessages.Filter = FilterMessage;
        FilteredLogs = CollectionViewSource.GetDefaultView(Logs);
        FilteredLogs.Filter = FilterLogEntry;

        DisplayName = Environment.MachineName;

        // The interface exposes the same discovery, receive, and log events for
        // every transport, so both services can be wired to one set of handlers.
        _wifi.PeerDiscovered += OnPeerDiscovered;
        _wifi.PeerLost += OnPeerLost;
        _wifi.PacketReceived += OnPacketReceived;
        _wifi.LogMessage += AddLog;

        _bluetooth.PeerDiscovered += OnPeerDiscovered;
        _bluetooth.PeerLost += OnPeerLost;
        _bluetooth.PacketReceived += OnPacketReceived;
        _bluetooth.LogMessage += AddLog;

        _fileTransfer.FileStarted += OnFileStarted;
        _fileTransfer.ProgressUpdated += OnFileProgress;
        _fileTransfer.FileReceived += OnFileReceived;
        _fileTransfer.LogMessage += AddLog;

        // Range collection notifications collapse hundreds of message/log updates
        // into one view refresh, keeping the dispatcher free for input and animations.
    }

    // ─── Startup ────────────────────────────────────────────────────────────

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
        cancellationToken = linkedCts.Token;

        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_persistedMessagesLoaded)
                return;

            EnsureSessionKeyManager();
            await LoadPersistedMessagesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _persistedMessagesLoaded = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
        cancellationToken = linkedCts.Token;

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
        await _wifi.StartAsync(cancellationToken);
        IsWifiConnected = _wifi.IsRunning;

        if (IsWifiConnected)
        {
            AddLog($"[WiFi] Listening on port {_wifi.ListenPort}", LogLevel.WiFi);
            AddLog("[WiFi] mDNS service discovery active", LogLevel.WiFi);
        }

        // Bluetooth startup
        AddLog("[Bluetooth] Scanning for devices...", LogLevel.Bluetooth);
        await _bluetooth.StartAsync(cancellationToken);
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
        TryResendQueuedTextMessages();
    }

    public void Stop()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    public async Task StopAsync()
    {
        await SaveMessagesImmediatelyAsync(CancellationToken.None);

        _lifetimeCts.Cancel();
        CancelAllAckRetries();
        _sessionKeyManager?.Dispose();
        _sessionKeyManager = null;
        if (_ownsLocalIdentity)
        {
            _localIdentity?.Dispose();
            _localIdentity = null;
            _ownsLocalIdentity = false;
        }
        await _wifi.StopAsync();
        await _bluetooth.StopAsync();
    }

    // ─── Manual Connect ─────────────────────────────────────────────────────

    public async Task ConnectManualAsync(CancellationToken cancellationToken = default)
    {
        var host = (ConnectIp ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            ShowToast("Enter a host or IP address", true);
            return;
        }

        if (!int.TryParse(ConnectPort, out int port) || port < 1 || port > 65535)
        {
            ShowToast("Enter a valid port number (1-65535)", true);
            return;
        }

        await _wifi.ConnectToPeerAsync(host, port, cancellationToken);
    }

    // ─── Messaging ──────────────────────────────────────────────────────────

    public async Task SendMessageAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(MessageInput)) return;
        if (SelectedPeer == null)
        {
            ShowToast("Select a peer to send message", true);
            return;
        }
        var selectedPeer = SelectedPeer;

        // Determine transport based on selected peer or broadcast
        string transport = selectedPeer.Transport == TransportType.Bluetooth ? "Bluetooth" : "WiFi";

        var msg = new ChatMessage
        {
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            Content = MessageInput,
            Type = MessageType.Text,
            Status = MessageStatus.Sending,
            ConversationId = selectedPeer.Id,
            TargetPeerId = selectedPeer.Id,
            Transport = transport
        };

        MessageInput = string.Empty;

        // Add to local history and always show in current view
        msg = AddMessageToHistory(selectedPeer.Id, msg);
        Messages.Add(msg);

        var jsonPayload = JsonConvert.SerializeObject(msg);
        var isEncrypted = false;
        if (EncryptionEnabled)
        {
            jsonPayload = Encrypt(jsonPayload);
            isEncrypted = true;
        }

        var packet = new NetworkPacket
        {
            Type = PacketType.Message,
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            TargetId = selectedPeer.Id,
            Payload = jsonPayload,
            IsEncrypted = isEncrypted,
            CryptoVersion = isEncrypted ? CryptoVersionAesGcm1 : null
        };

        try
        {
            if (await SendToPeerViaTransportAsync(selectedPeer.Id, packet, cancellationToken))
            {
                msg = ReplaceMessage(msg, msg with { Status = MessageStatus.Sent });
                TrackAckRetry(selectedPeer.Id, msg, isEncrypted, selectedPeer.DisplayName);

                // Log the send event with visual indicator
                AddLog($"[SENT \u2191] To: {selectedPeer.DisplayName} via {transport}", LogLevel.Sent);
            }
            else
            {
                msg = QueueOutgoingTextMessage(msg, $"No route to {selectedPeer.DisplayName}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            msg = QueueOutgoingTextMessage(msg, $"Failed to send to {selectedPeer.DisplayName}: {ex.Message}");
            _logger.LogError(ex, "Failed to send message {MessageId} to {PeerId}", msg.Id, selectedPeer.Id);
        }
    }

    public async Task SendFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (SelectedPeer == null) return;
        if (!SelectedPeer.IsDirectlyConnected)
        {
            ShowToast("File transfer requires a direct peer connection", true);
            return;
        }

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
            ConversationId = SelectedPeer.Id,
            TargetPeerId = SelectedPeer.Id,
            Transport = SelectedPeer.Transport == TransportType.Bluetooth ? "Bluetooth" : "WiFi"
        };

        msg = AddMessageToHistory(SelectedPeer.Id, msg);
        Messages.Add(msg);

        await foreach (var packet in _fileTransfer.ChunkFileAsync(
            filePath, SelectedPeer.Id, _wifi.LocalId, DisplayName, cancellationToken, msg.Id))
        {
            await SendToPeerViaTransportAsync(SelectedPeer.Id, packet, cancellationToken);
        }

        msg = ReplaceMessage(msg, msg with { Status = MessageStatus.Sent });
        OnPropertyChanged(nameof(Messages));
    }

    // ─── Event Handlers ─────────────────────────────────────────────────────

    private void OnPeerDiscovered(Peer peer)
    {
        _dispatcher.Invoke(() =>
        {
            // Deduplicate by ID — also guard against discovering ourselves
            if (peer.Id == _wifi.LocalId) return;

            if (!_peerById.TryGetValue(peer.Id, out var existing))
            {
                _peerById[peer.Id] = peer;
                Peers.Add(peer);
                AddLog($"Peer joined: {peer.DisplayName} via {peer.Transport} ({peer.HopDescription})");

                var sysMsg = new ChatMessage
                {
                    Type = MessageType.System,
                    ConversationId = peer.Id,
                    Content = $"{peer.DisplayName} joined the network"
                };
                AddMessageToHistory(peer.Id, sysMsg);
            }
            else
            {
                var shouldPromoteToDirect = peer.IsDirectlyConnected && !existing.IsDirectlyConnected;
                existing = ReplacePeer(existing, shouldPromoteToDirect
                    ? peer with { UnreadCount = existing.UnreadCount }
                    : existing with
                    {
                        Status = PeerStatus.Online,
                        LastSeen = DateTime.Now
                    });

                if (shouldPromoteToDirect)
                {
                    SendPeerListToPeer(peer);
                    _ = StartKeyExchangeIfNeededAsync(peer, _lifetimeCts.Token);
                    TryResendQueuedTextMessages();
                    return;
                }
                // Update transport if it changed (e.g. WiFi → Both)
                if (existing.Transport != peer.Transport)
                    ReplacePeer(existing, existing with { Transport = peer.Transport });
            }

            if (peer.IsDirectlyConnected)
            {
                SendPeerListToPeer(peer);
                _ = StartKeyExchangeIfNeededAsync(peer, _lifetimeCts.Token);
            }

            TryResendQueuedTextMessages();
        });
    }

    private void OnPeerLost(string peerId)
    {
        _dispatcher.Invoke(() =>
        {
            MarkPeerOffline(peerId);
        });
    }

    private void MarkPeerOffline(string peerId)
    {
        if (_peerById.TryGetValue(peerId, out var peer))
        {
            ReplacePeer(peer, peer with { Status = PeerStatus.Offline });
            AddLog($"Peer left: {peer.DisplayName}");

            var sysMsg = new ChatMessage
            {
                Type = MessageType.System,
                ConversationId = peerId,
                Content = $"{peer.DisplayName} left the network"
            };
            sysMsg = AddMessageToHistory(peerId, sysMsg);

            if (SelectedPeer?.Id == peerId)
                Messages.Add(sysMsg);
        }
    }

    private void OnPacketReceived(NetworkPacket packet)
    {
        _dispatcher.Invoke(() =>
        {
            switch (packet.Type)
            {
                case PacketType.Message:
                    _ = HandleIncomingMessageAsync(packet, _lifetimeCts.Token);
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

                case PacketType.FileComplete:
                    // FileTransferService completes incoming files by received chunk count.
                    AddLog($"Ignored FileComplete from {packet.SenderName ?? packet.SenderId}: chunks drive completion", LogLevel.FileTransfer);
                    break;

                case PacketType.PeerList:
                    MergePeerList(packet);
                    break;

                case PacketType.Goodbye:
                    if (string.IsNullOrWhiteSpace(packet.SenderId))
                    {
                        AddLog("Ignored Goodbye packet without sender id", LogLevel.Warning);
                        break;
                    }

                    MarkPeerOffline(packet.SenderId);
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

                case PacketType.KeyExchangeInit:
                    _ = HandleKeyExchangeInitAsync(packet, _lifetimeCts.Token);
                    break;

                case PacketType.KeyExchangeResponse:
                    _ = HandleKeyExchangeResponseAsync(packet, _lifetimeCts.Token);
                    break;

                case PacketType.KeyExchangeConfirm:
                    HandleKeyExchangeConfirm(packet);
                    break;

                case PacketType.Hello:
                case PacketType.HelloAck:
                    // Transport services handle handshake packets before application delivery.
                    break;

                default:
                    AddLog($"Ignored unsupported packet type: {packet.Type}", LogLevel.Warning);
                    break;
            }
        });
    }

    private void SendPeerListToPeer(Peer peer)
    {
        if (!peer.IsDirectlyConnected)
            return;

        var knownPeers = PeerTopology.CreateDirectPeerList(Peers, _wifi.LocalId, peer.Id);
        if (knownPeers.Length == 0)
            return;

        var packet = new NetworkPacket
        {
            Type = PacketType.PeerList,
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            TargetId = peer.Id,
            KnownPeers = knownPeers
        };

        _ = SendToPeerViaTransportAsync(peer.Id, packet, _lifetimeCts.Token);
    }

    private async Task StartKeyExchangeIfNeededAsync(Peer peer, CancellationToken cancellationToken)
    {
        if (!peer.IsDirectlyConnected || peer.Id == _wifi.LocalId)
            return;

        if (IsPeerBlocked(peer.Id))
        {
            AddLog($"[KeyExchange] Rejected key exchange with blocked peer {peer.DisplayName}", LogLevel.Warning);
            return;
        }

        var manager = EnsureSessionKeyManager();
        if (manager.GetActiveSession(peer.Id) != null)
            return;

        lock (_keyExchangeInFlight)
        {
            if (!_keyExchangeInFlight.Add(peer.Id))
                return;
        }

        try
        {
            var payload = manager.CreateOutboundKeyExchangeInit(peer.Id);
            var packet = CreateKeyExchangePacket(PacketType.KeyExchangeInit, peer.Id, payload);
            await SendToPeerViaTransportAsync(peer.Id, packet, cancellationToken);
            AddLog($"[KeyExchange] Started with {peer.DisplayName}", LogLevel.Info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_keyExchangeInFlight)
            {
                _keyExchangeInFlight.Remove(peer.Id);
            }

            AddLog($"[KeyExchange] Failed to start with {peer.DisplayName}: {ex.Message}", LogLevel.Warning);
            _logger.LogWarning(ex, "Key exchange start failed for {PeerId}", peer.Id);
        }
    }

    private async Task HandleKeyExchangeInitAsync(NetworkPacket packet, CancellationToken cancellationToken)
    {
        if (!TryDeserializeKeyExchangePayload<KeyExchangeInitPayload>(packet, out var payload))
            return;

        var peerName = GetPeerDisplayName(packet);
        try
        {
            if (!TryPrepareInboundKeyExchange(packet, payload, peerName))
                return;

            var response = EnsureSessionKeyManager().ProcessInboundKeyExchangeInit(payload);
            var responsePacket = CreateKeyExchangePacket(PacketType.KeyExchangeResponse, payload.SenderPeerId, response);
            await SendToPeerViaTransportAsync(payload.SenderPeerId, responsePacket, cancellationToken);
            AddLog($"[KeyExchange] Started by {peerName}; response sent", LogLevel.Info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddLog($"[KeyExchange] Rejected init from {peerName}: {ex.Message}", LogLevel.Warning);
            _logger.LogWarning(ex, "Key exchange init rejected from {PeerId}", packet.SenderId);
        }
    }

    private async Task HandleKeyExchangeResponseAsync(NetworkPacket packet, CancellationToken cancellationToken)
    {
        if (!TryDeserializeKeyExchangePayload<KeyExchangeResponsePayload>(packet, out var payload))
            return;

        var peerName = GetPeerDisplayName(packet);
        try
        {
            if (!TryPrepareInboundKeyExchange(packet, payload, peerName))
                return;

            var confirm = EnsureSessionKeyManager().ProcessInboundKeyExchangeResponse(payload);
            var confirmPacket = CreateKeyExchangePacket(PacketType.KeyExchangeConfirm, payload.SenderPeerId, confirm);
            await SendToPeerViaTransportAsync(payload.SenderPeerId, confirmPacket, cancellationToken);
            lock (_keyExchangeInFlight)
            {
                _keyExchangeInFlight.Remove(payload.SenderPeerId);
            }

            AddLog($"[KeyExchange] Established with {peerName}", LogLevel.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_keyExchangeInFlight)
            {
                _keyExchangeInFlight.Remove(payload.SenderPeerId);
            }

            AddLog($"[KeyExchange] Failed with {peerName}: {ex.Message}", LogLevel.Warning);
            _logger.LogWarning(ex, "Key exchange response failed from {PeerId}", packet.SenderId);
        }
    }

    private void HandleKeyExchangeConfirm(NetworkPacket packet)
    {
        if (!TryDeserializeKeyExchangePayload<KeyExchangeConfirmPayload>(packet, out var payload))
            return;

        var peerName = GetPeerDisplayName(packet);
        try
        {
            if (!TryPrepareInboundKeyExchange(packet, payload, peerName))
                return;

            EnsureSessionKeyManager().ProcessInboundKeyExchangeConfirm(payload);
            lock (_keyExchangeInFlight)
            {
                _keyExchangeInFlight.Remove(payload.SenderPeerId);
            }

            AddLog($"[KeyExchange] Established with {peerName}", LogLevel.Success);
        }
        catch (Exception ex)
        {
            AddLog($"[KeyExchange] Failed confirm from {peerName}: {ex.Message}", LogLevel.Warning);
            _logger.LogWarning(ex, "Key exchange confirm failed from {PeerId}", packet.SenderId);
        }
    }

    private bool TryPrepareInboundKeyExchange(NetworkPacket packet, KeyExchangePayload payload, string peerName)
    {
        if (string.IsNullOrWhiteSpace(packet.SenderId) ||
            !packet.SenderId.Equals(payload.SenderPeerId, StringComparison.Ordinal))
        {
            AddLog($"[KeyExchange] Rejected packet from {peerName}: sender mismatch", LogLevel.Warning);
            return false;
        }

        if (IsPeerBlocked(payload.SenderPeerId))
        {
            AddLog($"[KeyExchange] Rejected blocked peer {peerName}", LogLevel.Warning);
            return false;
        }

        var fingerprint = CryptoEncoding.ToHex(SHA256.HashData(payload.IdentityPublicKey));
        _peerTrustStore.UpsertPeerIdentity(
            payload.SenderPeerId,
            peerName,
            payload.IdentityPublicKey,
            fingerprint);
        _peerTrustStore.Save();
        return true;
    }

    private bool TryDeserializeKeyExchangePayload<TPayload>(NetworkPacket packet, out TPayload payload)
        where TPayload : KeyExchangePayload
    {
        payload = null!;

        if (string.IsNullOrWhiteSpace(packet.Payload))
        {
            AddLog($"[KeyExchange] Rejected {packet.Type} from {GetPeerDisplayName(packet)}: missing payload", LogLevel.Warning);
            return false;
        }

        try
        {
            payload = JsonConvert.DeserializeObject<TPayload>(packet.Payload)
                ?? throw new JsonException("Key exchange payload was empty.");
            return true;
        }
        catch (JsonException ex)
        {
            AddLog($"[KeyExchange] Rejected {packet.Type} from {GetPeerDisplayName(packet)}: invalid payload", LogLevel.Warning);
            _logger.LogWarning(ex, "Invalid key exchange payload from {PeerId}", packet.SenderId);
            return false;
        }
    }

    private NetworkPacket CreateKeyExchangePacket(PacketType packetType, string targetPeerId, KeyExchangePayload payload)
        => new()
        {
            Type = packetType,
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            TargetId = targetPeerId,
            Payload = JsonConvert.SerializeObject(payload)
        };

    private SessionKeyManager EnsureSessionKeyManager()
    {
        if (!_peerTrustStoreLoaded)
        {
            if (_loadPeerTrustStoreOnInitialize)
                _peerTrustStore.Load();
            _peerTrustStoreLoaded = true;
        }

        if (_localIdentity == null)
        {
            _localIdentity = _localIdentityStore.LoadOrCreate();
            _ownsLocalIdentity = true;
        }

        _sessionKeyManager ??= new SessionKeyManager(_wifi.LocalId, _localIdentity, _peerTrustStore);
        return _sessionKeyManager;
    }

    private bool IsPeerBlocked(string peerId)
        => _peerTrustStore.GetByPeerId(peerId)?.TrustState == TrustState.Blocked;

    private string GetPeerDisplayName(NetworkPacket packet)
        => string.IsNullOrWhiteSpace(packet.SenderName) ? packet.SenderId : packet.SenderName;

    private void MergePeerList(NetworkPacket packet)
    {
        if (string.IsNullOrWhiteSpace(packet.SenderId) ||
            packet.KnownPeers == null ||
            packet.KnownPeers.Length == 0)
        {
            return;
        }

        if (!_peerById.TryGetValue(packet.SenderId, out var relayPeer))
        {
            AddLog($"Ignored PeerList from unknown relay {packet.SenderName ?? packet.SenderId}", LogLevel.Info);
            return;
        }

        var added = 0;
        var updated = 0;

        foreach (var peerInfo in packet.KnownPeers)
        {
            if (!PeerTopology.TryCreateIndirectPeer(peerInfo, relayPeer, _wifi.LocalId, out var indirectPeer))
                continue;

            if (!_peerById.TryGetValue(indirectPeer.Id, out var existing))
            {
                _peerById[indirectPeer.Id] = indirectPeer;
                Peers.Add(indirectPeer);
                added++;
                continue;
            }

            if (existing.IsDirectlyConnected || indirectPeer.HopsAway >= existing.HopsAway)
                continue;

            ReplacePeer(existing, indirectPeer with { UnreadCount = existing.UnreadCount });
            updated++;
        }

        if (added > 0 || updated > 0)
        {
            AddLog($"Merged PeerList from {relayPeer.DisplayName}: {added} discovered, {updated} updated", LogLevel.Info);
            TryResendQueuedTextMessages();
        }
    }

    private async Task HandleIncomingMessageAsync(NetworkPacket packet, CancellationToken cancellationToken = default)
    {
        if (packet.Payload == null) return;

        var payload = packet.Payload;
        if (packet.IsEncrypted)
        {
            if (!TryDecrypt(payload, packet.CryptoVersion, out payload))
            {
                AddLog($"Dropped encrypted message from {packet.SenderName ?? packet.SenderId}: decryption failed", LogLevel.Warning);
                return;
            }
        }

        ChatMessage? msg;
        try
        {
            msg = JsonConvert.DeserializeObject<ChatMessage>(payload);
        }
        catch (JsonException ex)
        {
            AddLog($"Dropped message from {packet.SenderName ?? packet.SenderId}: invalid payload", LogLevel.Warning);
            _logger.LogWarning(ex, "Dropped invalid message payload from {SenderId}", packet.SenderId);
            return;
        }

        if (msg == null) return;

        var isBroadcast = string.IsNullOrWhiteSpace(packet.TargetId);
        var conversationId = isBroadcast ? BroadcastConversationId : packet.SenderId;
        msg = msg with
        {
            Status = MessageStatus.Delivered,
            ConversationId = conversationId,
            TargetPeerId = packet.TargetId
        };

        var peerId = packet.SenderId;
        var isDuplicate = !string.IsNullOrWhiteSpace(msg.Id) && _messageById.ContainsKey(msg.Id);

        if (isDuplicate)
        {
            if (!isBroadcast && SelectedPeer?.Id == peerId)
            {
                var readReceipt = new NetworkPacket
                {
                    Type = PacketType.ReadReceipt,
                    SenderId = _wifi.LocalId,
                    SenderName = DisplayName,
                    Payload = msg.Id
                };
                await SendToPeerViaTransportAsync(peerId, readReceipt, cancellationToken);
            }

            var duplicateAck = new NetworkPacket
            {
                Type = PacketType.MessageAck,
                SenderId = _wifi.LocalId,
                SenderName = DisplayName,
                Payload = msg.Id
            };
            await SendToPeerViaTransportAsync(peerId, duplicateAck, cancellationToken);
            return;
        }

        msg = AddMessageToHistory(conversationId, msg);

        // Show message if viewing this peer OR in broadcast view (no peer selected)
        bool isViewingThisPeer = !isBroadcast && SelectedPeer?.Id == peerId;
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
                await SendToPeerViaTransportAsync(peerId, ack, cancellationToken);
            }
        }
        else if (!isBroadcast)
        {
            // Viewing a different peer — increment unread badge
            if (_peerById.TryGetValue(peerId, out var peer))
            {
                ReplacePeer(peer, peer with { UnreadCount = peer.UnreadCount + 1 });
                UpdateUnreadCounts();
            }
        }

        // Send delivery receipt
        var deliveryAck = new NetworkPacket
        {
            Type = PacketType.MessageAck,
            SenderId = _wifi.LocalId,
            SenderName = DisplayName,
            Payload = msg.Id
        };
        await SendToPeerViaTransportAsync(peerId, deliveryAck, cancellationToken);

        // Log the receive event with visual indicator
        var senderName = packet.SenderName ?? "Unknown";
        var transport = msg.Transport ?? "WiFi";
        AddLog($"[RECEIVED \u2193] From: {senderName} via {transport}", LogLevel.Received);
    }

    private void HandleDeliveryAck(NetworkPacket packet)
    {
        var msgId = packet.Payload;
        if (msgId != null && _messageById.TryGetValue(msgId, out var msg))
            MarkMessageStatusAtLeast(msg, MessageStatus.Delivered);
    }

    private void HandleReadReceipt(NetworkPacket packet)
    {
        var msgId = packet.Payload;
        if (msgId != null && _messageById.TryGetValue(msgId, out var msg))
            MarkMessageStatusAtLeast(msg, MessageStatus.Read);
    }

    public async Task AddReactionAsync(string messageId, string emoji, CancellationToken cancellationToken = default)
    {
        // Find the message
        if (!_messageById.TryGetValue(messageId, out var msg)) return;

        // Toggle reaction: add if not present, remove if present
        bool isAdding = ToggleReaction(msg, emoji, _wifi.LocalId);
        msg = ReplaceMessage(msg, msg with { Reactions = CloneReactions(msg.Reactions) });

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
            await SendToPeerViaTransportAsync(msg.SenderId, packet, cancellationToken);
        }
        else
        {
            await SendToAllViaTransportAsync(packet, cancellationToken);
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

            if (!_messageById.TryGetValue(reaction.MessageId, out var msg)) return;

            if (reaction.IsAdded)
            {
                AddReactionUser(msg, reaction.Emoji, reaction.UserId);
            }
            else
            {
                RemoveReactionUser(msg, reaction.Emoji, reaction.UserId);
            }

            msg = ReplaceMessage(msg, msg with { Reactions = CloneReactions(msg.Reactions) });

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

    private void OnFileStarted(FileTransferStartedInfo info)
    {
        _dispatcher.Invoke(() =>
        {
            if (_messageById.ContainsKey(info.MessageId))
                return;

            var isBroadcast = string.IsNullOrWhiteSpace(info.TargetId);
            var conversationId = isBroadcast ? BroadcastConversationId : info.SenderId;
            var msg = new ChatMessage
            {
                Id = info.MessageId,
                SenderId = info.SenderId,
                SenderName = string.IsNullOrWhiteSpace(info.SenderName) ? "Unknown" : info.SenderName,
                Type = MessageType.File,
                Status = MessageStatus.Sending,
                FileName = info.FileName,
                FileSize = info.TotalSize,
                FileProgress = 0,
                ConversationId = conversationId,
                TargetPeerId = info.TargetId,
                Transport = ResolvePeerTransportName(info.SenderId)
            };

            msg = AddMessageToHistory(conversationId, msg);

            var isViewingThisPeer = !isBroadcast && SelectedPeer?.Id == info.SenderId;
            var isInBroadcastView = SelectedPeer == null;
            if (isViewingThisPeer || isInBroadcastView)
            {
                Messages.Add(msg);
            }
            else if (!isBroadcast && _peerById.TryGetValue(info.SenderId, out var peer))
            {
                ReplacePeer(peer, peer with { UnreadCount = peer.UnreadCount + 1 });
                UpdateUnreadCounts();
            }
        });
    }

    private void OnFileProgress(string msgId, double progress)
    {
        _dispatcher.Invoke(() =>
        {
            if (_messageById.TryGetValue(msgId, out var msg))
                ReplaceMessage(msg, msg with { FileProgress = progress });
        });
    }

    private void OnFileReceived(string msgId, string savedPath)
    {
        _dispatcher.Invoke(() =>
        {
            if (_messageById.TryGetValue(msgId, out var msg))
            {
                ReplaceMessage(msg, msg with
                {
                    FilePath = savedPath,
                    Status = MessageStatus.Delivered,
                    FileProgress = 1.0
                });
            }
            AddLog($"File received and saved to {savedPath}");
        });
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private ChatMessage AddMessageToHistory(string conversationId, ChatMessage msg)
    {
        msg = EnsureConversationId(msg, conversationId);

        if (!_messageHistory.TryGetValue(conversationId, out var history))
            _messageHistory[conversationId] = history = [];
        history.Add(msg);
        IndexMessage(msg);
        _ = SaveMessagesAsync(_lifetimeCts.Token);
        return msg;
    }

    private static ChatMessage EnsureConversationId(ChatMessage msg, string conversationId)
        => string.Equals(msg.ConversationId, conversationId, StringComparison.Ordinal)
            ? msg
            : msg with { ConversationId = conversationId };

    private string ResolveConversationId(ChatMessage msg)
    {
        if (!string.IsNullOrWhiteSpace(msg.ConversationId))
            return msg.ConversationId;

        if (!string.IsNullOrWhiteSpace(msg.TargetPeerId))
        {
            if (msg.TargetPeerId == _wifi.LocalId &&
                !string.IsNullOrWhiteSpace(msg.SenderId) &&
                msg.SenderId != _wifi.LocalId)
            {
                return msg.SenderId;
            }

            return msg.TargetPeerId;
        }

        if (!string.IsNullOrWhiteSpace(msg.SenderId) && msg.SenderId != _wifi.LocalId)
            return msg.SenderId;

        return BroadcastConversationId;
    }

    private ChatMessage ReplaceMessage(ChatMessage current, ChatMessage updated)
    {
        foreach (var history in _messageHistory.Values)
        {
            for (var i = 0; i < history.Count; i++)
            {
                if (history[i].Id == current.Id)
                    history[i] = updated;
            }
        }

        for (var i = 0; i < Messages.Count; i++)
        {
            if (Messages[i].Id == current.Id)
                Messages[i] = updated;
        }

        _messageById[updated.Id] = updated;
        IndexReactions(updated);
        if (updated.Status is MessageStatus.Delivered or MessageStatus.Read or MessageStatus.Failed)
            CancelAckRetry(updated.Id);
        _ = SaveMessagesAsync(_lifetimeCts.Token);
        return updated;
    }

    private ChatMessage MarkMessageStatusAtLeast(ChatMessage msg, MessageStatus status)
        => GetMessageStatusRank(msg.Status) >= GetMessageStatusRank(status)
            ? msg
            : ReplaceMessage(msg, msg with
            {
                Status = status,
                QueuedAt = status is MessageStatus.Delivered or MessageStatus.Read ? null : msg.QueuedAt
            });

    private static int GetMessageStatusRank(MessageStatus status) => status switch
    {
        MessageStatus.Sending => 0,
        MessageStatus.Sent => 1,
        MessageStatus.Delivered => 2,
        MessageStatus.Read => 3,
        MessageStatus.Failed => 4,
        _ => 0
    };

    private string ResolvePeerTransportName(string peerId)
    {
        if (_peerById.TryGetValue(peerId, out var peer) &&
            peer.Transport == TransportType.Bluetooth)
        {
            return "Bluetooth";
        }

        return "WiFi";
    }

    private Peer ReplacePeer(Peer current, Peer updated)
    {
        _peerById[updated.Id] = updated;

        for (var i = 0; i < Peers.Count; i++)
        {
            if (Peers[i].Id == current.Id)
                Peers[i] = updated;
        }

        if (SelectedPeer?.Id == current.Id)
        {
            SetProperty(ref _selectedPeer, updated, nameof(SelectedPeer));
        }

        return updated;
    }

    private async Task LoadPersistedMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var messages = await Task.Run(() => _messageStore.Load(), cancellationToken);
            foreach (var msg in messages)
            {
                var conversationId = ResolveConversationId(msg);
                var persistedMessage = EnsureConversationId(msg, conversationId);

                if (!_messageHistory.TryGetValue(conversationId, out var history))
                    _messageHistory[conversationId] = history = [];
                history.Add(persistedMessage);
                IndexMessage(persistedMessage);
            }
            _logger.LogInformation("Loaded {MessageCount} persisted messages", messages.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted messages");
        }
    }

    private async Task SaveMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var saveVersion = Interlocked.Increment(ref _saveVersion);
            await Task.Delay(SaveDebounceMs, cancellationToken);

            if (saveVersion != Volatile.Read(ref _saveVersion))
                return;

            var allMessages = _messageHistory.Values.SelectMany(m => m).ToList();

            await _saveLock.WaitAsync(cancellationToken);
            try
            {
                if (saveVersion != Volatile.Read(ref _saveVersion))
                    return;

                await Task.Run(() => _messageStore.Save(allMessages), cancellationToken);
            }
            finally
            {
                _saveLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save messages");
        }
    }

    private async Task SaveMessagesImmediatelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var saveVersion = Interlocked.Increment(ref _saveVersion);
            var allMessages = _messageHistory.Values.SelectMany(m => m).ToList();

            await _saveLock.WaitAsync(cancellationToken);
            try
            {
                if (saveVersion != Volatile.Read(ref _saveVersion))
                    return;

                await Task.Run(() => _messageStore.Save(allMessages), cancellationToken);
            }
            finally
            {
                _saveLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save messages");
        }
    }

    private void IndexMessage(ChatMessage msg)
    {
        if (!string.IsNullOrWhiteSpace(msg.Id))
        {
            _messageById[msg.Id] = msg;
            IndexReactions(msg);
        }
    }

    private void IndexReactions(ChatMessage msg)
    {
        var indexed = new Dictionary<string, HashSet<string>>();
        foreach (var reaction in msg.Reactions)
            indexed[reaction.Key] = new HashSet<string>(reaction.Value);

        _reactionUsersByMessage[msg.Id] = indexed;
    }

    private static Dictionary<string, List<string>> CloneReactions(Dictionary<string, List<string>> reactions)
        => reactions.ToDictionary(reaction => reaction.Key, reaction => reaction.Value.ToList());

    private Dictionary<string, HashSet<string>> GetReactionIndex(ChatMessage msg)
    {
        if (!_reactionUsersByMessage.TryGetValue(msg.Id, out var reactions))
        {
            IndexReactions(msg);
            reactions = _reactionUsersByMessage[msg.Id];
        }

        return reactions;
    }

    private bool ToggleReaction(ChatMessage msg, string emoji, string userId)
    {
        var reactions = GetReactionIndex(msg);
        if (reactions.TryGetValue(emoji, out var users) && users.Contains(userId))
        {
            RemoveReactionUser(msg, emoji, userId);
            return false;
        }

        AddReactionUser(msg, emoji, userId);
        return true;
    }

    private void AddReactionUser(ChatMessage msg, string emoji, string userId)
    {
        var reactions = GetReactionIndex(msg);

        if (!reactions.TryGetValue(emoji, out var users))
            reactions[emoji] = users = [];

        if (users.Add(userId))
        {
            if (!msg.Reactions.TryGetValue(emoji, out var visibleUsers))
                msg.Reactions[emoji] = visibleUsers = [];
            visibleUsers.Add(userId);
        }
    }

    private void RemoveReactionUser(ChatMessage msg, string emoji, string userId)
    {
        var reactions = GetReactionIndex(msg);

        if (!reactions.TryGetValue(emoji, out var users) || !users.Remove(userId))
            return;

        if (users.Count == 0)
            reactions.Remove(emoji);

        if (msg.Reactions.TryGetValue(emoji, out var visibleUsers))
        {
            visibleUsers.Remove(userId);
            if (visibleUsers.Count == 0)
                msg.Reactions.Remove(emoji);
        }
    }

    private void LoadMessagesForPeer(Peer? peer)
    {
        if (peer == null)
        {
            // Load broadcast history with date separators
            _messageHistory.TryGetValue("broadcast", out var broadcastHistory);
            ReplaceVisibleMessages(broadcastHistory ?? []);
            return;
        }

        peer = ReplacePeer(peer, peer with { UnreadCount = 0 });

        if (_messageHistory.TryGetValue(peer.Id, out var history))
            ReplaceVisibleMessages(history);
        else
            ReplaceVisibleMessages([]);

        UpdateUnreadCounts();
    }

    private void ReplaceVisibleMessages(List<ChatMessage> history)
    {
        // Build the visible window off-collection, then publish it as one Reset.
        // This avoids hundreds of per-item collection events when changing peers.
        Messages.ReplaceAll(BuildMessagesWithDateSeparators(history));
    }

    private static List<ChatMessage> BuildMessagesWithDateSeparators(List<ChatMessage> history)
    {
        DateTime? lastDate = null;
        var visibleHistory = history.Count > MaxVisibleHistoryMessages
            ? history.Skip(history.Count - MaxVisibleHistoryMessages)
            : history;
        var visibleMessages = new List<ChatMessage>();

        foreach (var msg in visibleHistory)
        {
            // Add date separator if date changed
            if (msg.Type != MessageType.System && msg.Type != MessageType.DateSeparator)
            {
                if (lastDate == null || msg.Timestamp.Date != lastDate.Value.Date)
                {
                    var separatorText = GetDateSeparatorText(msg.Timestamp);
                    visibleMessages.Add(new ChatMessage
                    {
                        Type = MessageType.DateSeparator,
                        Content = separatorText,
                        IsDateSeparator = true,
                        DateSeparatorText = separatorText
                    });
                    lastDate = msg.Timestamp.Date;
                }
            }
            visibleMessages.Add(msg);
        }

        return visibleMessages;
    }

    private static string GetDateSeparatorText(DateTime date)
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
        TotalUnreadCount = Peers.Sum(p => p.UnreadCount);
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

        // Pass the view-model lifetime token so delayed typing cleanup does not
        // resume after the window closes.
        _ = HideTypingIndicatorAfterDelayAsync(peerId, _lifetimeCts.Token);
    }

    private async Task HideTypingIndicatorAfterDelayAsync(string peerId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TypingIndicatorDurationMs, cancellationToken);
            await _dispatcher.InvokeAsync(() =>
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
            }, DispatcherPriority.Background, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void AddLog(string msg)
    {
        _logger.LogInformation("UI log: {Message}", msg);
        QueueLogEntry(CreateLogEntry(msg));
    }

    private void AddLog(string msg, LogLevel level)
    {
        _logger.Log(ToMicrosoftLogLevel(level), "UI log {UiLogLevel}: {Message}", level, msg);
        QueueLogEntry(CreateLogEntry(msg, level));
    }

    private void QueueLogEntry(LogEntry entry)
    {
        lock (_logBatchLock)
        {
            _pendingLogEntries.Add(entry);
            if (_logFlushQueued)
                return;

            _logFlushQueued = true;
        }

        // Coalesce bursts from startup and network callbacks into one dispatcher
        // operation so logging cannot starve input or storyboard frames.
        _dispatcher.BeginInvoke(FlushPendingLogs, DispatcherPriority.Background);
    }

    private void FlushPendingLogs()
    {
        List<LogEntry> batch;
        lock (_logBatchLock)
        {
            batch = [.. _pendingLogEntries];
            _pendingLogEntries.Clear();
            _logFlushQueued = false;
        }

        if (batch.Count == 0)
            return;

        batch.Reverse();
        Logs.InsertRange(0, batch);
        while (Logs.Count > MaxVisibleLogs)
            Logs.RemoveAt(Logs.Count - 1);
    }

    private static Microsoft.Extensions.Logging.LogLevel ToMicrosoftLogLevel(LogLevel level) => level switch
    {
        LogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
        LogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
        _ => Microsoft.Extensions.Logging.LogLevel.Information
    };

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

    // ─── Message Encryption (AES-GCM with shared key) ────────────────────────
    private const string CryptoVersionAesGcm1 = "AESGCM1";
    private const string EncryptionKey = "MeshChatSecretKey2024!";

    private string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText) || !EncryptionEnabled) return plainText;

        // AES-GCM provides authenticated encryption, so tampered ciphertext fails
        // during decryption instead of producing corrupted JSON.
        const int nonceSize = 12; // 96-bit nonce is the standard size for GCM.
        const int tagSize = 16;   // 128-bit authentication tag.

        // Hash the shared passphrase into a fixed 256-bit key expected by AES.
        // This preserves the existing shared-key model; a real deployment should
        // exchange or derive this key per peer instead of hard-coding it.
        var keyBytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(EncryptionKey));
        var nonce = new byte[nonceSize];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);

        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[tagSize];

        using var aes = new System.Security.Cryptography.AesGcm(keyBytes, tagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        // Keep packet serialization unchanged by storing binary crypto fields as
        // a single Base64 payload string: nonce | tag | ciphertext.
        var encryptedBytes = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, encryptedBytes, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, encryptedBytes, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, encryptedBytes, nonce.Length + tag.Length, cipherBytes.Length);

        return CryptoVersionAesGcm1 + ":" + Convert.ToBase64String(encryptedBytes);
    }

    private bool TryDecrypt(string encryptedText, string? cryptoVersion, out string plainText)
    {
        plainText = encryptedText;
        if (string.IsNullOrEmpty(encryptedText)) return false;
        if (cryptoVersion != CryptoVersionAesGcm1) return false;

        try
        {
            const string prefix = CryptoVersionAesGcm1 + ":";
            const int nonceSize = 12;
            const int tagSize = 16;

            if (!encryptedText.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            var encryptedBytes = Convert.FromBase64String(encryptedText[prefix.Length..]);
            if (encryptedBytes.Length < nonceSize + tagSize)
                return false;

            var nonce = encryptedBytes[..nonceSize];
            var tag = encryptedBytes[nonceSize..(nonceSize + tagSize)];
            var cipherBytes = encryptedBytes[(nonceSize + tagSize)..];
            var plainBytes = new byte[cipherBytes.Length];

            // Derive the same 256-bit AES key from the shared passphrase used by
            // Encrypt; the random nonce is stored with each packet, not secret.
            var keyBytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(EncryptionKey));

            using var aes = new System.Security.Cryptography.AesGcm(keyBytes, tagSize);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

            plainText = System.Text.Encoding.UTF8.GetString(plainBytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

}

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppressNotifications = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
        }
        finally
        {
            _suppressNotifications = false;
        }

        RaiseReset();
    }

    public void InsertRange(int index, IEnumerable<T> items)
    {
        var inserted = items.ToList();
        if (inserted.Count == 0)
            return;

        _suppressNotifications = true;
        try
        {
            for (var i = 0; i < inserted.Count; i++)
                Items.Insert(index + i, inserted[i]);
        }
        finally
        {
            _suppressNotifications = false;
        }

        RaiseReset();
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnPropertyChanged(e);
    }

    private void RaiseReset()
    {
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
