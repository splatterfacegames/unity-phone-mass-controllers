// Type declarations for pmc.js (godot-phone-mass-controllers controller SDK).

export type PMCStatus = 'connecting' | 'open' | 'reconnecting' | 'closed';

export type PMCRejectCode = 'bad_code' | 'full' | 'version' | 'banned' | 'bad_hello' | (string & {});

export interface PMCOptions {
  /** Display name sent in hello. */
  name?: string;
  /** Arbitrary JSON profile (e.g. colour, avatar) sent in hello. */
  profile?: Record<string, unknown>;
  /** Join code. Default: `?code=` from `location.search`, or ''. */
  code?: string;
  /** WebSocket URL. Default: `ws(s)://<location.host>/pmc/ws` (wss when the page is https). */
  url?: string;
  /** localStorage key for the rejoin token. Default: `pmc.token:<host of url>`. */
  tokenKey?: string;
}

export interface PMCWelcome {
  t: 'pmc.welcome';
  id: number;
  token: string;
  name: string;
  profile: Record<string, unknown>;
  rejoined: boolean;
  admin: boolean;
  /** Host clock, Unix epoch ms. */
  server_ms: number;
  join_url: string;
}

export interface PMCEventMap {
  welcome: PMCWelcome;
  /** JSON game message payload (`d`). */
  message: any;
  binary: ArrayBuffer;
  status: PMCStatus;
  reject: { code: PMCRejectCode; reason: string };
  kicked: string;
  replaced: undefined;
  /** Result of any `pmc.auth` reply. */
  auth: boolean;
  /** The join URL changed (ephemeral tunnel). The page may auto-follow https→https; otherwise show "re-scan the QR". */
  moved: { url: string };
}

export declare class PMCClient {
  constructor(opts?: PMCOptions);
  /** Player id assigned by the host; null before the first welcome. */
  id: number | null;
  name: string;
  profile: Record<string, unknown>;
  /** True once the host granted admin (welcome.admin or a successful auth). */
  admin: boolean;
  joinUrl: string;
  status: PMCStatus;
  code: string;
  url: string;
  /** Stored rejoin token ('' if none). Assign '' to forget it. */
  token: string;

  on<K extends keyof PMCEventMap>(event: K, fn: (arg: PMCEventMap[K]) => void): this;
  off<K extends keyof PMCEventMap>(event: K, fn: (arg: PMCEventMap[K]) => void): this;

  /** Send a JSON game message. Returns false (and drops it) when not connected. */
  send(data: unknown): boolean;
  /** Send a raw binary frame. Returns false when not connected. */
  sendBinary(data: ArrayBuffer | ArrayBufferView | Blob): boolean;
  /** Request admin elevation. Resolves false on a wrong PIN, lockout or disconnect. */
  auth(pin: string): Promise<boolean>;
  /** Update identity; remembered for future hellos. */
  setProfile(p: { name?: string; profile?: Record<string, unknown> }): void;
  /** Explicit leave (no grace). Stops reconnecting; the token is kept. */
  leave(): void;
  /** Resume connecting after the client stopped (reject, kick, replaced, leave). */
  reconnect(): void;
  /** Host clock (Unix epoch ms), corrected by the median of the last 5 ping offset samples. */
  serverNow(): number;
  /** Host-clock timestamp for stamping inputs (same clock as serverNow). */
  timestamp(): number;
  /** Rolling average round-trip time in ms from ping/pong (0 until the first pong). */
  readonly rttMs: number;
}

export declare function connect(opts?: PMCOptions): PMCClient;

/** Feature-detected `navigator.vibrate`. Returns false where unsupported (e.g. iOS Safari). */
export declare function vibrate(pattern: number | number[]): boolean;

/** Feedback kind for `feedback()`. */
export type PMCFeedbackKind = 'buzz' | 'success' | 'error' | (string & {});

/**
 * Tactile-ish feedback: vibrates where supported, else a 60 ms screen flash plus a short
 * WebAudio click (audio only after a user gesture). Returns which channel fired.
 */
export declare function feedback(kind?: PMCFeedbackKind): 'vibrate' | 'flash' | false;

/** Request a screen wake lock (secure contexts only). Resolves false silently when unavailable. */
export declare function wakeLock(): Promise<boolean>;

/**
 * Keep the screen on during play: wake lock where allowed, else a muted looping clip that
 * starts on the next user gesture (NoSleep-style). Resolves false only when neither can run.
 */
export declare function keepScreenOn(): Promise<boolean>;
