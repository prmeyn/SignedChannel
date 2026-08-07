import type { ChannelConnection } from './channel-connection.js';
import { CryptoCore } from './crypto-core.js';
import { SessionApi } from './session-api.js';
import { SessionStore } from './session-store.js';
import type { PushMessage } from './push-message.js';
import { fromBase64 } from './base64.js';

const WEB_BROWSER_ID_KEY = 'webBrowserId';

// Request action names — two-part, resolved by the server's namespace-convention
// dispatcher (PoC parity).
const ACTION_REGISTER_CONNECTION = 'WebBrowsers.RegisterConnectionAndGetStatus';
const ACTION_VERIFY_CONNECTION = 'WebBrowsers.VerifyConnectionId';
const ACTION_DOWNLOAD_PUSH = 'WebBrowsers.DownloadPushMessage';
const ACTION_KEEP_ALIVE = 'WebBrowsers.KeepAlive';

// The dispatcher's reason code when a signed-in session has expired server-side.
const UNAUTHORIZED_SESSION_EXPIRED = 'session_expired';

// Server→client push action names (PoC parity — note the "Action" suffix).
const PUSH_VERIFY_CONNECTION = 'WebBrowsers.VerifyConnectionIdAction';
const PUSH_DOWNLOAD = 'WebBrowsers.DownloadPushMessageAction';
const ACTION_CONNECTION_VERIFIED = 'ConnectionVerified';
// Remote sign-out (from the phone, or another tab of this session).
const PUSH_LOGOUT_SESSION = 'LogoutSession';

// Instruction discriminators inside a downloaded WebPushInstruction.
const INSTRUCTION_START_QR_CODE = 'StartQrCode';
const INSTRUCTION_LOGIN = 'Login';
const INSTRUCTION_CANCEL_AUTH = 'CancelAuth';

/** A phone number split the way SMSwitch's MobileNumber expects it. */
export interface MobileNumberInput {
  countryIsoCode: string;
  countryPhoneCode: string;
  phoneNumber: string;
}

/** Result of Authentication.InitiateAuthFlow — drives whether the OTP step is shown. */
export interface InitiateAuthFlowResult {
  sent: boolean;
  isFromTrustedBrowser: boolean;
  otpSmsSuccessfullySent: boolean;
  /** Trusted browser: ISO UTC instant the phone-approval window closes. */
  approvalExpiresAtUtc?: string;
  /** Untrusted browser: ISO UTC instant the SMS code stops being valid. */
  otpExpiresAtUtc?: string;
}

/** Result of Authentication.VerifyOTP — carries the approval deadline for the awaiting step. */
export interface VerifyOtpResult {
  verified: boolean;
  approvalExpiresAtUtc?: string;
}

/**
 * The authenticated channel — framework-agnostic. On connect it ensures a
 * registered session, registers this connection, answers the encrypted
 * VerifyConnectionId challenge, and becomes group-bound. Runs on top of one
 * {@link ChannelConnection}, so the same code serves Angular and the Razor page.
 */
export class AuthChannelCore {
  channelBound = false;
  isLoggedIn = false;
  onStateChange?: () => void;

  // Server-authoritative idle-session expiry (mirrored, never client-decided).
  /** Absolute UTC instant the session expires, learned from the server; null if unknown. */
  sessionExpiresAtUtc: string | null = null;
  /** Seconds before expiry to show the warning countdown (server-owned). */
  warningSeconds = 0;
  /** Fired whenever the known expiry changes (status, any action, or keep-alive). */
  onExpiryChange?: (expiresAtUtc: string | null, warningSeconds: number) => void;

  // Login-flow events (consumed by the login UI).
  onStartQrCode?: (code: string, expiresAtUtc?: string) => void;
  onLogin?: (payload: string | null) => void;
  onCancelAuth?: () => void;

  private started = false;

