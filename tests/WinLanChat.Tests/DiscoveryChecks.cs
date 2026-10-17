using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WinLanChat.Core.Models;
using WinLanChat.Core.Networking;

internal static class DiscoveryChecks
{
    internal static async Task DiscoverAndConnectAsync()
    {
        await using var a = new ChatNode("Аня");
        await using var b = new ChatNode("Борис");
        a.Start(0);
        b.Start(0);
        int discoveryBPort = 0;
        await using var discoveryA = Create("Аня", a.ListeningPort,
            () => discoveryBPort == 0 ? [] : [Endpoint(discoveryBPort)]);
        var peersA = Watch(discoveryA);
        discoveryA.Start();
        int discoveryAPort = discoveryA.BoundPort;
        await using var other = Create("Борис", b.ListeningPort, () => [Endpoint(discoveryAPort)]);
        var peersB = Watch(other);
        other.Start();
        discoveryBPort = other.BoundPort;
        await discoveryA.RefreshAsync();
        DiscoveredPeer peer = (await UntilAsync(peersA, peers => peers.Count == 1))[0];
        await UntilAsync(peersB, peers => peers.Count == 1);
        if (peer.Name != "Борис" || peer.Host != "127.0.0.1" || peer.Port != b.ListeningPort)
            throw new Exception("Discovery did not preserve the sender endpoint and advertised TCP port.");
        var message = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.MessageReceived += text => message.TrySetResult(text);
        await a.ConnectAsync(peer.Host, peer.Port);
        await a.SendAsync("Выбран из списка");
        if (await message.Task.WaitAsync(TimeSpan.FromSeconds(3)) != "Выбран из списка")
            throw new Exception("Connecting to a discovered participant failed.");
        await other.DisposeAsync();
        await UntilAsync(peersA, peers => peers.Count == 0);
    }

    internal static async Task SharedPortBroadcastAsync()
    {
        int port = 0;
        IReadOnlyList<IPEndPoint> Targets() => [new(IPAddress.Broadcast, port)];
        await using var a = Create("Одинаковое имя", 45832, Targets, interval: TimeSpan.FromMilliseconds(150));
        a.Start();
        port = a.BoundPort;
        await using var b = Create("Одинаковое имя", 45833, Targets, port, TimeSpan.FromMilliseconds(150));
        await using var c = Create("Одинаковое имя", 45834, Targets, port, TimeSpan.FromMilliseconds(150));
        var snapshots = Watch(a);
        var snapshotsB = Watch(b);
        var snapshotsC = Watch(c);
        b.Start();
        c.Start();
        IReadOnlyList<DiscoveredPeer> peers = await UntilAsync(snapshots, p => p.Count == 2);
        await UntilAsync(snapshotsB, p => p.Count == 2);
        await UntilAsync(snapshotsC, p => p.Count == 2);
        if (peers.Select(p => p.Id).Distinct().Count() != 2 || peers.Any(p => p.Port == 45832))
            throw new Exception("Participants were merged by name or included themselves.");
        for (int i = 0; i < 4; i++)
            await a.RefreshAsync();
        await Task.Delay(250);
        if (snapshots.Reader.TryRead(out var duplicate) && duplicate.Count != 2)
            throw new Exception("Repeated discovery created duplicate entries.");
    }

    internal static async Task ValidateAndExpireAsync()
    {
        await using var discovery = Create("Receiver", 45832, () => [], expiry: TimeSpan.FromMilliseconds(500));
        var snapshots = Watch(discovery);
        discovery.Start();
        using var raw = new UdpClient(AddressFamily.InterNetwork);
        Guid id = Guid.NewGuid();
        byte[] Packet(string app = "WinLanChat", int version = 1, string kind = "present", string? name = "Рабочий ПК",
            string? computer = "OFFICE", int tcpPort = 49999, Guid? peerId = null) =>
            JsonSerializer.SerializeToUtf8Bytes(new { App = app, Version = version, Kind = kind, Id = peerId ?? id,
                Name = name, ComputerName = computer, Port = tcpPort });
        foreach (byte[] bad in new[]
        {
            Encoding.UTF8.GetBytes("{bad json"), Encoding.UTF8.GetBytes("null"), new byte[2048],
            Packet(app: "OtherApp"), Packet(version: 99), Packet(kind: "unknown"), Packet(name: "bad\nname"),
            Packet(name: null), Packet(name: new string('x', 65)), Packet(computer: "bad\tcomputer"),
            Packet(tcpPort: 0), Packet(tcpPort: 65536), Packet(peerId: Guid.Empty)
        })
            await raw.SendAsync(bad, Endpoint(discovery.BoundPort));
        await raw.SendAsync(Packet(), Endpoint(discovery.BoundPort));
        var peers = await UntilAsync(snapshots, p => p.Count > 0);
        if (peers.Count != 1 || peers[0].Id != id || peers[0].Port != 49999)
            throw new Exception("Invalid packet accepted or valid packet rejected.");
        // A new endpoint/name for an existing identity replaces it instead of adding a duplicate.
        await raw.SendAsync(Packet(name: "Новое имя", tcpPort: 49998), Endpoint(discovery.BoundPort));
        await UntilAsync(snapshots, p => p.Count == 1 && p[0].Name == "Новое имя" && p[0].Port == 49998);
        await UntilAsync(snapshots, p => p.Count == 0);
    }

    internal static async Task SelfAndCancellationAsync()
    {
        using var raw = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var discovery = Create("Self", 45832, () => [Endpoint(((IPEndPoint)raw.Client.LocalEndPoint!).Port)]);
        var snapshots = Watch(discovery);
        discovery.Start();
        await discovery.RefreshAsync();
        UdpReceiveResult announcement = await raw.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await raw.SendAsync(announcement.Buffer, Endpoint(discovery.BoundPort));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await discovery.RefreshAsync(cancelled.Token); throw new Exception("Cancelled refresh succeeded."); }
        catch (OperationCanceledException) { }
        await Task.Delay(100);
        if (snapshots.Reader.TryRead(out var peers) && peers.Count > 0)
            throw new Exception("The local participant appeared in its own list.");
        int port = discovery.BoundPort;
        await discovery.DisposeAsync();
        using var replacement = new UdpClient(new IPEndPoint(IPAddress.Any, port));
    }

    private static PeerDiscovery Create(string name, int chatPort, Func<IReadOnlyList<IPEndPoint>> targets,
        int port = 0, TimeSpan? interval = null, TimeSpan? expiry = null) =>
        new(name, chatPort, port, targets, interval ?? TimeSpan.FromMinutes(1),
            expiry ?? TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50));

    private static IPEndPoint Endpoint(int port) => new(IPAddress.Loopback, port);
    private static Channel<IReadOnlyList<DiscoveredPeer>> Watch(PeerDiscovery discovery)
    {
        var channel = Channel.CreateUnbounded<IReadOnlyList<DiscoveredPeer>>();
        discovery.PeersChanged += peers => channel.Writer.TryWrite(peers);
        discovery.Error += error => channel.Writer.TryComplete(new Exception(error));
        return channel;
    }

    private static async Task<IReadOnlyList<DiscoveredPeer>> UntilAsync(Channel<IReadOnlyList<DiscoveredPeer>> snapshots,
        Func<IReadOnlyList<DiscoveredPeer>, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (true)
        {
            var peers = await snapshots.Reader.ReadAsync(timeout.Token);
            if (condition(peers))
                return peers;
        }
    }
}
