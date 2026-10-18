using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace WinLanChat.App.Services;

public interface IWindowsStartupService
{
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

public sealed class WindowsStartupService : IWindowsStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinLanChat";
    private readonly string _registryPath;
    private readonly string _command;

    public WindowsStartupService() : this(RunKey, CreateLaunchCommand(
        Environment.ProcessPath ?? throw new IOException("Не удалось определить путь приложения."),
        Assembly.GetEntryAssembly()?.Location ?? "")) { }

    internal WindowsStartupService(string registryPath, string command)
    {
        _registryPath = registryPath;
        _command = command;
    }

    internal static string CreateLaunchCommand(string processPath, string assemblyPath) =>
        string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{processPath}\" \"{assemblyPath}\""
            : $"\"{processPath}\"";

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }, cancellationToken);

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        // Disabling must not create an otherwise absent key or affect other applications.
        using var key = enabled
            ? Registry.CurrentUser.CreateSubKey(_registryPath, writable: true)
            : Registry.CurrentUser.OpenSubKey(_registryPath, writable: true);
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled)
            (key ?? throw new IOException("Не удалось открыть параметры автозапуска."))
                .SetValue(ValueName, _command, RegistryValueKind.String);
        else
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }, cancellationToken);
}
