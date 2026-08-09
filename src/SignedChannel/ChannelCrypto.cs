using System.Text.Json;
using Meyn.Utilities;
using PublicKeyUtils.CryptoKeys;

namespace SignedChannel;

/// <summary>
/// The channel's cryptographic primitives: base64-JSON encoding, session-id derivation, and
/// signature verification.
/// <para>
/// These are public because an application often speaks the same protocol over a second transport
/// — a companion mobile app, a background worker — and that code must verify signatures exactly as
/// the browser dispatcher does. A second implementation of a security check is a second place for
/// it to be subtly wrong.
/// </para>
/// </summary>
public static class ChannelCrypto
{
    // Case-insensitive: the inner JSON is produced by different clients with different casing
    // conventions, and all of them have to land in the same records.
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Decodes base64 JSON into <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">The payload decoded to null.</exception>
    public static T Deserialize<T>(string objectAsBase64) =>
        JsonSerializer.Deserialize<T>(CryptoUtils.FromBase64(objectAsBase64), CaseInsensitive)
            ?? throw new InvalidOperationException("Deserialization returned null");

    /// <summary>Encodes a value as base64 JSON.</summary>
    public static string Serialize<T>(T objectToBeSerialized) =>
        CryptoUtils.ToBase64(JsonSerializer.Serialize(objectToBeSerialized));

    /// <summary>
    /// The session id: <c>base64(SHA-512("{verifying}##{encryption}"))</c>.
    /// <para>
    /// Derived from the two public keys and nothing else. That is deliberate — the id is not a
    /// secret and travels in every request, so it must not be derived from anything about the
    /// person. It also makes registration naturally idempotent: the same key pair always yields
    /// the same id, so a retried handshake is detectable rather than creating a second session.
    /// </para>
    /// </summary>
    public static string ComputeSessionId(string verifyingPublicKeyBase64, string encryptionPublicKeyBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifyingPublicKeyBase64);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionPublicKeyBase64);
        return CryptoUtils.ToBase64(CryptoUtils.ComputeSha512Hash($"{verifyingPublicKeyBase64}##{encryptionPublicKeyBase64}"));
    }

    /// <summary>
    /// Verifies a signature against a registered verifying key.
    /// <para>
    /// Returns false rather than throwing on malformed input: a caller cannot then accidentally
    /// treat "the key would not parse" as anything other than a failed verification.
    /// </para>
    /// </summary>
    /// <param name="verifyingPublicKeyBase64">The session's registered verifying key.</param>
    /// <param name="hashAlgorithm">Hash the signature was produced with.</param>
    /// <param name="messageAsBase64">The signed bytes, exactly as received.</param>
    /// <param name="signatureAsBase64">The signature to check.</param>
    public static bool VerifySignature(
        string verifyingPublicKeyBase64,
        string hashAlgorithm,
        string messageAsBase64,
        string signatureAsBase64)
    {
        if (string.IsNullOrEmpty(verifyingPublicKeyBase64))
        {
            return false;
        }

        try
        {
            var signVerifyPublicKey = Deserialize<SignVerifyPublicKey>(verifyingPublicKeyBase64);
            return signVerifyPublicKey.Verify(
                hashAlgorithm: hashAlgorithm,
                message: messageAsBase64,
                signatureAsBase64: signatureAsBase64);
        }
        catch
        {
            return false;
        }
    }
}
