using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Playback;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// PLAN 2.9.2 K11: the Busy message goes to the user's sessions, with the words of its reason, at most once per user and peer every 30 seconds, and it is
/// not behind the offline setting.
/// </summary>
public sealed class ClientOfflineNoticeTests
{
    [Fact]
    public async Task Busy_ThrottledPerUserAndPeer()
    {
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        var sessions = DispatchProxy.Create<ISessionManager, Probe>();
        var probe = (Probe)(object)sessions;
        probe.Live = [Session(sessions, user, "s1"), Session(sessions, other, "s2")];
        var time = new ManualTimeProvider();
        var notice = new ClientOfflineNotice(sessions, () => false, time, NullLogger<ClientOfflineNotice>.Instance);

        notice.Busy(user, "peer", "Hops", BusyReason.PeerStreams);
        notice.Busy(user, "peer", "Hops", BusyReason.Conversions); // the same user and peer within 30 s: nothing
        notice.Busy(other, "peer", "Hops", BusyReason.UserStreams, 3); // another user has their own allowance
        notice.Busy(user, "other-peer", "Elsewhere", BusyReason.TooFast); // and so does another peer
        await WaitAsync(probe, 3);

        Assert.Equal(
            [
                ("s1", "Hops's server is sending as much as it can right now. Try again in a minute."),
                ("s2", "You're already playing 3 titles. Stop one, then try again."),
                ("s1", "Too many tries in a row. Wait a minute, then try again."),
            ],
            probe.Sent.OrderBy(static s => s.Seq).Select(static s => (s.Session, s.Text)).ToList());
        Assert.All(probe.Sent, static s => Assert.Equal("Shoal", s.Header));

        time.Advance(TimeSpan.FromSeconds(31));
        notice.Busy(user, "peer", "Hops", BusyReason.Conversions);
        await WaitAsync(probe, 4);
        Assert.Equal("Hops's server is converting as many videos as it can right now. Try again in a minute.", probe.Sent.OrderBy(static s => s.Seq).Last().Text);

        // the Busy allowance is its own: an offline message for the same user and peer still goes out (the setting is on for this one)
        var offline = new ClientOfflineNotice(sessions, () => true, time, NullLogger<ClientOfflineNotice>.Instance);
        offline.Busy(user, "peer", "Hops", BusyReason.PeerStreams);
        offline.PeerOffline(user, "peer", "Hops");
        await WaitAsync(probe, 6);
    }

    private static SessionInfo Session(ISessionManager manager, Guid user, string id) => new(manager, NullLogger.Instance) { Id = id, UserId = user };

    private static async Task WaitAsync(Probe probe, int count)
    {
        for (var i = 0; i < 50 && probe.Sent.Count < count; i++)
        {
            await Task.Delay(20);
        }

        await Task.Delay(50); // and nothing more arrives
        Assert.Equal(count, probe.Sent.Count);
    }

    public class Probe : DispatchProxy
    {
        private int _seq;

        public IReadOnlyList<SessionInfo> Live { get; set; } = [];

        public List<(int Seq, string Session, string Header, string Text)> Sent { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Sessions")
            {
                return Live;
            }

            if (targetMethod?.Name == nameof(ISessionManager.SendMessageCommand) && args is [_, string session, MessageCommand command, ..])
            {
                lock (Sent)
                {
                    Sent.Add((++_seq, session, command.Header ?? string.Empty, command.Text ?? string.Empty));
                }
            }

            return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }
}
