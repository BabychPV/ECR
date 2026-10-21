using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>Ціль мапи Lookup: поле шапки (<c>Kind = "header"</c>) за кодом у ВИХІДНІЙ версії.</summary>
/// <param name="Kind">Вид цілі. У v1 підтримується лише <c>header</c>; колонки Lookup — окремим кроком.</param>
/// <param name="Code">Код поля шапки.</param>
public sealed record VersionMigrationLookupTarget(string Kind, string Code);

/// <summary>Пара «запис старого довідника → запис нового».</summary>
/// <param name="From">Запис вихідного довідника.</param>
/// <param name="To">Запис довідника, який вказує цільова версія.</param>
public sealed record VersionMigrationLookupPair(long From, long To);

/// <summary>Мапа Lookup однієї цілі: поле шапки, довідник якого змінився між версіями.</summary>
/// <param name="Target">Ціль.</param>
/// <param name="Mappings">Пари «було → стало»; кілька <c>From</c> можуть вести в один <c>To</c> (злиття).</param>
public sealed record VersionMigrationLookupRemap(
    VersionMigrationLookupTarget Target,
    IReadOnlyList<VersionMigrationLookupPair> Mappings);

/// <summary>Запис цілі мапи, який не можна покласти в шапку.</summary>
/// <param name="EntryId">Запис нового довідника з мапи.</param>
/// <param name="Reason"><c>missing</c>, <c>foreign</c>, <c>inactive</c>, <c>deleted</c>, <c>notValidInWindow</c>.</param>
public sealed record LookupRemapNotUsable(long EntryId, string Reason);

/// <summary>Звіт мапи Lookup по одній цілі. ⛔ Лише id записів і кількості: ні значень, ні назв, ні документів.</summary>
/// <param name="Target">Ціль.</param>
/// <param name="Applied"><c>false</c> — ціль не стосується пари версій (поля немає або довідник не змінився).</param>
/// <param name="MappedValues">Скільки значень шапки переїде на нові записи.</param>
/// <param name="UnmappedValues">Скільки значень без придатного відповідника (разом зі значеннями без запису).</param>
/// <param name="MappedEntries">Скільки різних старих записів має придатний відповідник.</param>
/// <param name="UnmappedEntries">Скільки різних старих записів його не має.</param>
/// <param name="UnmappedEntryIds">Id старих записів без відповідника (до <see cref="VersionMigrationLookupRemaps.MaxListed"/>).</param>
/// <param name="UnmappedTruncated">Перелік <paramref name="UnmappedEntryIds"/> обрізано стелею.</param>
/// <param name="TargetNotUsable">Записи нового довідника з мапи, які не можна використати (до стелі).</param>
public sealed record LookupRemapReportItem(
    VersionMigrationLookupTarget Target,
    bool Applied,
    long MappedValues,
    long UnmappedValues,
    int MappedEntries,
    int UnmappedEntries,
    IReadOnlyList<long> UnmappedEntryIds,
    bool UnmappedTruncated,
    IReadOnlyList<LookupRemapNotUsable> TargetNotUsable);

/// <summary>Дані мапи одного поля шапки для планувальника: мапа, використання, придатність цілей.</summary>
/// <param name="Code">Код поля шапки.</param>
/// <param name="Mappings">Пари «було → стало» (<c>from → to</c>).</param>
/// <param name="UsedEntries">Скільки непорожніх значень шапки веде в кожен старий запис.</param>
/// <param name="ValuesWithoutEntry">Непорожні значення Lookup-поля без запису (аномалія): не мапляться ніколи.</param>
/// <param name="UnusableTargets">Записи <c>to</c>, що непридатні, з причиною.</param>
public sealed record LookupRemapField(
    string Code,
    IReadOnlyDictionary<long, long> Mappings,
    IReadOnlyDictionary<long, long> UsedEntries,
    long ValuesWithoutEntry,
    IReadOnlyDictionary<long, string> UnusableTargets);

/// <summary>Вхід планувальника: дані мап за полями шапки ВИХІДНОЇ версії.</summary>
/// <param name="Fields">Ключ — <c>HeaderFieldDefId</c> вихідної версії.</param>
public sealed record VersionMigrationLookupInput(IReadOnlyDictionary<int, LookupRemapField> Fields);

