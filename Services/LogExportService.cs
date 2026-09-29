using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// Buffered, streaming writer for log exports.
/// </summary>
/// <remarks>
/// <para>
/// Streams line by line instead of building one big string. The display buffer is bounded
/// (<c>MaxUiLogEntries</c>) rather than unbounded, so a single <c>string.Join</c> would technically
/// survive — but it would also hold a second full copy of the log in memory and stall whatever thread
/// ran it, and that is exactly the habit that stops being survivable the day someone exports a large
/// buffer. The streaming shape costs nothing here and removes the question.
/// </para>
/// <para>
/// The write runs on the thread pool: it is file IO plus per-line string assembly, and the UI thread
/// has no business doing either.
/// </para>
/// </remarks>
public sealed class LogExportService : ILogExportService
{
    /// <summary>Buffer for both the file stream and the writer.</summary>
    private const int BufferBytes = 64 * 1024;

    private readonly ILogger<LogExportService> _logger;

    public LogExportService(ILogger<LogExportService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<LogExportResult> ExportAsync(LogExportRequest request, CancellationToken cancellationToken = default)
        // The token is deliberately NOT handed to Task.Run. Doing so made the method throw after all: a
        // token that is already cancelled makes Task.Run return a cancelled task, and awaiting that
        // raises TaskCanceledException — through a method whose entire contract is "never throws".
        // Passing it into the body instead routes cancellation through the catch below, which reports it
        // as a result like every other failure. (Found by ExportAsync_WhenAlreadyCancelled test.)
        => Task.Run(() => Write(request, cancellationToken));

    private LogExportResult Write(LogExportRequest request, CancellationToken cancellationToken)
    {
        var linesWritten = 0;

        try
        {
            var directory = Path.GetDirectoryName(request.FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                // The picker normally guarantees this, but a path typed into the dialog may not exist,
                // and failing at the last step for a missing folder is a poor way to find that out.
                Directory.CreateDirectory(directory);
            }

            using var stream = new FileStream(
                request.FilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                BufferBytes,
                FileOptions.SequentialScan);

            // No BOM. This is a plain text dump that people grep, diff and feed to parsers, and a BOM
            // in the middle of one is only ever a nuisance — the encoding is stated in the header
            // instead, which is where a human looks.
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), BufferBytes);

            WriteHeader(writer, request);

            foreach (var entry in request.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // FormattedText is already exactly what the row shows, so the export cannot drift from
                // the screen — which is the entire promise of "export what I am looking at".
                writer.WriteLine(entry.FormattedText);
                linesWritten++;
            }

            writer.Flush();

            _logger.LogInformation(
                "Exported {Lines} log lines to {File} ({Bytes} bytes)",
                linesWritten,
                request.FilePath,
                stream.Length);

            return new LogExportResult(linesWritten, stream.Length, null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Log export to {File} was cancelled after {Lines} lines",
                request.FilePath,
                linesWritten);

            DeletePartialFile(request.FilePath);
            return new LogExportResult(linesWritten, 0, "导出已取消");
        }
        catch (Exception ex)
        {
            // Includes the real case this has to survive: the target file is open in another program,
            // or the folder is read-only.
            _logger.LogError(ex, "Failed to export the log to {File}", request.FilePath);
            DeletePartialFile(request.FilePath);
            return new LogExportResult(linesWritten, 0, $"导出失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Removes a half-written file after a cancellation or a failure.
    /// </summary>
    /// <remarks>
    /// An export exists complete or not at all. A truncated file left behind after a reported failure is
    /// how someone ends up analysing an incomplete log without knowing it — the worst possible outcome
    /// for a tool whose whole job is telling you what the wire said. Best-effort: the delete can fail for
    /// the very same reason the write did, and that must not turn a reported failure into a thrown one.
    /// </remarks>
    private void DeletePartialFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the partial export at {File}", filePath);
        }
    }

    /// <summary>
    /// Writes the <c>#</c> header block.
    /// </summary>
    /// <remarks>
    /// Every line is commented so the body can be piped straight into a parser without a preprocessing
    /// step, and so the provenance of the file is readable months later without the app.
    /// </remarks>
    private static void WriteHeader(StreamWriter writer, LogExportRequest request)
    {
        writer.WriteLine("# SerialPortTool 日志导出");
        writer.WriteLine($"# 导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"# 范围: {request.ScopeLabel}");
        writer.WriteLine($"# 行数: {request.Entries.Count}");

        if (!string.IsNullOrEmpty(request.SearchSummary))
        {
            // Reports the *search box's own contents*, not "the filters". The level and port filters are
            // applied before entries reach the display buffer, so restating them here would be claiming
            // to describe state this snapshot cannot actually see.
            writer.WriteLine($"# 搜索框: {request.SearchSummary}");
        }

        writer.WriteLine("# 编码: UTF-8 (无 BOM)");
        writer.WriteLine("# ---");
        writer.WriteLine();
    }
}
