# MeshChat

A peer-to-peer chat application for Windows that enables real-time communication between nearby devices without requiring a central server. Mesh-style relay support is limited and experimental.

## Features

- **Dual Transport Support** - Uses WiFi TCP for direct messaging and Bluetooth RFCOMM when pairing/platform support allows it
- **Automatic Peer Discovery** - Discovers nearby peers using mDNS (Multicast DNS)
- **Real-Time Messaging** - Send text messages with delivery tracking, typing indicators, and read receipts
- **Limited Mesh Relay** - Targeted packets, plus targetless chat `Message` broadcasts, can relay through connected peers while TTL allows
- **File Transfer** - Share files between peers with chunked transmission and progress tracking
- **Message Reactions** - Add emoji reactions to messages
- **Local Chat History** - Messages are persisted locally; offline store-and-forward delivery is not implemented
- **Built-in Logging** - Debug panel for monitoring network events

## Screenshots

MeshChat features a modern dark theme with:
- Custom frameless window with window controls
- Peer list sidebar with signal strength indicators
- Message bubbles with timestamps and delivery status
- File transfer progress bars
- Collapsible debug log panel

## Requirements

- Windows 10/11 (x64)
- .NET 8.0 Runtime (included in self-contained build)
- WiFi adapter for network discovery
- Bluetooth adapter (optional, for Bluetooth connectivity)

## Installation

### Pre-built Release
1. Download the latest release from the [Releases](https://github.com/justingit1/MeshChat/releases) page
2. Extract the ZIP file
3. Run `MeshChat.exe`

### Building from Source
```bash
# Clone the repository
git clone https://github.com/justingit1/MeshChat.git
cd MeshChat

# Restore and build
dotnet restore
dotnet build --configuration Release

# Publish as single-file executable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The executable will be in `bin/Release/net8.0/win-x64/publish/`.

## Usage

1. **Launch MeshChat** - The application starts and begins peer discovery
2. **Wait for peers** - Nearby devices running MeshChat will appear in the sidebar
3. **Select a peer** - Click on a peer in the list to open a chat
4. **Send messages** - Type in the input box and press Send or Enter
5. **Send files** - Click the attachment button to send files

### Network Discovery
- **WiFi**: Uses mDNS to advertise and discover services on the local network
- **Bluetooth**: Uses RFCOMM for device-to-device communication when devices are paired and the platform permits it

Both transports can work simultaneously. The application will use whichever connection is available.

### Mesh Relay Status
MeshChat has limited, experimental relay behavior rather than full topology-aware mesh routing:
- Direct WiFi messaging works over TCP connections.
- Bluetooth RFCOMM support exists, but depends on pairing and Windows/platform support.
- Targeted packets can be relayed by connected peers while TTL allows it.
- Targetless broadcast chat packets relay narrowly only for `PacketType.Message`.
- Duplicate packet IDs and visited-node tracking suppress relay loops.
- `PeerList`/`KnownPeers` shares currently direct peers for minimal topology discovery; discovered peers are marked indirect and are not used for store-and-forward.
- Store-and-forward offline delivery is not implemented.

## Project Structure

```
MeshChat/
├── App.xaml(.cs)           # Application entry point
├── MeshChat.csproj         # Project configuration
├── Peer.cs                 # Peer data model
├── ChatMessage.cs          # Message data model
├── NetworkPacket.cs        # Network protocol definitions
├── LogEntry.cs             # Logging data models
├── Logger.cs               # Static logging utility
├── MainViewModel.cs        # Main MVVM view model
├── Converters.cs           # WPF value converters
├── WiFiService.cs          # WiFi/TCP networking
├── BluetoothService.cs     # Bluetooth networking
├── FileTransferService.cs  # File transfer handling
├── Services/
│   └── MessageStore.cs     # JSON-based message persistence
├── Views/
│   └── MainWindow.xaml(.cs)# Main UI window
└── Resources/              # UI resources
```

## Technical Details

### Technology Stack
| Component | Technology |
|-----------|------------|
| Framework | .NET 8.0 |
| UI | WPF |
| MVVM | CommunityToolkit.Mvvm |
| JSON | Newtonsoft.Json |
| mDNS | Makaretu.Dns.Multicast |
| Bluetooth | InTheHand.Net.Bluetooth |

### Network Protocol
MeshChat uses a custom protocol over TCP/Bluetooth with these packet types:
- `Hello` / `HelloAck` - Peer announcement and acknowledgment
- `Message` / `MessageAck` - Chat messages with delivery confirmation
- `ReadReceipt` - Message read notifications
- `FileChunk` / `FileComplete` - File transfer packets
- `PeerList` - Shares currently direct peers for minimal indirect peer discovery
- `Typing` - Typing indicator
- `Reaction` - Emoji reactions
- `Goodbye` - Disconnect notification

`NetworkPacket` includes encryption metadata fields:
- `IsEncrypted` - indicates that the packet payload is encrypted
- `CryptoVersion` - identifies the encryption payload format, currently `AESGCM1`

### Security Notes
- When encryption is enabled, chat message payloads are protected with AES-GCM and marked with `IsEncrypted` plus `CryptoVersion = AESGCM1`.
- Current key handling is demo-grade: all peers use a shared application key derived from a hard-coded passphrase.
- MeshChat does not yet implement per-peer key exchange or peer identity verification.
- Local message history is stored as JSON at `%LOCALAPPDATA%\MeshChat\Data\messages.json`; it is not encrypted by MeshChat.
- Local persistence is chat history only; it is not store-and-forward offline delivery.
- This should not be described as production-grade end-to-end security.

### Performance Optimizations
- Self-contained single-file deployment
- Tiered compilation for faster startup
- Server GC for better throughput
- Frame rate limited to 30fps for reduced CPU usage

## Data Storage

- **Messages**: `%LOCALAPPDATA%\MeshChat\Data\messages.json`
- **Logs**: `%LOCALAPPDATA%\MeshChat\Logs\`
- **Received Files**: `Downloads\MeshChat\`

## Use Cases

- **Classroom Collaboration** - Students can chat and share files without internet
- **Offline Communication** - Local network communication when internet is unavailable
- **File Sharing** - Transfer files directly between computers on the same network
- **Networking Learning** - Demonstrates peer-to-peer networking and limited/experimental mesh relay concepts

## License

This project is provided as-is for educational and personal use.

## Acknowledgments

- [Makaretu.Dns.Multicast](https://github.com/richardschneider/net-mdns) - mDNS service discovery
- [InTheHand.Net.Bluetooth](https://github.com/inthehand/32feet) - Bluetooth connectivity
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - MVVM framework
