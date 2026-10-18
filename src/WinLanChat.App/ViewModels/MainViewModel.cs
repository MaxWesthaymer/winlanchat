using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using WinLanChat.App.Infrastructure;
using WinLanChat.App.Models;
using WinLanChat.App.Services;
using WinLanChat.Core.Models;
using WinLanChat.Core.Networking;

namespace WinLanChat.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ProfileSettingsStore? _settingsStore;
    private readonly IFileDialogs _fileDialogs;
    private readonly IWindowsStartupService? _startupService;
    private bool _startupLoaded;
    private bool _startWithWindows;
    private readonly Dictionary<Guid, FileAttachmentViewModel> _attachments = [];
    private readonly HashSet<Task> _fileTasks = [];
    private string? _attachmentPath;
    private long _attachmentLength;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _connecting;
    private ChatNode? _node;
    private PeerDiscovery? _discovery;
    private DiscoveredPeer? _selectedPeer;
    private bool _isRefreshing;
    private string _displayName = Environment.UserName;
    private string _localPort = "45832";
    private string _remoteHost = "";
    private string _remotePort = "45832";
    private string _draft = "";
    private string _status = "Запуск…";
    private string _localAddresses = "Определение адреса этого компьютера…";
    private string _errorMessage = "";
    private PeerInfo? _peer;
    private bool _isListening;
    private bool _isStarting;
    private bool _settingsLoaded;
    private bool _settingsNeedSave;
    private bool _isSettingsOpen;
    private ProfileSettings? _activeProfile;
    private bool _isConnecting;
    private bool _closed;
    private int _conversation;

    public MainViewModel(Dispatcher dispatcher, ProfileSettingsStore? settingsStore = null, IFileDialogs? fileDialogs = null,
        IWindowsStartupService? startupService = null)
    {
        _dispatcher = dispatcher;
        _settingsStore = settingsStore;
        _fileDialogs = fileDialogs ?? new FileDialogs();
        _startupService = startupService;
        ToggleWindowsStartupCommand = new AsyncCommand(ToggleWindowsStartupAsync,
            ex => ErrorMessage = "Не удалось изменить автозапуск: " + ex.Message,
            () => _startupService is not null && _startupLoaded && !_closed);
        ApplyProfileCommand = new AsyncCommand(ApplyProfileAsync, ReportError,
            () => CanEditProfile && CanEditConnection && !IsRefreshing && HasProfileChanges);
        ResetProfileCommand = new RelayCommand(ResetProfile, () => CanEditProfile && HasProfileChanges && _activeProfile is not null);
        ConnectCommand = new AsyncCommand(ConnectAsync, ReportError,
            () => IsListening && CanEditConnection && !string.IsNullOrWhiteSpace(RemoteHost));
        ConnectSelectedCommand = new AsyncCommand(ConnectSelectedAsync, ReportError,
            () => IsListening && CanEditConnection && SelectedPeer is not null);
        RefreshPeersCommand = new AsyncCommand(RefreshPeersAsync, ReportError,
            () => IsListening && !_isStarting && !IsRefreshing && !_closed);
        SendCommand = new AsyncCommand(SendAsync, ReportError,
            () => IsConnected && (!string.IsNullOrWhiteSpace(Draft) || HasDraftAttachment) && Draft.Length <= ChatNode.MaximumMessageLength);
        AttachFileCommand = new AsyncCommand(AttachFileAsync, ReportError, () => IsConnected && _node?.PeerSupportsFiles == true);
        RemoveAttachmentCommand = new RelayCommand(() => SetAttachment(null), () => HasDraftAttachment);
        DisconnectCommand = new RelayCommand(() => _node?.Disconnect(), () => IsConnected);
        CancelConnectCommand = new RelayCommand(() => _connecting?.Cancel(), () => IsConnecting);
        DismissErrorCommand = new RelayCommand(() => ErrorMessage = "");
    }

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<DiscoveredPeer> Peers { get; } = [];
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand ConnectSelectedCommand { get; }
    public AsyncCommand RefreshPeersCommand { get; }
    public AsyncCommand ApplyProfileCommand { get; }
    public RelayCommand ResetProfileCommand { get; }
    public AsyncCommand ToggleWindowsStartupCommand { get; }
    public bool StartWithWindows
    {
        get => _startWithWindows;
        private set => Set(ref _startWithWindows, value);
    }
    public bool IsSettingsOpen { get => _isSettingsOpen; set => Set(ref _isSettingsOpen, value); }
    public bool HasProfileChanges => _settingsNeedSave || !IsListening || _activeProfile is null
        || DisplayName.Trim() != _activeProfile.DisplayName
        || !int.TryParse(LocalPort, out int port) || port != _activeProfile.ListeningPort;
    public string ProfileHint => _isStarting ? "Применение настроек…"
        : IsConnected || IsConnecting ? "Чтобы применить изменения, завершите подключение."
        : HasProfileChanges ? "Изменения вступят в силу после применения."
        : "Настройки применены.";
    public string ActiveDisplayName => _activeProfile?.DisplayName ?? DisplayName;
    public DiscoveredPeer? SelectedPeer
    {
        get => _selectedPeer;
        set { if (Set(ref _selectedPeer, value)) RefreshCommands(); }
    }
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (Set(ref _isRefreshing, value))
            {
                OnPropertyChanged(nameof(DiscoveryStatus));
                RefreshCommands();
            }
        }
    }
    public string DiscoveryStatus => !IsListening
        ? _isStarting ? "Запуск поиска компьютеров…" : "Приём не запущен. Проверьте имя и порт в настройках."
        : IsRefreshing ? "Поиск компьютеров…"
        : Peers.Count == 0 ? "Пока никого нет. Запустите WinLanChat на другом ПК в этой сети."
        : $"Найдено: {Peers.Count}. Список обновляется автоматически.";
    public AsyncCommand SendCommand { get; }
    public AsyncCommand AttachFileCommand { get; }
    public RelayCommand RemoveAttachmentCommand { get; }
    public bool HasDraftAttachment => _attachmentPath is not null;
    public string DraftAttachmentName => _attachmentPath is null ? "" : Path.GetFileName(_attachmentPath);
    public string DraftAttachmentSize => FileAttachmentViewModel.FormatSize(_attachmentLength);
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand CancelConnectCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public string DisplayName
    {
        get => _displayName;
        set { if (Set(ref _displayName, value)) RefreshProfile(); }
    }
    public string LocalPort
    {
        get => _localPort;
        set { if (Set(ref _localPort, value)) RefreshProfile(); }
    }
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
                OnPropertyChanged(nameof(DiscoveryStatus));
                OnPropertyChanged(nameof(HasProfileChanges));
                RefreshCommands();
            }
        }
    }
    public bool CanEditProfile => !_isStarting && !_closed;
    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (Set(ref _isConnecting, value))
            {
                OnPropertyChanged(nameof(CanEditConnection));
                OnPropertyChanged(nameof(ProfileHint));
                RefreshCommands();
            }
        }
    }
    public bool IsConnected => _peer is not null;
    public bool CanEditConnection => !_closed && !_isStarting && !IsConnecting && !IsConnected;
    public string PeerName => _peer?.Name ?? "Ваш следующий разговор";
    public string PeerAddress => _peer?.Address ?? "Выберите компьютер в списке слева";
    public string ConnectionLabel => IsConnected ? "На связи" : "Нет подключения";

    public async Task InitializeAsync()
    {
        if (_closed || IsListening || _isStarting)
            return;
        SetStarting(true);
        try
        {
            string? loadError = null;
            if (!_startupLoaded && _startupService is not null)
            {
                try { StartWithWindows = await _startupService.IsEnabledAsync(_lifetime.Token); }
                catch (OperationCanceledException) when (_closed) { throw; }
                catch (Exception ex)
                {
                    loadError = "Не удалось проверить автозапуск: " + ex.Message;
                }
                _startupLoaded = true;
                ToggleWindowsStartupCommand.Refresh();
            }
            if (!_settingsLoaded && _settingsStore is not null)
            {
                _settingsLoaded = true;
                try
                {
                    if (await _settingsStore.LoadAsync(_lifetime.Token) is { } settings)
                    {
                        DisplayName = settings.DisplayName;
                        LocalPort = settings.ListeningPort.ToString();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    loadError = string.Join("\n", new[] { loadError,
                        "Не удалось прочитать настройки. Использованы параметры по умолчанию: " + ex.Message }
                        .Where(message => message is not null));
                }
            }
            await StartAsync(new ProfileSettings(DisplayName.Trim(), ParsePort(LocalPort)));
            if (loadError is not null)
                ErrorMessage = loadError;
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!IsListening)
            {
                Status = "Не удалось запустить приём";
                LocalAddresses = "Приём не запущен. Проверьте имя и порт.";
                IsSettingsOpen = true;
            }
            ReportError(ex);
        }
        finally
        {
            SetStarting(false);
        }
    }

    private void SetStarting(bool value)
    {
        _isStarting = value;
        OnPropertyChanged(nameof(CanEditProfile));
        OnPropertyChanged(nameof(CanEditConnection));
        OnPropertyChanged(nameof(DiscoveryStatus));
        RefreshProfile();
    }

    private async Task ToggleWindowsStartupAsync()
    {
        bool enabled = !StartWithWindows;
        ErrorMessage = "";
        try
        {
            await _startupService!.SetEnabledAsync(enabled, _lifetime.Token);
            StartWithWindows = enabled;
        }
        catch (OperationCanceledException) when (_closed) { }
        finally
        {
            // Restore the checkbox from the confirmed value even if the registry write failed.
            OnPropertyChanged(nameof(StartWithWindows));
        }
    }

    private void RefreshProfile()
    {
        OnPropertyChanged(nameof(HasProfileChanges));
        OnPropertyChanged(nameof(ProfileHint));
        OnPropertyChanged(nameof(ActiveDisplayName));
        RefreshCommands();
    }

    private void ResetProfile()
    {
        if (_activeProfile is not { } profile)
            return;
        DisplayName = profile.DisplayName;
        LocalPort = profile.ListeningPort.ToString();
    }

    private async Task ApplyProfileAsync()
    {
        var requested = new ProfileSettings(DisplayName.Trim(), ParsePort(LocalPort));
        requested.Validate(); // Invalid drafts must leave the running listener intact.
        ProfileSettings? previous = IsListening ? _activeProfile : null;
        SetStarting(true);
        ErrorMessage = "";
        try
        {
            bool needsRestart = !IsListening || requested != _activeProfile;
            if (needsRestart)
                await StopNetworkingAsync();
            try
            {
                if (needsRestart)
                    await StartAsync(requested);
            }
            catch (Exception ex) when (!_closed)
            {
                // Restore the previous working endpoint when a new port cannot be opened.
                await StopNetworkingAsync();
                if (previous is not null)
                {
                    try { await StartAsync(previous); }
                    catch (Exception restoreError) when (!_closed)
                    {
                        Status = "Не удалось запустить приём";
                        LocalAddresses = "Приём не запущен. Проверьте имя и порт.";
                        throw new IOException($"Не удалось применить настройки: {ex.Message}. Не удалось восстановить приём: {restoreError.Message}", restoreError);
                    }
                }
                else
                {
                    Status = "Не удалось запустить приём";
                    LocalAddresses = "Приём не запущен. Проверьте имя и порт.";
                }
                ReportError(ex);
                if (previous is not null)
                    ErrorMessage = $"Настройки не применены. {ErrorMessage} Прежние настройки восстановлены.";
                return;
            }
            DisplayName = requested.DisplayName;
            LocalPort = requested.ListeningPort.ToString();
            if (_settingsStore is not null)
            {
                try { await _settingsStore.SaveAsync(requested, _lifetime.Token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _settingsNeedSave = true;
                    ErrorMessage = "Настройки применены, но не сохранены для следующего запуска: " + ex.Message;
                    return;
                }
            }
            _settingsNeedSave = false;
        }
        catch (OperationCanceledException) when (_closed) { }
        finally { SetStarting(false); }
    }

    private async Task StartAsync(ProfileSettings profile)
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        profile.Validate();
        ErrorMessage = "";
        int port = profile.ListeningPort;
        var node = new ChatNode(profile.DisplayName);
        node.ConnectionChanged += peer => OnConnectionChanged(node, peer);
        node.MessageReceived += text => OnMessageReceived(node, text);
        node.FileOffered += offer => Post(node, () =>
        {
            var attachment = CreateAttachment(node, offer, false);
            AddMessage(new ChatMessage(PeerName, offer.Text, false, DateTimeOffset.Now) { Attachment = attachment });
        });
        node.FileTransferChanged += update => Post(node, () =>
        {
            if (_attachments.TryGetValue(update.Id, out var attachment))
            {
                attachment.Update(update);
                if (update.State is FileTransferState.Completed or FileTransferState.Rejected or FileTransferState.Cancelled or FileTransferState.Failed)
                    _attachments.Remove(update.Id);
            }
        });
        node.Error += message => OnNetworkError(node, message);
        try { node.Start(port); }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
        _node = node;
        _activeProfile = profile;
        RefreshProfile();
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
        if (!_closed)
            await RefreshPeersAsync();
    }

    private async Task RefreshPeersAsync()
    {
        if (_node is null || IsRefreshing || _closed)
            return;
        ErrorMessage = "";
        IsRefreshing = true;
        try
        {
            if (_discovery is null)
            {
                var discovery = new PeerDiscovery(_activeProfile!.DisplayName, _node.ListeningPort);
                discovery.PeersChanged += peers => OnPeersChanged(discovery, peers);
                discovery.Error += message => OnNetworkError(discovery, message);
                try { discovery.Start(); }
                catch
                {
                    await discovery.DisposeAsync();
                    throw;
                }
                _discovery = discovery;
            }
            await _discovery.RefreshAsync(_lifetime.Token);
            await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token);
        }
        catch (Exception ex) when (ex is SocketException or System.Net.NetworkInformation.NetworkInformationException)
        {
            ErrorMessage = $"Поиск компьютеров недоступен: {ex.Message}. Проверьте доступ UDP {PeerDiscovery.DiscoveryPort} в брандмауэре.";
        }
        finally { IsRefreshing = false; }
    }

    private void OnPeersChanged(PeerDiscovery source, IReadOnlyList<DiscoveredPeer> peers) => Post(source, () =>
    {
        Guid? selectedId = SelectedPeer?.Id;
        // Preserve list items and selection when only another participant changes.
        for (int i = Peers.Count - 1; i >= 0; i--)
            if (!peers.Any(p => p.Id == Peers[i].Id))
                Peers.RemoveAt(i);
        for (int i = 0; i < peers.Count; i++)
        {
            int existing = Peers.ToList().FindIndex(p => p.Id == peers[i].Id);
            if (existing < 0)
                Peers.Insert(i, peers[i]);
            else
            {
                if (Peers[existing] != peers[i])
                    Peers[existing] = peers[i];
                if (existing != i)
                    Peers.Move(existing, i);
            }
        }
        SelectedPeer = Peers.FirstOrDefault(p => p.Id == selectedId);
        OnPropertyChanged(nameof(DiscoveryStatus));
    });

    private Task ConnectSelectedAsync()
    {
        DiscoveredPeer? peer = SelectedPeer;
        return peer is null ? Task.CompletedTask : ConnectToAsync(peer.Host, peer.Port);
    }

    private Task ConnectAsync() => ConnectToAsync(RemoteHost.Trim(), ParsePort(RemotePort));

    private async Task ConnectToAsync(string host, int port)
    {
        if (_node is null)
            return;
        ErrorMessage = "";
        IsConnecting = true;
        Status = "Подключение…";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _connecting = cancellation;
        try { await _node.ConnectAsync(host, port, cancellation.Token); }
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
        if (_attachmentPath is { } path)
        {
            ChatNode node = _node;
            var offer = new FileOffer(Guid.NewGuid(), text, Path.GetFileName(path), _attachmentLength);
            var attachment = CreateAttachment(node, offer, true);
            AddMessage(new ChatMessage("Вы", text, true, DateTimeOffset.Now) { Attachment = attachment });
            Draft = "";
            SetAttachment(null);
            TrackFileTask(SendAttachmentAsync(node, attachment, path));
            return;
        }
        await _node.SendAsync(text, _lifetime.Token);
        if (conversation == _conversation)
        {
            AddMessage(new ChatMessage("Вы", text, true, DateTimeOffset.Now));
            if (Draft == text)
                Draft = "";
        }
    }

    private async Task AttachFileAsync()
    {
        int conversation = _conversation;
        if (_fileDialogs.ChooseAttachment() is { } path)
        {
            long length = await Task.Run(() => new FileInfo(path).Length, _lifetime.Token);
            if (length > 1L << 40) throw new IOException("Размер файла превышает 1 ТБ.");
            if (_closed || !IsConnected || conversation != _conversation) return;
            _attachmentLength = length;
            SetAttachment(path);
        }
    }

    private void SetAttachment(string? path)
    {
        _attachmentPath = path;
        OnPropertyChanged(nameof(HasDraftAttachment));
        OnPropertyChanged(nameof(DraftAttachmentName));
        OnPropertyChanged(nameof(DraftAttachmentSize));
        RefreshCommands();
    }

    private FileAttachmentViewModel CreateAttachment(ChatNode node, FileOffer offer, bool outgoing)
    {
        FileAttachmentViewModel? attachment = null;
        attachment = new FileAttachmentViewModel(offer, outgoing,
            () => SaveAttachmentAsync(node, attachment!),
            () => node.RejectFileAsync(offer.Id),
            () => node.CancelFileAsync(offer.Id), ReportError);
        _attachments.Add(offer.Id, attachment);
        return attachment;
    }

    private async Task SendAttachmentAsync(ChatNode node, FileAttachmentViewModel attachment, string path)
    {
        try { await node.SendFileAsync(attachment.Id, path, attachment.Offer.Text, _lifetime.Token, attachment.Offer.Length); }
        catch (OperationCanceledException)
        {
            attachment.Update(new(attachment.Id, FileTransferState.Cancelled));
            _attachments.Remove(attachment.Id);
        }
        catch (Exception ex)
        {
            attachment.Update(new(attachment.Id, FileTransferState.Failed, Error: ex.Message));
            _attachments.Remove(attachment.Id);
        }
    }

    private Task SaveAttachmentAsync(ChatNode node, FileAttachmentViewModel attachment)
    {
        if (_fileDialogs.ChooseDestination(attachment.FileName) is not { } path)
            return Task.CompletedTask;
        attachment.SavedPath = path;
        // Opening the native dialog grants consent, including its overwrite confirmation.
        Task task = ReceiveAttachmentAsync(node, attachment, path);
        TrackFileTask(task);
        return task;
    }

    private async Task ReceiveAttachmentAsync(ChatNode node, FileAttachmentViewModel attachment, string path)
    {
        try { await node.AcceptFileAsync(attachment.Id, path, overwrite: true, _lifetime.Token); }
        catch (OperationCanceledException)
        {
            attachment.Update(new(attachment.Id, FileTransferState.Cancelled));
            _attachments.Remove(attachment.Id);
        }
        catch (Exception ex)
        {
            attachment.Update(new(attachment.Id, FileTransferState.Failed, Error: ex.Message));
            _attachments.Remove(attachment.Id);
        }
    }

    private void TrackFileTask(Task task)
    {
        _fileTasks.Add(task);
        _ = ObserveFileTaskAsync(task);
    }

    private async Task ObserveFileTaskAsync(Task task)
    {
        try { await task; }
        finally { _fileTasks.Remove(task); }
    }

    private void OnConnectionChanged(ChatNode source, PeerInfo? peer) => Post(source, () => UpdateConnection(peer));

    private void UpdateConnection(PeerInfo? peer)
    {
        foreach (var attachment in _attachments.Values)
            attachment.Update(new(attachment.Id, FileTransferState.Failed, Error: "Соединение закрыто."));
        _attachments.Clear();
        _peer = peer;
        if (peer is not null)
        {
            _conversation++;
            Messages.Clear();
            Draft = "";
            SetAttachment(null);
            ErrorMessage = "";
        }
        Status = peer is null ? "Собеседник отключился" : "Соединение установлено";
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanEditConnection));
        OnPropertyChanged(nameof(PeerName));
        OnPropertyChanged(nameof(PeerAddress));
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(ProfileHint));
        RefreshCommands();
    }

    private void OnMessageReceived(ChatNode source, string text) => Post(source, () =>
        AddMessage(new ChatMessage(PeerName, text, false, DateTimeOffset.Now)));

    private void OnNetworkError(object source, string message) => Post(source, () => ErrorMessage = message);

    private void AddMessage(ChatMessage message)
    {
        // This first version keeps only the current conversation in memory.
        if (Messages.Count >= 2_000)
        {
            // Keep offers and active transfers reachable so the user can accept or cancel them.
            int oldest = Messages.ToList().FindIndex(m => m.Attachment is null || !_attachments.ContainsKey(m.Attachment.Id));
            if (oldest >= 0) Messages.RemoveAt(oldest);
        }
        Messages.Add(message);
    }

    private void Post(object source, Action action)
    {
        if (_closed || _dispatcher.HasShutdownStarted)
            return;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_closed && (ReferenceEquals(_node, source) || ReferenceEquals(_discovery, source)))
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
        ToggleWindowsStartupCommand.Refresh();
        ApplyProfileCommand.Refresh();
        ResetProfileCommand.Refresh();
        ConnectCommand.Refresh();
        ConnectSelectedCommand.Refresh();
        RefreshPeersCommand.Refresh();
        SendCommand.Refresh();
        AttachFileCommand.Refresh();
        RemoveAttachmentCommand.Refresh();
        DisconnectCommand.Refresh();
        CancelConnectCommand.Refresh();
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed)
            return;
        _closed = true;
        _lifetime.Cancel();
        await StopNetworkingAsync();
        await Task.WhenAll(_fileTasks.ToArray());
    }

    private async Task StopNetworkingAsync()
    {
        PeerDiscovery? discovery = _discovery;
        ChatNode? node = _node;
        _discovery = null;
        _node = null;
        IsListening = false;
        SelectedPeer = null;
        Peers.Clear();
        UpdateConnection(null);
        try
        {
            if (discovery is not null)
                await discovery.DisposeAsync();
        }
        finally
        {
            if (node is not null)
                await node.DisposeAsync();
        }
    }
}
