using System;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Sharing;
using Jellyfin.Plugin.Shoal.State;

namespace Jellyfin.Plugin.Shoal.Pairing;

/// <summary>The Home side of invite pairing (K22): redeem the invite with a nonce commitment, show the safety code, poll for the approval, take the token once.</summary>
public sealed partial class PairingService
{
    private sealed partial class Run
    {
        private string? _sas;

        private async Task RunInviteAsync()
        {
            var secret = _probe.InviteSecret!;
            var now = _service._time;
            await InvitePreflightAsync().ConfigureAwait(false);

            // The invite lives 15 minutes from its creation; Home gives up when that is over.
            SetStep(PairingStep.Initiating);
            var deadline = now.GetUtcNow() + InviteBook.InviteLife;
            var homeNonce = RandomNumberGenerator.GetBytes(16);
            var homeId = Hex12Of(_service._home.ServerId);
            var homeName = Jellyfin.Plugin.Shoal.Text.TextClean.Display(_service._home.ServerName, 64);

            byte[] Body(bool reveal) => JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = 1,
                invite = secret,
                homeNonceCommit = InviteCrypto.Commit(homeNonce),
                homeNonce = reveal ? InviteCrypto.Base64Url(homeNonce) : null,
                homeServerId = homeId,
                homeName = homeName.Length == 0 ? "Shoal" : homeName,
                homeDeviceId = _deviceId
            });

            var first = await InviteCallAsync(Body(false)).ConfigureAwait(false);
            ThrowIfEnded(first.State);
            if (first.State != "pending" || !InviteCrypto.TryBase64Url(first.PeerNonce, out var peerNonce) || peerNonce.Length != 16)
            {
                throw new AbortException(PairingErrorKind.BadPeerResponse);
            }

            // The Peer must be the server that was probed: its id is part of the safety code, so a different one would show a different code.
            if (!PeerTrust.TryNormalizeServerId(first.PeerServerId, out var peerId) || !string.Equals(peerId, _probe.ServerId, StringComparison.Ordinal))
            {
                throw new AbortException(PairingErrorKind.ServerChanged);
            }

            // The invite lives 15 minutes from when the friend made it, not from now: the Peer says how long is left (seconds, so clock differences do not matter).
            deadline = Sooner(deadline, first.ExpiresInSeconds, now.GetUtcNow());
            lock (_gate)
            {
                _sas = InviteCrypto.Sas(secret, homeNonce, peerNonce, homeId, peerId);
                _expiresUtc = deadline.UtcDateTime;
                _step = PairingStep.AwaitingApproval;
            }

