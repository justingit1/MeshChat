import {
  Bluetooth,
  ChevronLeft,
  ChevronRight,
  CircleDot,
  FileUp,
  MessageCircle,
  Network,
  Plus,
  Radio,
  Search,
  Send,
  Settings,
  Signal,
  Wifi,
} from "lucide-react";
import type { ButtonHTMLAttributes, InputHTMLAttributes } from "react";
import { useMemo, useState } from "react";

type PeerStatus = "online" | "away" | "offline";
type PeerTransport = "wifi" | "bluetooth" | "both";

export type MeshChatPeer = {
  id: string;
  displayName: string;
  status: PeerStatus;
  transport: PeerTransport;
  signalStrength: number;
  unreadCount?: number;
  lastMessage?: string;
  lastSeen?: string;
  hopsAway?: number;
};

export type MeshChatBroadcastMessage = {
  id: string;
  author: string;
  body: string;
  timestamp: string;
  isLocal?: boolean;
};

type MeshChatSidebarProps = {
  peers: MeshChatPeer[];
  selectedPeerId?: string;
  onSelectPeer?: (peer: MeshChatPeer) => void;
  onAddDevice?: () => void;
  className?: string;
};

type BroadcastPanelProps = {
  messages: MeshChatBroadcastMessage[];
  value: string;
  onChange: (value: string) => void;
  onSend: () => void;
  onSendFile?: () => void;
  title?: string;
  subtitle?: string;
  className?: string;
};

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: "primary" | "secondary" | "ghost";
};

type InputFieldProps = InputHTMLAttributes<HTMLInputElement>;

const statusDotStyles: Record<PeerStatus, string> = {
  online: "bg-emerald-400",
  away: "bg-amber-400",
  offline: "bg-zinc-500",
};

const statusTextStyles: Record<PeerStatus, string> = {
  online: "text-emerald-300",
  away: "text-amber-300",
  offline: "text-zinc-400",
};

const transportIcon = {
  wifi: Wifi,
  bluetooth: Bluetooth,
  both: Radio,
};

function cx(...classes: Array<string | false | null | undefined>) {
  return classes.filter(Boolean).join(" ");
}

function getInitial(name: string) {
  return name.trim().charAt(0).toUpperCase() || "?";
}

function signalTone(signalStrength: number) {
  if (signalStrength >= 75) return "text-emerald-300";
  if (signalStrength >= 45) return "text-amber-300";
  return "text-rose-300";
}

export function Button({
  variant = "secondary",
  className,
  children,
  ...props
}: ButtonProps) {
  return (
    <button
      {...props}
      className={cx(
        // Shared button geometry keeps primary, file, and icon actions visually consistent.
        "inline-flex h-11 items-center justify-center gap-2 rounded-xl px-4 text-sm font-medium outline-none transition focus-visible:ring-2 focus-visible:ring-cyan-400 focus-visible:ring-offset-2 focus-visible:ring-offset-zinc-950 disabled:cursor-not-allowed disabled:opacity-50",
        variant === "primary" &&
          "bg-cyan-500 text-zinc-950 hover:bg-cyan-400 active:bg-cyan-300",
        variant === "secondary" &&
          "border border-zinc-700 bg-zinc-800 text-zinc-100 hover:border-zinc-600 hover:bg-zinc-700",
        variant === "ghost" &&
          "text-zinc-300 hover:bg-zinc-800 hover:text-zinc-50",
        className,
      )}
    >
      {children}
    </button>
  );
}

export function InputField({ className, ...props }: InputFieldProps) {
  return (
    <input
      {...props}
      className={cx(
        // Inputs use the same dark surface family as the panels, avoiding bright fields in dark mode.
        "h-11 w-full rounded-xl border border-zinc-700 bg-zinc-900 px-3 text-sm text-zinc-50 outline-none transition placeholder:text-zinc-500 hover:bg-zinc-800 focus:border-cyan-400 focus:ring-2 focus:ring-cyan-400/20",
        className,
      )}
    />
  );
}

