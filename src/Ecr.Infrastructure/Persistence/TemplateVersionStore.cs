using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
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
public sealed partial class TemplateVersionStore(EcrDbContext db) : ITemplateVersionStore
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
            ? throw new NotFoundException(
                "ECR-TMPL-0404",
                $"Версії шаблону {templateVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    // Той самий ключ, що Repository<T,TId>.GetAsync/CreateDocumentHandler/
                    // TableRelationHandlers та решта: той самий факт «версії немає».
                    ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                    ["versionId"] = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })
            : (int)result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Блокуюче читання (<c>UPDLOCK</c>) обходить версіонування рядків
    /// RCSI: звичайний <c>SELECT</c> тут віддав би знімок до чужого коміту й
    /// не чекав би ні на кого — тобто не серіалізував би нічого. <c>HOLDLOCK</c>
    /// тримає блок до кінця транзакції навіть для відсутнього рядка.
    ///
    /// ⚠ Поза транзакцією блок звільнився б одразу після statement — виклик
    /// без неї є помилкою програміста, а не станом даних, тому
    /// <see cref="InvalidOperationException"/>, а не мовчазне «ніби заблоковано».
    /// </remarks>
    public async Task<Domain.Enums.TemplateVersionStatus> LockVersionForUpdateAsync(
        int templateVersionId, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "LockVersionForUpdateAsync викликано поза транзакцією: блок рядка версії звільнився б одразу.");

        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction)tx.GetDbTransaction();
        command.CommandText = """
            SELECT Status
            FROM   cfg.TemplateVersion WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
            WHERE  Id = @id;
            """;
        command.Parameters.AddWithValue("@id", templateVersionId);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull
            ? throw new NotFoundException(
                "ECR-TMPL-0404",
                $"Версії шаблону {templateVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                    ["versionId"] = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })
            : (Domain.Enums.TemplateVersionStatus)Convert.ToByte(result, System.Globalization.CultureInfo.InvariantCulture);
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
    /// <remarks>Той самий шлях через проєкт, що <see cref="HasDocumentsAsync"/>.</remarks>
    public Task<int> CountDocumentsAsync(int templateVersionId, CancellationToken ct)
        => db.Documents
             .AsNoTracking()
             .Join(db.Projects.AsNoTracking(),
                   d => d.ProjectId,
                   p => p.Id,
                   (d, p) => p.TemplateVersionId)
             .CountAsync(id => id == templateVersionId, ct);

    /// <inheritdoc />
    public async Task<int> CreateDraftAsync(
        int templateId, string versionNumber, int userId, DateTime utcNow, CancellationToken ct)
    {
        if (!await db.Templates.AnyAsync(t => t.Id == templateId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                "ECR-TMPL-0404", $"Шаблон {templateId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.template",
                    ["templateId"] = templateId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        await EnsureVersionNumberFreeAsync(templateId, versionNumber, ct).ConfigureAwait(false);

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
               .Include(v => v.HeaderFields)

               // ⛔ AsSplitQuery, а не один нероздільний запит (аудит
               // 2026-09-16, §6.3). Чотири СЕСТРИНСЬКІ колекції
               // (Columns/Rows/Formulas/ValidationRules) під тим самим Tables в
               // одному запиті дають ДЕКАРТІВ ДОБУТОК замість суми: на шаблоні
               // з десятками колонок і рядків це десятки тисяч зайвих рядків по
               // мережі на кожну публікацію. Той самий дефект уже виправлений у
               // `MetadataCache.LoadAsync` (окремі запити на колекцію), але не
               // в цьому, другому шляху до тієї самої структури.
               .AsSplitQuery()
               .FirstOrDefaultAsync(v => v.Id == templateVersionId, ct)
               .ConfigureAwait(false)
           ?? throw new NotFoundException(
               "ECR-TMPL-0404",
               $"Версії шаблону {templateVersionId} не існує.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                   ["versionId"] = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
               });

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
            .Include(v => v.HeaderFields)

            // ⛔ Той самий декартів добуток, що й у `GetWithStructureAsync`
            // (аудит §6.3) — і тут він дорожчий: клон читає ВСЮ структуру.
            .AsSplitQuery()
            .FirstOrDefaultAsync(v => v.Id == sourceVersionId, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-TMPL-0404",
                $"Версії шаблону {sourceVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                    ["versionId"] = sourceVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        // ⛔ X-30 (UX-прохід, четвертий раунд): номер, що вже є в шаблоні, —
        // `409` з ключем, а не `UQ_TemplateVersion` → голий `500`. Цей шлях
        // перевірки не мав зовсім, на відміну від `CreateDraftAsync` поруч.
        // Шаблон — той, що в ДЖЕРЕЛА: саме в нього клон і ляже.
        await EnsureVersionNumberFreeAsync(source.TemplateId, newVersion, ct).ConfigureAwait(false);

        var clonedFrom = source.Id;

        // ⛔ T2-02: зв'язки таблиць (Check/Rollup/…) не мають навігації з версії й
        // раніше мовчки губились при клоні. Читаються ДО `Prepare`: він скидає
        // ключі таблиць джерела, а зв'язок посилається на таблиці числом.
        var relationTemplates = await ReadRelationTemplatesAsync(source, ct).ConfigureAwait(false);

        // ⛔ A1-04: гранти посилаються на Id аркуша/таблиці/колонки, а `Prepare` ці Id скидає —
        // ідентичності запам'ятовуються ДО нього (аркуш, аркуш+таблиця, аркуш+таблиця+колонка за кодами).
        var grantSources = ReadGrantSources(source);

        var (clone, links) = TemplateVersionCloner.Prepare(source, newVersion, userId, utcNow);

        // ⛔ V-05: два збереження — одна транзакція. Формули посилаються на
        // колонки й рядки ЧИСЛОМ, а не навігацією, тож EF не може вставити їх
        // у тому самому пакеті, що й колонки (CK_Formula_Scope вимагає ключ
        // одразу). Спершу структура без формул, потім формули з новими
        // ключами. Без транзакції падіння другого кроку лишило б у базі
        // чернетку-сироту без формул — тобто «успішний» клон, який тихо
        // загубив обчислення.
        if (db.Database.CurrentTransaction is not null)
        {
            await SaveCloneAsync(clone, links, clonedFrom, relationTemplates, grantSources, ct).ConfigureAwait(false);
            return clone.Id;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await SaveCloneAsync(clone, links, clonedFrom, relationTemplates, grantSources, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return clone.Id;
    }

    /// <summary>
    /// Номер версії вільний у шаблоні; інакше <c>409 ECR-TMPL-0409</c> з ключем.
    /// </summary>
    /// <remarks>
    /// ⚠ Перевірка-передумова, а не заміна <c>UQ_TemplateVersion</c>: дві
    /// одночасні спроби з тим самим номером обидві її пройдуть, і друга впаде на
    /// індексі. Це гонитва двох адміністраторів над одним шаблоном — рідкісна, і
    /// її ціна (одна невдала спроба) нижча за блокування шаблону на час клону.
    /// </remarks>
    private async Task EnsureVersionNumberFreeAsync(int templateId, string versionNumber, CancellationToken ct)
    {
        if (await db.TemplateVersions
                .AnyAsync(v => v.TemplateId == templateId && v.Version == versionNumber, ct)
                .ConfigureAwait(false))
        {
            throw new Application.Errors.BusinessRuleException(
                "ECR-TMPL-0409", $"Версія {versionNumber} у цьому шаблоні вже існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0409.versionNumberTaken",
                    ["version"] = versionNumber,
                });
        }
    }

    /// <summary>Зв'язок джерела з таблицями, названими ідентичністю (аркуш + код), а не ключем.</summary>
    private sealed record RelationTemplate(
        string Code, TableRelationKind Kind, (string Sheet, string Table) Source, (string Sheet, string Table) Target,
        string MatchJson, string? MapJson, byte OnSourceChange, bool IsActive);

    private async Task<IReadOnlyList<RelationTemplate>> ReadRelationTemplatesAsync(
        TemplateVersion source, CancellationToken ct)
    {
        var identity = source.Sheets
            .SelectMany(s => s.Tables.Select(t => (t.Id, Key: (Sheet: s.Code, Table: t.Code))))
            .ToDictionary(x => x.Id, x => x.Key);

        var ids = identity.Keys.ToList();
        var relations = await db.TableRelations
            .AsNoTracking()
            .Where(r => ids.Contains(r.SourceTableDefId))
            .OrderBy(r => r.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Зв'язок, чия таблиця лежить поза версією (старі дані), не переноситься:
        // сенсу в ньому для нової версії немає, а `UQ`/FK лише заважали б.
        return [.. relations
            .Where(r => identity.ContainsKey(r.TargetTableDefId))
            .Select(r => new RelationTemplate(
                r.Code, r.RelationKind, identity[r.SourceTableDefId], identity[r.TargetTableDefId],
                r.MatchJson, r.MapJson, r.OnSourceChange, r.IsActive))];
    }

    /// <summary>
    /// Код клонованого зв'язку: <c>UQ_TableRelationDef</c> — унікальність по ВСІЙ системі
    /// (міграція індексу — окрема задача під токен), тож клон не може зберегти код джерела.
    /// Додається суфікс <c>_v&lt;id нової версії&gt;</c>; сам код обрізається до 64 символів.
    /// ⛔ T3-07: попередній суфікс <c>_v&lt;id&gt;</c> (код уже від клона) знімається ПЕРЕД додаванням
    /// нового — інакше клон клона ніс <c>CHK1_v4_v5</c> і суфікси росли з кожним клонуванням.
    /// </summary>
    internal static string ClonedRelationCode(string code, int cloneVersionId)
    {
        code = PreviousCloneSuffix().Replace(code, string.Empty);
        var suffix = $"_v{cloneVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return (code.Length + suffix.Length > 64 ? code[..(64 - suffix.Length)] : code) + suffix;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"_v\d+$")]
    private static partial System.Text.RegularExpressions.Regex PreviousCloneSuffix();

    private void CloneTableRelations(TemplateVersion clone, IReadOnlyList<RelationTemplate> templates)
    {
        var ids = clone.Sheets
            .SelectMany(s => s.Tables.Select(t => (t.Id, Key: (Sheet: s.Code, Table: t.Code))))
            .ToDictionary(x => x.Key, x => x.Id);

        foreach (var t in templates)
        {
            if (!ids.TryGetValue(t.Source, out var sourceId) || !ids.TryGetValue(t.Target, out var targetId))
            {
                continue;
            }

            var relation = new TableRelationDef(
                EcrCode.Create(ClonedRelationCode(t.Code, clone.Id)), sourceId, targetId, t.Kind, t.MatchJson);
            relation.Update(sourceId, targetId, t.Kind, t.MatchJson, t.MapJson, t.OnSourceChange, t.IsActive);
            db.TableRelations.Add(relation);
        }
    }

    /// <summary>Ресурс джерела для копіювання грантів: вид, Id джерела й ідентичність за кодами.</summary>
    private sealed record GrantSource(ResourceKind Kind, int SourceId, string Sheet, string? Table, string? Column);

    private static List<GrantSource> ReadGrantSources(TemplateVersion source)
    {
        var result = new List<GrantSource>();
        foreach (var sheet in source.Sheets)
        {
            result.Add(new GrantSource(ResourceKind.Sheet, sheet.Id, sheet.Code, null, null));
            foreach (var table in sheet.Tables)
            {
                result.Add(new GrantSource(ResourceKind.Table, table.Id, sheet.Code, table.Code, null));
                result.AddRange(table.Columns.Select(
                    c => new GrantSource(ResourceKind.Column, c.Id, sheet.Code, table.Code, c.Code)));
            }
        }

        return result;
    }

    /// <summary>
    /// ⛔ A1-04: ресурсні гранти (Deny і дозволи) на аркуш/таблицю/колонку копіюються на нові Id клону
    /// за кодами — так само, як це робить перенос документа між версіями. Без цього Deny на колонку
    /// версії 1.0 мовчки не діяв на документ версії 1.1 (fail-open). Копіюється й дозвіл: рівень
    /// під рівнем проєкту теж звужує доступ.
    /// </summary>
    /// <remarks>
    /// ⛔ Копіюються ЛИШЕ звужувальні гранти: Deny і дозвіл рівня ≤ Read. Дозвіл вище Read (аркуш
    /// Approve, колонка Write) без <c>ProjectId</c> діє в усіх проєктах версії, які користувач бачить
    /// (ent7 P2-2, <c>EditRules.Effective</c>), тож копія могла б ПІДНЯТИ доступ на клоні без рішення
    /// людини; Read-дозвіл підняти не може (грант діє лише при проєктному ≥ Read, S2). Не скопійований
    /// дозвіл дає fail-closed для підвищення; відомий залишок — Allow &gt; Read, що звужував рівень
    /// проєкту (напр. Write під Manage), на клоні не діє: адміністратор видає його заново.
    ///
    /// ⚠ Профіль прав кешується за відбитком грантів ролі (count і max(Id)): нові рядки його
    /// змінюють, тож інвалідація не потрібна. Ресурс клону без відповідника за кодом неможливий
    /// (клон ідентичний джерелу); якби трапився — гранту просто немає, він нічого не відкриває.
    /// </remarks>
    private async Task CloneResourceGrantsAsync(
        TemplateVersion clone, IReadOnlyList<GrantSource> sources, CancellationToken ct)
    {
        var map = new Dictionary<(ResourceKind, int), int>();
        var cloneSheets = clone.Sheets.ToDictionary(s => s.Code);
        foreach (var src in sources)
        {
            if (!cloneSheets.TryGetValue(src.Sheet, out var sheet))
            {
                continue;
            }

            if (src.Table is null)
            {
                map[(src.Kind, src.SourceId)] = sheet.Id;
                continue;
            }

            var table = sheet.Tables.FirstOrDefault(t => t.Code == src.Table);
            if (table is null)
            {
                continue;
            }

            if (src.Column is null)
            {
                map[(src.Kind, src.SourceId)] = table.Id;
            }
            else if (table.Columns.FirstOrDefault(c => c.Code == src.Column) is { } column)
            {
                map[(src.Kind, src.SourceId)] = column.Id;
            }
        }

        var kinds = new[] { ResourceKind.Sheet, ResourceKind.Table, ResourceKind.Column };
        var ids = sources.Select(s => s.SourceId).Distinct().ToList();
        var grants = await db.ResourceGrants
            .AsNoTracking()
            .Where(g => kinds.Contains(g.ResourceKind) && ids.Contains(g.ResourceId)
                        && (g.IsDeny || g.Level <= GrantLevel.Read))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        db.ResourceGrants.AddRange(grants
            .Where(g => map.ContainsKey((g.ResourceKind, g.ResourceId)))
            .Select(g => new ResourceGrant(
                g.RoleId, g.ResourceKind, map[(g.ResourceKind, g.ResourceId)], g.Level, g.IsDeny)));
    }

    private async Task SaveCloneAsync(
        TemplateVersion clone, TemplateVersionCloner.CloneLinks links, int clonedFrom,
        IReadOnlyList<RelationTemplate> relationTemplates, IReadOnlyList<GrantSource> grantSources,
        CancellationToken ct)
    {
        db.TemplateVersions.Add(clone);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        db.FormulaDefs.AddRange(TemplateVersionCloner.Relink(links));
        CloneTableRelations(clone, relationTemplates);
        await CloneResourceGrantsAsync(clone, grantSources, ct).ConfigureAwait(false);
        await CloneConditionalFormatsAsync(clone.Id, clonedFrom, ct).ConfigureAwait(false);
        SetClonedFrom(clone, clonedFrom);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Правила умовного форматування — частина структури: клон несе їх із собою
    /// (ФВ-2.7), інакше правила зникали б при кожній новій версії. Вони не мають
    /// навігації з <see cref="TemplateVersion"/>, тож копіюються окремо.
    /// </summary>
    private async Task CloneConditionalFormatsAsync(int cloneId, int sourceId, CancellationToken ct)
    {
        var source = await db.ConditionalFormatRules
            .AsNoTracking()
            .Where(r => r.TemplateVersionId == sourceId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        db.ConditionalFormatRules.AddRange(source.Select(r => new ConditionalFormatRule(
            cloneId, r.ColumnCode, r.Ordinal, r.Operator, r.Value, r.ValueTo,
            r.BackgroundHex, r.ForegroundHex, r.IsBold)));
    }
    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Q-225: раніше `page.Cursor` НІКОЛИ не читався — лише `Take(page.
    /// Limit)`, тобто версія шаблону за 50-ту (дефолтний ліміт) була
    /// назавжди невидима через цей метод, без жодної помилки. Той самий
    /// патерн курсорної пагінації, що вже в сусідньому `ListTemplatesAsync`
    /// цього файлу: на один рядок більше за сторінку — виявити, чи є ще,
    /// без окремого `COUNT` по всій таблиці.
    /// </remarks>
    public async Task<PagedResult<TemplateVersionSummary>> ListVersionsAsync(
        int templateId, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var after = Cursor.Decode(page.Cursor);

        var rows = await db.TemplateVersions
            .AsNoTracking()
            .Where(v => v.TemplateId == templateId && v.Id > after)
            .OrderBy(v => v.Id)
            .Take(page.Limit + 1)
            .Select(v => new TemplateVersionSummary(
                v.Id, v.Version, v.Status, v.PresentationRevision, v.ClonedFromVersionId, v.PublishedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit).ToList();

        return new PagedResult<TemplateVersionSummary>(
            items, hasMore ? Cursor.Encode(items[^1].Id) : null, TotalCount: null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Ліміт на шаблон — у SQL (корельований `Take` на шаблон дає
    /// `ROW_NUMBER() OVER (PARTITION BY TemplateId ...)`), а не в пам'яті:
    /// інакше один шаблон із тисячею версій тягнув би їх усі заради сотні.
    /// `GroupBy(...).SelectMany(g => g.Take(n))` EF не перекладає — звідси
    /// корень запиту від <c>Templates</c>.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<TemplateVersionSummary>>> ListVersionsForTemplatesAsync(
        IReadOnlyCollection<int> templateIds, int perTemplateLimit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(templateIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(perTemplateLimit, 1);

        if (templateIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<TemplateVersionSummary>>();
        }

        var ids = templateIds.Distinct().ToList();

        var rows = await db.Templates
            .AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .SelectMany(t => db.TemplateVersions
                .Where(v => v.TemplateId == t.Id)
                .OrderBy(v => v.Id)
                .Take(perTemplateLimit))
            .Select(v => new
            {
                v.TemplateId,
                Summary = new TemplateVersionSummary(
                    v.Id, v.Version, v.Status, v.PresentationRevision, v.ClonedFromVersionId, v.PublishedAt),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.TemplateId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TemplateVersionSummary>)[.. g.Select(r => r.Summary).OrderBy(s => s.Id)]);
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
                "ECR-TMPL-0409",
                $"Шаблон з кодом «{code}» уже існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0409.templateCodeTaken",
                    ["code"] = code,
                });
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
        CursorRequest page, string? query, IReadOnlyCollection<int> visibleProjectIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(visibleProjectIds);

        var after = Cursor.Decode(page.Cursor);

        var templates = db.Templates.AsNoTracking().Where(t => t.Id > after);

        var trimmed = query?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            if (trimmed.Length > 100)
            {
                trimmed = trimmed[..100];
            }

            var codeLike = "%" + EscapeLike(trimmed) + "%";

            // ⚠ Два шаблони назви: як лежить у рядку (сід вставляє неекранованим) і як пише
            // `LocalizedText.ToJson` (не-ASCII як `\uXXXX`). `:"%` — після першого значення, а не
            // в ключі мови. Той самий прийом, що `ColumnDefSearchStore`.
            var rawLike = "%:\"%" + EscapeLike(trimmed) + "%";
            var jsonLike = "%:\"%" + EscapeLike(JsonText(trimmed)) + "%";

            var nameIds = db.Database.SqlQuery<int>($"""
                SELECT t.Id AS Value
                  FROM cfg.Template AS t
                 WHERE t.NameL10n LIKE {rawLike} ESCAPE '!' OR t.NameL10n LIKE {jsonLike} ESCAPE '!'
                """);

            templates = templates.Where(t =>
                EF.Functions.Like(t.Code, codeLike, "!") || nameIds.Contains(t.Id));
        }

        // Беремо на один більше за сторінку: так видно, чи є наступна, без
        // окремого COUNT по всій таблиці.
        var rows = await templates
            .OrderBy(t => t.Id)
            .Take(page.Limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var pageRows = rows.Take(page.Limit).ToList();
        var templateIds = pageRows.Select(t => t.Id).ToList();

        // Агрегати лише по шаблонах сторінки: три запити на сторінку, а не по запиту на рядок.
        var versions = await db.TemplateVersions
            .AsNoTracking()
            .Where(v => templateIds.Contains(v.TemplateId))
            .Select(v => new
            {
                v.Id, v.TemplateId, v.Status, v.CreatedAt, v.CreatedByUserId, v.PublishedAt, v.DeprecatedAt,
                v.UpdatedAt,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var versionIds = versions.Select(v => v.Id).ToList();

        // ⛔ Лише проєкти, де читач бачить документи; без цього число розкрило б чужі проєкти.
        var documentsByVersion = new Dictionary<int, int>();
        if (visibleProjectIds.Count > 0 && versionIds.Count > 0)
        {
            var visible = visibleProjectIds as int[] ?? [.. visibleProjectIds];
            documentsByVersion = await db.Documents
                .AsNoTracking()
                .Join(
                    db.Projects.AsNoTracking(),
                    d => d.ProjectId,
                    p => p.Id,
                    (d, p) => new { p.Id, p.TemplateVersionId })
                .Where(x => visible.Contains(x.Id) && versionIds.Contains(x.TemplateVersionId))
                .GroupBy(x => x.TemplateVersionId)
                .Select(g => new { VersionId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.VersionId, x => x.Count, ct)
                .ConfigureAwait(false);
        }

        // Найновіша чернетка кожного шаблону й ім'я її автора (D-86: відображуване ім'я, не логін).
        var drafts = versions
            .Where(v => v.Status == TemplateVersionStatus.Draft)
            .GroupBy(v => v.TemplateId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Id).First());

        var authorIds = drafts.Values.Select(d => d.CreatedByUserId).Distinct().ToList();
        var authors = authorIds.Count == 0
            ? []
            : await db.Users
                .AsNoTracking()
                .Where(u => authorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct)
                .ConfigureAwait(false);

        var items = pageRows.Select(t =>
        {
            var own = versions.Where(v => v.TemplateId == t.Id).ToList();
            var updated = own
                .SelectMany(v => new[]
                {
                    v.CreatedAt, v.PublishedAt ?? default, v.DeprecatedAt ?? default, v.UpdatedAt ?? default,
                })
                .Append(t.CreatedAt)
                .Max();

            drafts.TryGetValue(t.Id, out var draft);

            return new TemplateSummary(
                t.Id,
                t.Code,
                own.Count,
                t.NameL10n,
                !t.IsActive,
                own.Sum(v => documentsByVersion.GetValueOrDefault(v.Id)),
                updated,
                draft is null ? null : authors.GetValueOrDefault(draft.CreatedByUserId),
                draft?.CreatedAt,
                t.ArchivedAt,
                draft is null ? null : draft.UpdatedAt ?? draft.CreatedAt);
        }).ToList();

        return new PagedResult<TemplateSummary>(
            items, hasMore ? Cursor.Encode(items[^1].Id) : null, TotalCount: null);
    }

    private const char LikeEscape = '!';

    /// <summary>Екранує метасимволи <c>LIKE</c> (<c>%</c>, <c>_</c>, <c>[</c>, сам <c>!</c>).</summary>
    private static string EscapeLike(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (ch is '%' or '_' or '[' or LikeEscape)
            {
                builder.Append(LikeEscape);
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>Рядок так, як його пише <c>LocalizedText.ToJson</c>, без лапок.</summary>
    private static string JsonText(string value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return json[1..^1];
    }

    /// <inheritdoc />
    public async Task<TemplateCard?> FindCardAsync(int templateId, CancellationToken ct)
    {
        var template = await db.Templates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == templateId, ct)
            .ConfigureAwait(false);

        if (template is null)
        {
            return null;
        }

        // Версії шаблону — одиниці-десятки, і з них рахуються ОБИДВА числа
        // (усього й опублікованих). Два `CountAsync` замість одного читання
        // дали б два звернення заради тієї самої вибірки.
        var versions = await db.TemplateVersions
            .AsNoTracking()
            .Where(v => v.TemplateId == templateId)
            .Select(v => new { v.Id, v.Status })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var versionIds = versions.Select(v => v.Id).ToList();

        // ⛔ Фільтр за версіями ЦЬОГО шаблону — те, заради чого лічильник
        // існує. Без нього числа були б загальними по базі: адміністратор
        // побачив би «документів 4 812» на шаблоні, де їх нуль, і не
        // заархівував би нічого й ніколи.
        var projects = await db.Projects
            .AsNoTracking()
            .CountAsync(p => versionIds.Contains(p.TemplateVersionId), ct)
            .ConfigureAwait(false);

        // Документ прив'язаний до ПРОЄКТУ, а версію шаблону тримає проєкт —
        // той самий шлях, що вже ходить `HasDocumentsAsync` вище.
        var documents = await db.Documents
            .AsNoTracking()
            .Join(db.Projects.AsNoTracking(),
                  d => d.ProjectId,
                  p => p.Id,
                  (d, p) => p.TemplateVersionId)
            .CountAsync(versionId => versionIds.Contains(versionId), ct)
            .ConfigureAwait(false);

        return new TemplateCard(
            template.Id,
            template.Code,
            template.NameL10n,
            template.IsActive,
            template.CreatedAt,
            new TemplateDependents(
                versions.Count,
                versions.Count(v => v.Status == Domain.Enums.TemplateVersionStatus.Published),
                projects,
                documents));
    }

    /// <inheritdoc />
    public async Task<Template?> FindTemplateAsync(int templateId, CancellationToken ct)
        => await db.Templates
            .FirstOrDefaultAsync(t => t.Id == templateId, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Template?> FindTemplateOfVersionAsync(int templateVersionId, CancellationToken ct)
        => await db.TemplateVersions
            .AsNoTracking()
            .Where(v => v.Id == templateVersionId)
            .Join(db.Templates.AsNoTracking(), v => v.TemplateId, t => t.Id, (_, t) => t)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

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
    public async Task<TableRelationDef?> FindTableRelationAsync(
        int templateVersionId, string code, CancellationToken ct)
    {
        // ⛔ МАТЕРІАЛІЗУЄМО ідентифікатори ОКРЕМИМ запитом, а не
        // вбудовуємо `TableIdsOfVersion(...)` як вкладений `IQueryable` у
        // `Contains`. EF Core визначає режим відстеження для ВСЬОГО
        // складеного дерева виразу одразу, а не по частинах: коли
        // `Contains` отримує НЕ звичайну колекцію, а IQueryable з власним
        // `AsNoTracking()` усередині (`TableIdsOfVersion` навмисно
        // AsNoTracking — вона лише перелічує ID, а не сутність, що
        // редагується), ця позначка мовчки поширюється на весь запит.
        // Наслідок був фатальний і без жодного видимого сліду: сутність
        // поверталася зі станом `Detached`, `Update(...)` мутував лише
        // відірваний від контексту об'єкт у пам'яті, а `SaveChangesAsync`
        // не бачив у ній жодної зміни — 200 OK з ехом нових значень при
        // повністю незмінному рядку в базі. Список (`ListTableRelationsAsync`)
        // цієї вади не має: там `AsNoTracking()` стоїть явно й навмисно.
        var tableIds = await TableIdsOfVersion(templateVersionId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await db.TableRelations
            .Where(r => r.Code == code && tableIds.Contains(r.SourceTableDefId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
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
                    $"Поле {change.EntityType}.{change.Field} не належить презентаційному шару.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-TMPL-0422.presentationFieldUnknown",
                        ["entityType"] = change.EntityType,
                        ["field"] = change.Field,
                    });
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
                    $"{change.EntityType} {change.EntityId} не належить версії {templateVersionId}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-TMPL-0404.presentationTarget",
                        ["entityType"] = change.EntityType,
                        ["entityId"] = change.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["versionId"] = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
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
            [("ColumnDef", "WidthPx")] = Int(columnScope, "WidthPx"),
            [("RowDef", "LabelL10n")] = Text(rowScope, "LabelL10n"),
            [("RowDef", "Ordinal")] = Int(rowScope, "Ordinal"),
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
