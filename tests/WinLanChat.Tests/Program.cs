using System.IO;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using WinLanChat.Core.Networking;

if (args.Length == 2 && args[0] == "--single-instance-worker")
    return SingleInstanceChecks.RunWorker(args[1]);
if (args.Length == 1 && args[0] == "--duplicate-app")
    return SingleInstanceChecks.RunDuplicateApplication();

var tests = new (string Name, Func<Task> Run)[]
{
    ("Single instance rejects duplicate and concurrent launches, signals activation and recovers after exit or crash", SingleInstanceChecks.LifecycleAsync),
    ("Message links preserve addresses and reject non-web protocols", MessageLinkChecks.ParsingAsync),
    ("Unicode messages in both directions and maximum message size", ExchangeAsync),
    ("Concurrent sends preserve complete frames", ConcurrentSendsAsync),
    ("Files preserve bytes, captions, empty text and chat during bidirectional transfers", FileTransferChecks.ExchangeAsync),
    ("File rejection, cancellation and disconnect preserve destinations and clean partial files", FileTransferChecks.RejectCancelAndDisconnectAsync),
    ("File transfer rejects unsafe names, malformed offers and unapproved overwrites", FileTransferChecks.ValidateAsync),
    ("Unsolicited, duplicate, oversized and incomplete files cannot replace destinations", FileTransferChecks.RejectInvalidPayloadsAsync),
    ("Invalid outgoing input does not break a conversation", ValidateInputAsync),
    ("A second connection is rejected without replacing the first", RejectSecondConnectionAsync),
    ("Disconnect and reconnect", ReconnectAsync),
    ("Cancelling handshake releases the connection slot", CancelHandshakeAsync),
    ("Oversized, negative and malformed frames are rejected", RejectMalformedFramesAsync),
    ("Shutdown closes an incomplete handshake and releases the port", ShutdownAsync),
    ("Discovery finds endpoints, connects and removes departing peers", DiscoveryChecks.DiscoverAndConnectAsync),
    ("Broadcast discovery shares a UDP port, updates automatically and keeps identities distinct", DiscoveryChecks.SharedPortBroadcastAsync),
    ("Discovery rejects invalid packets, updates identities and expires silent peers", DiscoveryChecks.ValidateAndExpireAsync),
    ("Discovery ignores itself, supports cancellation and releases the UDP port", DiscoveryChecks.SelfAndCancellationAsync),
    ("Profile settings persist, reject invalid files and survive cancelled saves", ProfileChecks.PersistenceAsync),
    ("Windows startup persists, quotes launch paths, cancels changes and preserves other registrations", WindowsStartupChecks.RegistryAsync),
    ("WPF attachments, startup, profile changes, Windows notifications, tray restore, background chat, shutdown and layout", () => UiSmoke.RunAsync(args.Length == 2 && args[0] == "--preview" ? args[1] : null))
};

int failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(name.StartsWith("WPF") ? 30 : 15));
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL  {name}: {ex}");
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} checks passed.");
return failures == 0 ? 0 : 1;

static async Task ExchangeAsync()
{
    await using var a = new ChatNode("Компьютер А");
    await using var b = new ChatNode("Компьютер Б");
    var atA = Inbox(a);
    var atB = Inbox(b);
    var hello = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    b.ConnectionChanged += peer => { if (peer is not null) hello.TrySetResult(peer.Name); };
    a.Start(0);
    b.Start(0);
    await a.ConnectAsync("127.0.0.1", b.ListeningPort);
    Equal("Компьютер А", await hello.Task);
    await a.SendAsync("Привет! 👋\nВторая строка");
    Equal("Привет! 👋\nВторая строка", await ReadAsync(atB));
    await b.SendAsync("Ответ получен");
    Equal("Ответ получен", await ReadAsync(atA));
    string large = new('Ж', ChatNode.MaximumMessageLength);
    await a.SendAsync(large);
    Equal(large, await ReadAsync(atB));
}

static async Task ConcurrentSendsAsync()
{
    await using var a = new ChatNode("A");
    await using var b = new ChatNode("B");
    var atB = Inbox(b);
    a.Start(0);
    b.Start(0);
    await a.ConnectAsync("127.0.0.1", b.ListeningPort);
    await Task.WhenAll(Enumerable.Range(0, 100).Select(i => a.SendAsync($"Сообщение {i}")));
    var received = new HashSet<string>();
    for (int i = 0; i < 100; i++)
        received.Add(await ReadAsync(atB));
    if (received.Count != 100 || !Enumerable.Range(0, 100).All(i => received.Contains($"Сообщение {i}")))
        throw new Exception("Missing or corrupted messages.");
}

