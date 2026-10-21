using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Jellyfin.Plugin.Shoal.Pairing;
using Jellyfin.Plugin.Shoal.Peers;
using Jellyfin.Plugin.Shoal.Security;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Visibility;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Pilot 23 REMOVE-SERVER-GHOST: "Remove server" with deleteLibrary took the library out of /Library/VirtualFolders, but /UserViews, the nav and My Media kept
/// listing it (empty), also after a scan. The rig runs the real <see cref="JellyfinLibrarySource"/> on a stand-in for core's library manager that keeps
/// core's rules (decompiled 12.1: CollectionFolder.CanDelete is false, so no scan deletes it; ValidateTopLibraryFolders drops the row of a collection folder
/// whose folder is gone, and the user root keeps the children it loaded until the next pass reloads them).
/// </summary>
public sealed class RemoveServerGhostTests
{
    /// <summary>
    /// What core holds about libraries: the folders on disk (VirtualFolders), the CollectionFolder rows in the database and the user root's loaded children
    /// (what /UserViews lists).
    /// </summary>
    internal sealed class CoreLibraries
    {
        public Dictionary<string, (Guid Id, string Path, CollectionTypeOptions? Type)> OnDisk { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Guid> Rows { get; } = new(StringComparer.Ordinal);

        public List<string>? Loaded { get; set; }

        public List<string> Calls { get; } = [];

        /// <summary>Gets what /UserViews lists for an admin.</summary>
        public IReadOnlyList<string> UserViews => Loaded ?? [.. Rows.Keys];

        public ILibraryManager Manager() => CoreProxy<ILibraryManager>.Create(Answer);

        public void Scan() => ValidateTop(); // a library scan runs the same top pass first

        private void ValidateTop()
        {
            // children = null, then reloaded from the database (new folders on disk get their row first)
            foreach (var name in OnDisk.Keys.Where(n => !Rows.ContainsKey(n)))
            {
                Rows[name] = OnDisk[name].Id;
            }

            Loaded = [.. Rows.Keys];
            foreach (var gone in Rows.Keys.Where(n => !OnDisk.ContainsKey(n)).ToList())
            {
                Rows.Remove(gone); // the row goes; the loaded children keep the item
            }
        }

        private object? Answer(MethodInfo method, object?[] args)
        {
            switch (method.Name)
            {
                case "GetVirtualFolders":
                    return OnDisk.Select(f => new VirtualFolderInfo { Name = f.Key, Locations = [f.Value.Path], ItemId = f.Value.Id.ToString("N"), CollectionType = f.Value.Type }).ToList();
                case "AddVirtualFolder":
                    OnDisk[(string)args[0]!] = (Guid.NewGuid(), ((MediaBrowser.Model.Configuration.LibraryOptions)args[2]!).PathInfos[0].Path, (CollectionTypeOptions?)args[1]);
                    Calls.Add("add");
                    return Task.CompletedTask;
                case "RemoveVirtualFolder":
                    OnDisk.Remove((string)args[0]!);
                    Calls.Add("remove:" + args[1]);
                    if ((bool)args[1]!)
                    {
                        ValidateTop();
                    }

                    return Task.CompletedTask;
                case "ValidateTopLibraryFolders":
                    Calls.Add("top");
                    ValidateTop();
                    return Task.CompletedTask;
                default:
                    return CoreProxy<ILibraryManager>.Empty(method.ReturnType);
            }
        }
    }

    /// <summary>
    /// An interface stand-in: the given answer, or an empty value (completed task, empty list, null).
    /// </summary>
    /// <typeparam name="T">The interface.</typeparam>
    public class CoreProxy<T> : DispatchProxy
        where T : class
    {
        private Func<MethodInfo, object?[], object?> _answer = (m, _) => Empty(m.ReturnType);

        public static T Create(Func<MethodInfo, object?[], object?>? answer = null)
        {
            var proxy = DispatchProxy.Create<T, CoreProxy<T>>();
            if (answer is not null)
            {
                ((CoreProxy<T>)(object)proxy)._answer = answer;
            }

            return proxy;
        }

        public static object? Empty(Type type)
        {
            if (type == typeof(void))
            {
                return null;
            }

            if (type == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [Empty(inner)]);
            }

            if (type.IsArray)
            {
                return Array.CreateInstance(type.GetElementType()!, 0);
            }

            if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
            {
                var element = type.GetGenericArguments()[0];
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(element));
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _answer(targetMethod!, args ?? []);
    }

    [Fact]
    public async Task RemoveServer_WithDeleteLibrary_TheLibraryLeavesTheUserViews_NotOnlyTheVirtualFolders()
    {
        using var rig = new PairingRig();
        var stubRoot = Path.Combine(rig.Dir.Path, "stubs");
        await rig.State.UpdateAsync(s =>
        {
            StateSamples.Fill(s);
            s.StubRoot = stubRoot;
        });
        var core = new CoreLibraries();
        core.OnDisk["Admin Movies"] = (Guid.NewGuid(), Path.Combine(rig.Dir.Path, "media"), CollectionTypeOptions.movies);
        core.Scan();
        var libraries = new JellyfinLibrarySource(core.Manager(), CoreProxy<ISubtitleManager>.Create(), CoreProxy<IMediaSegmentManager>.Create());
        var accounts = new FakeAccounts();
        var admin = accounts.Add("admin", admin: true);
        var visibility = new VisibilityService(rig.State, libraries, accounts, rig.Time, NullLogger<VisibilityService>.Instance);
        var service = new ShoalLibraryService(rig.State, libraries, visibility, rig.Time, NullLogger<ShoalLibraryService>.Instance);

        // The friend's library is added the way the wizard does it: it shows in both lists.
        Assert.True((await service.CreateAsync(StateSamples.PeerKeyA, "movies-1", admin.Id)).Ok);
        Assert.Contains("Hops Movies", core.UserViews);
        Assert.Contains(libraries.List(), l => l.Name == "Hops Movies");

        var gate = new NoPairing();
        var controller = new AdminPeerSettingsController(
            rig.State,
            new PeerDirectory(rig.State, rig.Secrets),
            rig.Client,
            rig.Registry,
            new PeerAddressService(rig.State, rig.Secrets, rig.Registry, gate),
            rig.Secrets,
            service,
            rig.Paths,
            NullLogger<AdminPeerSettingsController>.Instance,
            gate)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"confirmName":"Hops"}"""));

        var body = AdminTestSupport.Body(await controller.Remove(StateSamples.PeerKeyA, deleteLibrary: true, default));

        Assert.Equal(1, body.GetProperty("librariesRemoved").GetInt32());
        Assert.DoesNotContain(libraries.List(), l => l.Name == "Hops Movies");
        Assert.DoesNotContain("Hops Movies", core.UserViews); // the FAIL: it stayed here (and in the nav and My Media)
        Assert.DoesNotContain("Hops Movies", core.Rows.Keys);
        Assert.Contains("Admin Movies", core.UserViews); // the admin's own library is untouched
        Assert.Contains("remove:False", core.Calls);
        Assert.DoesNotContain("remove:True", core.Calls); // no full scan of every library is started
    }

    private sealed class NoPairing : IPairingGate
    {
        public bool IsLive(string peerKey) => false;

        public Task<bool> CancelAsync(string peerKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
