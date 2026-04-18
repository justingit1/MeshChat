# MeshChat

A peer-to-peer mesh networking chat application for Windows that enables real-time communication between nearby devices without requiring a central server.

## Features

- **Dual Transport Support** - Uses both WiFi (TCP) and Bluetooth for connectivity
- **Automatic Peer Discovery** - Discovers nearby peers using mDNS (Multicast DNS)
- **Real-Time Messaging** - Send text messages with delivery tracking, typing indicators, and read receipts
- **Multi-Hop Mesh Routing** - Messages can traverse up to 5 hops when direct connection isn't possible
- **File Transfer** - Share files between peers with chunked transmission and progress tracking
- **Message Reactions** - Add emoji reactions to messages
- **Local Chat History** - Messages are persisted locally
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
- **Bluetooth**: Uses RFCOMM for device-to-device communication

Both transports can work simultaneously. The application will use whichever connection is available.

### Mesh Routing
When peers aren't directly connected, MeshChat can route messages through intermediate peers:
- Messages can hop up to 5 times (configurable TTL)
- The network automatically learns about distant peers through peer list sharing
- Duplicate detection prevents infinite loops

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
- `PeerList` - Shared peer knowledge for mesh routing
- `Typing` - Typing indicator
- `Reaction` - Emoji reactions
- `Goodbye` - Disconnect notification

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
- **Networking Learning** - Demonstrates peer-to-peer and mesh networking concepts

## License

This project is provided as-is for educational and personal use.

## Acknowledgments

- [Makaretu.Dns.Multicast](https://github.com/richardschneider/net-mdns) - mDNS service discovery
- [InTheHand.Net.Bluetooth](https://github.com/inthehand/32feet) - Bluetooth connectivity
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - MVVM framework