static async Task ValidateInputAsync()
{
    try { _ = new ChatNode("bad\nname"); throw new Exception("Invalid name accepted."); }
    catch (ArgumentException) { }
    await using var a = new ChatNode("A");
    await using var b = new ChatNode("B");
    var atB = Inbox(b);
    a.Start(0);
    b.Start(0);
    await a.ConnectAsync("127.0.0.1", b.ListeningPort);
    foreach (string invalid in new[] { "   ", new string('x', ChatNode.MaximumMessageLength + 1) })
    {
        try { await a.SendAsync(invalid); throw new Exception("Invalid message accepted."); }
        catch (ArgumentException) { }
    }
    await a.SendAsync("still connected");
    Equal("still connected", await ReadAsync(atB));
}

static async Task RejectSecondConnectionAsync()
{
    await using var a = new ChatNode("A");
    await using var b = new ChatNode("B");
    await using var c = new ChatNode("C");
    var atB = Inbox(b);
    a.Start(0);
    b.Start(0);
    c.Start(0);
    await a.ConnectAsync("127.0.0.1", b.ListeningPort);
    bool rejected = false;
    try { await c.ConnectAsync("127.0.0.1", b.ListeningPort); }
    catch (Exception ex) when (ex is IOException or SocketException) { rejected = true; }
    if (!rejected)
        throw new Exception("Second connection unexpectedly succeeded.");
    await a.SendAsync("original conversation");
    Equal("original conversation", await ReadAsync(atB));
}

static async Task ReconnectAsync()
{
    await using var a = new ChatNode("A");
    await using var b = new ChatNode("B");
    var atA = Inbox(a);
    var disconnectedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var disconnectedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    a.ConnectionChanged += peer => { if (peer is null) disconnectedA.TrySetResult(); };
    b.ConnectionChanged += peer => { if (peer is null) disconnectedB.TrySetResult(); };
    a.Start(0);
    b.Start(0);
    await a.ConnectAsync("127.0.0.1", b.ListeningPort);
    a.Disconnect();
    await Task.WhenAll(disconnectedA.Task, disconnectedB.Task);
    await b.ConnectAsync("127.0.0.1", a.ListeningPort);
    await b.SendAsync("reconnected");
    Equal("reconnected", await ReadAsync(atA));
}

static async Task CancelHandshakeAsync()
{
    using var silent = new TcpListener(IPAddress.Loopback, 0);
    silent.Start();
    await using var a = new ChatNode("A");
    await using var b = new ChatNode("B");
    var atB = Inbox(b);
    a.Start(0);
    b.Start(0);
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
    Task connection = a.ConnectAsync("127.0.0.1", ((IPEndPoint)silent.LocalEndpoint).Port, cancel.Token);
    using TcpClient stalled = await silent.AcceptTcpClientAsync();
    try { await connection; throw new Exception("Cancelled handshake succeeded."); }
    catch (OperationCanceledException) { }
    await a.ConnectAsync("127.0.0.1", b.ListeningPort);
    await a.SendAsync("after cancellation");
    Equal("after cancellation", await ReadAsync(atB));
}

static async Task RejectMalformedFramesAsync()
{
    foreach (byte[] frame in new[]
    {
        FrameHeader(int.MaxValue),
        FrameHeader(-1),
        FrameHeader(0),
        Frame(Encoding.UTF8.GetBytes("{invalid json")),
        Frame(Encoding.UTF8.GetBytes("{\"Kind\":\"hello\",\"Version\":99,\"Name\":\"bad\"}")),
        Frame(Encoding.UTF8.GetBytes("{\"Kind\":\"hello\",\"Version\":1,\"Name\":\"bad\\nname\"}"))
    })
    {
        await using var node = new ChatNode("Receiver");
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Error += _ => rejected.TrySetResult();
        node.Start(0);
        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, node.ListeningPort);
        await raw.GetStream().WriteAsync(frame);
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // A bad packet must leave the listener available for a legitimate peer.
        await using var good = new ChatNode("Good");
        good.Start(0);
        await good.ConnectAsync("127.0.0.1", node.ListeningPort);
    }
}

static async Task ShutdownAsync()
{
    var node = new ChatNode("Receiver");
    node.Start(0);
    int port = node.ListeningPort;
    using var raw = new TcpClient();
    await raw.ConnectAsync(IPAddress.Loopback, port);
    await node.DisposeAsync();
    using var replacement = new TcpListener(IPAddress.Any, port);
    replacement.Start();
}

static Channel<string> Inbox(ChatNode node)
{
    var channel = Channel.CreateUnbounded<string>();
    node.MessageReceived += text => channel.Writer.TryWrite(text);
    return channel;
}
static async Task<string> ReadAsync(Channel<string> inbox) =>
    await inbox.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
static byte[] FrameHeader(int size)
{
    byte[] header = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(header, size);
    return header;
}
static byte[] Frame(byte[] payload) => [.. FrameHeader(payload.Length), .. payload];
static void Equal(string expected, string actual)
{
    if (expected != actual)
        throw new Exception("Received value differs from the expected value.");
}
