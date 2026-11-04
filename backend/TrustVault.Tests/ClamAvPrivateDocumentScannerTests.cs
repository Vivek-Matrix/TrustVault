using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TrustVault.Application.Documents;
using TrustVault.Infrastructure.Documents;

namespace TrustVault.Tests;

public sealed class ClamAvPrivateDocumentScannerTests
{
    [Theory]
    [InlineData("stream: OK\0", DocumentScanVerdict.Clean)]
    [InlineData("stream: Test.Signature FOUND\0", DocumentScanVerdict.Rejected)]
    public async Task Scanner_streams_content_using_the_ClamAV_instream_protocol(
        string response,
        DocumentScanVerdict expectedVerdict)
    {
        var contents = Enumerable.Range(0, 20_000)
            .Select(index => (byte)(index % 251))
            .ToArray();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RespondToScanAsync(listener, contents, response);
        var scanner = new ClamAvPrivateDocumentScanner(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));

        await using var content = new MemoryStream(contents, writable: false);
        var verdict = await scanner.ScanAsync(content, CancellationToken.None);
        await server;

        Assert.Equal(expectedVerdict, verdict);
    }

    [Fact]
    public async Task Scanner_rejects_non_verdict_responses()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RespondToScanAsync(listener, [1, 2, 3], "stream: ERROR\0");
        var scanner = new ClamAvPrivateDocumentScanner(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(5));

        await using var content = new MemoryStream([1, 2, 3], writable: false);
        await Assert.ThrowsAsync<IOException>(() =>
            scanner.ScanAsync(content, CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task Scanner_times_out_when_the_daemon_does_not_return_a_verdict()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptedClient = listener.AcceptTcpClientAsync();
        var scanner = new ClamAvPrivateDocumentScanner(
            "127.0.0.1",
            port,
            TimeSpan.FromMilliseconds(500));

        await using var content = new MemoryStream([1, 2, 3], writable: false);
        await Assert.ThrowsAsync<IOException>(() =>
            scanner.ScanAsync(content, CancellationToken.None));
        using var daemonClient = await acceptedClient;
    }

    private static async Task RespondToScanAsync(
        TcpListener listener,
        byte[] expectedContent,
        string response)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var command = new byte["zINSTREAM\0"u8.Length];
        await ReadExactlyAsync(stream, command);
        Assert.Equal("zINSTREAM\0", Encoding.ASCII.GetString(command));

        using var received = new MemoryStream();
        var chunkLength = new byte[sizeof(int)];
        while (true)
        {
            await ReadExactlyAsync(stream, chunkLength);
            var length = BinaryPrimitives.ReadInt32BigEndian(chunkLength);
            if (length == 0)
            {
                break;
            }

            var chunk = new byte[length];
            await ReadExactlyAsync(stream, chunk);
            await received.WriteAsync(chunk);
        }

        Assert.Equal(expectedContent, received.ToArray());
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer[read..]);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("ClamAV client closed the stream early.");
            }

            read += bytesRead;
        }
    }
}
