using TrustVault.Domain.Audit;

namespace TrustVault.Tests;

public sealed class AuditChainVerifierTests
{
    [Fact]
    public void Empty_chain_is_valid()
    {
        var result = AuditChainVerifier.Verify([]);

        Assert.True(result.IsValid);
        Assert.Equal(0, result.EventCount);
        Assert.Null(result.InvalidEventId);
    }

    [Fact]
    public void Valid_chain_is_accepted()
    {
        var events = CreateChain(3);

        var result = AuditChainVerifier.Verify(events);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.EventCount);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Existing_hash_with_database_formatted_json_metadata_is_accepted()
    {
        var auditEvent = new AuditEvent
        {
            Id = Guid.NewGuid(),
            ActorRole = "Viewer",
            Action = "document.grant",
            Result = AuditResult.Success,
            TimestampUtc = DateTime.UnixEpoch,
            RequestId = "request",
            MetadataJson = "{\"recipientUserId\":\"00000000-0000-0000-0000-000000000001\",\"permission\":\"READ\"}"
        };
        auditEvent.RecordHash = AuditEventHash.CalculateLegacy(auditEvent);
        auditEvent.MetadataJson =
            "{\"permission\": \"READ\", \"recipientUserId\": \"00000000-0000-0000-0000-000000000001\"}";

        var result = AuditChainVerifier.Verify([auditEvent]);

        Assert.True(result.IsValid);
        Assert.Equal(1, result.EventCount);
    }

    [Fact]
    public void Changed_event_contents_are_detected()
    {
        var events = CreateChain(2);
        events[0].Action = "document.changed";

        var result = AuditChainVerifier.Verify(events);

        Assert.False(result.IsValid);
        Assert.Equal(events[0].Id, result.InvalidEventId);
        Assert.Equal("Record hash does not match the event contents.", result.Error);
    }

    [Fact]
    public void Incorrect_record_hash_is_detected()
    {
        var events = CreateChain(1);
        events[0].RecordHash = new string('0', 64);

        var result = AuditChainVerifier.Verify(events);

        Assert.False(result.IsValid);
        Assert.Equal(events[0].Id, result.InvalidEventId);
    }

    [Fact]
    public void Broken_link_is_detected()
    {
        var events = CreateChain(2);
        events[1].PreviousHash = new string('0', 64);

        var result = AuditChainVerifier.Verify(events);

        Assert.False(result.IsValid);
        Assert.Equal(events[1].Id, result.InvalidEventId);
        Assert.Equal("Previous hash does not match the preceding event.", result.Error);
    }

    [Fact]
    public void Missing_middle_event_is_detected()
    {
        var chain = CreateChain(3);
        var events = new[] { chain[0], chain[2] };

        var result = AuditChainVerifier.Verify(events);

        Assert.False(result.IsValid);
        Assert.Equal(chain[2].Id, result.InvalidEventId);
    }

    private static List<AuditEvent> CreateChain(int count)
    {
        var events = new List<AuditEvent>(count);
        string? previousHash = null;
        for (var index = 0; index < count; index++)
        {
            var auditEvent = new AuditEvent
            {
                Id = Guid.NewGuid(),
                ActorRole = "Viewer",
                Action = $"document.action-{index}",
                Result = AuditResult.Success,
                TimestampUtc = DateTime.UnixEpoch.AddMinutes(index),
                RequestId = $"request-{index}",
                MetadataJson = "{}",
                PreviousHash = previousHash
            };
            auditEvent.RecordHash = AuditEventHash.Calculate(auditEvent);
            events.Add(auditEvent);
            previousHash = auditEvent.RecordHash;
        }

        return events;
    }
}
