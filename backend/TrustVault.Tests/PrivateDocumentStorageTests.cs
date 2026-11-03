using TrustVault.Infrastructure.Documents;

namespace TrustVault.Tests;

public sealed class PrivateDocumentStorageTests
{
    [Fact]
    public async Task Store_read_and_delete_use_the_generated_key_inside_private_root()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            $"trustvault-storage-tests-{Guid.NewGuid():N}");
        try
        {
            var storage = new FileSystemPrivateDocumentStorage(rootPath);
            var storageKey = Guid.NewGuid().ToString("N");
            var contents = new byte[] { 1, 2, 3, 4 };
            await using var source = new MemoryStream(contents);

            await storage.StoreAsync(storageKey, source, CancellationToken.None);
            using var result = new MemoryStream();
            await using (var stored = await storage.OpenReadAsync(
                storageKey,
                CancellationToken.None))
            {
                await stored.CopyToAsync(result);
            }

            Assert.Equal(contents, result.ToArray());
            await storage.DeleteAsync(storageKey, CancellationToken.None);
            Assert.False(File.Exists(Path.Combine(rootPath, $"{storageKey}.pdf")));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Storage_rejects_keys_that_are_not_generated_guid_values()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            $"trustvault-storage-tests-{Guid.NewGuid():N}");
        try
        {
            var storage = new FileSystemPrivateDocumentStorage(rootPath);
            await using var source = new MemoryStream([1]);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                storage.StoreAsync("..\\outside", source, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
