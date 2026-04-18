# MeshChat - Project Documentation Summary

## Table of Contents
1. [Project Overview](#project-overview)
2. [Core Features](#core-features)
3. [Technical Architecture](#technical-architecture)
4. [Network Protocol](#network-protocol)
5. [User Interface](#user-interface)
6. [Data Models](#data-models)
7. [Services](#services)
8. [Build Configuration](#build-configuration)
9. [Use Cases](#use-cases)
10. [Technical Decisions](#technical-decisions)

---

## Project Overview

**MeshChat** is a peer-to-peer mesh networking chat application for Windows. It enables real-time communication between nearby devices without requiring a central server, using a combination of WiFi (TCP) and Bluetooth for connectivity. The application is built with WPF (.NET 8.0) and is optimized for deployment on school laptops with performance tuning for low-end hardware.

### What It Does
- Discovers nearby peers automatically using mDNS (Multicast DNS)
- Enables real-time text messaging between peers on the same network
- Supports file transfers using chunked transmission
- Routes messages through multiple hops when direct connection is not possible
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
- **Bluetooth**: RFCOMM with a custom service GUID

Both transports can operate simultaneously, allowing the application to use whichever connection is available or prefer the stronger signal.

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

### 4. Multi-Hop Mesh Routing
- Messages can traverse up to 5 hops (configurable TTL)
- Prevents infinite loops using visited node tracking
- Peer list sharing allows peers to learn about distant nodes
- Relay peers can forward messages on behalf of others

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
| `HelloAck` | Response with known peer list |
| `Message` | Chat message payload |
| `MessageAck` | Delivery confirmation |
| `ReadReceipt` | Message read notification |
| `FileChunk` | Chunk of a file transfer |
| `FileComplete` | File transfer finished |
| `PeerList` | Shared peer knowledge for mesh routing |
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
- TcpPort: sender's listening port (for Hello)
- KnownPeers: peer list for mesh discovery
```

### Mesh Routing Algorithm
1. When a peer receives a packet, it checks `VisitedNodes` for duplicates
2. If not a duplicate and TTL > 0, the packet is forwarded to:
   - Target peer (if specified)
   - All known peers (if broadcast)
3. TTL is decremented at each hop
4. Peers share their `KnownPeers` list to build network topology

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
- HopsAway: distance in mesh (1 = direct)
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
- HopCount: hops traveled
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
- KnownPeers: peer list for mesh discovery
```

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
  3. Receiver assembles chunks into buffer
  4. On completion, saves to `Downloads/MeshChat/`

- **Progress Tracking**:
  - Events for progress updates (0.0 to 1.0)
  - Visual progress bar in UI

### MessageStore
- **Location**: `%LOCALAPPDATA%\MeshChat\Data\messages.json`
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

### 2. Offline Emergency Communication
When internet is down, MeshChat provides local network communication.

### 3. File Sharing
Transfer files (assignments, documents) directly between computers on the same network.

### 4. Network Discovery Learning
Demonstrates peer-to-peer networking, mDNS discovery, and mesh routing concepts.

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
- End-to-end encryption
- Message search
- Group chats
- File transfer resume
- Mobile companion app
- Network topology visualization