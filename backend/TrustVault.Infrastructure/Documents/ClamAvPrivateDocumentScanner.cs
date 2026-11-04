using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using TrustVault.Application.Documents;

namespace TrustVault.Infrastructure.Documents;

public sealed class ClamAvPrivateDocumentScanner(
    string host,
    int port,
    TimeSpan timeout) : IPrivateDocumentScanner
{
    private const int ChunkSize = 8192;
    private const int MaximumResponseBytes = 1024;
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

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
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

            throw new IOException($"ClamAV did not return a valid scan verdict: {response}");
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The ClamAV scan timed out.", exception);
        }
    }
}