export function MeshChatSidebar({
  peers,
  selectedPeerId,
  onSelectPeer,
  onAddDevice,
  className,
}: MeshChatSidebarProps) {
  const [collapsed, setCollapsed] = useState(false);
  const [query, setQuery] = useState("");

  const filteredPeers = useMemo(() => {
    const term = query.trim().toLowerCase();
    if (!term) return peers;

    return peers.filter((peer) =>
      [peer.displayName, peer.status, peer.transport, peer.lastMessage]
        .filter(Boolean)
        .some((value) => String(value).toLowerCase().includes(term)),
    );
  }, [peers, query]);

  const onlineCount = peers.filter((peer) => peer.status === "online").length;

  return (
    <aside
      className={cx(
        // The sidebar intentionally shares the same dark gray family as the broadcast composer.
        "flex h-full shrink-0 flex-col border-r border-zinc-800 bg-zinc-950 font-sans text-zinc-50 shadow-[1px_0_0_rgba(255,255,255,0.03)] transition-[width] duration-200 ease-out",
        collapsed ? "w-[76px]" : "w-80",
        className,
      )}
      aria-label="MeshChat devices"
      style={{ fontFamily: "Inter, Poppins, ui-sans-serif, system-ui, sans-serif" }}
    >
      <div className="flex h-16 items-center gap-3 border-b border-zinc-800 px-4">
        <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-cyan-500 text-zinc-950">
          <Network className="h-5 w-5" aria-hidden="true" />
        </div>

        {!collapsed && (
          <div className="min-w-0 flex-1">
            <h2 className="truncate text-base font-semibold text-zinc-50">MeshChat</h2>
            <p className="truncate text-xs text-zinc-400">
              {onlineCount} online / {peers.length} devices
            </p>
          </div>
        )}

        <Button
          type="button"
          variant="ghost"
          onClick={() => setCollapsed((value) => !value)}
          className="h-10 w-10 shrink-0 px-0"
          aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
        >
          {collapsed ? (
            <ChevronRight className="h-5 w-5" aria-hidden="true" />
          ) : (
            <ChevronLeft className="h-5 w-5" aria-hidden="true" />
          )}
        </Button>
      </div>

      {!collapsed && (
        <div className="space-y-3 border-b border-zinc-800 p-4">
          <div className="relative">
            <Search
              className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-zinc-500"
              aria-hidden="true"
            />
            <InputField
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              type="search"
              placeholder="Search devices"
              className="pl-10"
            />
          </div>

          <Button type="button" variant="primary" onClick={onAddDevice} className="w-full">
            <Plus className="h-4 w-4" aria-hidden="true" />
            Add device
          </Button>
        </div>
      )}

      <div className="min-h-0 flex-1 overflow-y-auto p-3">
        {filteredPeers.length > 0 ? (
          // Uniform spacing makes the peer list readable and keeps collapsed mode aligned.
          <div className="space-y-2">
            {filteredPeers.map((peer) => {
              const selected = peer.id === selectedPeerId;
              const TransportIcon = transportIcon[peer.transport];
              const unreadCount = peer.unreadCount ?? 0;

              return (
                <button
                  key={peer.id}
                  type="button"
                  onClick={() => onSelectPeer?.(peer)}
                  title={collapsed ? peer.displayName : undefined}
                  className={cx(
                    "group flex w-full items-center gap-3 rounded-xl p-2 text-left outline-none transition focus-visible:ring-2 focus-visible:ring-cyan-400",
                    selected
                      ? "bg-zinc-800 text-zinc-50 shadow-inner"
                      : "text-zinc-300 hover:bg-zinc-900 hover:text-zinc-50",
                  )}
                  aria-current={selected ? "page" : undefined}
                >
                  <div className="relative flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-zinc-800 text-sm font-semibold text-zinc-100">
                    {getInitial(peer.displayName)}
                    <span
                      className={cx(
                        "absolute -bottom-0.5 -right-0.5 h-3.5 w-3.5 rounded-full border-2 border-zinc-950",
                        statusDotStyles[peer.status],
                      )}
                      aria-label={peer.status}
                    />
                  </div>

                  {!collapsed && (
                    <>
                      <div className="min-w-0 flex-1">
                        <div className="flex items-center gap-2">
                          <span className="truncate text-sm font-medium text-zinc-50">
                            {peer.displayName}
                          </span>
                          {peer.hopsAway && peer.hopsAway > 1 && (
                            <span className="shrink-0 rounded-md bg-zinc-800 px-1.5 py-0.5 font-mono text-[11px] text-zinc-400">
                              {peer.hopsAway}h
                            </span>
                          )}
                        </div>
                        <div className="mt-1 flex items-center gap-2 text-xs text-zinc-400">
                          <CircleDot
                            className={cx("h-3 w-3", statusTextStyles[peer.status])}
                            aria-hidden="true"
                          />
                          <span className="capitalize">{peer.status}</span>
                          <span aria-hidden="true">.</span>
                          <TransportIcon className="h-3.5 w-3.5" aria-hidden="true" />
                          <span className="truncate capitalize">{peer.transport}</span>
                        </div>
                        {peer.lastMessage && (
                          <p className="mt-1 truncate text-xs text-zinc-500">
                            {peer.lastMessage}
                          </p>
                        )}
                      </div>

                      <div className="flex shrink-0 flex-col items-end gap-2">
                        <Signal
                          className={cx("h-4 w-4", signalTone(peer.signalStrength))}
                          aria-label={`${peer.signalStrength}% signal`}
                        />
                        {unreadCount > 0 && (
                          <span className="min-w-5 rounded-full bg-cyan-500 px-1.5 py-0.5 text-center text-[11px] font-semibold text-zinc-950">
                            {unreadCount > 99 ? "99+" : unreadCount}
                          </span>
                        )}
                      </div>
                    </>
                  )}
                </button>
              );
            })}
          </div>
        ) : (
          <div className="flex h-full flex-col items-center justify-center px-4 text-center">
            <div className="mb-3 flex h-11 w-11 items-center justify-center rounded-xl bg-zinc-900 text-zinc-400">
              <MessageCircle className="h-5 w-5" aria-hidden="true" />
            </div>
            {!collapsed && (
              <>
                <p className="text-sm font-medium text-zinc-100">No devices found</p>
                <p className="mt-1 text-xs text-zinc-500">
                  MeshChat is still scanning nearby peers.
                </p>
              </>
            )}
          </div>
        )}
      </div>

      <div className="border-t border-zinc-800 p-3">
        <Button
          type="button"
          variant="ghost"
          className={cx("w-full", collapsed ? "px-0" : "justify-start px-3")}
          aria-label="Open settings"
        >
          <Settings className="h-5 w-5 shrink-0" aria-hidden="true" />
          {!collapsed && <span>Settings</span>}
        </Button>
      </div>
    </aside>
  );
}