/// <summary>Один перенос значення: що саме перепише SQL (поле вихідної версії, старий і новий запис).</summary>
/// <param name="HeaderFieldDefId">Поле шапки вихідної версії.</param>
/// <param name="FieldCode">Його код.</param>
/// <param name="OldEntryId">Старий запис.</param>
/// <param name="NewEntryId">Новий запис.</param>
public sealed record VersionMigrationLookupRemapRow(
    int HeaderFieldDefId, string FieldCode, long OldEntryId, long NewEntryId);

/// <summary>Перевірка форми мап Lookup із запиту.</summary>
public static class VersionMigrationLookupRemaps
{
    /// <summary>Вид цілі, підтриманий у v1.</summary>
    public const string HeaderKind = "header";

    /// <summary>Стеля цілей в одному запиті.</summary>
    public const int MaxTargets = 50;

    /// <summary>Стеля пар на ціль.</summary>
    public const int MaxPairs = 5000;

    /// <summary>Найдовший код (як <c>EcrCode</c>).</summary>
    public const int MaxCodeLength = 64;

    /// <summary>Стеля id у переліках звіту.</summary>
    public const int MaxListed = 200;

    /// <summary>Стеля значень шапки, які читає перенос заради мапи й аудиту.</summary>
    public const int MaxHeaderValues = 100_000;

    /// <summary>Відхиляє зламану форму: вид, коди, дублі, порожні мапи, стелі.</summary>
    /// <param name="remaps">Мапи; <c>null</c> — без мапи.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-SCHM-0422</c>, ключ <c>migrateLookupRemapInvalid</c>.</exception>
    public static void Validate(IReadOnlyList<VersionMigrationLookupRemap>? remaps)
    {
        if (remaps is null || remaps.Count == 0)
        {
            return;
        }

        if (remaps.Count > MaxTargets)
        {
            throw Invalid($"Мап Lookup {remaps.Count}; найбільше — {MaxTargets}.");
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var remap in remaps)
        {
            if (remap?.Target is null || remap.Mappings is null)
            {
                throw Invalid("Кожна мапа Lookup мусить мати ціль і пари.");
            }

            if (!string.Equals(remap.Target.Kind, HeaderKind, StringComparison.Ordinal))
            {
                throw Invalid("Ціль мапи Lookup: підтримується лише поле шапки (kind = header).");
            }

            if (string.IsNullOrWhiteSpace(remap.Target.Code) || remap.Target.Code.Length > MaxCodeLength)
            {
                throw Invalid($"Код поля шапки в мапі Lookup мусить бути непорожнім (до {MaxCodeLength} символів).");
            }

            if (!targets.Add(remap.Target.Code))
            {
                throw Invalid($"Поле шапки «{remap.Target.Code}» зазначено в мапах Lookup двічі.");
            }

            if (remap.Mappings.Count == 0)
            {
                throw Invalid($"Мапа Lookup поля «{remap.Target.Code}» порожня.");
            }

            if (remap.Mappings.Count > MaxPairs)
            {
                throw Invalid($"Мапа Lookup поля «{remap.Target.Code}» має {remap.Mappings.Count} пар; найбільше — {MaxPairs}.");
            }

            var from = new HashSet<long>();
            foreach (var pair in remap.Mappings)
            {
                if (pair is null || pair.From <= 0 || pair.To <= 0)
                {
                    throw Invalid($"Мапа Lookup поля «{remap.Target.Code}»: id записів мусять бути додатними.");
                }

                if (!from.Add(pair.From))
                {
                    throw Invalid($"Мапа Lookup поля «{remap.Target.Code}»: запис {pair.From} зазначено двічі.");
                }
            }
        }
    }

    /// <summary>Помилка форми або змісту мапи (той самий ключ).</summary>
    /// <param name="message">Пояснення (лише коди й id).</param>
    public static BusinessRuleException Invalid(string message)
        => new(
            ErrorCodes.GuardedChangeWithoutStrategy,
            message,
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-SCHM-0422.migrateLookupRemapInvalid",
                ["maxTargets"] = MaxTargets.ToString(CultureInfo.InvariantCulture),
                ["maxPairs"] = MaxPairs.ToString(CultureInfo.InvariantCulture),
            });
}
