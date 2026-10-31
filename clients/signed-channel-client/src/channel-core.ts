import type { ChannelConnection } from './channel-connection.js';
import { CryptoCore } from './crypto-core.js';
import { SessionApi } from './session-api.js';
import { SessionStore } from './session-store.js';
import type { PushMessage } from './push-message.js';

const WEB_BROWSER_ID_KEY = 'webBrowserId';

// The channel's own wire names: everything under "Channel." is the package's, on both halves.
// Requests resolve to the server's built-in actions; pushes are the server's lifecycle messages.
const REQUEST_REGISTER = 'Channel.Register';
const REQUEST_VERIFY_CONNECTION = 'Channel.VerifyConnection';
const REQUEST_DOWNLOAD_INSTRUCTION = 'Channel.DownloadInstruction';
const REQUEST_KEEP_ALIVE = 'Channel.KeepAlive';
const REQUEST_SIGN_OUT = 'Channel.SignOut';

const PUSH_CONNECTION_CHALLENGE = 'Channel.ConnectionChallenge';
const PUSH_CONNECTION_VERIFIED = 'Channel.ConnectionVerified';
const PUSH_INSTRUCTION = 'Channel.Instruction';
const PUSH_SIGNED_IN = 'Channel.SignedIn';
const PUSH_SIGNED_OUT = 'Channel.SignedOut';

// The dispatcher's reason codes (server: ChannelNames.SessionExpiredCode / SessionEndedCode).
// Expired: a signed-in session ran out its window. Ended: the session was signed out — this tab
// missed the push — and is never usable again; the tab must start a new session.
const UNAUTHORIZED_SESSION_EXPIRED = 'session_expired';
const UNAUTHORIZED_SESSION_ENDED = 'session_ended';

/** Receives an instruction's payload — usually JSON, as the server-side code that pushed it wrote it. */
export type InstructionHandler = (payload: string | null) => void;

/**
 * The browser half of the channel, framework-agnostic. On connect it ensures a registered session,
 * registers the connection, answers the encrypted connection challenge and becomes bound to the
 * session's group. After that it carries the application's signed requests ({@link send}) and
 * delivers the application's instructions ({@link on}).
 *
 * Runs on top of one {@link ChannelConnection}, so the same code serves a framework service, a
 * plain script on a server-rendered page, or a test harness.
 */
export class ChannelCore {
  /** Whether this tab's connection has joined its session's group (session-wide pushes reach it). */
  channelBound = false;
  /** Whether the session is signed in, as the server last said. */
  isSignedIn = false;
  /** Fired when {@link channelBound} or {@link isSignedIn} changes. */
  onStateChange?: () => void;

  /** Absolute UTC instant the session expires, learned from the server; null if unknown. */
  sessionExpiresAtUtc: string | null = null;
  /** Seconds before expiry to show a warning (server-owned). */
  warningSeconds = 0;
  /** Fired whenever the known expiry changes (status, any action, or keep-alive). */
  onExpiryChange?: (expiresAtUtc: string | null, warningSeconds: number) => void;

  /** Fired when the session is signed in — by this tab's flow or any other. */
  onSignedIn?: () => void;
  /**
   * Fired once this tab has finished signing out — on request, remotely, or because the session
   * expired. Typically navigates to a sign-in page.
   */
  onSignedOut?: () => void;

  private readonly handlers = new Map<string, Set<InstructionHandler>>();
  private started = false;
  private signingOut = false;

  constructor(
    private readonly connection: ChannelConnection,
    private readonly api: SessionApi,
    private readonly settings: SessionStore,
    private readonly crypto: CryptoCore
  ) {
    // A session that expired, or was signed out while this tab missed the push, comes back as a
    // 401; drive the same sign-out a remote one gets. That drops the keys, so the next page load
    // starts a NEW session rather than signing the ended one in again.
    this.api.onUnauthorized = (code) => {
      if (code === UNAUTHORIZED_SESSION_EXPIRED || code === UNAUTHORIZED_SESSION_ENDED) {
        void this.signOut();
      }
    };
  }

  start(language = 'en'): void {
    if (this.started) {
      return;
    }
    this.started = true;

    this.connection.start();
    this.connection.on('ReceiveEncryptedMessage', (...args: unknown[]) => {
      void this.handlePush(args[0] as PushMessage);
    });
    this.connection.onConnected(() => {
      void this.handshake(language);
    });
  }

  /**
   * Subscribes to an instruction the server pushes with `PushInstructionAsync(sessionId, name, …)`.
   * Any number of handlers may listen to one name. Returns the unsubscribe function.
   */
  on(name: string, handler: InstructionHandler): () => void {
    let set = this.handlers.get(name);
    if (!set) {
      set = new Set();
      this.handlers.set(name, set);
    }
    set.add(handler);
    return () => {
      set.delete(handler);
    };
  }

  /**
   * Signed request with the page context every browser action carries (currentUrl and
   * referrerUrl). Resolves null-ish on rejection, so callers optional-chain the result.
   */
  async send<T>(actionName: string, payload: Record<string, unknown> = {}): Promise<T | null> {
    const result = await this.api.sendSignedRequest<T>(actionName, {
      currentUrl: window.location.href,
      referrerUrl: document.referrer || null,
      ...payload,
    });
    // Every signed-in response carries the freshly slid expiry (dispatcher-stamped), so the idle
    // countdown tracks real API activity with no extra round-trip.
    const expiry = (result as { sessionExpiresAtUtc?: string | null } | null)?.sessionExpiresAtUtc;
    if (expiry !== undefined) {
      this.updateExpiry(expiry ?? null);
    }
    return result;
  }

