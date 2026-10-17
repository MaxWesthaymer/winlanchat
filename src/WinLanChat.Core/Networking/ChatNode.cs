using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinLanChat.Core.Models;

namespace WinLanChat.Core.Networking;

/// <summary>A TCP listener with one direct conversation at a time. Events arrive on worker threads.</summary>
public sealed class ChatNode : IAsyncDisposable
{
    public const int MaximumMessageLength = ChatProtocol.MaximumTextLength;
    private readonly string _name;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly HashSet<Task> _tasks = [];
    private TcpListener? _listener;
    private Task? _acceptTask;
    private TcpClient? _reservedClient;
    private TcpClient? _connectedClient;
    private bool _disposed;

    public ChatNode(string name)
    {
        if (!ChatProtocol.IsValidName(name))
            throw new ArgumentException("Имя должно содержать от 1 до 64 символов без управляющих знаков.", nameof(name));
        _name = name.Trim();
    }

    public event Action<PeerInfo?>? ConnectionChanged;
    public event Action<string>? MessageReceived;
    public event Action<string>? Error;
    public int ListeningPort { get; private set; }

    public void Start(int port)
    {
        if (port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null)
                throw new InvalidOperationException("Приём подключений уже запущен.");
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start(8);
            _listener = listener;
            ListeningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptTask = AcceptLoopAsync(listener);
        }
    }

    public Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (host.Length > 253 || port is < 1 or > 65535)
            throw new ArgumentException("Проверьте адрес и порт собеседника.");
        var client = new TcpClient { NoDelay = true };
        if (!TryReserve(client))
        {
            client.Dispose();
            throw new InvalidOperationException("Завершите текущее подключение перед новым.");
        }
        Task task = ConnectCoreAsync(client, host, port, cancellationToken);
        Track(task);
        return task;
    }

    private async Task ConnectCoreAsync(TcpClient client, string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            await EstablishAsync(client, timeout.Token).ConfigureAwait(false);
            Track(ReceiveLoopAsync(client));
        }
        catch
        {
            Release(client);
            throw;
        }
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!ChatProtocol.IsValidText(text))
            throw new ArgumentException($"Сообщение должно содержать от 1 до {MaximumMessageLength} символов.", nameof(text));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        TcpClient? client = null;
        try
        {
            lock (_sync)
                client = _connectedClient;
            if (client is null)
                throw new InvalidOperationException("Собеседник не подключён.");
            linked.CancelAfter(TimeSpan.FromSeconds(15));
            await ChatProtocol.WriteAsync(client.GetStream(), new WireMessage("chat", Text: text), linked.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // Cancellation after a partial frame makes the stream unusable.
            client?.Dispose();
            throw;
        }
        finally { _sendGate.Release(); }
    }

    public void Disconnect()
    {
        lock (_sync)
            _reservedClient?.Dispose();
    }

    private bool TryReserve(TcpClient client)
    {
        lock (_sync)
        {
            if (_disposed || _listener is null || _reservedClient is not null)
                return false;
            _reservedClient = client;
            return true;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                client.NoDelay = true;
                if (TryReserve(client))
                    Track(HandleIncomingAsync(client));
                else
                    client.Dispose();
            }
        }
        catch (Exception ex) when (_shutdown.IsCancellationRequested && IsNetworkError(ex)) { }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            Error?.Invoke("Ошибка приёма подключений: " + ex.Message);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client)
    {
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await EstablishAsync(client, timeout.Token).ConfigureAwait(false);
            }
            await ReceiveLoopAsync(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            Release(client);
            if (!_shutdown.IsCancellationRequested)
                Error?.Invoke("Не удалось принять подключение: " + ex.Message);
        }
    }

    private async Task EstablishAsync(TcpClient client, CancellationToken cancellationToken)
    {
        NetworkStream stream = client.GetStream();
        await ChatProtocol.WriteAsync(stream, new WireMessage("hello", Name: _name), cancellationToken).ConfigureAwait(false);
        WireMessage hello = await ChatProtocol.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (hello.Kind != "hello" || hello.Version != 1 || !ChatProtocol.IsValidName(hello.Name))
            throw new InvalidDataException("Собеседник использует несовместимый протокол или недопустимое имя.");
        lock (_sync)
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            _connectedClient = client;
        }
        ConnectionChanged?.Invoke(new PeerInfo(hello.Name!, client.Client.RemoteEndPoint?.ToString() ?? "LAN"));
    }

    private async Task ReceiveLoopAsync(TcpClient client)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            while (!_shutdown.IsCancellationRequested)
            {
                WireMessage message = await ChatProtocol.ReadAsync(stream, _shutdown.Token).ConfigureAwait(false);
                if (message.Kind != "chat" || message.Version != 1 || !ChatProtocol.IsValidText(message.Text))
                    throw new InvalidDataException("Получено недопустимое сообщение.");
                MessageReceived?.Invoke(message.Text!);
            }
        }
        catch (EndOfStreamException) { }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            if (!_shutdown.IsCancellationRequested && ex is not ObjectDisposedException)
                Error?.Invoke("Соединение прервано: " + ex.Message);
        }
        finally { Release(client); }
    }

    private void Release(TcpClient client)
    {
        bool wasConnected = false;
        lock (_sync)
        {
            if (ReferenceEquals(_reservedClient, client))
            {
                wasConnected = ReferenceEquals(_connectedClient, client);
                _connectedClient = null;
                _reservedClient = null;
            }
        }
        client.Dispose();
        if (wasConnected)
            ConnectionChanged?.Invoke(null);
    }

    private void Track(Task task)
    {
        lock (_sync)
            _tasks.Add(task);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (_sync)
                _tasks.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static bool IsNetworkError(Exception ex) =>
        ex is IOException or InvalidDataException or SocketException or OperationCanceledException or ObjectDisposedException or JsonException;

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _shutdown.Cancel();
            _listener?.Stop();
            _reservedClient?.Dispose();
        }
        if (_acceptTask is not null)
            await _acceptTask.ConfigureAwait(false);
        Task[] tasks;
        lock (_sync)
            tasks = _tasks.ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception ex) when (IsNetworkError(ex)) { }
        // Keep the source alive for concurrent callers unwinding after shutdown.
    }
}
