namespace TrustVault.Domain.Documents;

public enum DocumentScanStatus
{
    PendingScan,
    Scanning,
    Clean,
    Rejected,
    ScanFailed
}
