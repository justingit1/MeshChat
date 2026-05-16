import {
  Bluetooth,
  ChevronLeft,
  ChevronRight,
  CircleDot,
  MessageCircle,
  Network,
  Plus,
  Radio,
  Search,
  Settings,
  Signal,
  Wifi,
} from "lucide-react";
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

type MeshChatSidebarProps = {
  peers: MeshChatPeer[];
  selectedPeerId?: string;
  onSelectPeer?: (peer: MeshChatPeer) => void;
  onAddDevice?: () => void;
  className?: string;
};

const statusDotStyles: Record<PeerStatus, string> = {
  online: "bg-emerald-500",
  away: "bg-amber-500",
  offline: "bg-slate-400",
};

const statusTextStyles: Record<PeerStatus, string> = {
  online: "text-emerald-700 dark:text-emerald-300",
  away: "text-amber-700 dark:text-amber-300",
  offline: "text-slate-500 dark:text-slate-400",
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
  if (signalStrength >= 75) return "text-emerald-600 dark:text-emerald-400";
  if (signalStrength >= 45) return "text-amber-600 dark:text-amber-400";
  return "text-rose-600 dark:text-rose-400";
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
        "flex h-full shrink-0 flex-col border-r border-slate-200 bg-white text-slate-950 transition-[width] duration-200 ease-out dark:border-slate-800 dark:bg-slate-950 dark:text-slate-50",
        collapsed ? "w-[72px]" : "w-80",
        className,
      )}
      aria-label="MeshChat devices"
    >
      <div className="flex h-16 items-center gap-3 border-b border-slate-200 px-4 dark:border-slate-800">
        <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-blue-600 text-white">
          <Network className="h-5 w-5" aria-hidden="true" />
        </div>

        {!collapsed && (
          <div className="min-w-0 flex-1">
            <h2 className="truncate text-base font-semibold tracking-normal">MeshChat</h2>
            <p className="truncate text-xs text-slate-500 dark:text-slate-400">
              {onlineCount} online / {peers.length} devices
            </p>
          </div>
        )}

        <button
          type="button"
          onClick={() => setCollapsed((value) => !value)}
          className="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-lg text-slate-500 outline-none transition hover:bg-slate-100 hover:text-slate-900 focus-visible:ring-2 focus-visible:ring-blue-500 dark:text-slate-400 dark:hover:bg-slate-900 dark:hover:text-slate-100"
          aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
        >
          {collapsed ? (
            <ChevronRight className="h-5 w-5" aria-hidden="true" />
          ) : (
            <ChevronLeft className="h-5 w-5" aria-hidden="true" />
          )}
        </button>
      </div>

      {!collapsed && (
        <div className="space-y-3 border-b border-slate-200 p-4 dark:border-slate-800">
          <div className="relative">
            <Search
              className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400"
              aria-hidden="true"
            />
            <input
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              type="search"
              placeholder="Search devices"
              className="h-11 w-full rounded-lg border border-slate-200 bg-white pl-10 pr-3 text-sm text-slate-950 outline-none transition placeholder:text-slate-400 focus:border-blue-500 focus:ring-2 focus:ring-blue-500/20 dark:border-slate-800 dark:bg-slate-900 dark:text-slate-50 dark:placeholder:text-slate-500"
            />
          </div>

          <button
            type="button"
            onClick={onAddDevice}
            className="inline-flex h-11 w-full items-center justify-center gap-2 rounded-lg bg-blue-600 px-3 text-sm font-medium text-white outline-none transition hover:bg-blue-700 focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 focus-visible:ring-offset-white active:bg-blue-800 dark:focus-visible:ring-offset-slate-950"
          >
            <Plus className="h-4 w-4" aria-hidden="true" />
            Add device
          </button>
        </div>
      )}

      <div className="min-h-0 flex-1 overflow-y-auto p-2">
        {filteredPeers.length > 0 ? (
          <div className="space-y-1">
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
                    "group flex w-full items-center gap-3 rounded-lg p-2 text-left outline-none transition focus-visible:ring-2 focus-visible:ring-blue-500",
                    selected
                      ? "bg-blue-50 text-blue-950 dark:bg-blue-950/40 dark:text-blue-100"
                      : "text-slate-700 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-900",
                  )}
                  aria-current={selected ? "page" : undefined}
                >
                  <div className="relative flex h-11 w-11 shrink-0 items-center justify-center rounded-lg bg-slate-100 text-sm font-semibold text-slate-700 dark:bg-slate-900 dark:text-slate-200">
                    {getInitial(peer.displayName)}
                    <span
                      className={cx(
                        "absolute -bottom-0.5 -right-0.5 h-3.5 w-3.5 rounded-full border-2 border-white dark:border-slate-950",
                        statusDotStyles[peer.status],
                      )}
                      aria-label={peer.status}
                    />
                  </div>

                  {!collapsed && (
                    <>
                      <div className="min-w-0 flex-1">
                        <div className="flex items-center gap-2">
                          <span className="truncate text-sm font-medium">{peer.displayName}</span>
                          {peer.hopsAway && peer.hopsAway > 1 && (
                            <span className="shrink-0 rounded-md bg-slate-100 px-1.5 py-0.5 font-mono text-[11px] text-slate-500 dark:bg-slate-800 dark:text-slate-400">
                              {peer.hopsAway}h
                            </span>
                          )}
                        </div>
                        <div className="mt-1 flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400">
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
                          <p className="mt-1 truncate text-xs text-slate-500 dark:text-slate-400">
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
                          <span className="min-w-5 rounded-full bg-blue-600 px-1.5 py-0.5 text-center text-[11px] font-semibold text-white">
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
            <div className="mb-3 flex h-11 w-11 items-center justify-center rounded-lg bg-slate-100 text-slate-500 dark:bg-slate-900 dark:text-slate-400">
              <MessageCircle className="h-5 w-5" aria-hidden="true" />
            </div>
            {!collapsed && (
              <>
                <p className="text-sm font-medium text-slate-900 dark:text-slate-100">No devices found</p>
                <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                  MeshChat is still scanning nearby peers.
                </p>
              </>
            )}
          </div>
        )}
      </div>

      <div className="border-t border-slate-200 p-3 dark:border-slate-800">
        <button
          type="button"
          className={cx(
            "flex h-11 w-full items-center rounded-lg text-sm text-slate-600 outline-none transition hover:bg-slate-100 hover:text-slate-950 focus-visible:ring-2 focus-visible:ring-blue-500 dark:text-slate-400 dark:hover:bg-slate-900 dark:hover:text-slate-100",
            collapsed ? "justify-center px-0" : "justify-start gap-3 px-3",
          )}
          aria-label="Open settings"
        >
          <Settings className="h-5 w-5 shrink-0" aria-hidden="true" />
          {!collapsed && <span className="font-medium">Settings</span>}
        </button>
      </div>
    </aside>
  );
}