export function BroadcastPanel({
  messages,
  value,
  onChange,
  onSend,
  onSendFile,
  title = "Broadcast",
  subtitle = "Messages sent to nearby MeshChat peers",
  className,
}: BroadcastPanelProps) {
  return (
    <section
      className={cx(
        // The broadcast panel uses the same base surface as the sidebar; message cards are one step lighter.
        "flex min-w-0 flex-1 flex-col bg-zinc-950 font-sans text-zinc-50",
        className,
      )}
      aria-label="Broadcast chat"
      style={{ fontFamily: "Inter, Poppins, ui-sans-serif, system-ui, sans-serif" }}
    >
      <header className="flex h-16 items-center justify-between gap-4 border-b border-zinc-800 px-6">
        <div className="min-w-0">
          <h1 className="truncate text-lg font-semibold text-zinc-50">{title}</h1>
          <p className="truncate text-sm text-zinc-400">{subtitle}</p>
        </div>

        <div className="shrink-0 rounded-full border border-zinc-800 bg-zinc-900 px-3 py-1 text-xs font-medium text-zinc-400">
          {messages.length} messages
        </div>
      </header>

      <div className="min-h-0 flex-1 overflow-y-auto px-6 py-5">
        {messages.length > 0 ? (
          // Message cards use consistent spacing and a lighter dark gray for contrast without white panels.
          <div className="space-y-3">
            {messages.map((message) => (
              <article
                key={message.id}
                className={cx(
                  "rounded-2xl border p-4 shadow-sm",
                  message.isLocal
                    ? "border-cyan-400/20 bg-cyan-500/10"
                    : "border-zinc-800 bg-zinc-900",
                )}
              >
                <div className="mb-2 flex items-center justify-between gap-3">
                  <h2 className="truncate text-sm font-semibold text-zinc-100">
                    {message.author}
                  </h2>
                  <time className="shrink-0 text-xs text-zinc-500">{message.timestamp}</time>
                </div>
                <p className="text-sm leading-6 text-zinc-300">{message.body}</p>
              </article>
            ))}
          </div>
        ) : (
          <div className="flex h-full flex-col items-center justify-center px-6 text-center">
            <div className="mb-3 flex h-12 w-12 items-center justify-center rounded-2xl bg-zinc-900 text-zinc-400">
              <MessageCircle className="h-6 w-6" aria-hidden="true" />
            </div>
            <p className="text-sm font-medium text-zinc-100">No broadcast messages yet</p>
            <p className="mt-1 text-sm text-zinc-500">Start a message to reach connected peers.</p>
          </div>
        )}
      </div>

      <footer className="border-t border-zinc-800 bg-zinc-950 p-4">
        <div className="flex items-end gap-3">
          <Button type="button" variant="secondary" onClick={onSendFile} className="shrink-0">
            <FileUp className="h-4 w-4" aria-hidden="true" />
            Send File
          </Button>

          <InputField
            value={value}
            onChange={(event) => onChange(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter" && !event.shiftKey) {
                event.preventDefault();
                onSend();
              }
            }}
            placeholder="Write a broadcast message"
            aria-label="Broadcast message"
          />

          <Button type="button" variant="primary" onClick={onSend} className="shrink-0">
            <Send className="h-4 w-4" aria-hidden="true" />
            Send
          </Button>
        </div>
      </footer>
    </section>
  );
}

export function MeshChatLayout({
  peers,
  selectedPeerId,
  onSelectPeer,
  onAddDevice,
  messages,
  messageValue,
  onMessageChange,
  onSendMessage,
  onSendFile,
}: {
  peers: MeshChatPeer[];
  selectedPeerId?: string;
  onSelectPeer?: (peer: MeshChatPeer) => void;
  onAddDevice?: () => void;
  messages: MeshChatBroadcastMessage[];
  messageValue: string;
  onMessageChange: (value: string) => void;
  onSendMessage: () => void;
  onSendFile?: () => void;
}) {
  return (
    <main className="flex h-screen overflow-hidden bg-zinc-950">
      <MeshChatSidebar
        peers={peers}
        selectedPeerId={selectedPeerId}
        onSelectPeer={onSelectPeer}
        onAddDevice={onAddDevice}
      />
      <BroadcastPanel
        messages={messages}
        value={messageValue}
        onChange={onMessageChange}
        onSend={onSendMessage}
        onSendFile={onSendFile}
      />
    </main>
  );
}
