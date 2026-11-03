using TrustVault.Application.Documents;

namespace TrustVault.Infrastructure.Documents;

public sealed class FileSystemPrivateDocumentStorage : IPrivateDocumentStorage
{
    private readonly string _rootPath;

    public FileSystemPrivateDocumentStorage(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public async Task StoreAsync(
        string storageKey,
        Stream content,
        CancellationToken cancellationToken)
    {
        var path = GetPath(storageKey);
        var created = false;
        var completed = false;
        try
        {
            await using (var destination = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                await content.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            completed = true;
        }
        finally
        {
            if (created && !completed && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public Task<Stream> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            GetPath(storageKey),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(storageKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetPath(string storageKey)
    {
        if (!Guid.TryParseExact(storageKey, "N", out _))
        {
            throw new ArgumentException("Invalid document storage key.", nameof(storageKey));
        }

        return Path.Combine(_rootPath, $"{storageKey}.pdf");
    }
}