  constructor(
    private readonly connection: ChannelConnection,
    private readonly api: SessionApi,
    private readonly settings: SessionStore,
    private readonly crypto: CryptoCore
  ) {
    // A signed-in session that expired server-side comes back as a 401; drive the
    // same sign-out the phone/remote-logout path uses.
    this.api.onUnauthorized = (code) => {
      if (code === UNAUTHORIZED_SESSION_EXPIRED) {
        void this.logout();
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

  private async handshake(language: string): Promise<void> {
    const connectionId = this.connection.connectionId();
    if (!connectionId) {
      return;
    }
    this.settings.setConnectionId(connectionId);

    const webBrowserId = this.ensureWebBrowserId();

    let sessionId = this.settings.getSessionId();
    if (!sessionId) {
      sessionId = await this.api.registerSession(webBrowserId, connectionId, language);
      if (!sessionId) {
        return;
      }
      this.settings.setSessionId(sessionId);
    }

    const status = await this.send<{ isLoggedIn: boolean; sessionExpiresAtUtc?: string | null; warningSeconds?: number }>(
      ACTION_REGISTER_CONNECTION, { webBrowserId, connectionId });
    this.isLoggedIn = !!status?.isLoggedIn;
    if (status?.warningSeconds) {
      this.warningSeconds = status.warningSeconds;
    }
    this.updateExpiry(status?.sessionExpiresAtUtc ?? null);
    this.notify();
  }

  /**
   * Signed request with the page context every browser action carries
   * (MessageWebBrowserRequestBase: currentUrl + referrerUrl — PoC parity).
   * Public so surfaces (e.g. the /orgs portal) can call BusinessLogic actions.
   */
  async send<T>(actionName: string, payload: Record<string, unknown> = {}): Promise<T | null> {
    const result = await this.api.sendSignedRequest<T>(actionName, {
      currentUrl: window.location.href,
      referrerUrl: document.referrer || null,
      ...payload,
    });
    // Every signed-in response carries the freshly-slid expiry (dispatcher-stamped),
    // so the idle countdown tracks real API activity with no extra round-trip.
    const expiry = (result as { sessionExpiresAtUtc?: string | null } | null)?.sessionExpiresAtUtc;
    if (expiry !== undefined) {
      this.updateExpiry(expiry ?? null);
    }
    return result;
  }

  /**
   * Explicit "keep me signed in" (the warning modal's Stay button, and the idle
   * service's zero-boundary re-check). Slides the server session and returns the
   * fresh expiry — null means the server considers the session gone (must re-auth).
   */
  async keepAlive(): Promise<string | null> {
    const r = await this.send<{ keepAliveExpiresAtUtc?: string | null; warningSeconds?: number }>(ACTION_KEEP_ALIVE);
    if (r?.warningSeconds) {
      this.warningSeconds = r.warningSeconds;
    }
    // send() already applied the base sessionExpiresAtUtc; keepAliveExpiresAtUtc is
    // the same value surfaced explicitly for callers/tests.
    return r?.keepAliveExpiresAtUtc ?? this.sessionExpiresAtUtc;
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
      case PUSH_VERIFY_CONNECTION: {
        const token = await this.decrypt(message.encryptedPayloadAsBase64);
        if (token) {
          await this.send(ACTION_VERIFY_CONNECTION, { connectionVerificationCode: token });
        }
        break;
      }
      case ACTION_CONNECTION_VERIFIED:
        this.channelBound = true;
        this.notify();
        break;
      case PUSH_DOWNLOAD: {
        const pointer = await this.decrypt(message.encryptedPayloadAsBase64);
        if (pointer) {
          await this.downloadAndDispatch(pointer);
        }
        break;
      }
      case PUSH_LOGOUT_SESSION:
        // Remote sign-out (phone or another tab): same handling as the PoC —
        // confirm the logout to the server, clear this tab's keys, go to login.
        await this.logout();
        break;
      default:
        break;
    }
  }

  /**
   * Sign this tab out (PoC loginService.logout): tell the server (idempotent —
   * also drops the session_secret cookie and notifies the phone's session list),
   * clear the per-tab session keys, and land on the login screen.
   */
  async logout(): Promise<void> {
    if (this.loggingOut) {
      return; // idempotent — also stops onUnauthorized re-entry during the logout call
    }
    this.loggingOut = true;
    this.isLoggedIn = false;
    this.updateExpiry(null);
    this.notify();
    try {
      await this.send('Authentication.Logout');
    } catch {
      // Best-effort: the session may already be logged out server-side.
    }
    this.settings.resetKeyPairs(false, true);
    window.location.assign('/login/');
  }

  private loggingOut = false;

  /** Exchange the pushed pointer for the stored instruction, then dispatch it. */
  private async downloadAndDispatch(pointer: string): Promise<void> {
    const response = await this.send<{ instructionsAsBase64: string | null }>(
      ACTION_DOWNLOAD_PUSH, { pushMessageIdAndOtt: pointer });
    if (!response?.instructionsAsBase64) {
      return;
    }
    const instruction = JSON.parse(fromBase64(response.instructionsAsBase64)) as { instruction: string; payload?: string | null };

    switch (instruction.instruction) {
      case INSTRUCTION_START_QR_CODE: {
        // payload is QrCodeAuthenticationDetails { id, code, expiresAtUtc }.
        const details = instruction.payload
          ? (JSON.parse(instruction.payload) as { code: string; expiresAtUtc?: string })
          : null;
        if (details?.code) {
          this.onStartQrCode?.(details.code, details.expiresAtUtc);
        }
        break;
      }
      case INSTRUCTION_LOGIN:
        this.isLoggedIn = true;
        this.notify();
        this.onLogin?.(instruction.payload ?? null);
        break;
      case INSTRUCTION_CANCEL_AUTH:
        this.onCancelAuth?.();
        break;
      default:
        break;
    }
  }

  /**
   * Start the login flow for a phone number (browser action). A trusted browser
   * goes straight to the phone push; an untrusted one must verify an SMS OTP first
   * (isFromTrustedBrowser=false → the client shows the code entry, then verifyOtp).
   */
  async initiateAuthFlow(mobileNumber: MobileNumberInput, resendCooldownPeriodInSeconds = 60): Promise<InitiateAuthFlowResult> {
    const r = await this.send<InitiateAuthFlowResult>('Authentication.InitiateAuthFlow', { mobileNumber, resendCooldownPeriodInSeconds });
    return {
      sent: !!r?.sent,
      isFromTrustedBrowser: !!r?.isFromTrustedBrowser,
      otpSmsSuccessfullySent: !!r?.otpSmsSuccessfullySent,
      approvalExpiresAtUtc: r?.approvalExpiresAtUtc,
      otpExpiresAtUtc: r?.otpExpiresAtUtc,
    };
  }

  /** Untrusted-browser step: verify the SMS OTP; trustBrowser remembers this browser. */
  async verifyOtp(verificationCode: string, trustBrowser: boolean): Promise<VerifyOtpResult> {
    const r = await this.send<{ otpVerified: boolean; approvalExpiresAtUtc?: string }>(
      'Authentication.VerifyOTP', { verificationCode, trustBrowser });
    return { verified: !!r?.otpVerified, approvalExpiresAtUtc: r?.approvalExpiresAtUtc };
  }

  /**
   * Ask the server to stop trusting this browser (drop its remembered SMS-OTP
   * verifications) — used when the phone-approval window lapses, so the next
   * sign-in must verify an SMS code again.
   */
  async stopTrustingThisBrowser(): Promise<void> {
    await this.send('Authentication.StopTrustingThisBrowser');
  }

  /**
   * After the Login push: converts the verified signed channel into the IdP auth
   * cookie and returns where to navigate — the preserved OIDC authorize URL when
   * the login was part of an OIDC flow (rid), else the portal.
   */
  async completeSignIn(rid: string | null): Promise<string> {
    const response = await this.send<{ redirectUrl: string | null }>('Authentication.CompleteSignIn', { rid });
    return response?.redirectUrl || '/orgs';
  }

  private async decrypt(encryptedPayloadAsBase64: string): Promise<string | null> {
    if (!encryptedPayloadAsBase64) {
      return null;
    }
    const key = this.settings.getDecryptingPrivateKey();
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
