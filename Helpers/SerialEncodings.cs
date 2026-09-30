using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace SerialPortTool.Helpers;

/// <summary>
/// The text encodings the receive and send paths can be switched between, and the only place their names
/// are resolved.
/// </summary>
/// <remarks>
/// <para>
/// UI-free and dependency-free so it can be linked into the logic-layer test project. Every member is
/// total: <see cref="Resolve"/> is reached from the per-port read thread (decoding, and the validation
/// that runs in front of it), where a malformed byte sequence must never surface as a receive-path
/// exception — so both fallbacks are replacement-based and an unusable name degrades to UTF-8 rather
/// than throwing.
/// </para>
/// <para>
/// GB18030 is deliberately the only multi-byte addition. It is the character set Chinese serial devices
/// actually emit, and it covers GBK / GB2312 as well, so a GB2312 device needs no separate entry.
/// </para>
/// </remarks>
public static class SerialEncodings
{
    /// <summary>Name of the default encoding. Also the value every unresolved lookup ends at.</summary>
    public const string Utf8Name = "UTF-8";

    /// <summary>Name of the legacy Chinese code page (54936), which also covers GBK and GB2312.</summary>
    public const string Gb18030Name = "GB18030";

    /// <summary>
    /// The names offered in the UI and accepted from <c>settings.json</c>, in display order.
    /// </summary>
    public static IReadOnlyList<string> SupportedNames { get; } = new[] { Utf8Name, Gb18030Name };

    private static readonly ConcurrentDictionary<string, Encoding> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers the code-page provider exactly once per process.
    /// </summary>
    /// <remarks>
    /// A <see cref="Lazy{T}"/> rather than a static constructor: a throwing static constructor would
    /// poison the whole type with a <see cref="TypeInitializationException"/>, which would take the
    /// receive path down instead of falling back to UTF-8. The result is captured so
    /// <see cref="DescribeAvailability"/> can report it without retrying.
    /// </remarks>
    private static readonly Lazy<bool> ProviderRegistration = new(RegisterProviderCore);

    /// <summary>Canonical name for storage and comparison; an unrecognised name becomes UTF-8.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Utf8Name;
        }

        var trimmed = name.Trim();
        foreach (var supported in SupportedNames)
        {
            if (string.Equals(supported, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return supported;
            }
        }

        return Utf8Name;
    }

    /// <summary>
    /// True when <paramref name="name"/> is one of <see cref="SupportedNames"/>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Normalize"/> so a caller can tell "the user picked UTF-8" from "the file
    /// said something we do not know and we silently used UTF-8" — the second case deserves a warning,
    /// and this helper has no logger of its own (it is linked into the test project).
    /// </remarks>
    public static bool IsSupported(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        foreach (var supported in SupportedNames)
        {
            if (string.Equals(supported, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a name to a usable <see cref="Encoding"/>. Never throws; UTF-8 is the last resort.
    /// </summary>
    /// <remarks>
    /// Cached because <see cref="Encoding.GetEncoding(string)"/> is not cheap and this is reached from
    /// the read thread. The returned instances are shared and are safe for concurrent use as long as the
    /// decoder is created per consumer (<see cref="Encoding.GetDecoder"/>), which is what the receive
    /// path does.
    /// </remarks>
    public static Encoding Resolve(string? name)
    {
        // Touch the registration first: resolving a code page before the provider is in place throws,
        // and the catch below would then quietly turn every GB18030 port into a UTF-8 one.
        _ = ProviderRegistration.Value;

        return Cache.GetOrAdd(Normalize(name), Create);
    }

    /// <summary>
    /// One-line description of what this machine can decode, for the startup log.
    /// </summary>
    /// <remarks>
    /// Doubles as the availability check for the self-contained publish: the code-page provider ships as
    /// part of the framework, but "the assembly is present" and "GetEncoding succeeds in the published
    /// output" are different statements, and only the second one matters. One line at startup is enough
    /// to settle it on any machine that has a GB18030 device attached.
    /// </remarks>
    public static string DescribeAvailability()
    {
        if (!ProviderRegistration.Value)
        {
            return $"Text encodings: {Utf8Name} only — the code-page provider could not be registered, " +
                   $"so {Gb18030Name} falls back to {Utf8Name}.";
        }

        var gb18030 = Resolve(Gb18030Name);
        return gb18030.CodePage == Resolve(Utf8Name).CodePage
            ? $"Text encodings: {Utf8Name} ({gb18030.CodePage}) only — {Gb18030Name} fell back to {Utf8Name}."
            : $"Text encodings: {Utf8Name} ({Resolve(Utf8Name).CodePage}), {Gb18030Name} ({gb18030.CodePage}).";
    }

    private static bool RegisterProviderCore()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return true;
        }
        catch (Exception)
        {
            // Only reachable if the framework assembly is missing, which the build would not have
            // allowed. Reported through DescribeAvailability instead of thrown: this must not be able to
            // stop the app from starting.
            return false;
        }
    }

    private static Encoding Create(string normalizedName)
    {
        // Encoding.UTF8 (rather than a fresh UTF8Encoding) so the default path is byte-for-byte what the
        // app used before the encodings became switchable.
        if (string.Equals(normalizedName, Utf8Name, StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(
                normalizedName,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback);
        }
        catch (ArgumentException)
        {
            // Unreachable when the provider registered, but a fallback is what keeps this member total.
            return Encoding.UTF8;
        }
    }
}
