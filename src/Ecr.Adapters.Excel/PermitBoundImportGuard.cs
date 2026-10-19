using Ecr.Application.Ports;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Таблиця з чинною прив'язкою «рядки з дозволу» (Permits, PM-10) не приймає з книги Excel значень зіставлених колонок
/// (ключ рядка, ліміт, довідкові).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Значення зіставлених колонок пише машина. Ліміти й довідкові колонки вже закриті рішенням доступу («лише для
/// читання»), але ключова колонка такою бути не зобов'язана, і книга переписала б ключ рядка дозволу. Вимога захисту не
/// має залежати від того, як налаштовано колонку.
/// </para>
/// <para>
/// Нових рядків книга не створює й без дозволу (імпорт відхиляє їх <c>ECR-ROW-0404</c>), тож охоронець їх не торкається.
/// Відмова - існуючий ключ причини <c>deny.ColumnReadOnly</c>: нових ключів каталогу немає. Решта колонок наявних рядків
/// (факти) імпортується як завжди.
/// </para>
/// </remarks>
public static class PermitBoundImportGuard
{
    /// <summary>Код причини відмови - той самий, що й у рішень доступу.</summary>
    public const string ReasonCode = "ECR-ACCS-0403";

    /// <summary>Прибирає зі змін diff значення зіставлених колонок; вони переходять у відмови.</summary>
    /// <param name="diff">Diff таблиці.</param>
    /// <param name="lockedColumnCodes">Коди зіставлених колонок (усі ролі).</param>
    /// <returns>Diff без заборонених змін.</returns>
    public static TableDiff Apply(TableDiff diff, IReadOnlySet<string> lockedColumnCodes)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(lockedColumnCodes);

        var rejected = new List<ImportRejection>(diff.Rejected);

        var kept = new List<ImportChange>(diff.Changes.Count);
        foreach (var change in diff.Changes)
        {
            if (!lockedColumnCodes.Contains(change.ColumnCode))
            {
                kept.Add(change);
                continue;
            }

            rejected.Add(new ImportRejection(
                change.RowKey,
                change.ColumnCode,
                ReasonCode,
                "Колонку заповнює дозвіл: значення з книги не приймається.",
                change.TableCode,
                change.TableNameL10n,
                MessageKey: "deny.ColumnReadOnly"));
        }

        var overwritable = diff.Overwritable?.Where(c => !lockedColumnCodes.Contains(c.ColumnCode)).ToList();

        return diff with { Changes = kept, Rejected = rejected, Overwritable = overwritable };
    }
}
