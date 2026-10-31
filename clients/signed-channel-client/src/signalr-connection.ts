import type { ChannelConnection } from './channel-connection.js';

/** Connection lifecycle, suitable for driving a status indicator. */
export type ConnectionStatus = 'Disconnected' | 'Connecting' | 'Connected';

/** The slice of a SignalR HubConnection this uses. */
export interface HubConnectionLike {
  connectionId: string | null;
  on(method: string, handler: (...args: unknown[]) => void): void;
  onreconnecting(cb: (err?: unknown) => void): void;
  onreconnected(cb: (id?: string) => void): void;
  onclose(cb: (err?: unknown) => void): void;
  start(): Promise<void>;
  stop(): Promise<void>;
}

/** The slice of the SignalR module this uses. */
export interface SignalRLike {
  HubConnectionBuilder: new () => {
    withUrl(url: string, options: { withCredentials: boolean }): {
      withAutomaticReconnect(): { build(): HubConnectionLike };
    };
  };
}

export interface SignalRChannelConnectionOptions {
  /**
   * The SignalR module. Injected rather than imported so this package pulls in no SignalR
   * dependency of its own: a bundled host passes the import, and a page that loads
   * signalr.min.js from a script tag passes the global. Both get the same behaviour without
   * the second one bundling a copy.
   */
  signalR: SignalRLike;

  /** Hub path. Defaults to the server package's own route. */
  hubUrl?: string;

  /** Send cookies with the negotiate/connect requests. Defaults to true. */
  withCredentials?: boolean;

  /** Called on every lifecycle change, for a badge or a signal. */
  onStatusChange?: (status: ConnectionStatus) => void;
}

/**
 * A {@link ChannelConnection} over SignalR, with automatic reconnect.
 *
 * Handlers registered through {@link on} survive reconnects, and {@link onConnected} fires again
 * after each one — the channel re-runs its handshake there, because a reconnect means a new
 * connection id that the server has not yet associated with the session.
 */
export class SignalRChannelConnection implements ChannelConnection {
  private connection?: HubConnectionLike;
  private id: string | null = null;
  private started = false;
  /** The page-hide stop in flight; a restore waits for it, since a stopping connection cannot start. */
  private stopping: Promise<void> | null = null;
  private readonly connectedCallbacks: Array<() => void> = [];

  constructor(private readonly options: SignalRChannelConnectionOptions) {}

  connectionId = (): string | null => this.id;

  on(methodName: string, handler: (...args: unknown[]) => void): void {
    this.connection?.on(methodName, handler);
  }

  onConnected(callback: () => void): void {
    this.connectedCallbacks.push(callback);
    if (this.id) {
      callback();
    }
  }

  start(): void {
    if (this.started) {
      return; // idempotent: one connection per host, never two
    }
    this.started = true;

    const { signalR, hubUrl = '/hubs/presence', withCredentials = true } = this.options;
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, { withCredentials })
      .withAutomaticReconnect()
      .build();

    this.connection.onreconnecting(() => this.setStatus('Connecting'));
    this.connection.onreconnected(() => this.markConnected());
    this.connection.onclose(() => this.setStatus('Disconnected'));

    // Disconnect on EVERY page hide — a redirect, a refresh, closing the tab, and also the page
    // being frozen into the back/forward cache (`persisted`) — so the server drops the connection
    // at once instead of counting a phantom. Leaving a cached page's connection open (as before
    // 10.3.1) kept it alive for as long as the browser kept the page: Chrome caches pages with a
    // WebSocket and keeps them 10 minutes, so a page left behind (the login page after signing
    // in) read as open on the server all that time. A page restored from the cache reconnects
    // instead (pageshow below), which the channel treats like any reconnect: a new handshake.
    if (typeof window !== 'undefined') {
      window.addEventListener('pagehide', () => {
        this.stopping = this.connection?.stop().catch(() => undefined) ?? null;
      });
      window.addEventListener('pageshow', (event) => {
        if (event.persisted) {
          void (this.stopping ?? Promise.resolve()).then(() => this.connect());
        }
      });
    }

    this.connect();
  }

  private connect(): void {
    this.setStatus('Connecting');
    this.connection
      ?.start()
      .then(() => this.markConnected())
      .catch(() => this.setStatus('Disconnected'));
  }

  private markConnected(): void {
    // The id is adopted BEFORE 'Connected' is announced. Anything reacting to that status has to
    // be able to read the connection id — the channel's handshake registers the session with it
    // and gives up if it is missing — so announcing the status first publishes a state that is
    // briefly a lie.
    this.id = this.connection?.connectionId ?? null;
    this.setStatus('Connected');
    for (const callback of this.connectedCallbacks) {
      callback();
    }
  }

  private setStatus(status: ConnectionStatus): void {
    this.options.onStatusChange?.(status);
  }
}
