using Avalonia.Markup.Xaml;

namespace DiskStats.App.Localization;

/// <summary>
/// Uebersetzt einen Text in AXAML: <c>Text="{loc:T Side_Drives}"</c>.
///
/// Loest einmal beim Laden auf. Das genuegt, weil die Sprache erst beim naechsten Start
/// wechselt — ein Wechsel im laufenden Betrieb muesste jeden Text binden, und das waere ein
/// Aufwand fuer einen Handgriff, den man einmal im Leben der Installation macht.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider provider) => Loc.T(Key);
}
