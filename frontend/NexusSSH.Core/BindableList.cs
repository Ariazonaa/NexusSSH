namespace NexusSSH.Core;

/// <summary>
/// Listen, die eine Oberfläche als Datenquelle bekommt (<c>ItemsSource</c>).
/// <para>
/// Unter Native AOT kann WinUI eine Liste nur anzeigen, wenn ihr Typ beim Übersetzen der App bekannt ist
/// (<c>NexusSSH.App/AotExposedTypes.cs</c> zählt sie auf). Eine Collection-Expression für
/// <see cref="IReadOnlyList{T}"/> erzeugt dagegen einen namenlosen Typ des Compilers, den niemand aufzählen kann –
/// die App stürzt dann beim Zuweisen ab. Deshalb entstehen solche Listen hier: immer eine echte
/// <see cref="List{T}"/>.
/// </para>
/// </summary>
public static class BindableList
{
    public static IReadOnlyList<T> Of<T>(IEnumerable<T> items) => new List<T>(items);
}
