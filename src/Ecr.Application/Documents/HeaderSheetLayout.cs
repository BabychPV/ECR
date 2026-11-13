using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Порядок і підписи аркуша шапки як у формі «2. Contract» Land (аудит 2026-11 X-5, D-477) — ТЕ САМЕ правило, що й у
/// картці документа (`src/Ecr.Web/src/features/documents/contractSection.ts`). ⚠ Змінюючи перелік тут, змініть і там.
/// </summary>
public static class HeaderSheetLayout
{
    private static readonly (string Key, string[] Names)[] ContractOrder =
    [
        ("area", ["area"]),
        ("contractor", ["contractor"]),
        ("region", ["region"]),
        ("location", ["location", "facility", "locationfacility"]),
        ("onshoreOffshore", ["onoffshore", "onshoreoffshore", "onshore", "offshore"]),
        ("filledInBy", ["filledinby", "filledby"]),
        ("contractHolder", ["contractholder"]),
        ("contractNumber", ["contractnumber", "contractno"]),
        ("activityType", ["typeofactivity", "activitytype"]),
        ("processedOn", ["processedon"]),
        ("fileNumber", ["filenumber", "businesskey"]),
        ("permitNumber", ["permitnumber", "permit"]),
        ("version", ["version"]),
    ];

    /// <summary>Ключ секції Contract для поля або <c>null</c> (за нормалізованим кодом чи англійським підписом).</summary>
    public static string? ContractKeyOf(string code, string? englishLabel)
    {
        var candidates = new[] { Normalize(code), Normalize(englishLabel) };
        foreach (var (key, names) in ContractOrder)
        {
            if (candidates.Any(c => c.Length > 0 && names.Contains(c, StringComparer.Ordinal)))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>«EN — RU», якщо обидві мови є й різні; інакше <paramref name="fallback"/> (як `bilingualLabel`).</summary>
    public static string BilingualLabel(LocalizedText label, string fallback)
    {
        ArgumentNullException.ThrowIfNull(label);
        var en = label.Values.GetValueOrDefault("en")?.Trim() ?? string.Empty;
        var ru = label.Values.GetValueOrDefault("ru")?.Trim() ?? string.Empty;
        return en.Length > 0 && ru.Length > 0 && !string.Equals(en, ru, StringComparison.Ordinal) ? $"{en} — {ru}" : fallback;
    }

    /// <summary>
    /// Значення підстановки в книзі «EN - RU» — формат стовпця NAME довідників Land
    /// (`docs/delivery/reference-data/land-contract/*.csv`: «Offshore - На море»), приймання F13 P3. Якщо однієї з мов
    /// немає, вони збігаються або англійська назва вже містить « - RU» (запис імпортовано з NAME) — <paramref name="fallback"/>.
    /// </summary>
    public static string BilingualValue(LocalizedText names, string fallback)
    {
        ArgumentNullException.ThrowIfNull(names);
        var en = names.Values.GetValueOrDefault("en")?.Trim() ?? string.Empty;
        var ru = names.Values.GetValueOrDefault("ru")?.Trim() ?? string.Empty;
        if (en.Length == 0 || ru.Length == 0 || string.Equals(en, ru, StringComparison.Ordinal))
        {
            return fallback;
        }

        return en.EndsWith(" - " + ru, StringComparison.Ordinal) ? en : $"{en} - {ru}";
    }

    /// <summary>
    /// X-4: звітний період вивантаження для аркуша шапки: <c>2026-11</c> (місячний і невідомий), <c>Q2 2026</c>
    /// (квартальний), <c>2026</c> (річний), <c>2026-03</c> (власний календар — рік і номер періоду).
    /// </summary>
    /// <param name="periodKey">Ключ періоду <c>Рік*100 + Номер</c>.</param>
    /// <param name="periodKind">Ім'я <c>PeriodKind</c> проєкту; <c>null</c> — як місячний.</param>
    public static string PeriodLabel(int periodKey, string? periodKind)
    {
        var period = new PeriodKey(periodKey);
        var year = period.Year.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
        var sequence = period.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return periodKind switch
        {
            nameof(Ecr.Domain.Enums.PeriodKind.Quarterly) => $"Q{sequence} {year}",
            nameof(Ecr.Domain.Enums.PeriodKind.Yearly) => year,
            _ => $"{year}-{period.Sequence.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)}",
        };
    }

    /// <summary>
    /// Рядки секції Contract за порядком Land (службові File Number/Version — на своєму місці, якщо секція є), далі решта
    /// полів у порядку шаблону, далі службові рядки, що не стали в секцію (File Number/Version без секції, період).
    /// Дубль ключа секції — серед решти, як у картці документа.
    /// </summary>
    public static IReadOnlyList<HeaderExportRow> Order(IReadOnlyList<HeaderExportRow> fields, IReadOnlyList<HeaderExportRow> service)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(service);

        var contract = new Dictionary<string, HeaderExportRow>(StringComparer.Ordinal);
        var other = new List<HeaderExportRow>();
        foreach (var row in fields)
        {
            if (row.ContractKey is { } key && contract.TryAdd(key, row))
            {
                continue;
            }

            other.Add(row);
        }

        var hasSection = contract.Count > 0;
        var tail = new List<HeaderExportRow>();
        foreach (var row in service)
        {
            // ✎ X-4 (рішення PO 11.11): File Number і Version — завжди. Із секцією Contract — на своєму місці за порядком
            // Land (полем шаблону з тим самим ключем не дублюються), без секції — у хвіст перед періодом.
            if (row.ContractKey is { } key && hasSection)
            {
                contract.TryAdd(key, row);
            }
            else
            {
                tail.Add(row);
            }
        }

        return [.. contract.Values.OrderBy(r => Rank(r.ContractKey!)), .. other, .. tail];
    }

    private static int Rank(string key) => Array.FindIndex(ContractOrder, e => e.Key == key);

    private static string Normalize(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : new string([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
