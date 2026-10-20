// Mandarina: a failure of an engine that the user can read in their language. JobQueue turns it into the file's
// ErrorKey (a key of the i18n files) and ErrorDetail, instead of the generic "conversion failed" plus the engine's
// English message. Used by the Microsoft Office engine: passwords, Office not answering, a window asking something.

namespace Filee.Core.Conversion;

/// <summary>An engine failure with a localization key for the user and a short technical detail.</summary>
/// <param name="key">Key of the text the user sees (e.g. <c>office.error.password</c>).</param>
/// <param name="detail">What the engine reported (a window title, an HRESULT): shown after the text, written to the log.</param>
/// <param name="inner">The exception that caused it.</param>
public sealed class ConversionFailureException(string key, string? detail = null, Exception? inner = null)
    : Exception(string.IsNullOrWhiteSpace(detail) ? key : $"{key}: {detail}", inner)
{
    /// <summary>Localization key of the message the user sees.</summary>
    public string Key { get; } = key;

    /// <summary>Technical detail to show after the message, or null.</summary>
    public string? Detail { get; } = string.IsNullOrWhiteSpace(detail) ? null : detail;
}
