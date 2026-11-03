using System.Text;
using TrustVault.Converter;

const long maximumFileBytes = 10 * 1024 * 1024;
const int maximumTextCharacters = 2_000_000;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maximumFileBytes;
});
builder.Services.AddSingleton<LibreOfficeConverter>();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost(
    "/api/convert/to-text",
    async (
        HttpRequest request,
        LibreOfficeConverter converter,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        var extension = request.Query["extension"].ToString();
        if (!extension.Equals(".doc", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "Only DOC and DOCX conversion is supported." });
        }

        try
        {
            var text = await converter.ExtractTextAsync(
                request.Body,
                extension,
                cancellationToken);
            return Results.Text(text, "text/plain", Encoding.UTF8);
        }
        catch (DocumentConversionUnavailableException exception)
        {
            logger.LogError(exception, "LibreOffice is unavailable.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The Word conversion engine is unavailable.");
        }
        catch (DocumentConversionException exception)
        {
            logger.LogWarning(exception, "The Word file could not be converted to text.");
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The Word file could not be converted.");
        }
    });

app.MapPost(
    "/api/convert/to-docx",
    async (
        HttpRequest request,
        LibreOfficeConverter converter,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        string text;
        try
        {
            using var reader = new StreamReader(
                request.Body,
                new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            text = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (DecoderFallbackException)
        {
            return Results.BadRequest(new { error = "Text content must be valid UTF-8." });
        }

        if (text.Length > maximumTextCharacters)
        {
            return Results.BadRequest(new { error = "Text content is too long to convert." });
        }

        try
        {
            var docx = await converter.CreateDocxAsync(text, cancellationToken);
            if (docx.LongLength > maximumFileBytes)
            {
                return Results.BadRequest(new { error = "The converted DOCX file is too large." });
            }

            return Results.File(
                docx,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        }
        catch (DocumentConversionUnavailableException exception)
        {
            logger.LogError(exception, "LibreOffice is unavailable.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The Word conversion engine is unavailable.");
        }
        catch (DocumentConversionException exception)
        {
            logger.LogWarning(exception, "Edited text could not be converted to DOCX.");
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The edited text could not be converted to DOCX.");
        }
    });

app.Run();
