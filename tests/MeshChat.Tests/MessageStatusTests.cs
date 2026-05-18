using System.Reflection;
using System.Runtime.Serialization;
using System.Collections.ObjectModel;
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

        Assert.Equal(MessageStatus.Failed, Assert.Single(vm.Messages).Status);
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

        Assert.Equal(MessageStatus.Failed, Assert.Single(vm.Messages).Status);
        Assert.Contains("socket closed", vm.ToastMessage);
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
#pragma warning disable SYSLIB0050
        var vm = (MainViewModel)FormatterServices.GetUninitializedObject(typeof(MainViewModel));
#pragma warning restore SYSLIB0050
        var lifetimeCts = new CancellationTokenSource();
        lifetimeCts.Cancel();

        SetField(vm, "_wifi", wifi);
        SetField(vm, "_bluetooth", bluetooth);
        SetField(vm, "_dispatcher", Dispatcher.CurrentDispatcher);
        SetField(vm, "_messageStore", new MessageStore());
        SetField(vm, "_logger", NullLogger<MainViewModel>.Instance);
        SetField(vm, "_messageHistory", new Dictionary<string, List<ChatMessage>>());
        SetField(vm, "_peerById", peers.ToDictionary(peer => peer.Id));
        SetField(vm, "_messageById", new Dictionary<string, ChatMessage>());
        SetField(vm, "_reactionUsersByMessage", new Dictionary<string, Dictionary<string, HashSet<string>>>());
        SetField(vm, "_saveLock", new SemaphoreSlim(1, 1));
        SetField(vm, "_initializeLock", new SemaphoreSlim(1, 1));
        SetField(vm, "_lifetimeCts", lifetimeCts);
        SetField(vm, "_logBatchLock", new object());
        SetField(vm, "_pendingLogEntries", new List<LogEntry>());
        SetBackingField(vm, "Peers", new ObservableCollection<Peer>(peers));
        SetBackingField(vm, "Messages", new BulkObservableCollection<ChatMessage>());
        SetBackingField(vm, "Logs", new BulkObservableCollection<LogEntry>());
        SetBackingField(vm, "FilteredMessages", CollectionViewSource.GetDefaultView(vm.Messages));
        SetBackingField(vm, "FilteredLogs", CollectionViewSource.GetDefaultView(vm.Logs));
        var selectedPeer = peers.FirstOrDefault();
        if (selectedPeer != null)
            SetField(vm, "_selectedPeer", selectedPeer);
        SetField(vm, "_messageInput", "hello");
        SetField(vm, "_displayName", "Local");

        return vm;
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
