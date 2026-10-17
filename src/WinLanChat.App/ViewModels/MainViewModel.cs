using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Windows.Threading;
using WinLanChat.App.Infrastructure;
using WinLanChat.App.Models;
using WinLanChat.Core.Models;
using WinLanChat.Core.Networking;

namespace WinLanChat.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _connecting;
    private ChatNode? _node;
    private string _displayName = Environment.UserName;
    private string _localPort = "45832";
    private string _remoteHost = "";
    private string _remotePort = "45832";
    private string _draft = "";
    private string _status = "Готов к запуску";
    private string _localAddresses = "Запустите приём, чтобы увидеть адрес этого компьютера.";
    private string _errorMessage = "";
    private PeerInfo? _peer;
    private bool _isListening;
    private bool _isConnecting;
    private bool _closed;
    private int _conversation;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        StartCommand = new AsyncCommand(StartAsync, ReportError, () => !IsListening);
        ConnectCommand = new AsyncCommand(ConnectAsync, ReportError,
            () => IsListening && !IsConnected && !IsConnecting && !string.IsNullOrWhiteSpace(RemoteHost));
        SendCommand = new AsyncCommand(SendAsync, ReportError,
            () => IsConnected && !string.IsNullOrWhiteSpace(Draft) && Draft.Length <= ChatNode.MaximumMessageLength);
        DisconnectCommand = new RelayCommand(() => _node?.Disconnect(), () => IsConnected);
        CancelConnectCommand = new RelayCommand(() => _connecting?.Cancel(), () => IsConnecting);
        DismissErrorCommand = new RelayCommand(() => ErrorMessage = "");
    }

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public AsyncCommand StartCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand SendCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand CancelConnectCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string LocalPort { get => _localPort; set => Set(ref _localPort, value); }
    public string RemotePort { get => _remotePort; set => Set(ref _remotePort, value); }
    public string RemoteHost
    {
        get => _remoteHost;
        set { if (Set(ref _remoteHost, value)) RefreshCommands(); }
    }
    public string Draft
    {
        get => _draft;
        set
        {
            if (Set(ref _draft, value))
            {
                OnPropertyChanged(nameof(DraftCounter));
                RefreshCommands();
            }
        }
    }
    public string DraftCounter => $"{Draft.Length:N0} / {ChatNode.MaximumMessageLength:N0}";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string LocalAddresses { get => _localAddresses; private set => Set(ref _localAddresses, value); }
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (Set(ref _errorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool IsListening
    {
        get => _isListening;
        private set
        {
            if (Set(ref _isListening, value))
            {
                OnPropertyChanged(nameof(CanEditProfile));
                RefreshCommands();
            }
        }
    }
    public bool CanEditProfile => !IsListening;
    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (Set(ref _isConnecting, value))
            {
                OnPropertyChanged(nameof(CanEditConnection));
                RefreshCommands();
            }
        }
    }
    public bool IsConnected => _peer is not null;
    public bool CanEditConnection => !IsConnecting && !IsConnected;
    public string PeerName => _peer?.Name ?? "Ваш следующий разговор";
    public string PeerAddress => _peer?.Address ?? "Подключитесь к компьютеру в локальной сети";
    public string ConnectionLabel => IsConnected ? "На связи" : "Нет подключения";

    private async Task StartAsync()
    {
        ErrorMessage = "";
        int port = ParsePort(LocalPort);
        var node = new ChatNode(DisplayName.Trim());
        node.ConnectionChanged += OnConnectionChanged;
        node.MessageReceived += OnMessageReceived;
        node.Error += OnNetworkError;
        try { node.Start(port); }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
        _node = node;
        IsListening = true;
        Status = "Ожидание подключения";
        LocalAddresses = $"Порт {port}";
        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(Dns.GetHostName(), _lifetime.Token);
            string list = string.Join("  •  ", addresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString()).Distinct());
            LocalAddresses = string.IsNullOrEmpty(list)
                ? $"LAN-адрес не найден. Порт {port}."
                : $"{list}  •  порт {port}";
        }
        catch (SocketException) { LocalAddresses = $"Не удалось определить LAN-адрес. Порт {port}."; }
    }

    private async Task ConnectAsync()
    {
        if (_node is null)
            return;
        int port = ParsePort(RemotePort);
        ErrorMessage = "";
        IsConnecting = true;
        Status = "Подключение…";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _connecting = cancellation;
        try { await _node.ConnectAsync(RemoteHost.Trim(), port, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            ErrorMessage = "Подключение отменено.";
        }
        finally
        {
            _connecting = null;
            IsConnecting = false;
            Status = IsConnected ? "Соединение установлено" : "Ожидание подключения";
        }
    }

    private async Task SendAsync()
    {
        if (_node is null)
            return;
        string text = Draft;
        int conversation = _conversation;
        ErrorMessage = "";
        await _node.SendAsync(text, _lifetime.Token);
        if (conversation == _conversation)
        {
            AddMessage(new ChatMessage("Вы", text, true, DateTimeOffset.Now));
            if (Draft == text)
                Draft = "";
        }
    }

    private void OnConnectionChanged(PeerInfo? peer) => Post(() =>
    {
        _peer = peer;
        if (peer is not null)
        {
            _conversation++;
            Messages.Clear();
            Draft = "";
            ErrorMessage = "";
        }
        Status = peer is null ? "Собеседник отключился" : "Соединение установлено";
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanEditConnection));
        OnPropertyChanged(nameof(PeerName));
        OnPropertyChanged(nameof(PeerAddress));
        OnPropertyChanged(nameof(ConnectionLabel));
        RefreshCommands();
    });

    private void OnMessageReceived(string text) => Post(() =>
        AddMessage(new ChatMessage(PeerName, text, false, DateTimeOffset.Now)));

    private void OnNetworkError(string message) => Post(() => ErrorMessage = message);

    private void AddMessage(ChatMessage message)
    {
        // This first version keeps only the current conversation in memory.
        if (Messages.Count >= 2_000)
            Messages.RemoveAt(0);
        Messages.Add(message);
    }

    private void Post(Action action)
    {
        if (_closed || _dispatcher.HasShutdownStarted)
            return;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_closed)
                action();
        });
    }

    private void ReportError(Exception ex)
    {
        if (_closed)
            return;
        ErrorMessage = ex switch
        {
            OperationCanceledException when _connecting?.IsCancellationRequested == true => "Подключение отменено.",
            OperationCanceledException => "Операция отменена или превышено время ожидания.",
            SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } => "Порт уже занят. Выберите другой порт.",
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "Компьютер отклонил подключение. Проверьте адрес, порт и запуск приложения.",
            SocketException => "Не удалось подключиться. Проверьте адрес, локальную сеть и брандмауэр Windows.",
            _ => ex.Message
        };
    }

    private static int ParsePort(string text) =>
        int.TryParse(text, out int port) && port is >= 1 and <= 65535
            ? port : throw new ArgumentException("Порт должен быть числом от 1 до 65535.");

    private void RefreshCommands()
    {
        StartCommand.Refresh();
        ConnectCommand.Refresh();
        SendCommand.Refresh();
        DisconnectCommand.Refresh();
        CancelConnectCommand.Refresh();
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed)
            return;
        _closed = true;
        _lifetime.Cancel();
        if (_node is not null)
        {
            _node.ConnectionChanged -= OnConnectionChanged;
            _node.MessageReceived -= OnMessageReceived;
            _node.Error -= OnNetworkError;
            await _node.DisposeAsync();
        }
    }
}
