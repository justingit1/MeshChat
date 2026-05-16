# MeshChat React Sidebar

This folder contains a React + Tailwind CSS sidebar redesign for MeshChat using the TypeUI `clean` style: minimal structure, explicit interaction states, 8px spacing rhythm, blue primary action, neutral surfaces, and dark-mode variants.

## Usage

```tsx
import { MeshChatSidebar, type MeshChatPeer } from "./MeshChatSidebar";

const peers: MeshChatPeer[] = [
  {
    id: "1",
    displayName: "Avery Chen",
    status: "online",
    transport: "wifi",
    signalStrength: 92,
    unreadCount: 3,
    lastMessage: "Ready to relay through the lab network.",
    hopsAway: 1,
  },
];

export function Layout() {
  return (
    <div className="flex h-screen bg-slate-50 dark:bg-slate-950">
      <MeshChatSidebar
        peers={peers}
        selectedPeerId="1"
        onSelectPeer={(peer) => console.log(peer)}
        onAddDevice={() => console.log("add device")}
      />
      <main className="min-w-0 flex-1" />
    </div>
  );
}
```

## Requirements

- Tailwind CSS with class-based dark mode, or an equivalent parent `dark` class strategy.
- `lucide-react` for icons.
- The component is controlled for selected peer state and internally controlled for collapse/search state.
