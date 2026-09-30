using Microsoft.Extensions.Logging.Abstractions;
using SerialPortTool.Core.Enums;
using SerialPortTool.Models;
using SerialPortTool.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SerialPortTool.Tests;

/// <summary>
/// The export writer.
/// </summary>
/// <remarks>
/// Every test here runs against a real file in the temp directory rather than a stream abstraction: what
/// this service promises is a file on disk that a person can open, so the encoding, the BOM question and
/// the delete-on-failure behaviour are exactly the parts worth asserting.
/// </remarks>
public sealed class LogExportServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "SerialPortTool.Tests", Guid.NewGuid().ToString("N"));

    private readonly LogExportService _service = new(NullLogger<LogExportService>.Instance);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private string TargetFile => Path.Combine(_directory, "export.txt");

    private static LogEntry Entry(string content, string portName = "COM3", bool received = true)
        => new()
        {
            Timestamp = new DateTime(2026, 9, 29, 12, 0, 0),
            PortName = portName,
            Content = content,
            Kind = received ? LogEntryKind.Received : LogEntryKind.Sent,
        };

    [Fact]
    public async Task ExportAsync_WritesOneLinePerEntryBehindACommentedHeader()
    {
        var entries = new List<LogEntry> { Entry("first"), Entry("second", received: false) };

        var result = await _service.ExportAsync(
            new LogExportRequest(entries, TargetFile, "当前视图（含筛选与搜索）", string.Empty));

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.LineCount);
        Assert.True(result.ByteCount > 0);

        var lines = await File.ReadAllLinesAsync(TargetFile);

        // Everything above the blank separator is a comment, so the body can be piped straight into a
        // parser without a preprocessing step. ("Above" excludes the separator itself: Take(bodyStart - 1)
        // — an off-by-one that made this assertion fail the first time it ran.)
        var bodyStart = Array.IndexOf(lines, string.Empty) + 1;
        Assert.Equal(entries[0].FormattedText, lines[bodyStart]);
        Assert.Equal(entries[1].FormattedText, lines[bodyStart + 1]);
        Assert.All(lines.Take(bodyStart - 1), line => Assert.StartsWith("#", line));
    }

    [Fact]
    public async Task ExportAsync_RecordsTheScopeRowCountAndSearchBoxInTheHeader()
    {
        var entries = new List<LogEntry> { Entry("only") };

        await _service.ExportAsync(
            new LogExportRequest(entries, TargetFile, "选中行", "\"error\"（正则）"));

        var text = await File.ReadAllTextAsync(TargetFile);

        Assert.Contains("选中行", text);
        Assert.Contains("行数: 1", text);
        Assert.Contains("\"error\"（正则）", text);
    }

    [Fact]
    public async Task ExportAsync_OmitsTheSearchLineWhenNothingWasSearched()
    {
        await _service.ExportAsync(
            new LogExportRequest(new List<LogEntry> { Entry("x") }, TargetFile, "当前视图", string.Empty));

        Assert.DoesNotContain("搜索框", await File.ReadAllTextAsync(TargetFile));
    }

    [Fact]
    public async Task ExportAsync_WritesUtf8WithoutABom()
    {
        await _service.ExportAsync(
            new LogExportRequest(new List<LogEntry> { Entry("温度 25℃") }, TargetFile, "当前视图", string.Empty));

        var head = (await File.ReadAllBytesAsync(TargetFile)).Take(3).ToArray();

        // A BOM in a text dump that people grep and diff is only ever a nuisance. The header states the
        // encoding instead, which is where a human looks.
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, head);
        Assert.Contains("温度 25℃", await File.ReadAllTextAsync(TargetFile, Encoding.UTF8));
    }

    [Fact]
    public async Task ExportAsync_WithNoEntriesStillWritesAReadableFile()
    {
        // The ViewModel refuses to open the picker for an empty selection, so this only documents what
        // the service itself does: a header and no body, rather than an exception or a zero-byte file.
        var result = await _service.ExportAsync(
            new LogExportRequest(new List<LogEntry>(), TargetFile, "选中行", string.Empty));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.LineCount);
        Assert.Contains("行数: 0", await File.ReadAllTextAsync(TargetFile));
    }

    [Fact]
    public async Task ExportAsync_ReportsAFailureInsteadOfThrowing_AndLeavesNoFile()
    {
        // A directory is a path the write cannot use, which is the shape of every real failure this has
        // to survive (a read-only folder, or the target open in another program).
        Directory.CreateDirectory(_directory);

        var result = await _service.ExportAsync(
            new LogExportRequest(new List<LogEntry> { Entry("x") }, _directory, "当前视图", string.Empty));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.ErrorMessage);
        Assert.StartsWith("导出失败：", result.ErrorMessage);
    }

    [Fact]
    public async Task ExportAsync_WhenAlreadyCancelled_ReportsCancellationWithoutThrowing()
    {
        var result = await _service.ExportAsync(
            new LogExportRequest(new List<LogEntry> { Entry("x") }, TargetFile, "当前视图", string.Empty),
            new CancellationToken(canceled: true));

        // The regression this locks down: passing the token to Task.Run made an already-cancelled token
        // surface as a TaskCanceledException straight out of a method documented as never throwing.
        Assert.False(result.Succeeded);
        Assert.Equal("导出已取消", result.ErrorMessage);

        // An export exists complete or not at all — a truncated file left behind is worse than none,
        // because it silently looks like a complete log.
        Assert.False(File.Exists(TargetFile));
    }

    [Fact]
    public async Task ExportAsync_CreatesTheTargetDirectoryWhenItIsMissing()
    {
        var nested = Path.Combine(_directory, "deeper", "still", "export.txt");

        var result = await _service.ExportAsync(
            new LogExportRequest(new List<LogEntry> { Entry("x") }, nested, "当前视图", string.Empty));

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(nested));
    }
}
