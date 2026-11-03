using System.Diagnostics;
using System.Text;

namespace TrustVault.Converter;

public sealed class LibreOfficeConverter(IConfiguration configuration)
{
    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromSeconds(30);
    private const long MaximumFileBytes = 10 * 1024 * 1024;
    private const long MaximumTextBytes = 10 * 1024 * 1024;
    private readonly string _executablePath =
        configuration["DocumentConversion:LibreOfficePath"] ?? "soffice";
    private readonly SemaphoreSlim _conversionLock = new(1, 1);

    public async Task<string> ExtractTextAsync(
        Stream content,
        string extension,
        CancellationToken cancellationToken)
    {
        await _conversionLock.WaitAsync(cancellationToken);
        string? workingDirectory = null;
        try
        {
            workingDirectory = CreateWorkingDirectory();
            var sourcePath = Path.Combine(workingDirectory, $"source{extension}");
            await CopyBoundedAsync(content, sourcePath, cancellationToken);
            var convertedPath = await ConvertAsync(
                workingDirectory,
                sourcePath,
                "txt:Text",
                cancellationToken);
            if (new FileInfo(convertedPath).Length > MaximumTextBytes)
            {
                throw new DocumentConversionException("The converted text is too large.");
            }

            return await File.ReadAllTextAsync(
                convertedPath,
                new UTF8Encoding(false, true),
                cancellationToken);
        }
        finally
        {
            try
            {
                if (workingDirectory is not null)
                {
                    Directory.Delete(workingDirectory, recursive: true);
                }
            }
            finally
            {
                _conversionLock.Release();
            }
        }
    }

    public async Task<byte[]> CreateDocxAsync(string text, CancellationToken cancellationToken)
    {
        await _conversionLock.WaitAsync(cancellationToken);
        string? workingDirectory = null;
        try
        {
            workingDirectory = CreateWorkingDirectory();
            var sourcePath = Path.Combine(workingDirectory, "edited.txt");
            await File.WriteAllTextAsync(
                sourcePath,
                text,
                new UTF8Encoding(false),
                cancellationToken);
            var convertedPath = await ConvertAsync(
                workingDirectory,
                sourcePath,
                "docx",
                cancellationToken);
            var docx = await File.ReadAllBytesAsync(convertedPath, cancellationToken);
            if (docx.LongLength is <= 0 or > MaximumFileBytes)
            {
                throw new DocumentConversionException(
                    "The converted DOCX file is empty or too large.");
            }

            return docx;
        }
        finally
        {
            try
            {
                if (workingDirectory is not null)
                {
                    Directory.Delete(workingDirectory, recursive: true);
                }
            }
            finally
            {
                _conversionLock.Release();
            }
        }
    }

    private async Task<string> ConvertAsync(
        string workingDirectory,
        string sourcePath,
        string outputFormat,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(workingDirectory, "output");
        var profileDirectory = Path.Combine(workingDirectory, "profile");
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(profileDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--safe-mode");
        startInfo.ArgumentList.Add("--norestore");
        startInfo.ArgumentList.Add($"-env:UserInstallation={new Uri(profileDirectory).AbsoluteUri}");
        startInfo.ArgumentList.Add("--convert-to");
        startInfo.ArgumentList.Add(outputFormat);
        startInfo.ArgumentList.Add("--outdir");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add(sourcePath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new DocumentConversionUnavailableException(
                    "LibreOffice could not be started.");
            }
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new DocumentConversionUnavailableException(
                "LibreOffice is required in the converter container.",
                exception);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConversionTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new DocumentConversionUnavailableException(
                    "LibreOffice timed out while converting the file.");
            }

            throw;
        }

        await Task.WhenAll(standardOutput, standardError);
        if (process.ExitCode != 0)
        {
            throw new DocumentConversionException(
                "LibreOffice could not convert the document.");
        }

        var convertedPath = Path.Combine(
            outputDirectory,
            $"{Path.GetFileNameWithoutExtension(sourcePath)}.{outputFormat.Split(':')[0]}");
        if (!File.Exists(convertedPath))
        {
            throw new DocumentConversionException(
                "LibreOffice did not create the converted file.");
        }

        return convertedPath;
    }

    private static async Task CopyBoundedAsync(
        Stream input,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var output = File.Create(destination);
        var buffer = new byte[81920];
        long totalBytes = 0;
        while (true)
        {
            var bytesRead = await input.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                return;
            }

            totalBytes += bytesRead;
            if (totalBytes > MaximumFileBytes)
            {
                throw new DocumentConversionException("The Word file is too large to convert.");
            }

            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
    }

    private static string CreateWorkingDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trustvault-converter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

public class DocumentConversionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class DocumentConversionUnavailableException(
    string message,
    Exception? innerException = null) : DocumentConversionException(message, innerException);
