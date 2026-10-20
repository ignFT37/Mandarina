// The languages Filee ships (Assets/i18n/<code>.json). The installer (src/Filee.Setup) links this file, so a language
// added here is offered by Setup too.

namespace Filee.App.Services;

internal static class AppLanguages
{
    /// <summary>Code → native name (shown in the language pickers of the app and of Setup).</summary>
    public static readonly IReadOnlyList<(string Code, string NativeName)> All =
    [
        ("en", "English"),
        ("es", "Español"),
        ("ko", "한국어"),
        ("zh-CN", "简体中文"),
        // Mandarina: only these four are complete; Filee's ja, pl, hi and ru files stay in Assets/i18n but are not offered.
    ];
}
