using WinLanChat.Core.Localization;

namespace WinLanChat.App.Services;

/// <summary>Reserves one messenger instance across Windows sessions and signals repeat launches.</summary>
internal sealed class SingleInstanceService : IDisposable
{
    internal const string InstanceName = @"Global\WinLanChat.SingleInstance";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _activationWait;
    private bool _disposed;

    private SingleInstanceService(Mutex mutex, EventWaitHandle activation)
    {
        _mutex = mutex;
        _activation = activation;
    }

    // Acquire and dispose on the application's UI thread: mutex ownership is thread-affine.
    public static SingleInstanceService? TryAcquire(string name = InstanceName)
    {
        Mutex? mutex = null;
        EventWaitHandle? activation = null;
        try
        {
            // Create the signal before acquiring the mutex so repeat launches during startup
            // remain pending until the first instance has created its window.
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
            mutex = new Mutex(false, name);
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                activation.Set();
                return null;
            }

            var instance = new SingleInstanceService(mutex, activation);
            mutex = null;
            activation = null;
            return instance;
        }
        catch (UnauthorizedAccessException)
        {
            // An instance belonging to another Windows user may deny access to its handles.
            // Do not start a second messenger in that case.
            return null;
        }
        finally
        {
            mutex?.Dispose();
            activation?.Dispose();
        }
    }

    public void ListenForActivation(Action activate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_activationWait is not null)
            throw new InvalidOperationException(Strings.ActivationListenerAlreadyRegistered);

        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation,
            (_, _) => activate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _activationWait?.Unregister(null);
        _activation.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
