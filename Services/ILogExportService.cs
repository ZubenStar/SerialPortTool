using SerialPortTool.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// Writes a snapshot of log entries to a text file.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IFileLoggerService"/> on purpose, and the difference is the whole point of
/// the feature: the file logger records <b>everything as it arrives</b>, whereas this records <b>what
/// the user is looking at</b> — after the search and the filters, or just the rows they selected.
/// Merging the two would mean the export inherits the "always the full stream" behaviour that makes
/// the log file useful and an export useless.
/// </para>
/// <para>
/// The service takes an already-materialised list rather than a live collection. An
/// <c>ObservableCollection</c> bound to a list control may only be enumerated on the UI thread, and
/// the caller therefore snapshots it there — while the formatting and the write happen off it.
/// </para>
/// </remarks>
public interface ILogExportService
{
    /// <summary>
    /// Writes <paramref name="request"/>'s entries, one per line, and never throws.
    /// </summary>
    /// <remarks>
    /// Failures come back as <see cref="LogExportResult.ErrorMessage"/> rather than as exceptions,
    /// because the only caller is a UI command whose job on failure is to say what went wrong.
    /// </remarks>
    Task<LogExportResult> ExportAsync(LogExportRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Everything one export needs. A record so the call site reads as a description.</summary>
/// <param name="Entries">The snapshot to write, in display order.</param>
/// <param name="FilePath">Destination. Overwritten if it exists — the picker already confirmed that.</param>
/// <param name="ScopeLabel">Human-readable description of what was captured, for the header.</param>
/// <param name="SearchSummary">Search-box contents, or empty. Recorded only when non-empty.</param>
public sealed record LogExportRequest(
    IReadOnlyList<LogEntry> Entries,
    string FilePath,
    string ScopeLabel,
    string SearchSummary);

/// <summary>Outcome of an export. <see cref="Succeeded"/> is the only thing callers must branch on.</summary>
/// <param name="LineCount">Entry lines written, excluding the header.</param>
/// <param name="ByteCount">Size of the finished file.</param>
/// <param name="ErrorMessage">Null on success; a message the UI can show verbatim otherwise.</param>
public sealed record LogExportResult(int LineCount, long ByteCount, string? ErrorMessage)
{
    public bool Succeeded => ErrorMessage is null;
}
