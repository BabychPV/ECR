// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.SubmitRace.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// AN-36b (рев'ю AN-36, P2-1): подання, що прийшло під час переносу версії, не
/// проходить за номером аркуша, якого після переносу вже немає.
/// </summary>
/// <remarks>
/// ⛔ Що було. Подання перевіряло склад документа (<c>HasSheetAsync</c>) ДО своєї
/// транзакції, а блокування структури брало без звірки. Перенос, що зафіксувався
/// між цими кроками, перенумеровував аркуші документа; подання далі будувало
/// валідацію за новою версією, де старого аркуша немає (жодної таблиці — жодної
/// помилки), і створювало <c>ApprovalState</c> на неіснуючий аркуш.
///
/// ⚠ Як відтворено: той самий прийом, що й для PATCH (<c>DocumentVersionMigrationTests.Race</c>) —
/// перенос стоїть перед <c>ApplyAsync</c> з винятковим блокуванням структури, друге
/// з'єднання шле подання, тест чекає, доки воно стане в чергу на блокування, і
/// відпускає перенос.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [Trait("Finding", "L6-02")]
    public async Task Подання_під_час_переносу_версії_не_проходить_за_аркушем_якого_вже_немає()
    {
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, AtyrauZone));
        // ⚠ Грант — дефолтний Manage: його вимагає перенос (AN-31), і він же покриває Submit.
        var s = await ArrangeAsync(
            Target.DropsC3AddsC4AndRow, extraPermission: "Document.View",
            periodKey: (siteToday.Year * 100) + siteToday.Month).ConfigureAwait(true);
        await OpenForEditingAsync(s).ConfigureAwait(true);
        var pause = new ApplyPause();

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IDocumentVersionMigrationStore>(sp => new PausingMigrationStore(
                new DocumentVersionMigrationStore(sp.GetRequiredService<EcrDbContext>()), pause))));

        using var migrator = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);
        using var submitter = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        // ── З'єднання 1: перенос доходить до `ApplyAsync` і стоїть ──
        var migration = PostAsync(migrator, s, "Safe", dryRun: false);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);

        // ── З'єднання 2: подання аркуша старої версії ──
        var submit = submitter.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/submit", UriKind.Relative),
            new { sheetDefId = s.Doc.SheetDefId, periodKey = s.Doc.PeriodKey.Value, acknowledgeWarnings = true });

        var blocked = await WaitUntilBlockedOrDoneAsync(submit).ConfigureAwait(true);
        pause.Release.TrySetResult();

        using var migrated = await migration.ConfigureAwait(true);
        var migratedBody = await migrated.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(migrated.StatusCode == HttpStatusCode.OK, $"Перенос: {migrated.StatusCode}: {migratedBody} {baseApp.ErrorsText}");

        using var submitted = await submit.ConfigureAwait(true);
        var submittedBody = await submitted.Content.ReadAsStringAsync().ConfigureAwait(true);

        // ⛔ Предмет тесту: аркуша, який подавали, після переносу немає — подання
        // мусить отримати «структуру змінено», а не пройти чи впасти інакше.
        Assert.True(
            submitted.StatusCode == HttpStatusCode.Conflict,
            $"Подання під час переносу відповіло {submitted.StatusCode}: {submittedBody}");
        var problem = JsonDocument.Parse(submittedBody).RootElement;
        Assert.Equal("ECR-DOC-4091", problem.GetProperty("errorCode").GetString());
        Assert.Equal(DocumentStructure.StructureChangedKey, problem.GetProperty("messageKey").GetString());

        // І стану погодження на аркуш, якого немає, не з'явилось.
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            Assert.False(await db.ApprovalStates.AnyAsync(a =>
                a.DocumentId == s.Doc.DocumentId && a.SheetDefId == s.Doc.SheetDefId).ConfigureAwait(true));
        }

        // ⚠ Відмова саме тому, що подання ЧЕКАЛО на перенос, — а не через збіг часу.
        Assert.True(blocked, "Подання не стало в чергу на блокування структури документа.");
    }
}
