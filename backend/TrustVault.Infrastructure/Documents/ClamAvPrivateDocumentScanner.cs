using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using TrustVault.Application.Documents;

namespace TrustVault.Infrastructure.Documents;

public sealed class ClamAvPrivateDocumentScanner(
    string host,
    int port,
    TimeSpan timeout,
    ILogger<ClamAvPrivateDocumentScanner>? logger = null) : IPrivateDocumentScanner
{
    private const int ChunkSize = 8192;
    private const int MaximumResponseBytes = 1024;
    private const int MaximumAttempts = 2;
    private static readonly byte[] PingCommand = "PING\n"u8.ToArray();
    private static readonly byte[] StreamCommand = "zINSTREAM\0"u8.ToArray();

    public async Task<DocumentScanVerdict> ScanAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("The ClamAV host is not configured.");
        }

        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("The ClamAV port is invalid.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The ClamAV timeout must be positive.");
        }

        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            if (attempt > 1)
            {
                if (!content.CanSeek)
                {
                    break;
                }

                content.Position = 0;
            }

            try
            {
                return await ScanOnceAsync(content, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new IOException("The ClamAV scan timed out.");
            }
            catch (IOException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = exception;
            }

            if (attempt < MaximumAttempts)
            {
                logger?.LogWarning(
                    "ClamAV scan attempt {Attempt} failed; retrying once.",
                    attempt);
            }
        }

        throw lastException ?? new IOException("The ClamAV scan failed.");
    }

    public async Task CheckReadinessAsync(CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        using var timeoutSource = CreateTimeoutToken(cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, timeoutSource.Token);
        await using var network = client.GetStream();
        await network.WriteAsync(PingCommand, timeoutSource.Token);

        var response = new byte[4];
        await ReadExactlyAsync(network, response, timeoutSource.Token);
        if (!response.AsSpan().SequenceEqual("PONG"u8))
        {
            throw new IOException("ClamAV did not acknowledge the readiness probe.");
        }
    }

    private async Task<DocumentScanVerdict> ScanOnceAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CreateTimeoutToken(cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, timeoutSource.Token);
        await using var network = client.GetStream();
        await network.WriteAsync(StreamCommand, timeoutSource.Token);

        var buffer = new byte[ChunkSize];
        var lengthBuffer = new byte[sizeof(int)];
        while (true)
        {
            var bytesRead = await content.ReadAsync(buffer, timeoutSource.Token);
            if (bytesRead == 0)
            {
                break;
            }

            BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, bytesRead);
            await network.WriteAsync(lengthBuffer, timeoutSource.Token);
            await network.WriteAsync(buffer.AsMemory(0, bytesRead), timeoutSource.Token);
        }

        BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, 0);
        await network.WriteAsync(lengthBuffer, timeoutSource.Token);

        var responseBuffer = new List<byte>();
        var responseByte = new byte[1];
        while (responseBuffer.Count < MaximumResponseBytes)
        {
            var bytesRead = await network.ReadAsync(responseByte, timeoutSource.Token);
            if (bytesRead == 0 || responseByte[0] is 0 or (byte)'\n')
            {
                break;
            }

            responseBuffer.Add(responseByte[0]);
        }

        if (responseBuffer.Count == MaximumResponseBytes)
        {
            throw new IOException("ClamAV returned an oversized scan response.");
        }

        var response = Encoding.ASCII.GetString(responseBuffer.ToArray());
        if (response.EndsWith(" OK", StringComparison.Ordinal))
        {
            return DocumentScanVerdict.Clean;
        }

        if (response.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            return DocumentScanVerdict.Rejected;
        }

        throw new IOException("ClamAV did not return a valid scan verdict.");
    }

    private CancellationTokenSource CreateTimeoutToken(CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        return timeoutSource;
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("The ClamAV host is not configured.");
        }

        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("The ClamAV port is invalid.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The ClamAV timeout must be positive.");
        }
    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (bytesRead == 0)
            {
                throw new IOException("ClamAV closed the readiness connection.");
            }

            read += bytesRead;
        }
    }
}