            // Poll with the nonce revealed until the Peer's admin decides.
            var failures = 0;
            var firstFailure = DateTimeOffset.MinValue;
            while (true)
            {
                await DelayUntilAsync(_lastPollStart == default ? now.GetUtcNow() : _lastPollStart + _service._options.PollInterval).ConfigureAwait(false);
                _cts.Token.ThrowIfCancellationRequested();
                if (now.GetUtcNow() >= deadline)
                {
                    throw new AbortException(PairingErrorKind.Expired);
                }

                _lastPollStart = now.GetUtcNow();
                InviteWire answer;
                try
                {
                    answer = await InviteCallAsync(Body(true)).ConfigureAwait(false);
                    failures = 0;
                    SetSilent(false);
                }
                catch (PeerCallException)
                {
                    failures++;
                    firstFailure = firstFailure == DateTimeOffset.MinValue ? now.GetUtcNow() : firstFailure;
                    SetSilent(failures >= _service._options.SilentAfterFailures);
                    if (now.GetUtcNow() - firstFailure >= _service._options.NetworkAbortAfter)
                    {
                        throw new AbortException(PairingErrorKind.Network);
                    }

                    continue;
                }

                ThrowIfEnded(answer.State);
                switch (answer.State)
                {
                    case "pending":
                        deadline = Sooner(deadline, answer.ExpiresInSeconds, now.GetUtcNow());
                        lock (_gate)
                        {
                            _expiresUtc = deadline.UtcDateTime;
                        }

                        continue;
                    case "denied":
                        throw new AbortException(PairingErrorKind.Denied);
                    case "codeMismatch":
                        throw new AbortException(PairingErrorKind.CodeMismatch);
                    case "approved" when !string.IsNullOrEmpty(answer.Token):
                        _token = answer.Token;
                        SetStep(PairingStep.Validating);
                        await ValidateAsync().ConfigureAwait(false);

                        // No compare step: the friend already typed the safety code (DESIGN 4.1b step 3).
                        await FinalizeAsync().ConfigureAwait(false);
                        return;
                    default:
                        throw new AbortException(PairingErrorKind.BadPeerResponse);
                }
            }
        }

        // The Peer answers 200 with a reason to a request that carries a valid secret of an invite that is over; anything else stays the one 404.
        private static void ThrowIfEnded(string? state)
        {
            var kind = state switch
            {
                "expired" => PairingErrorKind.Expired,
                "replaced" => PairingErrorKind.InviteReplaced,
                "cancelled" => PairingErrorKind.InviteCancelled,
                "uncollected" => PairingErrorKind.TokenNotCollected,
                "used" => PairingErrorKind.InviteInvalid,
                _ => (PairingErrorKind?)null,
            };
            if (kind is { } ended)
            {
                throw new AbortException(ended);
            }
        }

        private static DateTimeOffset Sooner(DateTimeOffset deadline, int? secondsLeft, DateTimeOffset now) =>
            secondsLeft is { } left && now.AddSeconds(Math.Max(0, left)) < deadline ? now.AddSeconds(Math.Max(0, left)) : deadline;

        private async Task InvitePreflightAsync()
        {
            SetStep(PairingStep.Preflight);
            try
            {
                using var identity = await CallAsync(Unauthenticated(), PeerRequest.GetAnonymous("/System/Info/Public"), PeerCallProfile.Info).ConfigureAwait(false);
                if (identity.StatusCode != HttpStatusCode.OK)
                {
                    throw new AbortException(PairingErrorKind.Network);
                }

                var info = await identity.ReadJsonAsync<SystemInfoWire>(_cts.Token).ConfigureAwait(false);
                if (!PeerTrust.TryNormalizeServerId(info.Id, out var id))
                {
                    throw new AbortException(PairingErrorKind.BadPeerResponse);
                }

                if (!string.Equals(id, _probe.ServerId, StringComparison.Ordinal))
                {
                    throw new AbortException(PairingErrorKind.ServerChanged);
                }
            }
            catch (PeerCallException)
            {
                throw new AbortException(PairingErrorKind.Network);
            }
        }

        // Every non-200 answer is the Peer's one 404 (unknown, expired, used, mismatched): Home cannot tell them apart, so it says "invalid".
        private async Task<InviteWire> InviteCallAsync(byte[] body)
        {
            using var response = await CallAsync(Unauthenticated(), PeerRequest.Post("/Shoal/Pair", body, authenticated: false), _service._options.StepProfile).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new AbortException(PairingErrorKind.RateLimited);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new AbortException((int)response.StatusCode >= 500 ? PairingErrorKind.Network : PairingErrorKind.InviteInvalid);
            }

            var wire = await response.ReadJsonAsync<InviteWire>(_cts.Token).ConfigureAwait(false);
            return wire.V == 1 ? wire : throw new AbortException(PairingErrorKind.BadPeerResponse);
        }
    }
}

#pragma warning disable SA1600 // Wire members: the names are the protocol's.
/// <summary>What <c>POST /Shoal/Pair</c> answers (DESIGN 4.1b). Only the members Home uses are read.</summary>
internal sealed class InviteWire
{
    [JsonPropertyName("v")]
    public int V { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("peerServerId")]
    public string? PeerServerId { get; set; }

    [JsonPropertyName("peerNonce")]
    public string? PeerNonce { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("expiresInSeconds")]
    public int? ExpiresInSeconds { get; set; }
}
#pragma warning restore SA1600
