// Contracts
export type { ChannelConnection } from './channel-connection.js';
export type { PushMessage } from './push-message.js';

// The channel
export { AuthChannelCore } from './auth-channel-core.js';
export type { InitiateAuthFlowResult, MobileNumberInput, VerifyOtpResult } from './auth-channel-core.js';
export { SessionApi } from './session-api.js';
export { SessionStore } from './session-store.js';
export { CryptoCore } from './crypto-core.js';

// SignalR transport
export { SignalRChannelConnection } from './signalr-connection.js';
export type {
  ConnectionStatus,
  HubConnectionLike,
  SignalRLike,
  SignalRChannelConnectionOptions,
} from './signalr-connection.js';

// Helpers whose exact byte handling the signing protocol depends on
export { toBase64, fromBase64, bytesToBase64, base64ToBytes } from './base64.js';
