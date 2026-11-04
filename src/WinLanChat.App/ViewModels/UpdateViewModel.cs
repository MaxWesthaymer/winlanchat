using System.Net.Http;
using WinLanChat.App.Infrastructure;
using WinLanChat.App.Services;
using WinLanChat.Core.Localization;
using WinLanChat.Core.Updates;

namespace WinLanChat.App.ViewModels;

public sealed class UpdateViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAppUpdateService _service;
    private readonly IUpdateInteraction _interaction;
    private readonly Func<bool> _hasPendingFiles;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private Task _running = Task.CompletedTask;
    private AppRelease? _release;
    private DownloadedUpdate? _download;
    private string _statusKey = nameof(Strings.UpdateIdle);
    private string _error = "";
    private double _progress;
    private bool _busy;
    private bool _downloading;
    private bool _closed;

    public UpdateViewModel(IAppUpdateService service, IUpdateInteraction interaction, Func<bool> hasPendingFiles)
    {
        _service = service;
        _interaction = interaction;
        _hasPendingFiles = hasPendingFiles;
        CheckCommand = new AsyncCommand(() => CheckAsync(), ReportError, () => !_busy && !_closed && _download is null);
        DownloadCommand = new AsyncCommand(() => RunAsync(DownloadAsync), ReportError,
            () => !_busy && !_closed && _release is not null && _download is null);
        InstallCommand = new RelayCommand(Install, () => !_busy && !_closed && _download is not null);
        CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => _busy && !_closed);
        Strings.LanguageChanged += Refresh;
    }

    public event Action? InstallationStarted;
    public Version CurrentVersion { get; } = typeof(UpdateViewModel).Assembly.GetName().Version ?? new Version(1, 0, 0);
    public string VersionLabel => Strings.Format(Strings.UpdateCurrentVersion,
        CurrentVersion.ToString(CurrentVersion.Revision > 0 ? 4 : 3));
    public string Status => _statusKey == nameof(Strings.UpdateAvailable)
        ? Strings.Format(Strings.UpdateAvailable, _release!.Version) : Strings.Get(_statusKey);
    public string Error => _error;
    public bool HasError => !string.IsNullOrEmpty(_error);
    public string ReleaseNotes => _release?.Notes ?? "";
    public bool HasUpdate => _release is not null;
    public bool IsReady => _download is not null;
    public bool IsBusy => _busy;
    public bool IsDownloading => _downloading;
    public double Progress => _progress;
    public AsyncCommand CheckCommand { get; }
    public AsyncCommand DownloadCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand CancelCommand { get; }

    public Task CheckAsync() => RunAsync(async token =>
    {
        _statusKey = nameof(Strings.UpdateChecking);
        Refresh();
        _release = await _service.CheckAsync(CurrentVersion, token);
        token.ThrowIfCancellationRequested();
        _statusKey = _release is null ? nameof(Strings.UpdateUpToDate) : nameof(Strings.UpdateAvailable);
    });

    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_closed || _busy || _download is not null) return Task.CompletedTask;
        _busy = true;
        _error = "";
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Refresh();
        _running = RunCoreAsync(action, _operation);
        return _running;
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> action, CancellationTokenSource operation)
    {
        try { await action(operation.Token); }
        catch (OperationCanceledException)
        {
            _statusKey = operation.IsCancellationRequested ? nameof(Strings.UpdateCancelled) : nameof(Strings.UpdateTimedOut);
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            _busy = false;
            _downloading = false;
            _operation = null;
            operation.Dispose();
            Refresh();
        }
    }

    private async Task DownloadAsync(CancellationToken token)
    {
        _downloading = true;
        _progress = 0;
        _statusKey = nameof(Strings.UpdateDownloading);
        Refresh();
        // Progress<T> returns to the UI context. Ignore callbacks queued after cancellation/disposal.
        var progress = new Progress<double>(value =>
        {
            if (_closed || token.IsCancellationRequested || !_downloading) return;
            _progress = value;
            OnPropertyChanged(nameof(Progress));
        });
        _download = await _service.DownloadAsync(_release!, progress, token);
        _statusKey = nameof(Strings.UpdateReady);
    }

    private void Install()
    {
        _error = "";
        _statusKey = nameof(Strings.UpdateReady);
        Refresh();
        if (_hasPendingFiles())
        {
            _error = Strings.UpdateFilesPending;
            Refresh();
            return;
        }
        if (!_interaction.ConfirmInstallation(_release!.Version.ToString())) return;
        // Native confirmation dialogs pump the dispatcher: offers can arrive while it is open.
        if (_closed || _hasPendingFiles())
        {
            _error = Strings.UpdateFilesPending;
            Refresh();
            return;
        }
        try
        {
            _interaction.StartInstaller(_download!.Path);
            InstallationStarted?.Invoke();
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private void ReportError(Exception ex)
    {
        if (_closed) return;
        _statusKey = nameof(Strings.UpdateFailed);
        _error = ex is HttpRequestException ? Strings.UpdateNetworkFailed : ex.Message;
        Refresh();
    }

    private void Refresh()
    {
        foreach (string name in new[] { nameof(VersionLabel), nameof(Status), nameof(Error), nameof(HasError),
            nameof(ReleaseNotes), nameof(HasUpdate), nameof(IsReady), nameof(IsBusy), nameof(IsDownloading), nameof(Progress) })
            OnPropertyChanged(name);
        CheckCommand.Refresh();
        DownloadCommand.Refresh();
        InstallCommand.Refresh();
        CancelCommand.Refresh();
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        Strings.LanguageChanged -= Refresh;
        _lifetime.Cancel();
        await _running;
        _download?.Dispose();
        _lifetime.Dispose();
        Refresh();
    }
}
