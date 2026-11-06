namespace TrustVault.Application.Documents;

public enum DocumentScanVerdict
{
    Clean,
    Rejected
}

public interface IPrivateDocumentScanner
{
    Task CheckReadinessAsync(CancellationToken cancellationToken);

    Task<DocumentScanVerdict> ScanAsync(Stream content, CancellationToken cancellationToken);
}
