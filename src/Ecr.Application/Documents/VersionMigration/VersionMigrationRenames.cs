using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>Перейменування колонки таблиці між версіями: «було → стало» за кодом колонки.</summary>
/// <param name="FromColumnCode">Код колонки у вихідній версії.</param>
/// <param name="ToColumnCode">Код колонки в цільовій версії.</param>
public sealed record VersionMigrationColumnRename(string FromColumnCode, string ToColumnCode);

/// <summary>
/// Перейменування таблиці між версіями: «було → стало» за кодом таблиці (і, за потреби, колонок).
/// </summary>
/// <param name="FromTableCode">Код таблиці у вихідній версії.</param>
/// <param name="ToTableCode">Код таблиці в цільовій версії; рівний <paramref name="FromTableCode"/> — лише перейменування колонок.</param>
/// <param name="SheetCode">Код аркуша; <c>null</c> — діє на будь-якому аркуші, де є така таблиця.</param>
/// <param name="Columns">Перейменування колонок цієї таблиці; <c>null</c> — колонки збігаються за кодами.</param>
/// <remarks>
/// ⚠ Мапа АДИТИВНА й нічого не змінює без неї: відповідність за кодом лишається першою. Запис, який до цієї
/// пари версій не стосується (немає таблиці-джерела або таблиці-приймача), мовчки пропускається — тому одну
/// й ту саму мапу можна передавати на кожен проєкт. Те, що мапа справді застосувала, звіт переносу називає
/// елементами виду <c>Renamed</c>.
/// </remarks>
public sealed record VersionMigrationTableRename(
    string FromTableCode,
    string ToTableCode,
    string? SheetCode = null,
    IReadOnlyList<VersionMigrationColumnRename>? Columns = null);

/// <summary>Перевірка форми мапи перейменувань із запиту.</summary>
public static class VersionMigrationRenames
{
    /// <summary>Стеля записів мапи в одному запиті.</summary>
    public const int MaxTableRenames = 100;

    /// <summary>Стеля перейменувань колонок в одному записі.</summary>
    public const int MaxColumnRenames = 500;

    /// <summary>Найдовший код (як <c>EcrCode</c>: до 64 символів).</summary>
    public const int MaxCodeLength = 64;

    /// <summary>
    /// Відхиляє мапу зі зламаною формою: порожні чи задовгі коди, повтор джерела, перевищення стелі.
    /// </summary>
    /// <param name="renames">Мапа; <c>null</c> — без мапи.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-SCHM-0422</c>, ключ <c>migrateRenameMapInvalid</c>.</exception>
    public static void Validate(IReadOnlyList<VersionMigrationTableRename>? renames)
    {
        if (renames is null || renames.Count == 0)
        {
            return;
        }

        if (renames.Count > MaxTableRenames)
        {
            throw Invalid($"Мапа перейменувань містить {renames.Count} записів; найбільше — {MaxTableRenames}.");
        }

        var seen = new HashSet<(string Sheet, string From)>();
        foreach (var rename in renames)
        {
            if (rename is null
                || !IsCode(rename.FromTableCode) || !IsCode(rename.ToTableCode)
                || (rename.SheetCode is not null && !IsCode(rename.SheetCode)))
            {
                throw Invalid("Кожен запис мапи перейменувань мусить мати непорожні коди таблиць (до 64 символів).");
            }

            var onlyColumns = string.Equals(rename.FromTableCode, rename.ToTableCode, StringComparison.Ordinal);
            if (onlyColumns && (rename.Columns is null || rename.Columns.Count == 0))
            {
                throw Invalid($"Запис «{rename.FromTableCode}» не перейменовує нічого: коди таблиці однакові й колонок немає.");
            }

            if (!seen.Add((rename.SheetCode ?? string.Empty, rename.FromTableCode)))
            {
                throw Invalid($"Таблицю «{rename.FromTableCode}» перейменовано в мапі двічі.");
            }

            if (rename.Columns is null)
            {
                continue;
            }

            if (rename.Columns.Count > MaxColumnRenames)
            {
                throw Invalid(
                    $"Запис «{rename.FromTableCode}» містить {rename.Columns.Count} перейменувань колонок; найбільше — {MaxColumnRenames}.");
            }

            var fromColumns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in rename.Columns)
            {
                if (column is null || !IsCode(column.FromColumnCode) || !IsCode(column.ToColumnCode))
                {
                    throw Invalid($"Запис «{rename.FromTableCode}»: коди колонок мусять бути непорожніми (до 64 символів).");
                }

                if (!fromColumns.Add(column.FromColumnCode))
                {
                    throw Invalid($"Запис «{rename.FromTableCode}»: колонку «{column.FromColumnCode}» перейменовано двічі.");
                }
            }
        }
    }

    private static bool IsCode(string? code)
        => !string.IsNullOrWhiteSpace(code) && code.Length <= MaxCodeLength;

    private static BusinessRuleException Invalid(string message)
        => new(
            ErrorCodes.GuardedChangeWithoutStrategy,
            message,
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-SCHM-0422.migrateRenameMapInvalid",
                ["maxTableRenames"] = MaxTableRenames.ToString(CultureInfo.InvariantCulture),
                ["maxColumnRenames"] = MaxColumnRenames.ToString(CultureInfo.InvariantCulture),
            });
}
