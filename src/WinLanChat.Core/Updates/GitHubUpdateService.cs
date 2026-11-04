using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinLanChat.Core.Localization;

namespace WinLanChat.Core.Updates;

public sealed record AppRelease(Version Version, string Notes, Uri DownloadUri, long Size, string Sha256);

public interface IAppUpdateService
{
    Task<AppRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken);
    Task<DownloadedUpdate> DownloadAsync(AppRelease release, IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>Holds a verified installer open for reading, preventing replacement before launch.</summary>
public sealed class DownloadedUpdate(string path, FileStream readLock) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose()
    {
        readLock.Dispose();
        try
        {
            File.Delete(Path);
            Directory.Delete(System.IO.Path.GetDirectoryName(Path)!);
        }
        catch (IOException) { } // A launched installer can still be using the file.
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class GitHubUpdateService : IAppUpdateService
{
    public const string Repository = "MaxWesthaymer/winlanchat";
    private const long MaximumInstallerSize = 1L << 30;
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly HttpClient _client;
    private readonly string _downloadRoot;

    public GitHubUpdateService(HttpClient? client = null, string? downloadRoot = null)
    {
        _client = client ?? SharedClient;
        _downloadRoot = downloadRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinLanChat-Updates");
    }

    public async Task<AppRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WinLanChat", currentVersion.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(Strings.UpdateNoRelease);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException(Strings.UpdateRateLimited);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var metadata = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (metadata.Length + count > 1024 * 1024)
                throw new InvalidDataException(Strings.UpdateInvalidRelease);
            metadata.Write(buffer, 0, count);
        }
        try
        {
            using var json = JsonDocument.Parse(metadata.ToArray());
            return ParseRelease(json.RootElement, currentVersion);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(Strings.UpdateInvalidRelease, ex);
        }
    }

    internal static AppRelease? ParseRelease(JsonElement json, Version currentVersion)
    {
        if (json.GetProperty("draft").GetBoolean() || json.GetProperty("prerelease").GetBoolean())
            return null;
        string tag = json.GetProperty("tag_name").GetString() ?? "";
        string number = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Regex.IsMatch(number, @"^\d+\.\d+\.\d+(\.\d+)?$", RegexOptions.CultureInvariant)
            || !Version.TryParse(number, out var version))
            throw new InvalidDataException(Strings.UpdateInvalidRelease);
        if (Normalize(version) <= Normalize(currentVersion))
            return null;
        string expectedName = $"WinLanChat-{number}-win-x64-Setup.exe";
        var assets = json.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == expectedName).ToArray();
        if (assets.Length != 1)
            throw new InvalidDataException(Strings.UpdateInstallerMissing);
        var asset = assets[0];
        string url = asset.GetProperty("browser_download_url").GetString() ?? "";
        string expectedUrl = $"https://github.com/{Repository}/releases/download/{tag}/{expectedName}";
        long size = asset.GetProperty("size").GetInt64();
        string digest = asset.TryGetProperty("digest", out var value) ? value.GetString() ?? "" : "";
        if (url != expectedUrl || size is <= 0 or > MaximumInstallerSize)
            throw new InvalidDataException(Strings.UpdateInvalidRelease);
        if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException(Strings.UpdateDigestMissing);
        string notes = json.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
            ? body.GetString() ?? "" : "";
        return new(version, notes[..Math.Min(notes.Length, 16_000)], new Uri(url), size, digest[7..]);
    }

    private static Version Normalize(Version value) =>
        new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));

    public async Task<DownloadedUpdate> DownloadAsync(AppRelease release, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!IsTrustedDownload(release.DownloadUri) || release.DownloadUri.Host != "github.com"
            || !release.DownloadUri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal)
            || release.Size is <= 0 or > MaximumInstallerSize
            || !Regex.IsMatch(release.Sha256, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException(Strings.UpdateInvalidRelease);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        string directory = System.IO.Path.Combine(_downloadRoot, Guid.NewGuid().ToString("N"));
        string path = System.IO.Path.Combine(directory, "WinLanChat-Setup.exe");
        FileStream? readLock = null;
        try
        {
            using var response = await GetDownloadAsync(release.DownloadUri, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException(Strings.UpdateIntegrityFailed);
            Directory.CreateDirectory(directory);
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[64 * 1024];
                long received = 0;
                int reportedPercent = 0;
                progress.Report(0);
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                    int count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > release.Size)
                        throw new InvalidDataException(Strings.UpdateIntegrityFailed);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    int percent = (int)(100L * received / release.Size);
                    if (percent > reportedPercent)
                    {
                        reportedPercent = percent;
                        progress.Report(percent);
                    }
                }
                if (received != release.Size)
                    throw new InvalidDataException(Strings.UpdateIntegrityFailed);
            }
            readLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(readLock, timeout.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(release.Sha256)))
                throw new InvalidDataException(Strings.UpdateIntegrityFailed);
            timeout.Token.ThrowIfCancellationRequested();
            return new DownloadedUpdate(path, readLock);
        }
        catch
        {
            readLock?.Dispose();
            try
            {
                File.Delete(path);
                if (Directory.Exists(directory)) Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetDownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (!IsTrustedDownload(uri)) throw new InvalidDataException(Strings.UpdateInvalidRelease);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("WinLanChat-Updater");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null) break;
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new InvalidDataException(Strings.UpdateInvalidRelease);
    }

    private static bool IsTrustedDownload(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
}
