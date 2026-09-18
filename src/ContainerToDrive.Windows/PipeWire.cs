using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContainerToDrive.Core;

namespace ContainerToDrive.Windows;

/// <summary>Four-byte little-endian length followed by UTF-8 JSON. One reader/writer per direction.</summary>
public static class PipeWire
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (value is null) throw new ArgumentNullException(nameof(value));
        token.ThrowIfCancellationRequested();
        var buffer = ArrayPool<byte>.Shared.Rent(Wire.MaxMessageBytes);
        try
        {
            // A non-expandable stream bounds our serialized output before any frame bytes are sent.
            using var payload = new MemoryStream(buffer, 0, Wire.MaxMessageBytes, writable: true, publiclyVisible: true);
            payload.SetLength(0);
            try { await JsonSerializer.SerializeAsync(payload, value, Wire.Json, token).ConfigureAwait(false); }
            catch (NotSupportedException) { throw new InvalidDataException("The message is too large or cannot be serialized."); }
            catch (JsonException) { throw new InvalidDataException("The message cannot be serialized."); }

            var length = checked((int)payload.Length);
            if (length is < 1 or > Wire.MaxMessageBytes) throw new InvalidDataException("Invalid message length.");
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            await stream.WriteAsync(header, token).ConfigureAwait(false);
            await stream.WriteAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            // Request frames can contain a SAS. Clear the entire rented buffer before returning it.
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > Wire.MaxMessageBytes) throw new InvalidDataException("Invalid message length.");

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactlyAsync(stream, buffer.AsMemory(0, length), token).ConfigureAwait(false);
            try
            {
                // Reject malformed UTF-8, JSON null, trailing non-whitespace data, and malformed JSON.
                Utf8.GetCharCount(buffer, 0, length);
                using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 32 });
                RejectDuplicateProperties(document.RootElement);
                var value = JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), Wire.Json)
                    ?? throw new InvalidDataException("An empty JSON message is not allowed.");
                if (value is Request { Credential: { } credential })
                {
                    try { Validation.ValidateCredentialShape(credential); }
                    catch (ArgumentException) { throw new InvalidDataException("The peer sent an invalid credential message."); }
                }
                return value;
            }
            catch (JsonException) { throw new InvalidDataException("The peer sent an invalid JSON message."); }
            catch (DecoderFallbackException) { throw new InvalidDataException("The peer sent invalid UTF-8."); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        try { await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false); }
        catch (EndOfStreamException) { throw new EndOfStreamException("The pipe closed before the complete frame arrived."); }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The peer sent duplicate JSON properties.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}