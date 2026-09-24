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
    /// <param name="Formula">Формула клону — ВИЛУЧЕНА з графа до першого збереження.</param>
    /// <param name="Table">Таблиця клону, якій формула належить.</param>
    /// <param name="ColumnCode">Код колонки в джерелі; <c>null</c> — формула не колонкова.</param>
    /// <param name="RowKey">Ключ рядка в джерелі; <c>null</c> — формула не рядкова.</param>
    public sealed record FormulaLink(FormulaDef Formula, TableDef Table, string? ColumnCode, string? RowKey);

    /// <summary>Посилання правила валідації рівня колонки на колонку за її кодом.</summary>
    /// <param name="Rule">Правило клону.</param>
    /// <param name="Table">Таблиця клону, якій правило належить.</param>
    /// <param name="ColumnCode">Код колонки в джерелі.</param>
    public sealed record RuleLink(ValidationRule Rule, TableDef Table, string ColumnCode);

    /// <summary>Усе, що треба перев'язати після першого збереження клону.</summary>
    /// <param name="Formulas">Формули, вилучені з графа до першого збереження.</param>
    /// <param name="Rules">Правила рівня колонки.</param>
    public sealed record CloneLinks(IReadOnlyList<FormulaLink> Formulas, IReadOnlyList<RuleLink> Rules);

    /// <summary>Готує клон і перелік посилань, які треба перев'язати після збереження.</summary>
    /// <param name="source">Версія-джерело з повністю завантаженим графом.</param>
    /// <param name="newVersion">Номер нової версії.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="utcNow">Момент створення.</param>
    /// <returns>Клон (БЕЗ формул) і посилання, які треба перев'язати.</returns>
    /// <remarks>
    /// ⛔ V-05: формули ВИЛУЧАЮТЬСЯ з графа клону. Раніше вони йшли в перше
    /// <c>SaveChanges</c> разом із колонками й рядками, але з <c>NULL</c> в
    /// <c>ColumnDefId</c>/<c>RowDefId</c> (нових ключів колонок ще не було),
    /// і <c>CK_Formula_Scope</c> відхиляв вставку — «Clone version» для
    /// будь-якої версії з формулою давав <c>500</c>. Формула не має навігації
    /// на колонку (лише число), тож EF не може впорядкувати вставку сам:
    /// спершу колонки й рядки, потім — формули з уже відомими ключами
    /// (<see cref="Relink"/>), обидва кроки в одній транзакції сховища.
    /// </remarks>
    public static (TemplateVersion Clone, CloneLinks Links) Prepare(
        TemplateVersion source, string newVersion, int userId, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Посилання ЗАПАМ'ЯТОВУЮТЬСЯ до скидання ключів: після нього
        // ColumnDefId уже нічого не означає, а Code лишається.
        var formulaLinks = new List<FormulaLink>();
        var ruleLinks = new List<RuleLink>();
        foreach (var sheet in source.Sheets)
        {
            foreach (var table in sheet.Tables)
            {
                foreach (var formula in table.Formulas)
                {
                    formulaLinks.Add(new FormulaLink(
                        formula,
                        table,
                        table.Columns.FirstOrDefault(c => c.Id == formula.ColumnDefId)?.Code,
                        table.Rows.FirstOrDefault(r => r.Id == formula.RowDefId)?.RowKeyValue));
                }

                // ⚠ Правило рівня колонки до V-05 клонувалося з ColumnDefId
                // ДЖЕРЕЛА як є: FK не падав (колонка джерела існує), але
                // правило чернетки мовчки перевіряло чужу колонку.
                foreach (var rule in table.ValidationRules)
                {
                    if (table.Columns.FirstOrDefault(c => c.Id == rule.ColumnDefId)?.Code is { } code)
                    {
                        ruleLinks.Add(new RuleLink(rule, table, code));
                    }
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
                    // посилання на колонку з Id = 0, якої не буває. Значення
                    // однаково ставить Relink — до вставки формула не доходить.
                    Set(formula, nameof(FormulaDef.ColumnDefId), null);
                    Set(formula, nameof(FormulaDef.RowDefId), null);
                }

                DetachFormulas(table);

                foreach (var rule in table.ValidationRules)
                {
                    Reset(rule, nameof(ValidationRule.Id));
                    Reset(rule, nameof(ValidationRule.TableDefId));
                    Set(rule, nameof(ValidationRule.ColumnDefId), null);
                }
            }
        }

        foreach (var field in source.HeaderFields)
        {
            Reset(field, nameof(HeaderFieldDef.Id));
            Reset(field, nameof(HeaderFieldDef.TemplateVersionId));
        }

        return (source, new CloneLinks(formulaLinks, ruleLinks));
    }

    /// <summary>
    /// Прив'язує вилучені формули й правила до колонок і рядків уже збереженого клону.
    /// </summary>
    /// <param name="links">Посилання, зібрані в <see cref="Prepare"/>.</param>
    /// <returns>Формули, готові до вставки: ключі таблиці, колонки й рядка — клону.</returns>
    /// <exception cref="InvalidOperationException">
    /// Колонки чи рядка з тим самим кодом у клоні немає — клон розійшовся з
    /// джерелом; мовчки лишити формулу без цілі означало б порушити
    /// <c>CK_Formula_Scope</c> або, гірше, загубити формулу.
    /// </exception>
    public static IReadOnlyList<FormulaDef> Relink(CloneLinks links)
    {
        ArgumentNullException.ThrowIfNull(links);

        var formulas = new List<FormulaDef>(links.Formulas.Count);
        foreach (var link in links.Formulas)
        {
            Set(link.Formula, nameof(FormulaDef.TableDefId), link.Table.Id);

            if (link.ColumnCode is { } code)
            {
                Set(link.Formula, nameof(FormulaDef.ColumnDefId), ColumnId(link.Table, code));
            }

            if (link.RowKey is { } key)
            {
                Set(link.Formula, nameof(FormulaDef.RowDefId),
                    link.Table.Rows.FirstOrDefault(r => r.RowKeyValue == key)?.Id
                    ?? throw new InvalidOperationException(
                        $"У клоні таблиці {link.Table.Code} немає рядка {key}: клон розійшовся з джерелом."));
            }

            formulas.Add(link.Formula);
        }

        foreach (var link in links.Rules)
        {
            Set(link.Rule, nameof(ValidationRule.ColumnDefId), ColumnId(link.Table, link.ColumnCode));
        }

        return formulas;
    }

    private static int ColumnId(TableDef table, string code)
        => table.Columns.FirstOrDefault(c => c.Code == code)?.Id
           ?? throw new InvalidOperationException(
               $"У клоні таблиці {table.Code} немає колонки {code}: клон розійшовся з джерелом.");

    /// <summary>Вилучає формули з колекції таблиці, щоб перше збереження їх не вставляло.</summary>
    private static void DetachFormulas(TableDef table)
    {
        var field = typeof(TableDef).GetField("_formulas", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "У TableDef немає поля _formulas: клон розійшовся з моделлю.");

        ((List<FormulaDef>)field.GetValue(table)!).Clear();
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
