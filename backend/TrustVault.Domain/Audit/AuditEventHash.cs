using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrustVault.Domain.Audit;

public static class AuditEventHash
{
    public static string Calculate(AuditEvent auditEvent)
    {
        return CalculateCore(auditEvent, CanonicalizeJson(auditEvent.MetadataJson));
    }

    public static string CalculateLegacy(AuditEvent auditEvent)
    {
        using var document = JsonDocument.Parse(auditEvent.MetadataJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteLegacyJson(document.RootElement, writer);
        }

        return CalculateCore(
            auditEvent,
            Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteLegacyJson(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            element.WriteTo(writer);
            return;
        }

        var properties = element.EnumerateObject().ToArray();
        var recipient = properties.FirstOrDefault(property =>
            property.NameEquals("recipientUserId"));
        var permission = properties.FirstOrDefault(property =>
            property.NameEquals("permission"));
        if (properties.Length == 2 && recipient.Name is not null && permission.Name is not null)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("recipientUserId");
            recipient.Value.WriteTo(writer);
            writer.WritePropertyName("permission");
            permission.Value.WriteTo(writer);
            writer.WriteEndObject();
            return;
        }

        element.WriteTo(writer);
    }

    private static string CalculateCore(AuditEvent auditEvent, string metadataJson)
    {
        var canonicalRecord = JsonSerializer.Serialize(new
        {
            auditEvent.Id,
            auditEvent.ActorUserId,
            auditEvent.ActorRole,
            auditEvent.DocumentId,
            auditEvent.Action,
            auditEvent.Result,
            auditEvent.TimestampUtc,
            auditEvent.RequestId,
            auditEvent.IpAddress,
            auditEvent.UserAgent,
            MetadataJson = metadataJson,
            auditEvent.PreviousHash
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRecord)))
            .ToLowerInvariant();
    }

    private static string CanonicalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(
                    property => property.Name,
                    StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
