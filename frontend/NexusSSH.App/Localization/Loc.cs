using Microsoft.UI.Xaml.Markup;
using NexusSSH.Core.Localization;

namespace NexusSSH.App.Localization;

/// <summary>
/// XAML-Markup-Extension für die Lokalisierung: <c>Text="{loc:Loc Key=Settings}"</c> liefert den übersetzten Text zum
/// Schlüssel. Aufgelöst wird beim Laden des XAML, die Sprache steht zu diesem Zeitpunkt fest (Wechsel per Neustart).
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Loc : MarkupExtension
{
    /// <summary>Schlüssel im Katalog (<see cref="L"/>).</summary>
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue() => L.Get(Key);
}
