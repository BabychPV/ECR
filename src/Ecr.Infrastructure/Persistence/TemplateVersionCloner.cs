using System.Reflection;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Глибокий клон структури версії шаблону (ФВ-2.8, ФВ-7.1).
/// </summary>
/// <remarks>
/// ⚠ Клонування зроблене **скиданням ключів**, а не переліком властивостей.
/// Перелік довелося б доповнювати руками на кожне нове поле, і одного разу
/// його забули б — клон мовчки втратив би, скажімо, <c>Precision</c> колонки,
/// а виявилося б це на розбіжності в третьому знаку вже поданого звіту.
/// Скидання ключів переносить **усе**, включно з полями, яких ще немає.
///
/// ⚠ Ідентичності зберігаються: <c>Code</c> і <c>RowKey</c> у клоні ті самі
/// (ФВ-2.8). Саме на них посилаються формули; змінити їх означало б, що
/// формули клону посилаються в порожнечу.
///
/// ⛔ Дані документів не копіюються ніколи — вони лишаються на старій версії
/// до явної міграції (ФВ-7.7).
/// </remarks>
public static class TemplateVersionCloner
{
    /// <summary>Посилання формули на колонку і рядок за їхніми ідентичностями.</summary>
    /// <param name="Formula">Формула клону.</param>
    /// <param name="ColumnCode">Код колонки в джерелі; <c>null</c> — формула не колонкова.</param>
    /// <param name="RowKey">Ключ рядка в джерелі; <c>null</c> — формула не рядкова.</param>
    public sealed record FormulaLink(FormulaDef Formula, string? ColumnCode, string? RowKey);

    /// <summary>Готує клон і перелік посилань, які треба перев'язати після збереження.</summary>
    /// <param name="source">Версія-джерело з повністю завантаженим графом.</param>
    /// <param name="newVersion">Номер нової версії.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="utcNow">Момент створення.</param>
    /// <returns>Клон і посилання формул на колонки та рядки.</returns>
    public static (TemplateVersion Clone, IReadOnlyList<FormulaLink> Links) Prepare(
        TemplateVersion source, string newVersion, int userId, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Посилання ЗАПАМ'ЯТОВУЮТЬСЯ до скидання ключів: після нього
        // ColumnDefId уже нічого не означає, а Code лишається.
        var links = new List<FormulaLink>();
        foreach (var sheet in source.Sheets)
        {
            foreach (var table in sheet.Tables)
            {
                foreach (var formula in table.Formulas)
                {
                    links.Add(new FormulaLink(
                        formula,
                        table.Columns.FirstOrDefault(c => c.Id == formula.ColumnDefId)?.Code,
                        table.Rows.FirstOrDefault(r => r.Id == formula.RowDefId)?.RowKeyValue));
                }
            }
        }

        Reset(source, nameof(TemplateVersion.Id));
        Set(source, nameof(TemplateVersion.Version), newVersion);
        Set(source, nameof(TemplateVersion.Status), TemplateVersionStatus.Draft);
        Set(source, nameof(TemplateVersion.PresentationRevision), 0);
        Set(source, nameof(TemplateVersion.CreatedAt), utcNow);
        Set(source, nameof(TemplateVersion.CreatedByUserId), userId);
        Set(source, nameof(TemplateVersion.PublishedAt), null);
        Set(source, nameof(TemplateVersion.PublishedByUserId), null);

        foreach (var sheet in source.Sheets)
        {
            Reset(sheet, nameof(SheetDef.Id));
            Reset(sheet, nameof(SheetDef.TemplateVersionId));

            foreach (var table in sheet.Tables)
            {
                Reset(table, nameof(TableDef.Id));
                Reset(table, nameof(TableDef.SheetDefId));

                foreach (var column in table.Columns)
                {
                    Reset(column, nameof(ColumnDef.Id));
                    Reset(column, nameof(ColumnDef.TableDefId));
                }

                foreach (var row in table.Rows)
                {
                    Reset(row, nameof(RowDef.Id));
                    Reset(row, nameof(RowDef.TableDefId));
                }

                foreach (var formula in table.Formulas)
                {
                    Reset(formula, nameof(FormulaDef.Id));
                    Reset(formula, nameof(FormulaDef.TableDefId));

                    // Обнуляються НЕ в нуль, а в null: нуль означав би
                    // посилання на колонку з Id = 0, якої не буває, і FK
                    // впала б на вставці замість того, щоб лишитися порожньою.
                    Set(formula, nameof(FormulaDef.ColumnDefId), null);
                    Set(formula, nameof(FormulaDef.RowDefId), null);
                }

                foreach (var rule in table.ValidationRules)
                {
                    Reset(rule, nameof(ValidationRule.Id));
                    Reset(rule, nameof(ValidationRule.TableDefId));
                }
            }
        }

        return (source, links);
    }

    /// <summary>Перев'язує формули клону на його власні колонки і рядки.</summary>
    /// <param name="clone">Уже збережений клон із новими ключами.</param>
    /// <param name="links">Посилання, зібрані в <see cref="Prepare"/>.</param>
    public static void Relink(TemplateVersion clone, IReadOnlyList<FormulaLink> links)
    {
        ArgumentNullException.ThrowIfNull(clone);
        ArgumentNullException.ThrowIfNull(links);

        // ⚠ Порівняння за ПОСИЛАННЯМ, а не за значенням. Entity<TId>.Equals
        // порівнює Id, а в клоні всі Id щойно скинуті в нуль — тобто всі
        // формули «рівні» одна одній, і словник злився б в один запис.
        var byFormula = new Dictionary<object, FormulaLink>(ReferenceEqualityComparer.Instance);
        foreach (var link in links)
        {
            byFormula[link.Formula] = link;
        }

        foreach (var sheet in clone.Sheets)
        {
            foreach (var table in sheet.Tables)
            {
                foreach (var formula in table.Formulas)
                {
                    if (!byFormula.TryGetValue(formula, out var link))
                    {
                        continue;
                    }

                    if (link.ColumnCode is { } code)
                    {
                        Set(formula, nameof(FormulaDef.ColumnDefId),
                            table.Columns.FirstOrDefault(c => c.Code == code)?.Id);
                    }

                    if (link.RowKey is { } key)
                    {
                        Set(formula, nameof(FormulaDef.RowDefId),
                            table.Rows.FirstOrDefault(r => r.RowKeyValue == key)?.Id);
                    }
                }
            }
        }
    }

    private static void Reset(object entity, string property) => Set(entity, property, 0);

    private static void Set(object entity, string property, object? value)
    {
        var info = entity.GetType().GetProperty(
            property, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"У {entity.GetType().Name} немає властивості {property}: клон розійшовся з моделлю.");

        info.SetValue(entity, value);
    }
}
