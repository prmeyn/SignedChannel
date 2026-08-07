interface StoredKeys {
  signingPrivateKey?: JsonWebKey;
  decryptingPrivateKey?: JsonWebKey;
}

/**
 * Browser-storage access for the session — framework-agnostic. Private keys +
 * sessionId live in sessionStorage (ephemeral, per-tab, shared across same-origin
 * navigation within a tab); webBrowserId in localStorage (stable, non-secret).
 */
export class SessionStore {
  private readonly SESSION_KEY = 'sessionSettings';
  private readonly SESSION_STORAGE_KEY = 'sessionStorageId';
  private readonly CONNECTION_STORAGE_KEY = 'connectionId';
  private readonly IS_LOGGED_IN_KEY = 'isLoggedIn';
  private readonly RETURN_URL_KEY = 'returnUrl';

  private getKeys(): StoredKeys {
    return JSON.parse(sessionStorage.getItem(this.SESSION_KEY) || '{}');
  }

  getConnectionId(): string | null {
    return sessionStorage.getItem(this.CONNECTION_STORAGE_KEY);
  }
  setConnectionId(connectionId: string): void {
    sessionStorage.setItem(this.CONNECTION_STORAGE_KEY, connectionId);
  }

  getSessionId(): string | null {
    return sessionStorage.getItem(this.SESSION_STORAGE_KEY);
  }
  setSessionId(sessionId: string): void {
    sessionStorage.setItem(this.SESSION_STORAGE_KEY, sessionId);
  }

  getIsLoggedIn(): boolean {
    const value = sessionStorage.getItem(this.IS_LOGGED_IN_KEY);
    return value ? JSON.parse(value) : false;
  }
  setIsLoggedIn(isLoggedIn: boolean): void {
    sessionStorage.setItem(this.IS_LOGGED_IN_KEY, JSON.stringify(isLoggedIn));
  }

  getReturnUrl(): string | null {
    return localStorage.getItem(this.RETURN_URL_KEY);
  }
  setReturnUrl(returnUrl: string): void {
    if (!returnUrl || returnUrl.trim() === '' || returnUrl.trim() === '/' || returnUrl.trim() === '/login') {
      return;
    }
    localStorage.setItem(this.RETURN_URL_KEY, returnUrl);
  }
  clearReturnUrl(): void {
    localStorage.removeItem(this.RETURN_URL_KEY);
  }

  resetKeyPairs(clearLocalStorage = false, dontRefresh = false): void {
    if (clearLocalStorage) {
      localStorage.clear();
    }
    sessionStorage.clear();
    if (!dontRefresh) {
      setTimeout(() => window.location.assign('/'), 10);
    }
  }

  getSigningPrivateKey(): JsonWebKey | undefined {
    return this.getKeys().signingPrivateKey;
  }
  saveSigningPrivateKey(signingPrivateKey: JsonWebKey): void {
    const keys = this.getKeys();
    keys.signingPrivateKey = signingPrivateKey;
    sessionStorage.setItem(this.SESSION_KEY, JSON.stringify(keys));
  }

  getDecryptingPrivateKey(): JsonWebKey | undefined {
    return this.getKeys().decryptingPrivateKey;
  }
  saveDecryptingPrivateKey(decryptingPrivateKey: JsonWebKey): void {
    const keys = this.getKeys();
    keys.decryptingPrivateKey = decryptingPrivateKey;
    sessionStorage.setItem(this.SESSION_KEY, JSON.stringify(keys));
  }
}
