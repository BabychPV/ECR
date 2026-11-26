// tests/Ecr.Api.Tests/DuplicateRequestHttpTests.Cleanup.cs
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B161 (правило 394): кожен тест класу прибирає рядки, які створив сам, — і лише їх (за ідентифікаторами й кодами,
/// записаними в <see cref="Created"/> у момент створення).
/// </summary>
/// <remarks>
/// ⚠ Прибирання — у <see cref="IAsyncLifetime.DisposeAsync"/>: xUnit створює екземпляр класу на КОЖЕН тест і кличе
/// його після тесту за будь-якого результату, тобто це той самий <c>finally</c>, лише один на всі тести, а не
/// копія в кожному. Падіння прибирання червонить тест: залишок не ховається мовчки.
/// <para>
/// Що лишається свідомо (і чому):
/// історія системно-версійованих <c>dic.RegistryEntry</c>/<c>dic.RegistryValue</c> (<c>*History</c>) — рядки історії
/// не видаляються без вимкнення <c>SYSTEM_VERSIONING</c>, а вимикати обмеження тест не має права;
/// журнали аудиту (<c>aud.*</c>) — таблиці незмінні тригерами <c>TR_*_Immutable</c> (архів незмінний, B69);
/// значення послідовностей (<c>doc.TableInstanceSeq</c>, <c>doc.TableRowSeq</c>, IDENTITY) — не повертаються.
/// </para>
/// </remarks>
public sealed partial class DuplicateRequestHttpTests : IAsyncLifetime
{
    private readonly Created _created = new();

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await using var db = Db();

        // Фонові задачі тесту (експорт, зріз звіту) ще можуть працювати над документом: спершу дочекатися їх кінця,
        // інакше прибирання видалило б рядок, який задача от-от оновить.
        await WaitJobsAsync(db).ConfigureAwait(false);

        foreach (var jobId in _created.JobIds)
        {
            await db.JobProgresses.Where(j => j.JobId == jobId).ExecuteDeleteAsync().ConfigureAwait(false);
        }

        foreach (var document in _created.Documents)
        {
            await DeleteDocumentChainAsync(db, document).ConfigureAwait(false);
        }

        foreach (var code in _created.PermitTypeCodes)
        {
            await DeletePermitTypeAsync(db, code).ConfigureAwait(false);
        }

        foreach (var registryId in _created.RegistryIds)
        {
            await DeleteRegistriesAsync(
                db, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"SELECT {registryId}"), permitTypeCode: null)
                .ConfigureAwait(false);
        }

        await DeleteSecurityAsync(db).ConfigureAwait(false);
    }

    private async Task WaitJobsAsync(Ecr.Infrastructure.Persistence.EcrDbContext db)
    {
        var jobIds = _created.JobIds;
        var documentIds = _created.Documents.Select(d => (long?)d.DocumentId).ToList();
        if (jobIds.Count == 0 && documentIds.Count == 0)
        {
            return;
        }

        for (var attempt = 0; attempt < 120; attempt++)
        {
            var busy = await db.JobProgresses.AsNoTracking()
                .AnyAsync(j => (jobIds.Contains(j.JobId) || documentIds.Contains(j.DocumentId))
                               && (j.State == "Queued" || j.State == "Running"))
                .ConfigureAwait(false);
            if (!busy)
            {
                return;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        Assert.Fail($"задачі тесту не дійшли до кінця за 30 с — прибирання не видалятиме документ під ними.\n{App.ErrorsText}");
    }

    /// <summary>Документи проєкту (і створені тестом через API), проєкт з періодами і шаблон <see cref="TestDocumentBuilder"/>.</summary>
    private static async Task DeleteDocumentChainAsync(Ecr.Infrastructure.Persistence.EcrDbContext db, TestDocument document)
    {
        // ⚠ Версія шаблону — назад у чернетку перед видаленням опису: тригери незмінності (`TR_ColumnDef_Immutable`,
        // `TR_RowDef_Immutable`) не пускають зміни опису опублікованої версії. Це рядок самого тесту.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @p int = {document.ProjectId}, @t int = {document.TemplateId};
            DECLARE @docs TABLE (Id bigint PRIMARY KEY);
            INSERT @docs SELECT Id FROM doc.Document WHERE ProjectId = @p;

            DELETE FROM itg.JobProgress WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE st FROM wf.ApprovalStep AS st JOIN wf.ApprovalRoute AS r ON r.Id = st.ApprovalRouteId WHERE r.ProjectId = @p;
            DELETE FROM wf.ApprovalRoute WHERE ProjectId = @p;
            DELETE FROM wf.ApprovalEvent WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE FROM wf.ApprovalState WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE FROM wf.ValidationResult WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE FROM calc.SubmissionSnapshot WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE FROM doc.DocumentHeaderValue WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE FROM doc.DocumentIndexValue WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE FROM doc.DocumentPeriodReopen WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE FROM doc.RowLock WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE FROM doc.CellPermitPart WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE FROM doc.PermitSegment WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE cv FROM doc.CellValue AS cv
              JOIN doc.TableRow AS tr ON tr.PeriodKey = cv.PeriodKey AND tr.Id = cv.TableRowId
              JOIN doc.TableInstance AS ti ON ti.PeriodKey = tr.PeriodKey AND ti.Id = tr.TableInstanceId
              WHERE ti.DocumentId IN (SELECT Id FROM @docs);
            DELETE tr FROM doc.TableRow AS tr
              JOIN doc.TableInstance AS ti ON ti.PeriodKey = tr.PeriodKey AND ti.Id = tr.TableInstanceId
              WHERE ti.DocumentId IN (SELECT Id FROM @docs);
            DELETE FROM doc.TableInstance WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE FROM doc.DocumentSheet WHERE DocumentId IN (SELECT Id FROM @docs);
            DELETE FROM doc.Document WHERE ProjectId = @p;
            -- Закріплений поточний період (тест «закріплення») тримає FK на період і CK_Project_Pinned — спершу знімається.
            UPDATE doc.Project SET CurrentPeriodMode = 0, CurrentPeriodId = NULL, CurrentPeriodPinnedReason = NULL WHERE Id = @p;
            DELETE FROM doc.ProjectTemplateVersion WHERE ProjectId = @p;
            DELETE FROM doc.ProjectTemplate WHERE ProjectId = @p;
            DELETE FROM doc.Period WHERE ProjectId = @p;
            DELETE FROM doc.Project WHERE Id = @p;

            UPDATE cfg.TemplateVersion SET Status = 0 WHERE TemplateId = @t;
            DELETE cd FROM cfg.ColumnDef AS cd JOIN cfg.TableDef AS td ON td.Id = cd.TableDefId JOIN cfg.SheetDef AS sd ON sd.Id = td.SheetDefId
              JOIN cfg.TemplateVersion AS tv ON tv.Id = sd.TemplateVersionId WHERE tv.TemplateId = @t;
            DELETE rd FROM cfg.RowDef AS rd JOIN cfg.TableDef AS td ON td.Id = rd.TableDefId JOIN cfg.SheetDef AS sd ON sd.Id = td.SheetDefId
              JOIN cfg.TemplateVersion AS tv ON tv.Id = sd.TemplateVersionId WHERE tv.TemplateId = @t;
            DELETE td FROM cfg.TableDef AS td JOIN cfg.SheetDef AS sd ON sd.Id = td.SheetDefId
              JOIN cfg.TemplateVersion AS tv ON tv.Id = sd.TemplateVersionId WHERE tv.TemplateId = @t;
            DELETE sd FROM cfg.SheetDef AS sd JOIN cfg.TemplateVersion AS tv ON tv.Id = sd.TemplateVersionId WHERE tv.TemplateId = @t;
            DELETE FROM cfg.TemplateVersion WHERE TemplateId = @t;
            DELETE FROM cfg.Template WHERE Id = @t;
            COMMIT TRANSACTION;
            """).ConfigureAwait(false);
    }

    /// <summary>Тип дозволу за кодом: його реєстри (шапка й таблиці) з дозволами-записами, метадані полів і сам тип.</summary>
    private static async Task DeletePermitTypeAsync(Ecr.Infrastructure.Persistence.EcrDbContext db, string code)
        => await DeleteRegistriesAsync(
            db,
            """
            SELECT HeaderRegistryDefId FROM cfg.PermitTypeDef WHERE Code = @code
            UNION SELECT t.RegistryDefId FROM cfg.PermitTypeTable AS t JOIN cfg.PermitTypeDef AS p ON p.Id = t.PermitTypeDefId WHERE p.Code = @code
            """,
            code).ConfigureAwait(false);

    /// <summary>Реєстри з переліку <paramref name="registrySelect"/> з усім вмістом; з кодом типу — і сам тип дозволу.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="registrySelect">Запит Id реєстрів; лише сталий текст тесту, без даних (дані — у <c>@code</c>).</param>
    /// <param name="permitTypeCode">Код типу дозволу (параметр <c>@code</c>) або <c>null</c>.</param>
    private static async Task DeleteRegistriesAsync(
        Ecr.Infrastructure.Persistence.EcrDbContext db, string registrySelect, string? permitTypeCode)
    {
        var sql = $$"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @code nvarchar(100) = {0};
            DECLARE @r TABLE (Id int PRIMARY KEY);
            INSERT @r {{registrySelect}};
            DECLARE @e TABLE (Id bigint PRIMARY KEY);
            INSERT @e SELECT Id FROM dic.RegistryEntry WHERE RegistryDefId IN (SELECT Id FROM @r);

            DELETE FROM dic.RegistryEntryKey WHERE RegistryEntryId IN (SELECT Id FROM @e);
            DELETE FROM dic.RegistryValue WHERE RegistryEntryId IN (SELECT Id FROM @e);
            DELETE FROM dic.RegistryEntryLink WHERE LeftEntryId IN (SELECT Id FROM @e) OR RightEntryId IN (SELECT Id FROM @e);
            DELETE FROM dic.RegistryExternalKey WHERE RegistryEntryId IN (SELECT Id FROM @e);
            DELETE FROM dic.RegistryEntry WHERE Id IN (SELECT Id FROM @e);

            DELETE m FROM cfg.PermitFieldMeta AS m JOIN cfg.PermitTypeDef AS p ON p.Id = m.PermitTypeDefId WHERE p.Code = @code;
            DELETE t FROM cfg.PermitTypeTable AS t JOIN cfg.PermitTypeDef AS p ON p.Id = t.PermitTypeDefId WHERE p.Code = @code;
            DELETE FROM cfg.PermitTypeDef WHERE Code = @code;

            DELETE kf FROM cfg.RegistryKeyField AS kf JOIN cfg.RegistryKeyDef AS kd ON kd.Id = kf.RegistryKeyDefId
              WHERE kd.RegistryDefId IN (SELECT Id FROM @r);
            DELETE FROM cfg.RegistryKeyDef WHERE RegistryDefId IN (SELECT Id FROM @r);
            DELETE FROM cfg.RegistryFieldDef WHERE RegistryDefId IN (SELECT Id FROM @r);
            DELETE FROM cfg.RegistryRuleDef WHERE RegistryDefId IN (SELECT Id FROM @r);
            DELETE FROM cfg.RegistryUse WHERE RegistryDefId IN (SELECT Id FROM @r);
            DELETE FROM cfg.RegistryDefinitionDraft WHERE RegistryDefId IN (SELECT Id FROM @r);
            DELETE FROM cfg.RegistryImportProfile WHERE RegistryDefId IN (SELECT Id FROM @r);
            DELETE FROM cfg.RegistryDef WHERE Id IN (SELECT Id FROM @r);
            COMMIT TRANSACTION;
            """;

        // ⚠ Текст запиту складається лише зі сталих фрагментів цього файла (і Id реєстру тесту); код — параметр.
        await db.Database.ExecuteSqlRawAsync(sql, permitTypeCode ?? string.Empty).ConfigureAwait(false);
    }

    /// <summary>Користувачі (свої й створені через API), їхні ролі, права, призначення, гранти, налаштування й спроби входу.</summary>
    private async Task DeleteSecurityAsync(Ecr.Infrastructure.Persistence.EcrDbContext db)
    {
        var names = _created.UserNames;
        var roleIds = _created.RoleIds;
        var roleCodes = _created.RoleCodes;

        var userIds = await db.Users.Where(u => names.Contains(u.UserName)).Select(u => u.Id).ToListAsync().ConfigureAwait(false);
        roleIds.AddRange(await db.Roles.Where(r => roleCodes.Contains(r.Code)).Select(r => r.Id).ToListAsync().ConfigureAwait(false));

        await db.ResourceGrants.Where(g => roleIds.Contains(g.RoleId)).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.RolePermissions.Where(p => roleIds.Contains(p.RoleId)).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.RoleAssignments.Where(a => roleIds.Contains(a.RoleId) || (a.UserId != null && userIds.Contains(a.UserId.Value)))
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await db.UserPreferences.Where(p => userIds.Contains(p.UserId)).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.LoginAttempts.Where(a => names.Contains(a.UserName)).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.Roles.Where(r => roleIds.Contains(r.Id)).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.Users.Where(u => userIds.Contains(u.Id)).ExecuteDeleteAsync().ConfigureAwait(false);
    }

    /// <summary>Що створив поточний тест — для прибирання.</summary>
    private sealed class Created
    {
        public List<string> UserNames { get; } = [];

        public List<int> RoleIds { get; } = [];

        public List<string> RoleCodes { get; } = [];

        public List<TestDocument> Documents { get; } = [];

        public List<int> RegistryIds { get; } = [];

        public List<string> PermitTypeCodes { get; } = [];

        public List<string> JobIds { get; } = [];
    }
}
