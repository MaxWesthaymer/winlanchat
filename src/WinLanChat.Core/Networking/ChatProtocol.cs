using System.Buffers.Binary;
using System.Text.Json;

namespace WinLanChat.Core.Networking;

internal sealed record WireMessage(string Kind, int Version = 1, string? Name = null, string? Text = null);

internal static class ChatProtocol
{
    internal const int MaximumFrameBytes = 65_536;
    internal const int MaximumTextLength = 8_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8 };

    internal static async Task WriteAsync(Stream stream, WireMessage message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaximumFrameBytes)
            throw new InvalidDataException("Сообщение превышает допустимый размер.");
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<WireMessage> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("Недопустимый размер сетевого сообщения.");
        byte[] payload = new byte[size];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<WireMessage>(payload, JsonOptions)
            ?? throw new InvalidDataException("Получено пустое сетевое сообщение.");
    }

    internal static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && !name.Any(char.IsControl);

    internal static bool IsValidText(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= MaximumTextLength;
}
