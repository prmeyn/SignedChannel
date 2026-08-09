namespace SignedChannel;

/// <summary>
/// What the browser posts to register a session: the two public keys it just generated, plus a
/// signature proving it holds the matching private key for the signing one.
/// </summary>
/// <param name="WebBrowserId">Stable per-browser id, persisted by the client across sessions.</param>
/// <param name="SignalRConnectionId">The presence connection this session starts with.</param>
/// <param name="SelectedLanguageIsoCode">Language the browser is asking to be served in.</param>
/// <param name="VerifyingPublicKeyBase64">Public key every later request is verified against.</param>
/// <param name="EncryptionPublicKeyRequestWithTimestampAsBase64">
/// Base64 JSON of <see cref="EncryptionPublicKeyWithTimestampRequest"/> — carried as an opaque
/// blob because it is the exact byte sequence the signature covers.
/// </param>
/// <param name="EncryptionPublicKeySignatureAsBase64">Signature over that blob.</param>
/// <param name="HashAlgorithm">Hash the signature was produced with.</param>
public sealed record SessionPublicKeysRegistrationRequest(
    string WebBrowserId,
    string SignalRConnectionId,
    string SelectedLanguageIsoCode,
    string VerifyingPublicKeyBase64,
    string EncryptionPublicKeyRequestWithTimestampAsBase64,
    string EncryptionPublicKeySignatureAsBase64,
    string HashAlgorithm);

/// <summary>
/// The signed inner blob of a registration: the encryption key, and when it was made. The
/// timestamp is what bounds how long a captured registration stays usable.
/// </summary>
public sealed record EncryptionPublicKeyWithTimestampRequest(
    string EncryptionPublicKeyBase64,
    DateTimeOffset TimeStampWithOffSetUTC);

/// <summary>
/// The handshake's answer. The session id comes back <em>encrypted to the browser's own
/// encryption key</em>, so it is established without ever crossing the wire in the clear — an
/// observer of the exchange cannot replay requests as that session.
/// </summary>
public sealed record SessionPublicKeysRegistrationResponse(string EncryptedSessionIdAsBase64);

/// <summary>
/// The envelope every browser request travels in. The signature covers the UTF-8 bytes of
/// <see cref="MessagePayloadRequestAsBase64"/> exactly as sent, which is why the payload stays a
/// string here and is only decoded after verification.
/// </summary>
public sealed record SignedWebBrowserPayloadRequest(
    string MessagePayloadRequestAsBase64,
    string SignatureAsBase64,
    string HashAlgorithm);

/// <summary>
/// The signed content: which action, on whose behalf, when, with what.
/// </summary>
/// <param name="SessionId">Session making the request; its key verifies the signature.</param>
/// <param name="SignalRConnectionId">Connection to address any push about this request to.</param>
/// <param name="ActionName">Wire name, <c>"A.B"</c>, resolved by namespace convention.</param>
/// <param name="TimeStampWithOffSetUTC">Signing time; outside the freshness window it is refused.</param>
/// <param name="PayloadAsBase64">Base64 JSON of the action's request type, or null when it takes none.</param>
public sealed record WebBrowserMessagePayloadRequest(
    string SessionId,
    string SignalRConnectionId,
    string ActionName,
    DateTimeOffset TimeStampWithOffSetUTC,
    string? PayloadAsBase64);
