using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

internal sealed class FakeAdminNotifier : IAdminNotifier
{
    private readonly List<(string Header, string Text)> _messages = [];

    public IReadOnlyList<(string Header, string Text)> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    public Task NotifyAdminsAsync(string header, string text, CancellationToken cancellationToken = default)
    {
        lock (_messages)
        {
            _messages.Add((header, text));
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeHomeIdentity : IHomeIdentity
{
    public string ServerId { get; set; } = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    public string ServerName { get; set; } = "Home";
}

/// <summary>
/// Everything a pairing test needs, wired like the plugin wires it, with the time limits scaled down.
/// </summary>
internal sealed class PairingRig : IDisposable
{
    public static readonly Guid Admin = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public PairingRig(Action<PairingOptionsBuilder>? configure = null, TimeProvider? time = null, string homeName = "Home")
    {
        Dir = new TempDirectory();
        Paths = new ShoalPaths(Dir.Path);
        Log = new CapturingLogger();
        Time = time ?? TimeProvider.System;
        State = new StateStore(Paths, Log, Time, null);
        Key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Secrets = new SecretStore(Paths, State, Log, name => name == SecretStore.KeyVariable ? Key : null, null, null, null);
        Home = new FakeHomeIdentity { ServerName = homeName };
        Registry = new PeerRegistry(Time);
        Client = new PeerClient(() => Home.ServerName, () => "0.1.0.0", Time, Registry) { EnforceAddressPolicy = false };
        Health = new PeerHealthStore(Paths, Log);
        Notifier = new FakeAdminNotifier();
        var builder = new PairingOptionsBuilder();
        configure?.Invoke(builder);
        Options = builder.Build();
        Service = new PairingService(State, Secrets, Client, Registry, Health, Home, Notifier, Paths, Log, Time, Options);
    }

    public TempDirectory Dir { get; }

    public ShoalPaths Paths { get; }

    public CapturingLogger Log { get; }

    public TimeProvider Time { get; }

    public StateStore State { get; }

    public string Key { get; }

    public SecretStore Secrets { get; }

    public PeerRegistry Registry { get; }

    public PeerClient Client { get; }

    public PeerHealthStore Health { get; }

    public FakeHomeIdentity Home { get; }

    public FakeAdminNotifier Notifier { get; }

    public PairingOptions Options { get; }

    public PairingService Service { get; private set; }

    /// <summary>Builds a second service over the same files, as after a restart of Home.</summary>
    public PairingService Restart()
    {
        Service.Dispose();
        Service = new PairingService(State, Secrets, Client, Registry, Health, Home, Notifier, Paths, Log, Time, Options);
        return Service;
    }

    public async Task<ProbeResult> ProbeAsync(FakeJellyfinPeer peer, string? peerHandle = null)
    {
        var result = await Service.ProbeAsync(peerHandle is null
            ? new ProbeRequest { Url = peer.BaseUrl, Network = PeerNetwork.Tailnet }
            : new ProbeRequest { PeerHandle = peerHandle });
        Assert.True(result.Ok, "probe failed: " + result.Failure + " " + result.AddressProblem);
        return result;
    }

    public async Task<PairingSessionInfo> BeginAsync(FakeJellyfinPeer peer, string name = "Hops", string? peerHandle = null)
    {
        var probe = await ProbeAsync(peer, peerHandle);
        return await Service.BeginAsync(Admin, probe.ProbeHandle!, name, PairingMethod.Qc);
    }

    public async Task<PairingSessionInfo> UntilAsync(Func<PairingSessionInfo, bool> predicate, int timeoutMs = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        PairingSessionInfo? last = null;
        while (DateTime.UtcNow < until)
        {
            last = Service.GetSession();
            if (last is not null && predicate(last))
            {
                return last;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("The session did not reach the expected state; it was " + (last is null ? "absent" : last.Step + "/" + last.State + "/" + last.Error));
    }

    public Task<PairingSessionInfo> UntilStepAsync(PairingStep step, int timeoutMs = 8000) => UntilAsync(s => s.Step == step, timeoutMs);

    public async Task<PairingSessionInfo> UntilFinishedAsync(int timeoutMs = 8000)
    {
        var finished = await UntilAsync(s => s.State != "running", timeoutMs);
        if (Service.CurrentCompletion is { } completion)
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        return Service.GetSession() ?? finished;
    }

    public PeerState Peer(string peerKey) => State.Current.Home.Peers.Single(p => p.PeerKey == peerKey);

    public void Dispose()
    {
        Service.Dispose();
        Client.Dispose();
        Secrets.Dispose();
        State.Dispose();
        Dir.Dispose();
    }

    /// <summary>Every file Shoal wrote, as text, for "the token is nowhere" checks.</summary>
    public string AllFilesAsText()
    {
        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(Dir.Path, "*", SearchOption.AllDirectories))
        {
            builder.AppendLine(Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        }

        return builder.ToString();
    }
}

internal sealed class PairingOptionsBuilder
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(40);

    public TimeSpan PollTotal { get; set; } = TimeSpan.FromMilliseconds(400);

    public TimeSpan BaselineWindow { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan LocalDeadline { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan NetworkAbortAfter { get; set; } = TimeSpan.FromMilliseconds(1500);

    public TimeSpan ConfirmWindow { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan ProbeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public bool RealPolicy { get; set; }

    public PairingOptions Build()
    {
        var profile = new PeerCallProfile("test", PollTotal > TimeSpan.FromSeconds(1) ? PollTotal : TimeSpan.FromSeconds(1), PollTotal, PollTotal, PollTotal, 64 * 1024);
        return new PairingOptions
        {
            PollInterval = PollInterval,
            PollProfile = profile,
            StepProfile = profile,
            BaselineWindow = BaselineWindow,
            LocalDeadline = LocalDeadline,
            NetworkAbortAfter = NetworkAbortAfter,
            ConfirmWindow = ConfirmWindow,
            ProbeLifetime = ProbeLifetime,
            AddressValidator = RealPolicy ? null : Permissive
        };
    }

    private static AddressCheck Permissive(string? url, PeerNetwork network, bool ack, string? proxy)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new AddressCheck(AddressProblem.NotAUrl, null);
        }

        return new AddressCheck(AddressProblem.None, new PeerAddress(uri, uri.Host, network, true, uri.Scheme == "http", null));
    }
}
