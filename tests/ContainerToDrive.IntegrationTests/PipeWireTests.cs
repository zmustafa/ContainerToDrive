using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ContainerToDrive.Core;
using ContainerToDrive.Windows;
using Xunit;

namespace ContainerToDrive.IntegrationTests;

[Trait("Category", "LocalIntegration")]
public sealed class PipeWireTests
{
    [Fact]
    public async Task RequestAndResponseRoundTripOverAnActualCurrentUserNamedPipe()
    {
        var root = LocalTestRoot.Create();
        var name = "ContainerToDrive-LocalIntegration-" + Path.GetFileName(root.Root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync(deadline.Token);
        await Task.WhenAll(accept, client.ConnectAsync(deadline.Token));

        var credential = SyntheticCredential.Create();
        var request = new Request
        {
            Operation = "Save",
            Profile = SyntheticCredential.Profile(),
            Credential = CredentialSubmission.ContainerSas(credential.Url),
            Confirm = true
        };
        var incoming = PipeWire.ReadAsync<Request>(server, deadline.Token);
        await PipeWire.WriteAsync(client, request, deadline.Token);
        var received = await incoming;
        Assert.Equal(request with { Credential = null }, received with { Credential = null });
        // Compare string contents without an assertion that dumps the SAS on failure.
        var secretMatches = received.Credential?.SasUrl == credential.Url;
        Assert.True(secretMatches, "The synthetic credential did not survive pipe framing.");

        var response = Response.Ok("Synthetic local response");
        var reply = PipeWire.ReadAsync<Response>(client, deadline.Token);
        await PipeWire.WriteAsync(server, response, deadline.Token);
        Assert.Equal(response, await reply);
        root.AssertNoPlaintext([credential]);
    }

    [Fact]
    public async Task WriteUsesLittleEndianUtf8LengthAndReadHandlesFragmentedConcatenatedFrames()
    {
        using var stream = new MemoryStream();
        var first = Response.Ok("Synthetic 日本語 response");
        var second = Response.Fail("Second frame");
        await PipeWire.WriteAsync(stream, first);
        await PipeWire.WriteAsync(stream, second);
        var bytes = stream.ToArray();
        var payload = JsonSerializer.SerializeToUtf8Bytes(first, Wire.Json);
        Assert.Equal(payload.Length, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(payload, bytes.AsSpan(sizeof(int), payload.Length).ToArray());

        // Deterministic short reads of header AND payload, independent of how Windows chunks a pipe.
        using var fragmented = new FragmentedReadStream(bytes);
        Assert.Equal(first, await PipeWire.ReadAsync<Response>(fragmented));
        Assert.Equal(second, await PipeWire.ReadAsync<Response>(fragmented));
        Assert.Equal(fragmented.Length, fragmented.Position);
        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeWire.ReadAsync<Response>(fragmented));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EmptyOrTruncatedHeaderFails(int headerBytes)
    {
        using var stream = new FragmentedReadStream(new byte[headerBytes]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeWire.ReadAsync<Request>(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(Wire.MaxMessageBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidLengthIsRejectedBeforeReadingOrAllocatingItsPayload(int length)
    {
        // A valid trailing payload must remain unread even when a hostile length claims gigabytes.
        using var stream = new FragmentedReadStream(Frame(length, "{}"u8.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeWire.ReadAsync<Request>(stream));
        Assert.Equal((long)sizeof(int), stream.Position);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(2, "{")]
    [InlineData(64, "{}")]
    [InlineData(Wire.MaxMessageBytes, "{}")]
    public async Task TruncatedPayloadCannotDeserializeEvenIfAvailableBytesAreValidJson(int length, string payload)
    {
        using var stream = new FragmentedReadStream(Frame(length, Encoding.UTF8.GetBytes(payload)));
        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeWire.ReadAsync<Request>(stream));
    }

    [Theory]
    [InlineData("null")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("{}{}")]
    [InlineData("{}garbage")]
    [InlineData("[]")]
    [InlineData("{\"protocolVersion\":\"invalid\"}")]
    [InlineData("{\"unknownField\":1}")]
    [InlineData("{\"sas\":\"obsolete\"}")]
    [InlineData("{\"protocolVersion\":2,\"protocolVersion\":2}")]
    [InlineData("{\"credential\":{\"kind\":\"AccountKey\",\"expectedRevision\":0,\"sasUrl\":\"mixed\",\"accountKey\":\"mixed\"}}")]
    [InlineData("{\"credential\":{\"kind\":\"ContainerSas\",\"expectedRevision\":0,\"sasUrl\":null,\"accountKey\":null}}")]
    public async Task CorruptOrEmptyJsonCannotBecomeARequest(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var stream = new FragmentedReadStream(Frame(bytes.Length, bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeWire.ReadAsync<Request>(stream));
    }

    [Fact]
    public async Task MalformedUtf8IsRejectedInsteadOfReplacementCharacterDecoding()
    {
        byte[] payload = [0x22, 0xc3, 0x28, 0x22]; // Quoted string with an invalid continuation byte.
        using var stream = new FragmentedReadStream(Frame(payload.Length, payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeWire.ReadAsync<string>(stream));
    }

    [Fact]
    public async Task MaximumSizeIsAcceptedForBothWriteAndRead()
    {
        // ASCII needs no JSON escaping: two quotes plus this string are exactly the permitted size.
        var text = new string('x', Wire.MaxMessageBytes - 2);
        using var stream = new MemoryStream();
        await PipeWire.WriteAsync(stream, text);
        Assert.Equal((long)Wire.MaxMessageBytes + sizeof(int), stream.Length);
        stream.Position = 0;
        Assert.Equal(text, await PipeWire.ReadAsync<string>(stream));
    }

    [Fact]
    public async Task OversizedWriteDoesNotEmitAPartialFrame()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeWire.WriteAsync(stream, new string('x', Wire.MaxMessageBytes)));
        Assert.Equal(0L, stream.Length);
    }

    [Fact]
    public async Task NullWriteIsRejectedBeforeAnyFrameBytes()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentNullException>(() => PipeWire.WriteAsync<Request>(stream, null!));
        Assert.Equal(0L, stream.Length);
    }

    [Fact]
    public async Task CancelledReadAndWriteDoNotConsumeOrEmitBytes()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stream = new MemoryStream(Frame(2, "{}"u8.ToArray()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PipeWire.ReadAsync<Request>(stream, cancelled.Token));
        Assert.Equal(0L, stream.Position);
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PipeWire.WriteAsync(output, new Request(), cancelled.Token));
        Assert.Equal(0L, output.Length);
    }

    private static byte[] Frame(int length, byte[] payload)
    {
        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, length);
        payload.CopyTo(frame, sizeof(int));
        return frame;
    }

    private sealed class FragmentedReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(1, count), cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(1, buffer.Length)]);
    }
}