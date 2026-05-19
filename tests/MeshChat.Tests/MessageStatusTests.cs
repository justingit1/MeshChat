using System.Reflection;
using System.Runtime.Serialization;
using System.Collections;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using MeshChat.Models;
using MeshChat.Services;
using MeshChat.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace MeshChat.Tests;

public sealed class MessageStatusTests
{
    [Fact]
    public async Task SendMessageAsync_Success_StartsSendingThenMarksSent()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        ChatMessage? messageDuringSend = null;
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        wifi.OnSendToPeer = (_, _) =>
        {
            messageDuringSend = Assert.Single(vm.Messages);
        };

        await vm.SendMessageAsync();

        Assert.NotNull(messageDuringSend);
        Assert.Equal(MessageStatus.Sending, messageDuringSend.Status);
        var messageAfterSend = Assert.Single(vm.Messages);
        Assert.Equal(MessageStatus.Sent, messageAfterSend.Status);
        var send = Assert.Single(wifi.Sends);
        var payload = JsonConvert.DeserializeObject<ChatMessage>(send.Packet.Payload!);
        Assert.NotNull(payload);
        Assert.Equal(messageAfterSend.Id, payload.Id);
        Assert.Equal(MessageStatus.Sending, payload.Status);
        Assert.Empty(bluetooth.Sends);
    }

    [Fact]
    public async Task SendMessageAsync_UnavailableIndirectRoute_MarksFailed()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 2
        });

        await vm.SendMessageAsync();

        var message = Assert.Single(vm.Messages);
        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.NotNull(message.QueuedAt);
        Assert.Equal(0, message.QueueRetryCount);
        Assert.Empty(wifi.Sends);
        Assert.Empty(bluetooth.Sends);
        Assert.Contains("No route", vm.ToastMessage);
    }

    [Fact]
    public async Task SendMessageAsync_TransportException_MarksFailed()
    {
        var wifi = new FakeNetworkService
        {
            LocalId = "local",
            SendException = new InvalidOperationException("socket closed")
        };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });

        await vm.SendMessageAsync();

        var message = Assert.Single(vm.Messages);
        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.NotNull(message.QueuedAt);
        Assert.Contains("socket closed", vm.ToastMessage);
    }

    [Fact]
    public async Task SendMessageAsync_BlockedPeer_DoesNotSendDirectMessage()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        vm.BlockSelectedPeer();

        await vm.SendMessageAsync();

        Assert.Empty(vm.Messages);
        Assert.Empty(wifi.Sends);
        Assert.Contains("blocked", vm.ToastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendMessageAsync_NoAck_RetriesWithSameChatMessageIdAndFreshPacketIds()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, RetryLifetime(), new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });

        await vm.SendMessageAsync();
        await WaitUntilAsync(() => wifi.Sends.Count >= 3);

        Assert.Single(vm.Messages);
        var chatMessageIds = wifi.Sends
            .Select(send => JsonConvert.DeserializeObject<ChatMessage>(send.Packet.Payload!)!.Id)
            .ToList();
        Assert.Equal(3, chatMessageIds.Count);
        Assert.Single(chatMessageIds.Distinct());
        Assert.Equal(3, wifi.Sends.Select(send => send.Packet.Id).Distinct().Count());

        InvokeReceiptHandler(vm, "HandleDeliveryAck", chatMessageIds[0]);
    }

    [Fact]
    public async Task SendMessageAsync_AckBeforeTimeout_CancelsRetry()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, RetryLifetime(), new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });

        await vm.SendMessageAsync();
        var messageId = Assert.Single(vm.Messages).Id;

        InvokeReceiptHandler(vm, "HandleDeliveryAck", messageId);
        await Task.Delay(350);

        Assert.Single(wifi.Sends);
        Assert.Equal(MessageStatus.Delivered, Assert.Single(vm.Messages).Status);
    }

    [Fact]
    public async Task SendMessageAsync_NoAck_DoesNotAddDuplicateVisibleMessages()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, RetryLifetime(), new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });

        await vm.SendMessageAsync();
        await WaitUntilAsync(() => wifi.Sends.Count >= 3);

        Assert.Single(vm.Messages);
        InvokeReceiptHandler(vm, "HandleDeliveryAck", Assert.Single(vm.Messages).Id);
    }

    [Fact]
    public async Task SendMessageAsync_NoAckAfterRetryLimit_MarksFailed()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, RetryLifetime(), new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });

        await vm.SendMessageAsync();
        await WaitUntilAsync(() => Assert.Single(vm.Messages).Status == MessageStatus.Failed);

        Assert.Equal(3, wifi.Sends.Count);
        Assert.NotNull(Assert.Single(vm.Messages).QueuedAt);
        Assert.Contains("No delivery acknowledgement", vm.ToastMessage);
    }

    [Fact]
    public async Task QueuedTextMessage_PeerDiscoveryAttemptsResendWithoutDuplicateVisibleMessage()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 2
        });

        await vm.SendMessageAsync();
        var queued = Assert.Single(vm.Messages);
        var queuedAt = queued.QueuedAt;

        InvokePeerDiscovered(vm, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        await WaitUntilAsync(() => wifi.Sends.Count == 1);

        var resent = Assert.Single(vm.Messages);
        Assert.Equal(queued.Id, resent.Id);
        Assert.Equal(MessageStatus.Sent, resent.Status);
        Assert.Null(resent.QueuedAt);
        Assert.Equal(1, resent.QueueRetryCount);
        Assert.NotEqual(queuedAt, resent.LastQueueAttemptAt);
        Assert.Equal(resent.Id, JsonConvert.DeserializeObject<ChatMessage>(wifi.Sends[0].Packet.Payload!)!.Id);
    }

    [Fact]
    public async Task QueuedTextMessage_PeerListRouteAttemptsResendThroughRelay()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(
            wifi,
            bluetooth,
            new Peer
            {
                Id = "target",
                DisplayName = "Target",
                Status = PeerStatus.Online,
                Transport = TransportType.WiFi,
                HopsAway = 3
            },
            new Peer
            {
                Id = "relay",
                DisplayName = "Relay",
                Status = PeerStatus.Online,
                Transport = TransportType.WiFi,
                HopsAway = 1
            });

        await vm.SendMessageAsync();

        InvokeMergePeerList(vm, new NetworkPacket
        {
            SenderId = "relay",
            SenderName = "Relay",
            KnownPeers =
            [
                new PeerInfo
                {
                    Id = "target",
                    Name = "Target",
                    HopsAway = 1
                }
            ]
        });
        await WaitUntilAsync(() => wifi.Sends.Count == 1);

        Assert.Equal("relay", wifi.Sends[0].PeerId);
        Assert.Equal("target", wifi.Sends[0].Packet.TargetId);
        Assert.Single(vm.Messages);
    }

    [Fact]
    public async Task QueuedTextMessage_DeliveryAckLeavesQueue()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 2
        });

        await vm.SendMessageAsync();
        InvokePeerDiscovered(vm, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        await WaitUntilAsync(() => wifi.Sends.Count == 1);

        InvokeReceiptHandler(vm, "HandleDeliveryAck", Assert.Single(vm.Messages).Id);

        var delivered = Assert.Single(vm.Messages);
        Assert.Equal(MessageStatus.Delivered, delivered.Status);
        Assert.Null(delivered.QueuedAt);
    }

    [Fact]
    public async Task QueuedTextMessage_RetryLimitPreventsInfiniteResend()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        var queued = new ChatMessage
        {
            SenderId = "local",
            SenderName = "Local",
            Content = "hello",
            Type = MessageType.Text,
            Status = MessageStatus.Failed,
            ConversationId = "peer",
            TargetPeerId = "peer",
            QueuedAt = DateTime.UtcNow,
            QueueRetryCount = 3
        };
        AddMessageToHistory(vm, "peer", queued);
        vm.Messages.Add(queued);

        InvokePeerDiscovered(vm, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        await Task.Delay(150);

        Assert.Empty(wifi.Sends);
        Assert.Equal(MessageStatus.Failed, Assert.Single(vm.Messages).Status);
    }

    [Fact]
    public async Task QueuedTextMessage_BlockedPeerDoesNotResend()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        vm.BlockSelectedPeer();
        var queued = new ChatMessage
        {
            SenderId = "local",
            SenderName = "Local",
            Content = "hello",
            Type = MessageType.Text,
            Status = MessageStatus.Failed,
            ConversationId = "peer",
            TargetPeerId = "peer",
            QueuedAt = DateTime.UtcNow
        };
        AddMessageToHistory(vm, "peer", queued);
        vm.Messages.Add(queued);

        InvokePeerDiscovered(vm, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        await Task.Delay(150);

        Assert.Empty(wifi.Sends);
        Assert.Equal(MessageStatus.Failed, Assert.Single(vm.Messages).Status);
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_BlockedDirectMessage_DropsWithoutAck()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        vm.BlockSelectedPeer();

        await InvokeIncomingMessageAsync(vm, CreateIncomingPacket(CreateIncomingMessage("message-1")));

        Assert.Empty(vm.Messages);
        Assert.Empty(wifi.Sends);
        Assert.Empty(GetMessageHistory(vm));
    }

    [Fact]
    public void OnPacketReceived_BlockedTargetedReceipt_DoesNotAdvanceMessageStatus()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        var message = new ChatMessage
        {
            SenderId = "local",
            SenderName = "Local",
            Content = "hello",
            Type = MessageType.Text,
            Status = MessageStatus.Sent,
            ConversationId = "peer",
            TargetPeerId = "peer"
        };
        AddMessageToHistory(vm, "peer", message);
        vm.Messages.Add(message);
        vm.BlockSelectedPeer();

        InvokePacketReceived(vm, new NetworkPacket
        {
            Type = PacketType.MessageAck,
            SenderId = "peer",
            SenderName = "Peer",
            TargetId = "local",
            Payload = message.Id
        });

        Assert.Equal(MessageStatus.Sent, Assert.Single(vm.Messages).Status);
    }

    [Theory]
    [InlineData(PacketType.MessageAck)]
    [InlineData(PacketType.ReadReceipt)]
    public void OnPacketReceived_BlockedUntargetedReceipt_DoesNotAdvanceMessageStatus(PacketType packetType)
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        var message = new ChatMessage
        {
            SenderId = "local",
            SenderName = "Local",
            Content = "hello",
            Type = MessageType.Text,
            Status = MessageStatus.Sent,
            ConversationId = "peer",
            TargetPeerId = "peer"
        };
        AddMessageToHistory(vm, "peer", message);
        vm.Messages.Add(message);
        vm.BlockSelectedPeer();

        InvokePacketReceived(vm, new NetworkPacket
        {
            Type = packetType,
            SenderId = "peer",
            SenderName = "Peer",
            Payload = message.Id
        });

        Assert.Equal(MessageStatus.Sent, Assert.Single(vm.Messages).Status);
    }

    [Fact]
    public void Receipts_AdvanceStatusWithoutRegressingRead()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth);
        var message = new ChatMessage
        {
            Id = "message-1",
            SenderId = "local",
            SenderName = "Local",
            Content = "hello",
            Status = MessageStatus.Sent,
            ConversationId = "peer",
            TargetPeerId = "peer"
        };
        AddMessageToHistory(vm, "peer", message);
        vm.Messages.Add(message);

        InvokeReceiptHandler(vm, "HandleDeliveryAck", message.Id);
        Assert.Equal(MessageStatus.Delivered, Assert.Single(vm.Messages).Status);

        InvokeReceiptHandler(vm, "HandleReadReceipt", message.Id);
        Assert.Equal(MessageStatus.Read, Assert.Single(vm.Messages).Status);

        InvokeReceiptHandler(vm, "HandleDeliveryAck", message.Id);
        Assert.Equal(MessageStatus.Read, Assert.Single(vm.Messages).Status);
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_FirstReceive_AddsVisibleAndHistoryMessage()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        var message = CreateIncomingMessage("message-1");

        await InvokeIncomingMessageAsync(vm, CreateIncomingPacket(message));

        var visible = Assert.Single(vm.Messages);
        Assert.Equal("message-1", visible.Id);
        Assert.Equal(MessageStatus.Delivered, visible.Status);
        Assert.Equal("peer", visible.ConversationId);
        var history = GetMessageHistory(vm);
        Assert.Single(history["peer"]);
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_DuplicateChatMessageId_DoesNotAddVisibleOrHistoryMessage()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        var message = CreateIncomingMessage("message-1");

        await InvokeIncomingMessageAsync(vm, CreateIncomingPacket(message, "packet-1"));
        await InvokeIncomingMessageAsync(vm, CreateIncomingPacket(message, "packet-2"));

        Assert.Single(vm.Messages);
        var history = GetMessageHistory(vm);
        Assert.Single(history["peer"]);
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_DuplicateChatMessageId_StillSendsMessageAck()
    {
        var wifi = new FakeNetworkService { LocalId = "local" };
        var bluetooth = new FakeNetworkService { LocalId = "local" };
        var vm = CreateViewModel(wifi, bluetooth, new Peer
        {
            Id = "peer",
            DisplayName = "Peer",
            Status = PeerStatus.Online,
            Transport = TransportType.WiFi,
            HopsAway = 1
        });
        var message = CreateIncomingMessage("message-1");

        await InvokeIncomingMessageAsync(vm, CreateIncomingPacket(message, "packet-1"));
        await InvokeIncomingMessageAsync(vm, CreateIncomingPacket(message, "packet-2"));

        var messageAcks = wifi.Sends
            .Where(send => send.Packet.Type == PacketType.MessageAck)
            .ToList();
        Assert.Equal(2, messageAcks.Count);
        Assert.All(messageAcks, send => Assert.Equal("message-1", send.Packet.Payload));
    }

    private static MainViewModel CreateViewModel(
        FakeNetworkService wifi,
        FakeNetworkService bluetooth,
        params Peer[] peers)
    {
        var lifetimeCts = new CancellationTokenSource();
        lifetimeCts.Cancel();
        return CreateViewModel(wifi, bluetooth, lifetimeCts, peers);
    }

    private static MainViewModel CreateViewModel(
        FakeNetworkService wifi,
        FakeNetworkService bluetooth,
        CancellationTokenSource? lifetimeCts,
        params Peer[] peers)
    {
#pragma warning disable SYSLIB0050
        var vm = (MainViewModel)FormatterServices.GetUninitializedObject(typeof(MainViewModel));
#pragma warning restore SYSLIB0050
        lifetimeCts ??= new CancellationTokenSource();

        SetField(vm, "_wifi", wifi);
        SetField(vm, "_bluetooth", bluetooth);
        SetField(vm, "_dispatcher", Dispatcher.CurrentDispatcher);
        SetField(vm, "_messageStore", new MessageStore());
        SetField(vm, "_logger", NullLogger<MainViewModel>.Instance);
        SetField(vm, "_peerTrustStore", new PeerTrustStore(CreateTempTrustStorePath()));
        SetField(vm, "_loadPeerTrustStoreOnInitialize", false);
        SetField(vm, "_peerTrustStoreLoaded", true);
        SetField(vm, "_messageHistory", new Dictionary<string, List<ChatMessage>>());
        SetField(vm, "_peerById", peers.ToDictionary(peer => peer.Id));
        SetField(vm, "_messageById", new Dictionary<string, ChatMessage>());
        SetField(vm, "_reactionUsersByMessage", new Dictionary<string, Dictionary<string, HashSet<string>>>());
        SetField(vm, "_ackRetryCancellations", new Dictionary<string, CancellationTokenSource>());
        SetField(vm, "_keyExchangeInFlight", new HashSet<string>());
        SetField(vm, "_queuedSendInFlight", new HashSet<string>());
        SetField(vm, "_ackRetryLock", new object());
        SetField(vm, "_queuedSendLock", new object());
        SetField(vm, "_saveLock", new SemaphoreSlim(1, 1));
        SetField(vm, "_initializeLock", new SemaphoreSlim(1, 1));
        SetField(vm, "_lifetimeCts", lifetimeCts);
        SetField(vm, "_logBatchLock", new object());
        SetField(vm, "_pendingLogEntries", new List<LogEntry>());
        SetBackingField(vm, "Peers", new ObservableCollection<Peer>(peers));
        SetBackingField(vm, "Messages", new BulkObservableCollection<ChatMessage>());
        SetBackingField(vm, "Logs", new BulkObservableCollection<LogEntry>());
        if (lifetimeCts.IsCancellationRequested)
        {
            SetBackingField(vm, "FilteredMessages", CollectionViewSource.GetDefaultView(vm.Messages));
            SetBackingField(vm, "FilteredLogs", CollectionViewSource.GetDefaultView(vm.Logs));
        }
        else
        {
            SetBackingField(vm, "FilteredMessages", new TestCollectionView(vm.Messages));
            SetBackingField(vm, "FilteredLogs", new TestCollectionView(vm.Logs));
        }
        var selectedPeer = peers.FirstOrDefault();
        if (selectedPeer != null)
            SetField(vm, "_selectedPeer", selectedPeer);
        SetField(vm, "_messageInput", "hello");
        SetField(vm, "_displayName", "Local");

        return vm;
    }

    private static CancellationTokenSource RetryLifetime() => new();

    private static string CreateTempTrustStorePath()
        => Path.Combine(
            Path.GetTempPath(),
            "MeshChatMessageStatusTests",
            Guid.NewGuid().ToString("N"),
            "trusted-peers.json");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(25, cts.Token);
        }
    }

    private sealed class TestCollectionView(IEnumerable source) : ICollectionView
    {
        public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;
        public IEnumerable SourceCollection { get; } = source;
        public Predicate<object>? Filter { get; set; }
        public bool CanFilter => true;
        public SortDescriptionCollection SortDescriptions { get; } = [];
        public bool CanSort => false;
        public ObservableCollection<GroupDescription> GroupDescriptions { get; } = [];
        public ReadOnlyObservableCollection<object>? Groups => null;
        public bool CanGroup => false;
        public bool IsEmpty => !SourceCollection.Cast<object>().Any();
        public object? CurrentItem => SourceCollection.Cast<object>().FirstOrDefault();
        public int CurrentPosition => IsEmpty ? -1 : 0;
        public bool IsCurrentAfterLast => IsEmpty;
        public bool IsCurrentBeforeFirst => IsEmpty;
        public event NotifyCollectionChangedEventHandler? CollectionChanged { add { } remove { } }
        public event CurrentChangingEventHandler? CurrentChanging { add { } remove { } }
        public event EventHandler? CurrentChanged { add { } remove { } }
        public bool Contains(object item) => SourceCollection.Cast<object>().Contains(item);
        public IDisposable DeferRefresh() => NullDisposable.Instance;
        public IEnumerator GetEnumerator() => SourceCollection.GetEnumerator();
        public bool MoveCurrentTo(object item) => Contains(item);
        public bool MoveCurrentToFirst() => !IsEmpty;
        public bool MoveCurrentToLast() => !IsEmpty;
        public bool MoveCurrentToNext() => false;
        public bool MoveCurrentToPosition(int position) => position == 0 && !IsEmpty;
        public bool MoveCurrentToPrevious() => false;
        public void Refresh() { }

        private sealed class NullDisposable : IDisposable
        {
            public static NullDisposable Instance { get; } = new();
            public void Dispose() { }
        }
    }

    private static void AddMessageToHistory(MainViewModel vm, string conversationId, ChatMessage message)
    {
        var method = typeof(MainViewModel).GetMethod(
            "AddMessageToHistory",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(vm, [conversationId, message]);
    }

    private static void InvokeReceiptHandler(MainViewModel vm, string methodName, string messageId)
    {
        var method = typeof(MainViewModel).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(vm, [new NetworkPacket { Payload = messageId }]);
    }

    private static async Task InvokeIncomingMessageAsync(MainViewModel vm, NetworkPacket packet)
    {
        var method = typeof(MainViewModel).GetMethod(
            "HandleIncomingMessageAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task)method.Invoke(vm, [packet, CancellationToken.None])!;
        await task;
    }

    private static void InvokePacketReceived(MainViewModel vm, NetworkPacket packet)
    {
        var method = typeof(MainViewModel).GetMethod(
            "OnPacketReceived",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(vm, [packet]);
    }

    private static void InvokePeerDiscovered(MainViewModel vm, Peer peer)
    {
        var method = typeof(MainViewModel).GetMethod(
            "OnPeerDiscovered",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(vm, [peer]);
    }

    private static void InvokeMergePeerList(MainViewModel vm, NetworkPacket packet)
    {
        var method = typeof(MainViewModel).GetMethod(
            "MergePeerList",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(vm, [packet]);
    }

    private static ChatMessage CreateIncomingMessage(string id)
        => new()
        {
            Id = id,
            SenderId = "peer",
            SenderName = "Peer",
            Content = "hello",
            Type = MessageType.Text,
            Status = MessageStatus.Sent,
            TargetPeerId = "local"
        };

    private static NetworkPacket CreateIncomingPacket(ChatMessage message, string packetId = "packet-1")
        => new()
        {
            Id = packetId,
            Type = PacketType.Message,
            SenderId = "peer",
            SenderName = "Peer",
            TargetId = "local",
            Payload = JsonConvert.SerializeObject(message)
        };

    private static Dictionary<string, List<ChatMessage>> GetMessageHistory(MainViewModel vm)
    {
        var field = typeof(MainViewModel).GetField("_messageHistory", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (Dictionary<string, List<ChatMessage>>)field.GetValue(vm)!;
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = typeof(MainViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(instance, value);
    }

    private static void SetBackingField(object instance, string propertyName, object value)
    {
        var field = typeof(MainViewModel).GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(instance, value);
    }

    private sealed class FakeNetworkService : INetworkService
    {
        public List<(string PeerId, NetworkPacket Packet)> Sends { get; } = [];
        public Exception? SendException { get; init; }
        public Action<string, NetworkPacket>? OnSendToPeer { get; set; }

        public string LocalId { get; set; } = string.Empty;
        public string LocalName { get; set; } = "Local";
        public int ListenPort => 0;
        public bool IsAvailable => true;
        public bool IsRunning => true;

        public event Action<Peer>? PeerDiscovered { add { } remove { } }
        public event Action<string>? PeerLost { add { } remove { } }
        public event Action<NetworkPacket>? PacketReceived { add { } remove { } }
        public event Action<string>? LogMessage { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Stop() { }
        public Task StopAsync() => Task.CompletedTask;

        public Task SendToAllAsync(NetworkPacket packet, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SendToPeerAsync(string peerId, NetworkPacket packet, CancellationToken cancellationToken = default)
        {
            OnSendToPeer?.Invoke(peerId, packet);
            if (SendException != null)
                throw SendException;

            Sends.Add((peerId, packet));
            return Task.CompletedTask;
        }

        public Task ConnectToPeerAsync(string address, int? port = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Dispose() { }
    }
}
