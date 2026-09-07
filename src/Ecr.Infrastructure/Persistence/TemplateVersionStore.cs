using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Операції над версією шаблону, які неможливо виразити через EF без гонки.
/// </summary>
/// <remarks>
/// ⚠ Файла немає в дереві `05-skeleton.md` §1: порт уведений `Q-032`,
/// реалізація — `Q-050`.
/// </remarks>
public sealed class TemplateVersionStore(EcrDbContext db) : ITemplateVersionStore
{
    /// <inheritdoc />
    /// <remarks>
    /// ⚠ ОДИН statement із <c>OUTPUT</c> (<c>R-B7</c>). Послідовність
    /// «прочитати → додати одиницю → записати» під паралельними
    /// презентаційними правками дає два однакові значення ревізії, а ревізія —
    /// це ключ кешу <c>v{id}:r{rev}</c>. Два різні знімки під одним ключем
    /// означають, що частина інстансів віддає стару структуру, і знайти це
    /// потім практично неможливо.
    /// </remarks>
    public async Task<int> IncrementPresentationRevisionAsync(int templateVersionId, CancellationToken ct)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        if (db.Database.CurrentTransaction is { } tx)
        {
            command.Transaction = (SqlTransaction)tx.GetDbTransaction();
        }

        command.CommandText = """
            UPDATE cfg.TemplateVersion
            SET    PresentationRevision = PresentationRevision + 1
            OUTPUT inserted.PresentationRevision
            WHERE  Id = @id;
            """;
        command.Parameters.AddWithValue("@id", templateVersionId);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull
            ? throw new NotFoundException("ECR-TMPL-0404", $"Версії шаблону {templateVersionId} не існує.")
            : (int)result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Питання не про кількість, а про факт: якщо на версії вже є документи,
    /// структурна правка заборонена незалежно від того, один він чи мільйон.
    /// Тому <c>AnyAsync</c>, а не <c>CountAsync</c>.
    /// </remarks>
    public Task<bool> HasDocumentsAsync(int templateVersionId, CancellationToken ct)
        => db.Documents
             .AsNoTracking()
             .Join(db.Projects.AsNoTracking(),
                   d => d.ProjectId,
                   p => p.Id,
                   (d, p) => p.TemplateVersionId)
             .AnyAsync(id => id == templateVersionId, ct);

