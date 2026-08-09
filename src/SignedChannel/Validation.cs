using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using PublicKeyUtils.CryptoKeys;

namespace SignedChannel;

/// <summary>
/// Checks a session handshake before any session is created.
/// <para>
/// The substance of it is proving the browser holds the private half of the key it is registering:
/// the encryption key travels inside a blob signed by the signing key, and that signature is
/// verified here. Without it anyone could register a session against someone else's public key.
/// </para>
/// </summary>
public sealed class PublicKeysRegistrationRequestValidatorV1 : AbstractValidator<SessionPublicKeysRegistrationRequest>
{
    /// <summary>Creates the validator.</summary>
    public PublicKeysRegistrationRequestValidatorV1(TimestampFreshness freshness)
    {
        RuleFor(request => request).Custom((request, context) =>
        {
            if (string.IsNullOrWhiteSpace(request?.SignalRConnectionId))
            {
                context.AddFailure("SignalRConnectionId is missing but required.");
                return;
            }

            EncryptionPublicKeyWithTimestampRequest encryptionRequest;
            try
            {
                encryptionRequest = ChannelCrypto.Deserialize<EncryptionPublicKeyWithTimestampRequest>(
                    request.EncryptionPublicKeyRequestWithTimestampAsBase64);
            }
            catch (Exception ex)
            {
                context.AddFailure($"Unable to Deserialize EncryptionPublicKeyRequestWithTimestampAsBase64: {ex.Message}");
                return;
            }

            if (!freshness.BeAValidDateWithOffset(encryptionRequest.TimeStampWithOffSetUTC))
            {
                context.AddFailure("Unable to extract a valid timestamp.");
                return;
            }
            if (!freshness.IsFresh(encryptionRequest.TimeStampWithOffSetUTC))
            {
                context.AddFailure("Timestamp has expired.");
                return;
            }

            SignVerifyPublicKey signVerifyPublicKey;
            try
            {
                signVerifyPublicKey = ChannelCrypto.Deserialize<SignVerifyPublicKey>(request.VerifyingPublicKeyBase64);
            }
            catch (Exception ex)
            {
                context.AddFailure($"Unable to Deserialize VerifyingPublicKeyBase64: {ex.Message}");
                return;
            }

            EncryptDecryptPublicKey encryptDecryptPublicKey;
            try
            {
                encryptDecryptPublicKey = ChannelCrypto.Deserialize<EncryptDecryptPublicKey>(encryptionRequest.EncryptionPublicKeyBase64);
            }
            catch (Exception ex)
            {
                context.AddFailure($"Unable to Deserialize EncryptionPublicKeyBase64: {ex.Message}");
                return;
            }

            // Exercised now rather than trusted: the handshake's response is encrypted with this
            // key, so a key that parses but cannot encrypt would fail after the session was
            // already created, leaving a session the browser can never learn the id of.
            try
            {
                _ = encryptDecryptPublicKey.Encrypt(Guid.NewGuid().ToString());
            }
            catch
            {
                context.AddFailure("Unable to Encrypt with EncryptDecryptPublicKey.");
                return;
            }

            if (string.IsNullOrWhiteSpace(request.HashAlgorithm))
            {
                context.AddFailure("HashAlgorithm is missing but required.");
                return;
            }
            if (string.IsNullOrWhiteSpace(request.EncryptionPublicKeySignatureAsBase64))
            {
                context.AddFailure("EncryptionPublicKeySignatureAsBase64 is missing but required.");
                return;
            }

            if (!signVerifyPublicKey.Verify(
                    hashAlgorithm: request.HashAlgorithm,
                    message: request.EncryptionPublicKeyRequestWithTimestampAsBase64,
                    signatureAsBase64: request.EncryptionPublicKeySignatureAsBase64))
            {
                context.AddFailure("Signature Mismatch");
            }
        });
    }
}

/// <summary>
/// The gate every browser request passes through: signature, freshness, and that the session
/// exists.
/// <para>
/// A missing session is reported with error code <c>401</c> so the dispatcher can answer it as a
/// rejection rather than a validation failure — the client treats the two very differently, one
/// meaning "fix your request" and the other "register again".
/// </para>
/// </summary>
public sealed class SignedPayloadRequestValidatorV1 : AbstractValidator<SignedWebBrowserPayloadRequest>
{
    /// <summary>Creates the validator.</summary>
    public SignedPayloadRequestValidatorV1(IChannelSessionStore sessionStore, TimestampFreshness freshness)
    {
        RuleFor(signedPayloadRequest => signedPayloadRequest).CustomAsync(async (signedPayloadRequest, context, _) =>
        {
            WebBrowserMessagePayloadRequest messagePayloadRequest;
            try
            {
                messagePayloadRequest = ChannelCrypto.Deserialize<WebBrowserMessagePayloadRequest>(
                    signedPayloadRequest.MessagePayloadRequestAsBase64);
            }
            catch (Exception ex)
            {
                context.AddFailure($"Unable to Deserialize MessagePayloadRequest: {ex.Message}");
                return;
            }

            if (!freshness.BeAValidDateWithOffset(messagePayloadRequest.TimeStampWithOffSetUTC))
            {
                context.AddFailure("Unable to extract a valid timestamp.");
                return;
            }
            if (!freshness.IsFresh(messagePayloadRequest.TimeStampWithOffSetUTC))
            {
                context.AddFailure("Timestamp has expired.");
                return;
            }

            try
            {
                var sessionInfo = await sessionStore.GetByIdAsync(messagePayloadRequest.SessionId);
                var verificationPublicKeyAsBase64 = sessionInfo?.VerifyingPublicKeyBase64;

                if (string.IsNullOrEmpty(verificationPublicKeyAsBase64))
                {
                    context.AddFailure(new ValidationFailure
                    {
                        ErrorCode = StatusCodes.Status401Unauthorized.ToString(),
                        ErrorMessage = "Session not found"
                    });
                    return;
                }

                // Verified against the key registered for this session — not one supplied by the
                // request — which is what ties the request to the session rather than merely
                // proving it was signed by somebody.
                if (!ChannelCrypto.VerifySignature(
                        verifyingPublicKeyBase64: verificationPublicKeyAsBase64,
                        hashAlgorithm: signedPayloadRequest.HashAlgorithm,
                        messageAsBase64: signedPayloadRequest.MessagePayloadRequestAsBase64,
                        signatureAsBase64: signedPayloadRequest.SignatureAsBase64))
                {
                    context.AddFailure("Verification of signature FAILED.");
                }
            }
            catch (Exception ex)
            {
                context.AddFailure($"Unable to perform verification Error: {ex.Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(messagePayloadRequest.ActionName))
            {
                context.AddFailure("ActionName is missing but required.");
            }
        });
    }
}
