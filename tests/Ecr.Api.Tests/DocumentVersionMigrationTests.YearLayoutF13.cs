// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.YearLayoutF13.cs
using System.Net;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ LAND-YEAR L3 (F13 «Аудиту»): частини місяця дозволу, гранти колонки, відбиток мапи місяців і прив'язки методологій
/// при переносі «копія на місяць» → «рік, місяці в колонках». Сценарій — <see cref="ArrangeYearAsync"/>.
/// </summary>
/// <remarks>
/// Мутаційні докази (кожна збирається і червонить свій тест), усе в <c>DocumentVersionMigrationStore</c>:
/// (1) UPDATE частин <c>doc.CellPermitPart</c> через <c>@monthColumnMap</c> — <c>AND 1 = 0</c> у WHERE (тест частин);
/// (2) <c>SELECT 3, o, n FROM @monthColumnMap</c> у <c>@grantMap</c> — <c>WHERE 1 = 0</c> (тест грантів);
/// (3) <c>PlanHash</c>: <c>plan.MonthColumns.Count &gt; 0</c> → <c>&gt; 100000</c> (тест відбитка);
/// (4) <c>CountUnmappedBindingsAsync</c>: <c>months.All</c> → <c>months.Any</c> (тест прив'язок).
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    [Trait("Task", "LAND-YEAR-L3")]
    [Trait("F13", "L3-M1")]
    public async Task Перенос_на_рік_частини_місяця_дозволу_колонки_C_переїжджають_у_C_Mmm_свого_місяця()
    {
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            long entryId;
            await using (var db = y.Builder.CreateContext())
            {
                var registry = new RegistryDef(EcrCode.Create($"PRM_L3{Guid.NewGuid():N}"[..20]), Name("Permit L3"), isTemporal: true);
                db.RegistryDefs.Add(registry);
                await db.SaveChangesAsync().ConfigureAwait(true);
                var entry = new RegistryEntry(registry.Id, EcrCode.Create("L3PART"), Name("KZ-L3"));
                db.RegistryEntries.Add(entry);
                await db.SaveChangesAsync().ConfigureAwait(true);
                entryId = entry.Id;

                // Вересень поділено на дві частини, березень — одна частина; усе на немісячній колонці F.
                db.CellPermitParts.Add(new CellPermitPart(
                    209909, y.Doc.DocumentId, y.Doc.TableDefId, null, y.Row1Key, y.SourceF, 1, entryId,
                    new DateOnly(2099, 9, 1), new DateOnly(2099, 9, 14), 40m));
                db.CellPermitParts.Add(new CellPermitPart(
                    209909, y.Doc.DocumentId, y.Doc.TableDefId, null, y.Row1Key, y.SourceF, 2, entryId,
                    new DateOnly(2099, 9, 15), new DateOnly(2099, 9, 30), 50m));
                db.CellPermitParts.Add(new CellPermitPart(
                    209903, y.Doc.DocumentId, y.Doc.TableDefId, null, y.Row1Key, y.SourceF, 1, entryId,
                    new DateOnly(2099, 3, 1), new DateOnly(2099, 3, 31), 30m));
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            using var app = CreateYearApp();
            using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);
            var applied = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
            var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(applied.StatusCode == HttpStatusCode.OK, $"{applied.StatusCode}: {body} {app.ErrorsText}");

            await using var check = y.Builder.CreateContext();
            var parts = await check.CellPermitParts.AsNoTracking()
                .Where(p => p.DocumentId == y.Doc.DocumentId).OrderBy(p => p.PeriodKeyValue).ThenBy(p => p.PartNo)
                .Select(p => new { p.PeriodKeyValue, p.PartNo, p.ColumnDefId, p.TableDefId, p.ValueNumeric })
                .ToListAsync().ConfigureAwait(true);

            // ⛔ Предмет тесту: частини місяця m — на F_Mmm цільової таблиці, на F не лишилось нічого.
            Assert.Equal(
                [
                    (209903, (byte)1, y.TargetFMonths[3], y.TargetTableDefId, (decimal?)30m),
                    (209909, (byte)1, y.TargetFMonths[9], y.TargetTableDefId, (decimal?)40m),
                    (209909, (byte)2, y.TargetFMonths[9], y.TargetTableDefId, (decimal?)50m),
                ],
                parts.Select(p => (p.PeriodKeyValue, p.PartNo, p.ColumnDefId, p.TableDefId, p.ValueNumeric)).ToList());

            // Читач історії дозволів за Id ЦІЛЬОВОЇ версії бачить обидві частини вересня на F_M09.
            var read = await new PermitHistoryStore(check).CellPartsAsync(
                209909, y.Doc.DocumentId, y.TargetTableDefId, null, y.Row1Key, y.TargetFMonths[9], default).ConfigureAwait(true);
            Assert.Equal([((byte)1, 40m), ((byte)2, 50m)], read.Select(p => (p.PartNo, p.ValueNumeric ?? 0m)));
        }
        finally
        {
            await CleanupYearAsync(y).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    [Trait("Task", "LAND-YEAR-L3")]
    [Trait("F13", "L3-M2")]
    public async Task Перенос_на_рік_заборона_на_колонку_C_копіюється_на_кожну_C_Mmm_і_діє_в_доступі()
    {
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            // Користувач: Write на проєкт, заборона на немісячну F (fail-open, якщо заборона не дійде до F_Mmm).
            int userId;
            int roleId;
            await using (var db = y.Builder.CreateContext())
            {
                var name = $"l3g_{Guid.NewGuid():N}"[..20];
                var user = new User(name, name, AuthProvider.Local);
                user.SetPassword(new PasswordHasher().Hash(Password));
                db.Users.Add(user);
                var role = new Role(
                    EcrCode.Create($"L3G{Guid.NewGuid():N}"[..14]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "L3 column deny" }));
                db.Roles.Add(role);
                await db.SaveChangesAsync().ConfigureAwait(true);

                db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, y.Doc.ProjectId, GrantLevel.Write));
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Column, y.SourceF, GrantLevel.None, isDeny: true));
                await db.SaveChangesAsync().ConfigureAwait(true);
                (userId, roleId) = (user.Id, role.Id);
            }

            using (var app = CreateYearApp())
            {
                using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);
                var applied = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
                var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
                Assert.True(applied.StatusCode == HttpStatusCode.OK, $"{applied.StatusCode}: {body} {app.ErrorsText}");
            }

            // ⛔ Предмет тесту (база): заборона стоїть на КОЖНІЙ з 12 місячних колонок, і стара на F лишилась.
            int targetSheetId;
            await using (var db = y.Builder.CreateContext())
            {
                var denied = await db.ResourceGrants.AsNoTracking()
                    .Where(g => g.RoleId == roleId && g.ResourceKind == ResourceKind.Column && g.IsDeny)
                    .Select(g => g.ResourceId).ToListAsync().ConfigureAwait(true);
                Assert.Equal(
                    [y.SourceF, .. Enumerable.Range(1, 12).Select(m => y.TargetFMonths[m])],
                    denied.Order().ToList());
                targetSheetId = await db.TableDefs.AsNoTracking().Where(t => t.Id == y.TargetTableDefId)
                    .Select(t => t.SheetDefId).SingleAsync().ConfigureAwait(true);
            }

            // Той самий оракул, що вирішує доступ до комірки: F_M09 закрита, сусідній місячний ввід M9 — ні.
            using var after = CreateYearApp();
            using var scope = after.Services.CreateScope();
            var access = scope.ServiceProvider.GetRequiredService<IAccessDecisionService>();
            var profile = await access.BuildProfileAsync(userId, CancellationToken.None).ConfigureAwait(true);
            GrantLevel Effective(int column) => EditRules.Effective(profile, default(CellAccessContext) with
            {
                ProjectId = y.Doc.ProjectId,
                SheetDefId = targetSheetId,
                TableDefId = y.TargetTableDefId,
                ColumnDefId = column,
                SheetCode = y.Doc.SheetCode,
            });

            Assert.Equal(GrantLevel.None, Effective(y.TargetFMonths[9]));
            Assert.True(Effective(y.TargetM9) > GrantLevel.None, "заборона F не повинна поширитися на місячний ввід");
        }
        finally
        {
            await CleanupYearAsync(y).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    [Trait("Task", "LAND-YEAR-L3")]
    [Trait("F13", "L3-M3")]
    public async Task Відновлення_переносу_на_рік_з_іншою_мапою_місяців_422()
    {
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            using var app = CreateYearApp();
            VersionMigrationPlan plan;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var cache = scope.ServiceProvider.GetRequiredService<IMetadataCache>();
                var from = await cache.GetAsync(y.SourceVersionId, CancellationToken.None).ConfigureAwait(true);
                var to = await cache.GetAsync(y.TargetVersionId, CancellationToken.None).ConfigureAwait(true);
                await using var db0 = y.Builder.CreateContext();
                var usage = await new DocumentVersionMigrationStore(db0).ReadScopeAsync(y.Doc.ProjectId, CancellationToken.None).ConfigureAwait(true);
                plan = VersionMigrationPlanner.Plan(from, to, usage.Cells, usage.HeaderValues, monthColumns: true);
            }

            Assert.Equal(12, plan.MonthColumns.Count);

            await using (var db = y.Builder.CreateContext())
            await using (var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true))
            {
                await new DocumentVersionMigrationStore(db)
                    .BeginMigrationAsync(y.Doc.ProjectId, y.TargetVersionId, plan, y.UserId, CancellationToken.None).ConfigureAwait(true);
                await tx.CommitAsync().ConfigureAwait(true);
            }

            // Та сама мапа — повторний Begin дозволений (контроль: відмова нижче — саме через мапу місяців).
            await using (var db = y.Builder.CreateContext())
            await using (var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true))
            {
                await new DocumentVersionMigrationStore(db)
                    .BeginMigrationAsync(y.Doc.ProjectId, y.TargetVersionId, plan, y.UserId, CancellationToken.None).ConfigureAwait(true);
                await tx.RollbackAsync().ConfigureAwait(true);
            }

            // ⛔ Предмет тесту: решта мапи та сама, змінено лише мапу місяців (без місяців / без грудня) — 422.
            VersionMigrationPlan[] changed = [plan with { MonthColumns = [] }, plan with { MonthColumns = [.. plan.MonthColumns.Take(11)] }];
            foreach (var other in changed)
            {
                await using var db = y.Builder.CreateContext();
                await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
                var refused = await Assert.ThrowsAsync<BusinessRuleException>(
                    () => new DocumentVersionMigrationStore(db).BeginMigrationAsync(
                        y.Doc.ProjectId, y.TargetVersionId, other, y.UserId, CancellationToken.None)).ConfigureAwait(true);
                await tx.RollbackAsync().ConfigureAwait(true);

                Assert.Equal("ECR-SCHM-0422", refused.ErrorCode);
                Assert.Equal("err.ECR-SCHM-0422.migrateResumeMismatch", refused.Details!["messageKey"]);
            }

            Assert.Equal(y.SourceVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));
        }
        finally
        {
            // Маркер обірваного переносу (власний проєкт тесту) знімаємо, щоб його не підхопило відновлення.
            await using (var db = y.Builder.CreateContext())
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE doc.Project SET ExternalSettingsJson = NULL WHERE Id = {y.Doc.ProjectId}").ConfigureAwait(true);
            }

            await CleanupYearAsync(y).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    [Trait("Finding", "D-13")]
    [Trait("Task", "LAND-YEAR-L3")]
    [Trait("F13", "L3-M4")]
    public async Task Прив_язка_C_покрита_лише_11_з_12_місячних_колонок_блокує_перенос_на_рік()
    {
        var y = await ArrangeYearAsync(targetMonths: 12).ConfigureAwait(true);
        try
        {
            var methodologyId = await AddBindingAsync(y.Doc.TableDefId, y.SourceF).ConfigureAwait(true);
            for (var m = 1; m <= 11; m++)
            {
                await AddBindingAsync(y.TargetTableDefId, y.TargetFMonths[m], methodologyId).ConfigureAwait(true);
            }

            using var app = CreateYearApp();
            using var client = await SignedInAsync(app, y.UserName).ConfigureAwait(true);

            // ⛔ Предмет тесту: грудня без прив'язки — прив'язка F не перенесена (D-13), сухий прогін і перенос відмовляють.
            var dry = await PostYearAsync(client, y, dryRun: true).ConfigureAwait(true);
            var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
            var report = JsonDocument.Parse(dryBody).RootElement;
            Assert.False(report.GetProperty("canApply").GetBoolean(), dryBody);
            Assert.Contains("bindingsNotMapped", report.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));

            var refused = await PostYearAsync(client, y, dryRun: false).ConfigureAwait(true);
            await AssertProblemAsync(
                refused, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateBindingsNotMapped").ConfigureAwait(true);
            Assert.Equal(y.SourceVersionId, await YearProjectVersionAsync(y).ConfigureAwait(true));

            // Контроль: з прив'язкою грудня (усі 12) — відмови bindingsNotMapped немає.
            await AddBindingAsync(y.TargetTableDefId, y.TargetFMonths[12], methodologyId).ConfigureAwait(true);
            var full = JsonDocument.Parse(await (await PostYearAsync(client, y, dryRun: true).ConfigureAwait(true))
                .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
            Assert.True(full.GetProperty("canApply").GetBoolean(), full.ToString());
            Assert.DoesNotContain("bindingsNotMapped", full.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
        }
        finally
        {
            await CleanupYearAsync(y).ConfigureAwait(true);
        }
    }
}
