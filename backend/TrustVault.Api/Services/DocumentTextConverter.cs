using System.Text;

namespace TrustVault.Api.Services;

public interface IDocumentTextConverter
{
    Task<string> ExtractTextAsync(
        Stream content,
        string extension,
        CancellationToken cancellationToken);

    Task<byte[]> CreateDocxAsync(string text, CancellationToken cancellationToken);
}

public sealed class DocumentTextConverterClient(
    HttpClient httpClient,
    IConfiguration configuration) : IDocumentTextConverter
{
    private const int MaximumConversionResponseBytes = 10 * 1024 * 1024;
    private readonly HttpClient _httpClient = httpClient;
    private readonly string _baseUrl =
        configuration["DocumentConversion:Endpoint"] ?? "http://localhost:6100/";

    public async Task<string> ExtractTextAsync(
        Stream content,
        string extension,
        CancellationToken cancellationToken)
    {
        using var requestContent = new StreamContent(content);
        requestContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(
            $"api/convert/to-text?extension={Uri.EscapeDataString(extension)}",
            requestContent,
            cancellationToken);
        if (response.Content.Headers.ContentLength > MaximumConversionResponseBytes)
        {
            throw new DocumentConversionException("The converted text is too large to edit.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > MaximumConversionResponseBytes)
        {
            throw new DocumentConversionException("The converted text is too large to edit.");
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new DocumentConversionException(
                "The converter returned text that is not valid UTF-8.",
                exception);
        }
    }

    public async Task<byte[]> CreateDocxAsync(
        string text,
        CancellationToken cancellationToken)
    {
        using var requestContent = new StringContent(text, Encoding.UTF8, "text/plain");
        using var response = await SendAsync(
            "api/convert/to-docx",
            requestContent,
            cancellationToken);
        if (response.Content.Headers.ContentLength > MaximumConversionResponseBytes)
        {
            throw new DocumentConversionException("The converted DOCX file is too large.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > MaximumConversionResponseBytes)
        {
            throw new DocumentConversionException("The converted DOCX file is too large.");
        }

        return bytes;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string requestPath,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(
                new Uri(new Uri(_baseUrl, UriKind.Absolute), requestPath),
                content,
                cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new DocumentConversionUnavailableException(
                "The isolated Word conversion service is unavailable.",
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DocumentConversionUnavailableException(
                "The isolated Word conversion service timed out.",
                exception);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        response.Dispose();
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            throw new DocumentConversionUnavailableException(
                "The isolated Word conversion service is unavailable.");
        }

        throw new DocumentConversionException(
            "The Word file could not be converted.");
    }
}

public class DocumentConversionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class DocumentConversionUnavailableException(
    string message,
    Exception? innerException = null) : DocumentConversionException(message, innerException);
