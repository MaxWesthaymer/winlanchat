using System.IO;
using System.Text.Json;
using WinLanChat.App.Models;
using WinLanChat.Core.Localization;

namespace WinLanChat.App.Services;

public sealed class ProfileSettingsStore(string path)
{
    public static ProfileSettingsStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinLanChat", "settings.json"));

    public async Task<ProfileSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            var settings = await JsonSerializer.DeserializeAsync<ProfileSettings>(input, cancellationToken: cancellationToken)
                ?? throw new JsonException(Strings.EmptySettingsFile);
            settings.Validate();
            return settings;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task SaveAsync(ProfileSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, settings, cancellationToken: cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