    /// <inheritdoc />
    public async Task<int> CreateDraftAsync(
        int templateId, string versionNumber, int userId, DateTime utcNow, CancellationToken ct)
    {
        if (!await db.Templates.AnyAsync(t => t.Id == templateId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException("ECR-TMPL-0404", $"Шаблон {templateId} не знайдено.");
        }

        if (await db.TemplateVersions
                .AnyAsync(v => v.TemplateId == templateId && v.Version == versionNumber, ct)
                .ConfigureAwait(false))
        {
            throw new Application.Errors.BusinessRuleException(
                "ECR-TMPL-0409", $"Версія {versionNumber} у цьому шаблоні вже існує.");
        }

        var version = new TemplateVersion(templateId, versionNumber, userId, utcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return version.Id;
    }

    /// <inheritdoc />
    public async Task<TemplateVersion> GetWithStructureAsync(int templateVersionId, CancellationToken ct)
        => await db.TemplateVersions
               // ⚠ БЕЗ AsNoTracking, на відміну від `CloneAsync`: публікація
               // міняє стан версії й проставляє формулам порядок обчислення.
               .Include(v => v.Sheets).ThenInclude(sheet => sheet.Tables).ThenInclude(t => t.Columns)
               .Include(v => v.Sheets).ThenInclude(sheet => sheet.Tables).ThenInclude(t => t.Rows)
               .Include(v => v.Sheets).ThenInclude(sheet => sheet.Tables).ThenInclude(t => t.Formulas)
               .Include(v => v.Sheets).ThenInclude(sheet => sheet.Tables).ThenInclude(t => t.ValidationRules)
               .FirstOrDefaultAsync(v => v.Id == templateVersionId, ct)
               .ConfigureAwait(false)
           ?? throw new NotFoundException(
               "ECR-TMPL-0404", $"Версії шаблону {templateVersionId} не існує.");

    /// <inheritdoc />
    public async Task<int> CloneAsync(
        int sourceVersionId, string newVersion, int userId, DateTime utcNow, CancellationToken ct)
    {
        // ⚠ AsNoTracking навмисно: граф зараз перетворять на НОВІ сутності
        // скиданням ключів. Відстежувані об'єкти EF сприйняв би як зміну
        // джерела — тобто спроба клонувати перетворилася б на псування
        // оригіналу.
        var source = await db.TemplateVersions
            .AsNoTracking()
            .Include(v => v.Sheets).ThenInclude(sheet => sheet.Tables).ThenInclude(t => t.Columns)
            .Include(v => v.Sheets).ThenInclude(sheet => sheet.Tables).ThenInclude(t => t.Rows)
            .Include(v => v.Sheets).ThenInclude(sheet => sheet.Tables).ThenInclude(t => t.Formulas)
            .Include(v => v.Sheets).ThenInclude(sheet => sheet.Tables).ThenInclude(t => t.ValidationRules)
            .FirstOrDefaultAsync(v => v.Id == sourceVersionId, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-TMPL-0404", $"Версії шаблону {sourceVersionId} не існує.");

        var clonedFrom = source.Id;
        var (clone, links) = TemplateVersionCloner.Prepare(source, newVersion, userId, utcNow);

        db.TemplateVersions.Add(clone);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Формули посилаються на колонки і рядки ЧИСЛОМ, а не навігацією, тому
        // EF їх не перев'язує: це доводиться робити після того, як база
        // призначила нові ключі.
        TemplateVersionCloner.Relink(clone, links);
        SetClonedFrom(clone, clonedFrom);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return clone.Id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TemplateVersionSummary>> ListVersionsAsync(
        int templateId, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        return await db.TemplateVersions
            .AsNoTracking()
            .Where(v => v.TemplateId == templateId)
            .OrderBy(v => v.Id)
            .Take(page.Limit)
            .Select(v => new TemplateVersionSummary(
                v.Id, v.Version, v.Status, v.PresentationRevision, v.ClonedFromVersionId, v.PublishedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CreateTemplateAsync(
        string code,
        IReadOnlyDictionary<string, string> name,
        int userId,
        DateTime utcNow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (await db.Templates.AnyAsync(t => t.Code == code, ct).ConfigureAwait(false))
        {
            throw new Application.Errors.BusinessRuleException(
                "ECR-TMPL-0409", $"Шаблон з кодом «{code}» уже існує.");
        }

        var template = new Template(
            Domain.ValueObjects.EcrCode.Create(code),
            new Domain.ValueObjects.LocalizedText(name.ToDictionary(StringComparer.Ordinal)),
            userId,
            utcNow);

        db.Templates.Add(template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return template.Id;
    }

    /// <inheritdoc />
    public async Task<PagedResult<TemplateSummary>> ListTemplatesAsync(
        CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var after = Cursor.Decode(page.Cursor);

        // Беремо на один більше за сторінку: так видно, чи є наступна, без
        // окремого COUNT по всій таблиці.
        var rows = await db.Templates
            .AsNoTracking()
            .Where(t => t.Id > after)
            .OrderBy(t => t.Id)
            .Take(page.Limit + 1)
            .Select(t => new TemplateSummary(
                t.Id, t.Code, db.TemplateVersions.Count(v => v.TemplateId == t.Id)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit).ToList();

        return new PagedResult<TemplateSummary>(
            items, hasMore ? Cursor.Encode(items[^1].Id) : null, TotalCount: null);
    }

    /// <inheritdoc />
    public async Task<int> ReplaceFormulaDependenciesAsync(
        int templateVersionId,
        IReadOnlyList<FormulaDependency> dependencies,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        // ⚠ Формули версії — межа заміни. Видаляти «всі залежності з
        // FormulaDefId у списку нових» було б помилкою: формула, яку з версії
        // прибрали, лишила б свої залежності назавжди.
        // ⚠ Навігацій між рівнями структури в моделі немає (вони односторонні
        // від батька до дітей), тому зв'язок збирається join'ами — так само,
        // як це робить решта сховища.
        var formulaIds = await (
                from formula in db.FormulaDefs.AsNoTracking()
                join table in db.TableDefs.AsNoTracking() on formula.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                where sheet.TemplateVersionId == templateVersionId
                select formula.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var stale = await db.FormulaDependencies
            .Where(d => d.FormulaDefId != null && formulaIds.Contains(d.FormulaDefId.Value))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        db.FormulaDependencies.RemoveRange(stale);
        await db.FormulaDependencies.AddRangeAsync(dependencies, ct).ConfigureAwait(false);

        return dependencies.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FormulaDependency>> ListFormulaDependenciesAsync(
        int templateVersionId, CancellationToken ct)
        => await (
                from dependency in db.FormulaDependencies.AsNoTracking()
                join formula in db.FormulaDefs.AsNoTracking()
                    on dependency.FormulaDefId equals formula.Id
                join table in db.TableDefs.AsNoTracking() on formula.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                where sheet.TemplateVersionId == templateVersionId
                orderby dependency.FormulaDefId, dependency.SortOrder
                select dependency)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PeriodAccessRuleDef>> ListPeriodAccessRulesAsync(
        int templateVersionId, CancellationToken ct)
        => await db.PeriodAccessRules
            .AsNoTracking()
            .Where(r => r.TemplateVersionId == templateVersionId)
            .OrderBy(r => r.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Ідентифікатори таблиць версії — основа всіх трьох запитів про зв'язки.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <returns>Запит, що дає ідентифікатори таблиць версії.</returns>
    /// <remarks>
    /// ⚠ Один вираз на три виклики навмисно: шлях від таблиці до версії
    /// (<c>TableDef → SheetDef → TemplateVersion</c>) — саме те місце, де
    /// друга копія тихо забула б з'єднання і повернула б зв'язки чужої версії.
    /// </remarks>
    private IQueryable<int> TableIdsOfVersion(int templateVersionId)
        => db.TableDefs
             .AsNoTracking()
             .Join(db.SheetDefs.AsNoTracking(),
                   t => t.SheetDefId,
                   s => s.Id,
                   (t, s) => new { t.Id, s.TemplateVersionId })
             .Where(x => x.TemplateVersionId == templateVersionId)
             .Select(x => x.Id);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TableRelationDef>> ListTableRelationsAsync(
        int templateVersionId, CancellationToken ct)
    {
        var tables = TableIdsOfVersion(templateVersionId);

        return await db.TableRelations
            .AsNoTracking()
            .Where(r => tables.Contains(r.SourceTableDefId))
            .OrderBy(r => r.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<TableRelationDef?> FindTableRelationAsync(
        int templateVersionId, string code, CancellationToken ct)
    {
        var tables = TableIdsOfVersion(templateVersionId);

        // ⛔ БЕЗ AsNoTracking: цю сутність зараз змінить `Update`, і без
        // відстеження `SaveChanges` не побачив би жодної правки.
        return db.TableRelations
            .Where(r => r.Code == code && tables.Contains(r.SourceTableDefId))
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, string>> ListTableCodesAsync(
        int templateVersionId, CancellationToken ct)
    {
        var rows = await db.TableDefs
            .AsNoTracking()
            .Join(db.SheetDefs.AsNoTracking(),
                  t => t.SheetDefId,
                  s => s.Id,
                  (t, s) => new { t.Id, t.Code, s.TemplateVersionId })
            .Where(x => x.TemplateVersionId == templateVersionId)
            .Select(x => new { x.Id, x.Code })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(x => x.Id, x => x.Code);
    }

    /// <inheritdoc />
    public async Task<int> ApplyPresentationAsync(
        int templateVersionId, IReadOnlyList<PresentationChange> changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        var affected = 0;

        foreach (var change in changes)
        {
            // ⛔ Білий список, а не назва поля із запиту в тексті SQL. Клас
            // зміни перевіряє обробник, але сховище не має покладатися на
            // чужу перевірку: і назва поля, і тип сутності приходять із
            // мережі. Невідома пара — відмова, а не мовчазний пропуск:
            // мовчання тут означало б «патч застосовано», коли не застосовано
            // нічого, — рівно той дефект, від якого цей метод і з'явився.
            if (!PresentationColumns.TryGetValue((change.EntityType, change.Field), out var sql))
            {
                throw new BusinessRuleException(
                    "ECR-TMPL-0422",
                    $"Поле {change.EntityType}.{change.Field} не належить презентаційному шару.");
            }

            await using var command = connection.CreateCommand();
            if (db.Database.CurrentTransaction is { } tx)
            {
                command.Transaction = (SqlTransaction)tx.GetDbTransaction();
            }

            command.CommandText = sql;
            command.Parameters.AddWithValue("@id", change.EntityId);
            command.Parameters.AddWithValue("@version", templateVersionId);
            command.Parameters.AddWithValue("@value", (object?)change.Value ?? DBNull.Value);

            var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // ⚠ Нуль рядків — не «нічого не змінилося», а «сутності немає в
            // ЦІЙ версії». Ідентифікатор приходить із мережі, і мовчазний
            // нуль означав би, що патч чужої версії виглядає успішним.
            if (rows == 0)
            {
                throw new NotFoundException(
                    "ECR-TMPL-0404",
                    $"{change.EntityType} {change.EntityId} не належить версії {templateVersionId}.");
            }

            affected += rows;
        }

        return affected;
    }

    /// <summary>
    /// Дозволені презентаційні поля і те, як їх оновити.
    /// </summary>
    /// <remarks>
    /// ⛔ Кожен <c>UPDATE</c> обмежений ВЕРСІЄЮ через ланцюг належності:
    /// колонка → таблиця → аркуш → версія. Без цієї умови патч однієї версії
    /// міняв би підписи в будь-якій іншій, і власник другої не дізнався б
    /// про це ніяк.
    ///
    /// ⚠ Перелік збігається з <c>ChangeClassifier.PresentationFields</c> — і
    /// це навмисне дублювання в іншій формі: там перелік НАЗВ для рішення
    /// «чи дозволено», тут — відображення назви в колонку. Розбіжність між
    /// ними ловить сторож <c>Презентаційні_поля_мають_куди_записатися</c>.
    /// </remarks>
    private static readonly Dictionary<(string Entity, string Field), string>
        PresentationColumns = Build();

    private static Dictionary<(string, string), string> Build()
    {
        const string columnScope = """
            UPDATE c SET c.{0} = {1}
            FROM   cfg.ColumnDef c
            JOIN   cfg.TableDef t ON t.Id = c.TableDefId
            JOIN   cfg.SheetDef s ON s.Id = t.SheetDefId
            WHERE  c.Id = @id AND s.TemplateVersionId = @version;
            """;

        const string rowScope = """
            UPDATE r SET r.{0} = {1}
            FROM   cfg.RowDef r
            JOIN   cfg.TableDef t ON t.Id = r.TableDefId
            JOIN   cfg.SheetDef s ON s.Id = t.SheetDefId
            WHERE  r.Id = @id AND s.TemplateVersionId = @version;
            """;

        const string tableScope = """
            UPDATE t SET t.{0} = {1}
            FROM   cfg.TableDef t
            JOIN   cfg.SheetDef s ON s.Id = t.SheetDefId
            WHERE  t.Id = @id AND s.TemplateVersionId = @version;
            """;

        const string sheetScope = """
            UPDATE s SET s.{0} = {1}
            FROM   cfg.SheetDef s
            WHERE  s.Id = @id AND s.TemplateVersionId = @version;
            """;

        // ⚠ Значення приходить рядком і приводиться в SQL, а не в C#: тип
        // колонки знає база, і `TRY_CONVERT` тут дав би тихий `NULL` замість
        // відмови. Порожній рядок для числа — помилка виклику, і вона має
        // бути гучною.
        static string Text(string scope, string column) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, scope, column, "@value");

        static string Int(string scope, string column) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, scope, column, "CONVERT(int, @value)");

        static string Bit(string scope, string column) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, scope, column, "CONVERT(bit, @value)");

        return new Dictionary<(string, string), string>()
        {
            [("ColumnDef", "HeaderL10n")] = Text(columnScope, "HeaderL10n"),
            [("ColumnDef", "Ordinal")] = Int(columnScope, "Ordinal"),
            [("ColumnDef", "DisplayFormat")] = Text(columnScope, "DisplayFormat"),
            [("ColumnDef", "IsHidden")] = Bit(columnScope, "IsHidden"),
            [("ColumnDef", "StyleId")] = Int(columnScope, "StyleId"),
            [("RowDef", "LabelL10n")] = Text(rowScope, "LabelL10n"),
            [("SheetDef", "NameL10n")] = Text(sheetScope, "NameL10n"),
            [("SheetDef", "IsVisible")] = Bit(sheetScope, "IsVisible"),
            [("TableDef", "NameL10n")] = Text(tableScope, "NameL10n"),
        };
    }

    /// <summary>Проставляє <c>ClonedFromVersionId</c> — поле з приватним сетером.</summary>
    private static void SetClonedFrom(TemplateVersion clone, int sourceVersionId)
        => typeof(TemplateVersion)
            .GetProperty(nameof(TemplateVersion.ClonedFromVersionId))!
            .SetValue(clone, sourceVersionId);
}
