// tests/Ecr.Api.Tests/PublishBindingRaceApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// HSE301 C5b: публікація шаблону й паралельна відв'язка методології від
/// колонки тієї самої версії.
/// </summary>
/// <remarks>
/// ⛔ Публікація читає активні прив'язки (<c>ListBoundColumnIdsAsync</c>,
/// перевірка <c>ECR-TMPL-4226</c>) під блоком рядка версії, а обробник
/// прив'язок блоку не брав: відв'язка комітилася посеред публікації, і
/// перевірка бачила стан, якого на момент коміту вже не було.
///
/// ⚠ Той самий прийом, що в <see cref="PublishDraftEditRaceApiTests"/>:
/// шлюз над справжнім сховищем тримає публікацію ПІСЛЯ блоку й читання
/// знімка. Відв'язка під блоком чекає коміту публікації; без блоку вона
/// комітиться, поки публікація стоїть.
///
/// ⚠ Після коміту публікації відв'язка бачить уже <c>Published</c> і, оскільки це
/// останнє джерело <c>Formula</c>-колонки, отримує <c>409 ECR-TMPL-4091</c>
/// (<c>D-215</c>); прив'язка лишається активною. Без блоку та сама відв'язка
/// комітилася б посеред публікації ще на чернетці, а публікація відхилялася б
/// <c>4226</c> на стані, що змінився під нею.
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishBindingRaceApiTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Скільки відв'язка має простояти, поки публікація тримає блок.</summary>
    /// <remarks>
    /// Без блоку відв'язка — кілька запитів до бази і завершується за частки
    /// секунди; під блоком вона не може завершитися, доки шлюз не відпустить
    /// публікацію, тож зелений шлях від величини вікна не залежить.
    /// </remarks>
    private static readonly TimeSpan HoldWindow = TimeSpan.FromSeconds(5);

    /// <remarks>
    /// Мутація: прибрати <c>LockVersionForUpdateAsync</c> у
    /// <c>SaveCalculationBindingHandler.HandleAsync</c> — відв'язка завершується
    /// у вікні, поки публікація тримає блок, тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відв_язка_під_час_публікації_чекає_її_коміту_і_бачить_уже_опубліковану_версію()
    {
        var draft = await ArrangeAsync();
        var gate = new PublishDraftEditRaceApiTests.FirstReadGate();

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped(sp => PublishDraftEditRaceApiTests.GatedTemplateVersionStore.Create(
                new TemplateVersionStore(sp.GetRequiredService<EcrDbContext>()), gate))));
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit", "Template.Publish", "Calculation.EditRule");

        gate.Arm();

        var publish = client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{draft.VersionId}/publish", UriKind.Relative),
            new { reason = "C5b race" });

        // Публікація вже тримає блок версії й прочитала знімок — тепер відв'язка.
        var arrived = await Task.WhenAny(gate.FirstArrived, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(arrived == gate.FirstArrived, $"Публікація не дійшла до читання знімка.\n{baseApp.ErrorsText}");

        var unbind = client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{draft.MethodologyId}/bindings/{draft.ColumnId}/OUT1", UriKind.Relative),
            new { matchJson = "{}", isActive = false });

        var early = await Task.WhenAny(unbind, Task.Delay(HoldWindow));
        var finishedUnderPublish = early == unbind;

        // Відпускаємо публікацію (друге «прибуття» шлюзу) і чекаємо обох.
        await gate.PassAsync();
        await Task.WhenAll(publish, unbind);

        using var published = await publish;
        using var unbound = await unbind;
        var publishBody = await published.Content.ReadAsStringAsync();
        var unbindBody = await unbound.Content.ReadAsStringAsync();

        // ⛔ Головне: відв'язка не завершилася, поки публікація тримала блок версії.
        Assert.False(
            finishedUnderPublish,
            $"Відв'язка завершилася посеред публікації ({(int)unbound.StatusCode} {unbindBody}): "
            + "блок рядка версії не взято.");

        Assert.True(
            published.IsSuccessStatusCode,
            $"Публікація: {(int)published.StatusCode} {publishBody}\n{baseApp.ErrorsText}");

        // Після коміту публікації відв'язка бачить Published: це останнє джерело
        // Formula-колонки, тож D-215 відмовляє, а не лишає колонку без джерела.
        Assert.True(
            unbound.StatusCode == HttpStatusCode.Conflict,
            $"{(int)unbound.StatusCode}: {unbindBody}\n{baseApp.ErrorsText}");
        using (var problem = JsonDocument.Parse(unbindBody))
        {
            Assert.Equal("ECR-TMPL-4091", problem.RootElement.GetProperty("errorCode").GetString());
        }

        await using var db = Context();
        var status = await db.TemplateVersions
            .Where(v => v.Id == draft.VersionId).Select(v => v.Status).SingleAsync();
        var bindingActive = await db.CalculationBindings
            .Where(b => b.ColumnDefId == draft.ColumnId && b.MethodologyId == draft.MethodologyId)
            .Select(b => b.IsActive).SingleAsync();

        Assert.Equal(TemplateVersionStatus.Published, status);
        Assert.True(bindingActive, "Відмовлена відв'язка мала відкотитися цілком.");
    }

    /// <summary>
    /// Чернетка з колонкою типу <c>Formula</c> без формули шаблону, чиє єдине
    /// джерело — активна прив'язка методології: саме її публікація й перевіряє.
    /// </summary>
    private async Task<Draft> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Context();

        var template = new Template(EcrCode.Create($"C5B{tag}"), Name("C5b"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var column = new ColumnDef(table.Id, EcrCode.Create("CF"), Name("CF"), 1, CellDataType.Formula);
        db.ColumnDefs.Add(column);
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item));

        var methodology = new Methodology(EcrCode.Create($"M{tag}"), Name("M"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        // ⚠ D-R2: публікація шаблону відхиляє активну прив'язку до методології без
        // опублікованої версії, тож методологія стенду має бодай одну опубліковану.
        var methodologyVersion = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 9, Now);
        methodology.AddVersion(methodologyVersion);
        methodology.PublishVersion(
            methodologyVersion,
            publishedByUserId: 10,
            changeReason: "D-R2",
            effectiveFrom: new DateOnly(2026, 1, 1),
            testsPassed: true,
            utcNow: Now);
        await db.SaveChangesAsync();

        db.CalculationBindings.Add(new CalculationBinding(table.Id, column.Id, methodology.Id, "OUT1", "{}"));
        await db.SaveChangesAsync();

        return new Draft(version.Id, column.Id, methodology.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Draft(int VersionId, int ColumnId, int MethodologyId);
}
