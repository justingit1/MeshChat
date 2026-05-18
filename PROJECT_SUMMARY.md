# MeshChat - Project Documentation Summary

## Table of Contents
1. [Project Overview](#project-overview)
2. [Core Features](#core-features)
3. [Technical Architecture](#technical-architecture)
4. [Network Protocol](#network-protocol)
5. [Security Notes](#security-notes)
6. [User Interface](#user-interface)
7. [Data Models](#data-models)
8. [Services](#services)
9. [Build Configuration](#build-configuration)
10. [Use Cases](#use-cases)
11. [Technical Decisions](#technical-decisions)

---

## Project Overview

**MeshChat** is a peer-to-peer chat application for Windows with limited, experimental mesh relay behavior. It enables real-time communication between nearby devices without requiring a central server, using WiFi TCP and Bluetooth RFCOMM connectivity where supported. The application is built with WPF (.NET 8.0) and is optimized for deployment on school laptops with performance tuning for low-end hardware.

### What It Does
- Discovers nearby peers automatically using mDNS (Multicast DNS)
- Enables real-time text messaging between directly connected peers on the same network
- Supports file transfers using chunked transmission
- Relays targeted packets, plus targetless broadcast chat `Message` packets, through connected peers while TTL allows
- Provides visual feedback with message status (sending, sent, delivered, read)
- Supports emoji reactions on messages
- Maintains chat history with local persistence

### Target Environment
- **Platform**: Windows 10/11 (x64)
- **Runtime**: .NET 8.0 (self-contained, no installation required)
- **Deployment**: Single portable executable optimized for school laptops

---

## Core Features

### 1. Dual Transport Support
The application supports two transport mechanisms:
- **WiFi**: TCP sockets with mDNS service discovery on port 45678
- **Bluetooth**: RFCOMM with a custom service GUID, depending on pairing and Windows/platform support

Both transports can operate simultaneously, allowing the application to use whichever connection is available.

### 2. Automatic Peer Discovery
- Uses **mDNS (Multicast DNS)** via the Makaretu.Dns.Multicast library
- Service type: `_meshchat._tcp`
- Peers advertise themselves with their ID, name, and listening port
- Bluetooth peers are discovered via device inquiry

### 3. Real-Time Messaging
- Text messages with delivery tracking
- Typing indicators (shows when a peer is typing)
- Read receipts (when messages are viewed)
- Message reactions with emoji support
- Date separators in chat history

### 4. Limited Mesh Relay
- Targeted packets can traverse connected peers while TTL allows it
- Targetless broadcast chat packets relay narrowly for `PacketType.Message`
- Duplicate packet IDs and visited-node tracking suppress relay loops
- Relay peers can forward packets on behalf of others when they are already connected
- `PeerList` packets share currently direct peers for minimal topology discovery; discovered peers are indirect only
- Store-and-forward offline delivery is not implemented

### 5. File Transfer
- Chunked file transfer (32KB chunks)
- Progress tracking during transfer
- Automatic save to user's Downloads/MeshChat folder
- Handles duplicate filenames with timestamp suffixes
- Transfer throttling (5ms delay between chunks)

### 6. Logging System
- Real-time logging panel in the UI
- Color-coded log entries by category:
  - WiFi (network events)
  - Bluetooth (BT events)
  - FileTransfer (file operations)
  - Peer (peer discovery/loss)
  - Sent/Received (message events)
- Persistent file logging to `%LOCALAPPDATA%\MeshChat\Logs\`

---

## Technical Architecture

### Project Structure
```
MeshChat/
├── App.xaml(.cs)           # Application entry point, performance tuning
├── MeshChat.csproj         # Project configuration
├── Peer.cs                 # Peer data model
├── ChatMessage.cs          # Message data model
├── NetworkPacket.cs        # Network protocol definitions
├── LogEntry.cs             # Logging data models
├── Logger.cs               # Static logging utility
├── MainViewModel.cs        # Main MVVM view model (1129 lines)
├── Converters.cs           # WPF value converters (30,362 bytes)
├── WiFiService.cs          # WiFi/TCP networking (280 lines)
├── BluetoothService.cs     # Bluetooth networking (340 lines)
├── FileTransferService.cs  # File transfer handling
├── Services/
│   └── MessageStore.cs     # JSON-based message persistence
├── Views/
│   └── MainWindow.xaml(.cs)# Main UI window
└── Resources/              # UI resources
```

### Technology Stack
| Component | Technology | Version |
|-----------|------------|---------|
| Framework | .NET | 8.0 |
| UI | WPF | (built-in) |
| MVVM | CommunityToolkit.Mvvm | 8.2.2 |
| JSON | Newtonsoft.Json | 13.0.3 |
| mDNS | Makaretu.Dns.Multicast | 0.27.0 |
| Bluetooth | InTheHand.Net.Bluetooth | 4.1.40 |

### Architecture Pattern
- **MVVM (Model-View-ViewModel)** with CommunityToolkit.Mvvm
- **Services** for network operations (WiFi, Bluetooth, FileTransfer)
- **Observable collections** for reactive UI updates
- **Dispatcher** for thread-safe UI updates from background tasks

---

## Network Protocol

### Packet Types
The application uses a custom protocol over TCP/Bluetooth with the following packet types:

| Type | Purpose |
|------|---------|
| `Hello` | Peer announces itself on the network |
| `HelloAck` | Handshake response |
| `Message` | Chat message payload |
| `MessageAck` | Delivery confirmation |
| `ReadReceipt` | Message read notification |
| `FileChunk` | Chunk of a file transfer |
| `FileComplete` | File transfer finished |
| `PeerList` | Shares currently direct peers for minimal indirect peer discovery |
| `Goodbye` | Peer is disconnecting |
| `Typing` | Typing indicator |
| `Reaction` | Emoji reaction to a message |

### Packet Structure (NetworkPacket)
```
- Id: unique packet identifier
- Type: PacketType enum
- SenderId: origin peer ID
- SenderName: origin peer display name
- TargetId: destination peer ID (null for broadcast)
- Ttl: time-to-live (hop limit, default 5)
- VisitedNodes: array of node IDs for loop prevention
- CreatedAt: UTC timestamp
- Payload: JSON-encoded type-specific data
- IsEncrypted: whether Payload contains encrypted data
- CryptoVersion: encryption payload format identifier, currently AESGCM1
- TcpPort: sender's listening port (for Hello)
- KnownPeers: peer list field used for minimal indirect peer discovery
```

### Message Payload Encryption
- When encryption is enabled in the UI, chat `Message` packet payloads are encrypted with AES-GCM.
- Encrypted chat payloads are marked with `IsEncrypted = true` and `CryptoVersion = AESGCM1`.
- The `AESGCM1` payload format stores nonce, authentication tag, and ciphertext in the packet payload.
- Current key handling is demo-grade and shared-key based: the AES key is derived from a hard-coded application passphrase.
- There is no per-peer key exchange yet.
- There is no peer identity verification yet.
- This implementation should not be described as production-grade end-to-end security.

### Mesh Relay Behavior
1. Transport services suppress duplicate packets by packet ID.
2. Packets relay only when TTL allows it and the local node is not the target.
3. Targeted packets may be forwarded to currently connected peers that are not in `VisitedNodes`.
4. Targetless broadcast packets are forwarded only when they are `PacketType.Message`.
5. TTL is decremented at each relay hop and the local node is appended to `VisitedNodes`.
6. `PeerList`/`KnownPeers` data builds minimal indirect peer awareness only; offline store-and-forward delivery is not implemented.

---

## User Interface

### Window Design
- **Frameless custom window** with modern styling
- **AllowsTransparency** for rounded corners and custom title bar
- **Minimum size**: 900x600 pixels
- **Default size**: 1150x720 pixels
- **Window controls**: Custom minimize, maximize, close buttons

### Main Layout
```
┌─────────────────────────────────────────────────────────────────┐
│  [Custom Title Bar with Drag Area]           [─] [□] [×]       │
├────────────────┬────────────────────────────────────────────────┤
│                │  [Peer Name]                    [Transport]   │
│  PEER LIST     │  [Signal Strength]              [Status]      │
│  ────────────  ├────────────────────────────────────────────────┤
│  [Peer 1]      │                                                │
│  [Peer 2]      │           MESSAGE LIST AREA                   │
│  [Peer 3]      │                                                │
│  ...           │  - Date separators                             │
│                │  - Message bubbles (sent/received)            │
│                │  - File transfer progress                      │
│                │  - Typing indicators                           │
│                │                                                │
├────────────────┴────────────────────────────────────────────────┤
│  [Message Input]                              [Send] [Attach]  │
└─────────────────────────────────────────────────────────────────┘
```

### UI Features
- **Smooth animations**: Message fade-in, sidebar slide, header transitions
- **Peer avatars**: Gradient background with initial letter
- **Signal strength**: Visual bars with color coding
- **Transport indicators**: WiFi/Bluetooth icons with color
- **Toast notifications**: For errors and status updates
- **Log panel**: Collapsible debug/log viewer
- **Custom scrollbars**: Modern styled scrolling

### Color Scheme
- Dark theme with accent colors:
  - Primary accent: Blue (#0A84FF)
  - Background: Dark gray (#1E1E2E)
  - Surface: Slightly lighter (#2A2A3E)
  - Sent messages: Accent blue
  - Received messages: Surface gray

---

## Data Models

### Peer
```csharp
- Id: unique identifier (GUID)
- DisplayName: user's display name
- Status: Online/Away/Offline
- Transport: WiFi/Bluetooth/Both
- SignalStrength: 0-100
- IpAddress: IPv4 address
- TcpPort: listening port
- BluetoothAddress: BT MAC address
- HopsAway: observed hop distance (currently 1 for directly discovered peers)
- RelayPeerId: which peer relays to this one
- LastSeen: timestamp
- UnreadCount: unread message count
```

### ChatMessage
```csharp
- Id: unique identifier
- SenderId: sender's peer ID
- SenderName: sender's display name
- Content: message text
- Type: Text/File/System/DateSeparator
- Status: Sending/Sent/Delivered/Read/Failed
- Timestamp: when sent
- FileName, FileSize, FilePath, FileProgress: file transfer fields
- TargetPeerId: destination (null = broadcast)
- HopCount: hops traveled when present in message metadata
- VisitedNodes: routing path
- Transport: "WiFi" or "Bluetooth"
- Reactions: dictionary of emoji -> list of user IDs
```

### NetworkPacket
```csharp
- Id: unique packet identifier
- Type: PacketType enum
- SenderId, SenderName, TargetId
- Ttl: hop limit (default 5)
- VisitedNodes: routing history
- CreatedAt: UTC timestamp
- Payload: JSON data
- TcpPort: sender's port
- KnownPeers: peer list field used for minimal indirect peer discovery
- IsEncrypted: encrypted payload flag
- CryptoVersion: encryption payload format identifier, currently AESGCM1
```

---

## Security Notes

- AES-GCM protects chat message payloads when encryption is enabled.
- Encryption metadata on `NetworkPacket` identifies AESGCM1 encrypted packets using `IsEncrypted` and `CryptoVersion`.
- Current key handling is demo-grade and shared-key based; all peers use the same application passphrase-derived key.
- Per-peer key exchange is not implemented yet.
- Peer identity verification is not implemented yet.
- Local message history is persisted as JSON and is not encrypted by MeshChat.
- The current implementation should not be described as production-grade end-to-end security.

---

## Services

### WiFiService (280 lines)
- **Responsibilities**:
  - TCP server listening on dynamic port
  - mDNS service advertisement and discovery
  - Peer connection management
  - Packet sending/receiving over TCP

- **Key Methods**:
  - `StartAsync()`: Initialize listener and mDNS
  - `Stop()`: Clean shutdown
  - `SendPacketAsync()`: Send to specific peer or broadcast
  - `ConnectToPeer()`: Establish TCP connection

- **Event Handlers**:
  - `PeerDiscovered`: New peer found
  - `PeerLost`: Peer disconnected
  - `PacketReceived`: Incoming data

### BluetoothService (340 lines)
- **Responsibilities**:
  - Bluetooth listener on RFCOMM
  - Device discovery
  - Peer connection management
  - Packet exchange over Bluetooth

- **Key Features**:
  - 3-second timeout for Bluetooth availability check
  - Custom service GUID: `7b713000-019d-4001-923f-917300f8623d`
  - Connection pooling with `ConcurrentDictionary`

### FileTransferService
- **Chunk Size**: 32KB per chunk
- **Flow**:
  1. Sender reads file in chunks
  2. Each chunk sent as `FileChunk` packet
  3. Receiver assembles chunks into a partial file
  4. On completion, validates SHA-256 when `FileSha256` metadata is present
  5. If validation succeeds, saves to `Downloads/MeshChat/`

- **Progress Tracking**:
  - Events for progress updates (0.0 to 1.0)
  - Visual progress bar in UI

### MessageStore
- **Location**: `%LOCALAPPDATA%\MeshChat\Data\messages.json`
- **Storage format**: Plain JSON; MeshChat does not encrypt local message history
- **Operations**:
  - `Load()`: Read messages from JSON
  - `Save()`: Persist messages to JSON
  - `Clear()`: Delete all messages

---

## Build Configuration

The project is optimized for portable deployment on school laptops:

```xml
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>true</SelfContained>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
<PublishReadyToRun>true</PublishReadyToRun>
<TieredCompilation>true</TieredCompilation>
<gcServer>true</gcServer>
<gcConcurrent>true</gcConcurrent>
<PublishTrimmed>false</PublishTrimmed>
<DebugType>none</DebugType>
<DebugSymbols>false</DebugSymbols>
<Optimize>true</Optimize>
```

### Performance Optimizations
- **Tiered Compilation**: Faster JIT for better startup
- **Server GC**: Better throughput for networked application
- **Concurrent GC**: Non-blocking garbage collection
- **ReadyToRun**: Pre-compiled native code
- **Single File**: No external dependencies, easy distribution

### App.xaml.cs Optimizations
```csharp
// Limit frame rate to 30fps to reduce CPU/GPU usage
Timeline.DesiredFrameRateProperty.OverrideMetadata(
    typeof(Timeline),
    new FrameworkPropertyMetadata(30));

// Use Display mode for faster text rendering
TextOptions.TextFormattingModeProperty.OverrideMetadata(
    typeof(Window),
    new FrameworkPropertyMetadata(TextFormattingMode.Display));
```

---

## Use Cases

### 1. Classroom Collaboration
Students in the same classroom can chat and share files without internet access or a server.

### 2. Local Emergency Communication
When internet is down, MeshChat can provide local network communication between currently connected peers. It does not provide delay-tolerant store-and-forward offline delivery.

### 3. File Sharing
Transfer files (assignments, documents) directly between computers on the same network.

### 4. Network Discovery Learning
Demonstrates peer-to-peer networking, mDNS discovery, and limited/experimental mesh relay concepts.

### 5. Low-Bandwidth Communication
Efficient protocol designed for school networks with limited bandwidth.

---

## Technical Decisions

### Why mDNS?
- Zero configuration required
- Automatic service discovery
- Built into the Makaretu.Dns.Multicast library
- Works on local networks without DNS servers

### Why Dual Transport?
- WiFi: Higher bandwidth, longer range
- Bluetooth: Works when WiFi is unavailable
- Fallback ensures connectivity in various scenarios

### Why Chunked File Transfer?
- Prevents memory exhaustion for large files
- Allows progress tracking
- Enables resume capability (future enhancement)
- 32KB chunks provide good balance between overhead and responsiveness

### Why JSON Serialization?
- Human-readable protocol for debugging
- Newtonsoft.Json provides robust handling
- Sufficient performance for message sizes

### Why Custom Window Frame?
- Modern appearance matching chat applications
- Full control over styling
- Removes Windows-native title bar appearance

---

## Future Enhancements (Not Implemented)
- Voice/video calls
- Per-peer key exchange and peer identity verification for production-grade end-to-end security
- Message search
- Group chats
- File transfer resume
- Mobile companion app
- Network topology visualization
- Full peer-list topology learning and route selection
- Store-and-forward offline delivery
