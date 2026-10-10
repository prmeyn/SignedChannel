/** The session's two private keys — non-extractable, usable in place only. */
export interface SessionKeys {
  signingPrivateKey: CryptoKey;
  decryptingPrivateKey: CryptoKey;
}

interface StoredKeyRecord extends SessionKeys {
  id: string;
  createdAt: number;
}

const DB_NAME = 'signed-channel';
const DB_VERSION = 1;
const KEY_STORE = 'sessionKeys';

/**
 * Browser-storage access for the session — framework-agnostic. The sessionId lives
 * in sessionStorage (ephemeral, per-tab, shared across same-origin navigation within
 * a tab); webBrowserId in localStorage (stable, non-secret).
 *
 * The private keys are non-extractable CryptoKeys, so they cannot go in
 * sessionStorage (strings only). They are structured-cloned into IndexedDB under a
 * random per-tab id, and that id is what sessionStorage holds — which keeps the
 * per-tab semantics, since IndexedDB itself is shared by every tab of the origin.
 * Where IndexedDB is unusable (some private-browsing modes) the keys stay in memory
 * only: a reload then finds a sessionId with no keys and registers a fresh session.
 */
export class SessionStore {
  private readonly SESSION_STORAGE_KEY = 'sessionStorageId';
  private readonly KEYS_ID_KEY = 'sessionKeysId';
  private readonly CONNECTION_STORAGE_KEY = 'connectionId';
  private readonly IS_LOGGED_IN_KEY = 'isLoggedIn';
  private readonly RETURN_URL_KEY = 'returnUrl';
  // Where earlier versions kept the private keys, as extractable JWKs.
  private readonly LEGACY_KEYS_KEY = 'sessionSettings';

  /**
   * How long a key record may sit in IndexedDB before it is swept. Records are
   * deleted on sign-out, but a tab closed while signed in leaves one behind; the
   * sweep reclaims those. Keep it above the server's absolute session lifetime
   * (SessionExpiryOptions.AbsoluteSeconds, 8 hours by default). Default 24 hours.
   */
  staleKeyRecordMaxAgeSeconds = 86400;

  // This tab's keys once loaded — a cache in front of IndexedDB, and the only copy
  // when IndexedDB is unusable.
  private keys: SessionKeys | null = null;
  private db: Promise<IDBDatabase | null> | null = null;

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

  /**
   * Clears this tab's session and deletes its key record. The synchronous part runs
   * before the first await, so a request issued while the delete is in flight
   * already sees no keys.
   */
  async resetKeyPairs(clearLocalStorage = false, dontRefresh = false): Promise<void> {
    const keysId = sessionStorage.getItem(this.KEYS_ID_KEY);
    this.keys = null;
    if (clearLocalStorage) {
      localStorage.clear();
    }
    sessionStorage.clear();
    if (keysId) {
      await this.deleteRecord(keysId);
    }
    if (!dontRefresh) {
      setTimeout(() => window.location.assign('/'), 10);
    }
  }

  /** Persists a freshly generated pair for this tab, replacing any previous one. */
  async saveKeys(keys: SessionKeys): Promise<void> {
    this.dropLegacyKeys();
    this.keys = keys;
    const previousId = sessionStorage.getItem(this.KEYS_ID_KEY);
    sessionStorage.removeItem(this.KEYS_ID_KEY);

    const db = await this.openDb();
    if (!db) {
      return; // memory only
    }
    const id = crypto.randomUUID();
    const record: StoredKeyRecord = { id, ...keys, createdAt: Date.now() };
    try {
      const tx = db.transaction(KEY_STORE, 'readwrite');
      tx.objectStore(KEY_STORE).put(record);
      if (previousId) {
        tx.objectStore(KEY_STORE).delete(previousId);
      }
      await completion(tx);
      sessionStorage.setItem(this.KEYS_ID_KEY, id);
    } catch (e) {
      console.warn('SignedChannel: could not persist session keys; they will not survive a reload.', e);
    }
  }

  async getSigningPrivateKey(): Promise<CryptoKey | undefined> {
    return (await this.loadKeys())?.signingPrivateKey;
  }

  async getDecryptingPrivateKey(): Promise<CryptoKey | undefined> {
    return (await this.loadKeys())?.decryptingPrivateKey;
  }

  private async loadKeys(): Promise<SessionKeys | null> {
    if (this.keys) {
      return this.keys;
    }
    this.dropLegacyKeys();
    const id = sessionStorage.getItem(this.KEYS_ID_KEY);
    if (!id) {
      return null;
    }
    const db = await this.openDb();
    if (!db) {
      return null;
    }
    try {
      const record = await result<StoredKeyRecord | undefined>(
        db.transaction(KEY_STORE, 'readonly').objectStore(KEY_STORE).get(id));
      // Re-check: a reset may have run while the read was in flight.
      if (record && sessionStorage.getItem(this.KEYS_ID_KEY) === id) {
        this.keys = { signingPrivateKey: record.signingPrivateKey, decryptingPrivateKey: record.decryptingPrivateKey };
      }
    } catch (e) {
      console.warn('SignedChannel: could not read session keys.', e);
    }
    return this.keys;
  }

  private async deleteRecord(id: string): Promise<void> {
    const db = await this.openDb();
    if (!db) {
      return;
    }
    try {
      const tx = db.transaction(KEY_STORE, 'readwrite');
      tx.objectStore(KEY_STORE).delete(id);
      await completion(tx);
    } catch {
      // Left for the stale-record sweep.
    }
  }

  /** Extractable JWKs from an earlier version are never read — removing them is the point. */
  private dropLegacyKeys(): void {
    sessionStorage.removeItem(this.LEGACY_KEYS_KEY);
  }

  private openDb(): Promise<IDBDatabase | null> {
    this.db ??= new Promise<IDBDatabase | null>((resolve) => {
      let request: IDBOpenDBRequest;
      try {
        if (typeof indexedDB === 'undefined') {
          resolve(null);
          return;
        }
        request = indexedDB.open(DB_NAME, DB_VERSION);
      } catch {
        resolve(null);
        return;
      }
      request.onupgradeneeded = () => {
        request.result.createObjectStore(KEY_STORE, { keyPath: 'id' });
      };
      request.onsuccess = () => {
        const db = request.result;
        // Never hold up a future schema upgrade opened by a newer version in another tab.
        db.onversionchange = () => db.close();
        void this.sweepStaleRecords(db);
        resolve(db);
      };
      request.onerror = () => resolve(null);
      request.onblocked = () => resolve(null);
    });
    return this.db;
  }

  private async sweepStaleRecords(db: IDBDatabase): Promise<void> {
    const cutoff = Date.now() - this.staleKeyRecordMaxAgeSeconds * 1000;
    const ownId = sessionStorage.getItem(this.KEYS_ID_KEY);
    try {
      const tx = db.transaction(KEY_STORE, 'readwrite');
      const cursorRequest = tx.objectStore(KEY_STORE).openCursor();
      cursorRequest.onsuccess = () => {
        const cursor = cursorRequest.result;
        if (!cursor) {
          return;
        }
        const record = cursor.value as StoredKeyRecord;
        if (record.id !== ownId && !(record.createdAt >= cutoff)) {
          cursor.delete();
        }
        cursor.continue();
      };
      await completion(tx);
    } catch {
      // Best effort; the next page load tries again.
    }
  }
}

function result<T>(request: IDBRequest): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result as T);
    request.onerror = () => reject(request.error);
  });
}

function completion(tx: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error ?? new DOMException('Transaction aborted', 'AbortError'));
  });
}
