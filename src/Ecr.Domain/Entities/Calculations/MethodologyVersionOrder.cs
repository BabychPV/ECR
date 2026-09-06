using System.Globalization;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Ключ «чинності» версії методології: за яким правилом обирається версія,
/// коли їх кілька (<c>ФВ-9.3</c>, поправка 5-біс директиви ПК-1 №05).
/// </summary>
/// <param name="EffectiveFrom">Дата набуття чинності; <c>null</c> — не опублікована.</param>
/// <param name="Version">Рядок версії, напр. <c>1.1.0.0</c>.</param>
/// <param name="Id">Ідентифікатор — останній засіб визначеності.</param>
/// <remarks>
/// ⛔ Правило подається як **наш вибір**, а не як відтворення чинної системи:
/// відтворювати нема чого. <c>Q_Common.cs:14-16</c> робить <c>SELECT</c>
/// <b>без <c>ORDER BY</c></b>, а <c>StagesBase.cs:23</c> бере
/// <c>List.Find</c>, тобто ПЕРШИЙ рядок у тому порядку, який поверне SQL
/// Server. Ніякого <c>MAX(версія)</c> там немає, і перебудова індексу здатна
/// мовчки змінити, за якою формулою пораховано рік (<c>H-24d-4</c>).
///
/// ⚠ Той самий дефект був і в нас, у трьох місцях одразу:
/// <c>Methodology.VersionOn</c>, <c>MethodologyResolver.ResolveVersionAsync</c>
/// і <c>MethodologyStore</c> сортували лише за <c>EffectiveFrom</c>, а за
/// рівних дат брали «перший». Коментар над <c>VersionOn</c> при цьому
/// стверджував, що вибір однозначний **за побудовою**, бо публікація не дає
/// двом версіям почати одного дня.
///
/// ⛔ Твердження було хибне. Перевірка публікації дивиться лише на
/// <c>IsPublished</c>, а вибір версії бере ще й <c>Deprecated</c> — бо
/// виведена з обігу версія лишається чинною для періодів, які вона рахувала.
/// Тож пара «виведена + нова від тієї самої дати» проходить перевірку і
/// створює рівно ту неоднозначність, якої коментар обіцяв не допустити.
/// Дев'ять таких перекриттів чекають у чинному корпусі.
/// </remarks>
public readonly record struct MethodologyVersionKey(
    DateOnly? EffectiveFrom, string? Version, int Id)
{
    /// <summary>
    /// Порівняння за чинністю: більший ключ — чинніша версія.
    /// </summary>
    /// <remarks>
    /// Порядок правил: <b>пізніша <c>EffectiveFrom</c></b> → <b>старша
    /// версія</b> → <b>більший <c>Id</c></b>. Третє правило не має змісту для
    /// методолога і стоїть лише заради того, щоб відповідь не залежала від
    /// порядку рядків: якщо дві версії збігаються і датою, і номером, будь-яка
    /// відповідь однаково довільна — але вона має бути **тією самою** сьогодні
    /// й після перебудови індексу.
    /// </remarks>
    public static IComparer<MethodologyVersionKey> Currency { get; } = new CurrencyComparer();

    /// <summary>Порівнювач за правилом чинності.</summary>
    private sealed class CurrencyComparer : IComparer<MethodologyVersionKey>
    {
        /// <summary>Порівнює два ключі.</summary>
        /// <param name="x">Перший.</param>
        /// <param name="y">Другий.</param>
        public int Compare(MethodologyVersionKey x, MethodologyVersionKey y)
        {
            var byDate = Nullable.Compare(x.EffectiveFrom, y.EffectiveFrom);
            if (byDate != 0)
            {
                return byDate;
            }

            var byVersion = CompareVersions(x.Version, y.Version);

            return byVersion != 0 ? byVersion : x.Id.CompareTo(y.Id);
        }
    }

    /// <summary>
    /// Порівнює номери версій.
    /// </summary>
    /// <param name="left">Перший номер.</param>
    /// <param name="right">Другий номер.</param>
    /// <remarks>
    /// ⛔ Через <see cref="System.Version"/>, а не як рядки: порядкове
    /// порівняння ставить <c>1.10.0.0</c> **перед** <c>1.9.0.0</c>, бо
    /// «1» &lt; «9». Десята версія методології — не екзотика, і помилка
    /// виявилася б як розрахунок за застарілою формулою без жодної ознаки збою.
    ///
    /// ⚠ Нерозбірний номер вважається МЕНШИМ за розбірний. Причина не в
    /// естетиці: про версію, номер якої ми не розуміємо, не можна сказати
    /// нічого, і надавати їй перевагу означало б обирати те, чого не знаєш.
    /// Два нерозбірні порівнюються порядково — аби відповідь була стала.
    /// </remarks>
    private static int CompareVersions(string? left, string? right)
    {
        var leftOk = System.Version.TryParse(left, out var leftVersion);
        var rightOk = System.Version.TryParse(right, out var rightVersion);

        if (leftOk && rightOk)
        {
            return leftVersion!.CompareTo(rightVersion);
        }

        if (leftOk != rightOk)
        {
            return leftOk ? 1 : -1;
        }

        return string.CompareOrdinal(left, right);
    }

    /// <summary>Читабельне подання — для повідомлень про неоднозначність.</summary>
    /// <returns>Текст на кшталт <c>1.1.0.0 від 2026-01-01</c>.</returns>
    public override string ToString()
        => EffectiveFrom is { } from
            ? string.Create(CultureInfo.InvariantCulture, $"{Version} від {from:yyyy-MM-dd}")
            : $"{Version} (не опублікована)";
}
