/**
 * The connection the channel needs, reduced to four members.
 *
 * The channel is defined against this rather than against SignalR directly, so the same channel
 * implementation serves any host: an Angular service, a plain script on a server-rendered page,
 * or a test harness with no socket at all. A default SignalR implementation ships alongside;
 * this interface is what makes it replaceable rather than assumed.
 */
export interface ChannelConnection {
  /** The current connection id, or null before the connection is established. */
  readonly connectionId: () => string | null;

  /** Subscribe to a server→client method. */
  on(methodName: string, handler: (...args: unknown[]) => void): void;

  /** Run a callback once the connection is up, and again after any reconnect. */
  onConnected(callback: () => void): void;

  /** Begin connecting. */
  start(): void;
}