  /**
   * "Keep me signed in": slides the server session and returns the fresh expiry. Null means the
   * server considers the session signed out.
   */
  async keepAlive(): Promise<string | null> {
    const r = await this.send<{ sessionExpiresAtUtc?: string | null; warningSeconds?: number }>(REQUEST_KEEP_ALIVE);
    if (r?.warningSeconds) {
      this.warningSeconds = r.warningSeconds;
    }
    // send() has already applied the stamped expiry, null for a signed-out session.
    return this.sessionExpiresAtUtc;
  }

  /**
   * Signs this tab out: tells the server (which tells the session's other tabs), clears the tab's
   * session keys, then fires {@link onSignedOut}. Idempotent.
   */
  async signOut(): Promise<void> {
    if (this.signingOut) {
      return; // also stops onUnauthorized re-entering during the sign-out request
    }
    this.signingOut = true;
    this.isSignedIn = false;
    this.updateExpiry(null);
    this.notify();
    try {
      await this.send(REQUEST_SIGN_OUT);
    } catch {
      // Best-effort: the session may already be signed out server-side.
    }
    await this.settings.resetKeyPairs(false, true);
    this.onSignedOut?.();
  }

  private async handshake(language: string): Promise<void> {
    const connectionId = this.connection.connectionId();
    if (!connectionId) {
      return;
    }
    this.settings.setConnectionId(connectionId);

    const webBrowserId = this.ensureWebBrowserId();

    let sessionId = this.settings.getSessionId();
    // A session id with no keys behind it can never sign: the keys lived only in the previous
    // page's memory (IndexedDB unusable), or this tab predates key storage in IndexedDB. Start over
    // with a fresh session.
    if (sessionId && !(await this.settings.getSigningPrivateKey())) {
      sessionId = null;
      this.settings.setIsLoggedIn(false);
    }
    if (!sessionId) {
      sessionId = await this.api.registerSession(webBrowserId, connectionId, language);
      if (!sessionId) {
        return;
      }
      this.settings.setSessionId(sessionId);
    }

    const status = await this.send<{ isSignedIn: boolean; warningSeconds?: number }>(REQUEST_REGISTER);
    this.isSignedIn = !!status?.isSignedIn;
    if (status?.warningSeconds) {
      this.warningSeconds = status.warningSeconds;
    }
    if (!this.isSignedIn) {
      this.updateExpiry(null);
    }
    this.notify();
  }

  private updateExpiry(expiresAtUtc: string | null): void {
    if (expiresAtUtc === this.sessionExpiresAtUtc) {
      return;
    }
    this.sessionExpiresAtUtc = expiresAtUtc;
    this.onExpiryChange?.(this.sessionExpiresAtUtc, this.warningSeconds);
  }

  private async handlePush(message: PushMessage): Promise<void> {
    if (!message?.actionName) {
      return;
    }
    if (message.expiryTimestampInUtc && new Date(message.expiryTimestampInUtc).getTime() < Date.now()) {
      return;
    }

    switch (message.actionName) {
      case PUSH_CONNECTION_CHALLENGE: {
        const token = await this.decrypt(message.encryptedPayloadAsBase64);
        if (token) {
          await this.send(REQUEST_VERIFY_CONNECTION, { token });
        }
        break;
      }
      case PUSH_CONNECTION_VERIFIED:
        this.channelBound = true;
        this.notify();
        break;
      case PUSH_INSTRUCTION: {
        const pointer = await this.decrypt(message.encryptedPayloadAsBase64);
        if (pointer) {
          await this.downloadAndDispatch(pointer);
        }
        break;
      }
      case PUSH_SIGNED_IN:
        this.isSignedIn = true;
        this.notify();
        this.onSignedIn?.();
        break;
      case PUSH_SIGNED_OUT:
        // Signed out elsewhere — another tab, a companion device, an IdP logout. Confirming it to
        // the server is harmless (idempotent) and lets the server clear this browser's cookies.
        await this.signOut();
        break;
      default:
        break;
    }
  }

  private async downloadAndDispatch(pointer: string): Promise<void> {
    const instruction = await this.send<{ name: string | null; payload: string | null }>(
      REQUEST_DOWNLOAD_INSTRUCTION, { pointer });
    if (!instruction?.name) {
      return; // expired, or not this session's
    }
    for (const handler of [...(this.handlers.get(instruction.name) ?? [])]) {
      try {
        handler(instruction.payload ?? null);
      } catch (error) {
        // One failing handler must not keep the instruction from the others.
        console.error(`Handler for instruction '${instruction.name}' failed`, error);
      }
    }
  }

  private async decrypt(encryptedPayloadAsBase64: string): Promise<string | null> {
    if (!encryptedPayloadAsBase64) {
      return null;
    }
    const key = await this.settings.getDecryptingPrivateKey();
    if (!key) {
      return null;
    }
    return this.crypto.decryptEncryptedStringAsBase64(encryptedPayloadAsBase64, key);
  }

  private ensureWebBrowserId(): string {
    let id = localStorage.getItem(WEB_BROWSER_ID_KEY);
    if (!id) {
      id = crypto.randomUUID();
      localStorage.setItem(WEB_BROWSER_ID_KEY, id);
    }
    return id;
  }

  private notify(): void {
    this.onStateChange?.();
  }
}
