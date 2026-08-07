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

    // Disconnect promptly on a real page unload (a redirect, a refresh, closing the tab) so the
    // server drops this connection immediately instead of waiting out the SignalR timeout —
    // anything counting live connections would otherwise show a phantom. `persisted` means the
    // page is being frozen into the bfcache and may come back, so leave that one alone.
    if (typeof window !== 'undefined') {
      window.addEventListener('pagehide', (event) => {
        if (!event.persisted) {
          void this.connection?.stop();
        }
      });
    }

    this.setStatus('Connecting');
    this.connection
      .start()
      .then(() => this.markConnected())
      .catch(() => this.setStatus('Disconnected'));
  }

  private markConnected(): void {
    this.setStatus('Connected');
    this.id = this.connection?.connectionId ?? null;
    for (const callback of this.connectedCallbacks) {
      callback();
    }
  }

  private setStatus(status: ConnectionStatus): void {
    this.options.onStatusChange?.(status);
  }
}
