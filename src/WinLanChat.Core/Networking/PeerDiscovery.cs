using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using WinLanChat.Core.Models;

namespace WinLanChat.Core.Networking;

/// <summary>IPv4 LAN presence. Events arrive on worker threads; addresses come from UDP senders.</summary>
public sealed class PeerDiscovery : IAsyncDisposable
{
    public const int DiscoveryPort = 45831;
    private const int MaximumPeers = 1024;
    private const int MaximumPacketBytes = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 4 };
    private readonly Guid _id = Guid.NewGuid();
    private readonly string _name;
    private readonly int _chatPort;
    private readonly int _discoveryPort;
    private readonly Func<IReadOnlyList<IPEndPoint>> _targets;
    private readonly TimeSpan _announceInterval;
    private readonly TimeSpan _expiry;
    private readonly TimeSpan _tick;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Presence> _peers = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private UdpClient? _udp;
    private Task? _receiveTask;
    private Task? _maintenanceTask;
    private bool _disposed;

    public PeerDiscovery(string name, int chatPort)
        : this(name, chatPort, DiscoveryPort, BroadcastTargets,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(1)) { }

    internal PeerDiscovery(string name, int chatPort, int discoveryPort,
        Func<IReadOnlyList<IPEndPoint>> targets, TimeSpan announceInterval, TimeSpan expiry, TimeSpan tick)
    {
        if (!ChatProtocol.IsValidName(name))
            throw new ArgumentException("Недопустимое имя участника.", nameof(name));
        if (chatPort is < 1 or > 65535 || discoveryPort is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(chatPort));
        _name = name.Trim();
        _chatPort = chatPort;
        _discoveryPort = discoveryPort;
        _targets = targets;
        _announceInterval = announceInterval;
        _expiry = expiry;
        _tick = tick;
    }

    public event Action<IReadOnlyList<DiscoveredPeer>>? PeersChanged;
    public event Action<string>? Error;
    internal int BoundPort => ((IPEndPoint)_udp!.Client.LocalEndPoint!).Port;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_udp is not null)
                throw new InvalidOperationException("Поиск уже запущен.");
            var udp = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                udp.ExclusiveAddressUse = false;
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.EnableBroadcast = true;
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
                _udp = udp;
            }
            catch { udp.Dispose(); throw; }
            _receiveTask = ReceiveAsync(udp);
            _maintenanceTask = MaintainAsync();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        await BroadcastAsync("query", linked.Token).ConfigureAwait(false);
    }

    private byte[] Packet(string kind) => JsonSerializer.SerializeToUtf8Bytes(
        new Announcement("WinLanChat", 1, kind, _id, _name, Environment.MachineName, _chatPort), JsonOptions);

    private async Task BroadcastAsync(string kind, CancellationToken cancellationToken)
    {
        // Re-read adapters on each refresh to pick up Wi-Fi/VPN/network changes.
        IReadOnlyList<IPEndPoint> targets = await Task.Run(_targets, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;
        bool sent = false;
        foreach (IPEndPoint target in targets)
        {
            try
            {
                await SendAsync(Packet(kind), target, cancellationToken).ConfigureAwait(false);
                sent = true;
            }
            catch (SocketException ex) { lastError = ex; }
        }
        if (!sent && lastError is not null)
            throw lastError;
    }

    private async Task SendAsync(byte[] packet, IPEndPoint target, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UdpClient udp = _udp ?? throw new InvalidOperationException("Поиск ещё не запущен.");
            await udp.SendAsync(packet, target, cancellationToken).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    private async Task ReceiveAsync(UdpClient udp)
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await udp.ReceiveAsync(_shutdown.Token).ConfigureAwait(false); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
                {
                    // Windows can report ICMP from a peer that closed before our reply arrived.
                    continue;
                }
                if (received.Buffer.Length > MaximumPacketBytes)
                    continue;
                Announcement? packet;
                try { packet = JsonSerializer.Deserialize<Announcement>(received.Buffer, JsonOptions); }
                catch (JsonException) { continue; }
                if (packet is null || packet.App != "WinLanChat" || packet.Version != 1 ||
                    packet.Kind is not ("query" or "present" or "bye") || packet.Id == Guid.Empty || packet.Id == _id ||
                    !ChatProtocol.IsValidName(packet.Name) || !ChatProtocol.IsValidName(packet.ComputerName) ||
                    packet.Port is < 1 or > 65535)
                    continue;
                IPAddress address = received.RemoteEndPoint.Address;
                byte first = address.GetAddressBytes()[0];
                if (first is 0 or >= 224)
                    continue;
                bool changed = false;
                bool reply = false;
                lock (_sync)
                {
                    _peers.TryGetValue(packet.Id, out Presence? previous);
                    if (packet.Kind == "bye")
                    {
                        if (previous?.Peer.Host == address.ToString())
                            changed = _peers.Remove(packet.Id);
                    }
                    else if (previous is not null || _peers.Count < MaximumPeers)
                    {
                        var peer = new DiscoveredPeer(packet.Id, packet.Name!, packet.ComputerName!, address.ToString(), packet.Port);
                        long now = Environment.TickCount64;
                        reply = packet.Kind == "query" && (previous is null || now - previous.LastReply >= 1000);
                        changed = previous?.Peer != peer;
                        _peers[packet.Id] = new Presence(peer, now, reply ? now : previous?.LastReply ?? long.MinValue / 2);
                    }
                    if (changed)
                        Publish();
                }
                if (reply)
                {
                    try { await SendAsync(Packet("present"), received.RemoteEndPoint, _shutdown.Token).ConfigureAwait(false); }
                    catch (SocketException) { /* A departing peer must not stop discovery. */ }
                }
            }
        }
        catch (Exception ex) when (_shutdown.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (SocketException ex) { Error?.Invoke("Поиск компьютеров остановлен: " + ex.Message); }
    }

    private async Task MaintainAsync()
    {
        using var timer = new PeriodicTimer(_tick);
        long lastAnnouncement = Environment.TickCount64;
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                long now = Environment.TickCount64;
                lock (_sync)
                {
                    Guid[] expired = _peers.Where(p => now - p.Value.LastSeen >= _expiry.TotalMilliseconds)
                        .Select(p => p.Key).ToArray();
                    foreach (Guid id in expired)
                        _peers.Remove(id);
                    if (expired.Length > 0)
                        Publish();
                }
                if (now - lastAnnouncement < _announceInterval.TotalMilliseconds)
                    continue;
                lastAnnouncement = now;
                try { await BroadcastAsync("query", _shutdown.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is SocketException or NetworkInformationException)
                { Error?.Invoke("Не удалось обновить список компьютеров: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    // Called under _sync so receive/expiry snapshots cannot arrive out of order.
    private void Publish() => PeersChanged?.Invoke(_peers.Values.Select(p => p.Peer)
        .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Host, StringComparer.Ordinal).ToArray());

    private static IReadOnlyList<IPEndPoint> BroadcastTargets()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Parse("127.255.255.255") };
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (UnicastIPAddressInformation info in adapter.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork || info.IPv4Mask.Equals(IPAddress.Any))
                    continue;
                byte[] ip = info.Address.GetAddressBytes();
                byte[] mask = info.IPv4Mask.GetAddressBytes();
                if (mask[3] >= 254) // /31 and /32 have no broadcast address.
                    continue;
                addresses.Add(new IPAddress(ip.Zip(mask, (a, m) => (byte)(a | ~m)).ToArray()));
            }
        }
        return addresses.Select(a => new IPEndPoint(a, DiscoveryPort)).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        if (_udp is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            try { await BroadcastAsync("bye", timeout.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or NetworkInformationException) { }
        }
        _shutdown.Cancel();
        _udp?.Dispose();
        await Task.WhenAll(_receiveTask ?? Task.CompletedTask, _maintenanceTask ?? Task.CompletedTask).ConfigureAwait(false);
    }

    private sealed record Announcement(string? App, int Version, string? Kind, Guid Id, string? Name, string? ComputerName, int Port);
    private sealed record Presence(DiscoveredPeer Peer, long LastSeen, long LastReply);
}
