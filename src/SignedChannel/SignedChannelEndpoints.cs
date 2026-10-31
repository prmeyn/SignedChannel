using System.Net;
using System.Text.Json;
using FluentValidation;
using Meyn.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PublicKeyUtils.CryptoKeys;

namespace SignedChannel;

/// <summary>
/// The channel's two HTTP endpoints: the session handshake and the signed-action dispatcher.
/// <para>
/// Browser-to-server traffic is HTTP, not SignalR. The hub carries server-to-client push only,
/// which keeps every request the browser makes subject to the same signature check on the way in.
/// </para>
/// </summary>
public static class SignedChannelEndpoints
{
    /// <summary>
    /// Maps the handshake and dispatcher routes.
    /// <para>
    /// Map these <em>before</em> any catch-all or SPA fallback route, or the fallback will answer
    /// the channel's own requests.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapSignedChannel(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<SignedChannelOptions>>().Value;
        options.Validate();
        ActionResolver.ThrowIfHostDefinesReservedActions(options);

        MapRegister(app, options);
        MapDispatcher(app, options);

        return app;
    }

    /// <summary>
    /// Maps the presence hub at <see cref="SignedChannelOptions.PresenceHubRoute"/>.
    /// <para>
    /// Separate from <see cref="MapSignedChannel"/> because an application that subclasses
    /// <see cref="PresenceHub"/> maps its own type instead — call
    /// <c>MapHub&lt;MyHub&gt;(route)</c> directly in that case.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapSignedChannelHub(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<SignedChannelOptions>>().Value;
        app.MapHub<PresenceHub>(options.PresenceHubRoute);
        return app;
    }

    // --- the handshake: register the session's two public keys ---------------------------------
    private static void MapRegister(IEndpointRouteBuilder app, SignedChannelOptions options)
    {
        app.MapPost(options.RegisterRoute, async (
            HttpRequest httpRequest,
            [FromBody] SessionPublicKeysRegistrationRequest request,
            PublicKeysRegistrationRequestValidatorV1 validator,
            IChannelSessionStore sessionStore,
            IServiceProvider serviceProvider) =>
        {
            var validationResult = await validator.ValidateAsync(request);
            if (!validationResult.IsValid)
            {
                return Results.ValidationProblem(validationResult.ToDictionary(), statusCode: (int)HttpStatusCode.BadRequest);
            }

            var encryptionRequest = ChannelCrypto.Deserialize<EncryptionPublicKeyWithTimestampRequest>(
                request.EncryptionPublicKeyRequestWithTimestampAsBase64);

            var sessionId = ChannelCrypto.ComputeSessionId(
                request.VerifyingPublicKeyBase64, encryptionRequest.EncryptionPublicKeyBase64);

            // The id is a pure function of the keys, so a repeated handshake with the same pair
            // lands here rather than quietly replacing a live session.
            if (await sessionStore.GetByIdAsync(sessionId) is not null)
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }

            // One secret per BROWSER, shared by all its sessions (one per tab): reuse the one the
            // browser already holds. Minting a new one per registration (before 10.4) overwrote the
            // browser's only cookie, so opening a tab broke every other signed-in tab's next request.
            // The secret is there to stop a signing key stolen by script being used outside this
            // browser, which a per-browser secret does just as well. Only a value of our own shape
            // is reused; anything else is replaced.
            var existing = httpRequest.Cookies[options.SessionCookieName];
            var cookieId = Guid.TryParse(existing, out var existingId) ? existingId.ToString() : Guid.NewGuid().ToString();
            var record = new ChannelSessionRecord
            {
                Id = sessionId,
                WebBrowserId = request.WebBrowserId,
                CookieIdHash = CryptoUtils.ComputeSha512Hash(cookieId),
                StartDateTimeOffset = encryptionRequest.TimeStampWithOffSetUTC,
                VerifyingPublicKeyBase64 = request.VerifyingPublicKeyBase64,
                EncryptionPublicKeyBase64 = encryptionRequest.EncryptionPublicKeyBase64,
                PreferredLanguageIsoCode = request.SelectedLanguageIsoCode,
                ConnectionIds = [request.SignalRConnectionId],
                ApiVersion = options.ApiVersion
            };

            var observer = serviceProvider.GetService<IChannelSessionObserver>();
            if (observer is not null)
            {
                await observer.OnSessionRegisteredAsync(new SessionRegisteredContext(
                    SessionId: sessionId,
                    WebBrowserId: request.WebBrowserId,
                    SignalRConnectionId: request.SignalRConnectionId,
                    PreferredLanguageIsoCode: request.SelectedLanguageIsoCode,
                    UserAgent: httpRequest.Headers.UserAgent.ToString(),
                    ApiVersion: options.ApiVersion));
            }

            // Secure=true is right for production. Over plain http://localhost the browser drops
            // the cookie, which is harmless here because it only gates sessions that have a user
            // id — a state the handshake never reaches. Appended even when reused: it slides the
            // expiry for the whole browser.
            httpRequest.HttpContext.Response.Cookies.Append(options.SessionCookieName, cookieId, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.Add(options.SessionCookieLifetime)
            });

            var encryptionPublicKey = ChannelCrypto.Deserialize<EncryptDecryptPublicKey>(encryptionRequest.EncryptionPublicKeyBase64);
            await sessionStore.CreateAsync(record);

            // Encrypted to the browser's own key: the session id is established without ever
            // appearing in the clear on the wire.
            return Results.Created(options.RegisterRoute,
                new SessionPublicKeysRegistrationResponse(Convert.ToBase64String(encryptionPublicKey.Encrypt(sessionId))));
        });
    }

    // --- the signed-action dispatcher ----------------------------------------------------------
    private static void MapDispatcher(IEndpointRouteBuilder app, SignedChannelOptions options)
    {
        app.MapPost(options.ActionRoute, async (
            HttpRequest httpRequest,
            [FromBody] SignedWebBrowserPayloadRequest signedPayloadRequest,
            SignedPayloadRequestValidatorV1 validator,
            IChannelSessionStore sessionStore,
            ActionResolver actionResolver,
            SessionExpiryPolicy sessionExpiry,
            IServiceProvider serviceProvider,
            IHostEnvironment environment,
            ILogger<SignedPayloadRequestValidatorV1> logger,
            CancellationToken cancellationToken) =>
        {
            var validationResult = await validator.ValidateAsync(signedPayloadRequest);

            // A missing session is not a malformed request: the client's answer to it is to
            // register again, not to correct anything.
            if (validationResult.Errors.Any(e => e.ErrorCode == StatusCodes.Status401Unauthorized.ToString()))
            {
                return Results.Unauthorized();
            }
            if (!validationResult.IsValid)
            {
                return Results.ValidationProblem(validationResult.ToDictionary(), statusCode: (int)HttpStatusCode.BadRequest);
            }

            var message = ChannelCrypto.Deserialize<WebBrowserMessagePayloadRequest>(signedPayloadRequest.MessagePayloadRequestAsBase64);

            var action = actionResolver.Resolve(message.ActionName);
            if (action is null)
            {
                return Results.NotFound(message.ActionName);
            }

            // Development-only actions bypass real verification by design, which makes them an
            // account-takeover primitive anywhere else. Gated centrally rather than per action,
            // because a single action that forgets to guard itself is enough. Outside Development
            // one is indistinguishable from an action that does not exist.
            if (action.IsDevOnly && !environment.IsDevelopment())
            {
                return Results.NotFound(message.ActionName);
            }

            object actionInstance;
            try
            {
                actionInstance = ActivatorUtilities.CreateInstance(serviceProvider, action.Type);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Browser action {ActionName} unavailable (missing dependencies)", message.ActionName);
                return Results.NotFound(message.ActionName);
            }

            var session = await sessionStore.GetByIdAsync(message.SessionId);

            // A signed-out session is ended for good: nothing more runs on it, signed in or not.
            // The browser's answer is to drop its keys and start a new session. Letting it carry on
            // anonymously instead (as before 10.3) meant a tab that missed the sign-out push could
            // sign the SAME session in again — reviving keys that were signed out, merging two
            // sign-ins into one session, and handing a reused session to whoever signed in next.
            // Channel.SignOut stays allowed: it is the tab's own idempotent confirmation.
            if (session?.LogoutDateTimeOffset is not null && message.ActionName != ChannelNames.SignOut)
            {
                return Results.Json(new { code = ChannelNames.SessionEndedCode }, statusCode: (int)HttpStatusCode.Unauthorized);
            }

            var userId = session?.LogoutDateTimeOffset is null ? session?.UserId : null;

            // Binds the signed request to a cookie the page's own scripts cannot read, so a stolen
            // signing key alone is not enough. Only meaningful once there is something to protect,
            // so anonymous sessions skip it.
            if (!string.IsNullOrEmpty(userId))
            {
                var cookieId = httpRequest.Cookies[options.SessionCookieName];
                if (string.IsNullOrWhiteSpace(cookieId) || session!.CookieIdHash != CryptoUtils.ComputeSha512Hash(cookieId))
                {
                    // Refused, but the cookie is left alone: it is the browser's, and the other tabs'
                    // sessions are bound to it. Deleting it here (before 10.4) let one stale tab sign
                    // out every other tab of the browser.
                    return Results.Unauthorized();
                }
            }

            // Server-authoritative expiry. An expired session is logged out here, reaching exactly
            // the state a remote logout produces, so everything keyed off that state agrees. It is
            // stamped with when it expired, not now: this request may come hours later.
            if (!string.IsNullOrEmpty(userId) && session is not null)
            {
                var now = DateTimeOffset.UtcNow;
                if (sessionExpiry.IsExpired(session, now))
                {
                    await sessionStore.SetLogoutAsync(message.SessionId, sessionExpiry.ComputeExpiry(session));
                    return Results.Json(new { code = ChannelNames.SessionExpiredCode }, statusCode: (int)HttpStatusCode.Unauthorized);
                }

                var lastActivity = session.LastActivityDateTimeOffset ?? session.StartDateTimeOffset;
                if ((now - lastActivity).TotalSeconds >= sessionExpiry.TouchThrottleSeconds)
                {
                    await sessionStore.TouchActivityAsync(message.SessionId, now);
                    session.LastActivityDateTimeOffset = now; // so the stamp below reflects the slide
                }
            }

            var deviceResolver = serviceProvider.GetService<ISessionDeviceResolver>();
            var latestDeviceId = userId is null || deviceResolver is null
                ? null
                : await deviceResolver.GetLatestDeviceIdAsync(userId);

            var payloadJson = string.IsNullOrWhiteSpace(message.PayloadAsBase64)
                ? null
                : CryptoUtils.FromBase64(message.PayloadAsBase64);
            object? payload = null;
            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    payload = JsonSerializer.Deserialize(payloadJson, action.RequestType);
                }
                catch (Exception deserializeException)
                {
                    return Results.ValidationProblem(
                        new Dictionary<string, string[]> { ["Unable to Deserialize: "] = [deserializeException.Message] },
                        statusCode: (int)HttpStatusCode.BadRequest,
                        title: message.ActionName);
                }
            }

            try
            {
                var actionValidationResult = (WebBrowserActionsValidationResult)action.ValidateMethod.Invoke(
                    actionInstance, [message.SessionId, userId, latestDeviceId, payload])!;
                if (!actionValidationResult.IsValid)
                {
                    return Results.ValidationProblem(
                        actionValidationResult.Errors ?? [],
                        statusCode: (int)HttpStatusCode.BadRequest,
                        title: message.ActionName);
                }

                var hasAccess = (Task<bool>)action.AuthorizeMethod.Invoke(
                    actionInstance, [httpRequest, message.SessionId, userId, latestDeviceId, payload])!;
                if (!await hasAccess)
                {
                    return Results.Unauthorized();
                }

                var jobId = Guid.NewGuid().ToString();
                var startedAt = DateTimeOffset.UtcNow;
                var task = (Task)action.ProcessMethod.Invoke(actionInstance,
                    [httpRequest, message.SessionId, message.SignalRConnectionId, userId, latestDeviceId, payload, jobId, cancellationToken])!;
                await task.ConfigureAwait(false);

                var response = (MessageWebBrowserResponseBase?)task.GetType().GetProperty("Result")!.GetValue(task);
                if (response is null)
                {
                    return Results.BadRequest();
                }

                // Mirrors the server-owned expiry onto every signed-in response, so the client's
                // countdown tracks the window that just slid without a separate round-trip.
                if (!string.IsNullOrEmpty(userId) && session is not null)
                {
                    response.SessionExpiresAtUtc = sessionExpiry.ComputeExpiry(session);
                }

                var recorder = serviceProvider.GetService<IChannelActivityRecorder>();
                if (recorder is not null)
                {
                    var requestBase = payload as MessageWebBrowserRequestBase;
                    // Detached on purpose: an action must not fail because its audit row didn't land.
                    _ = recorder.RecordAsync(new ActionCompletedContext(
                        SessionId: message.SessionId,
                        UserId: userId,
                        ActionName: message.ActionName,
                        JobId: jobId,
                        RequestAsBase64: message.PayloadAsBase64,
                        Response: response,
                        CurrentUrl: requestBase?.CurrentUrl,
                        ReferrerUrl: requestBase?.ReferrerUrl,
                        HttpRequest: httpRequest,
                        StartedAt: startedAt,
                        CompletedAt: DateTimeOffset.UtcNow));
                }

                return Results.Accepted(options.ActionRoute, response);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in browser action {ActionName}", message.ActionName);
                return Results.Problem("Check server logs for details");
            }
        });
    }
}
