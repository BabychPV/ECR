// tests/Ecr.Application.Tests/Templates/PublishTemplateVersionConcurrencyTests.cs
using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// ФВ-2.9: одночасна публікація однієї версії двома адміністраторами —
/// рівно одна проходить, друга отримує <c>ECR-TMPL-0409</c> (409) і не
/// дописує нічого в уже опубліковане.
/// </summary>
/// <remarks>
/// ⛔ Механізм — блок рядка версії (<c>UPDLOCK, HOLDLOCK</c>) на початку
/// транзакції публікації, а не <c>RowVersion</c> (див. REQ-CLOSURE №17).
/// Тест механізму не називає: він перевіряє СПОСТЕРЕЖУВАНИЙ результат.
///
/// ⚠ Справжня СУБД і два незалежні контексти зі своїми з'єднаннями. Щоб
/// перегони були детермінованими, а не «пощастило», обгортка над справжнім
/// сховищем ПІСЛЯ взяття блоку тримає його 400 мс: без блоку в обробнику
/// обидві публікації за цей час прочитали б <c>Draft</c> і обидві б
/// пройшли. Обгортка нічого не підміняє — лише дає другому виклику час
/// дійти до блоку.
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishTemplateVersionConcurrencyTests(SqlServerFixture sql)
{
    private const int Actor = 9;
    private static readonly DateTime Now = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.9")]
    public async Task Дві_одночасні_публікації_однієї_версії_рівно_одна_успішна_друга_409()
    {
        var versionId = await ArrangeVersionAsync();

        using var start = new ManualResetEventSlim(false);

        Task<Exception?> Run() => Task.Run(async () =>
        {
            start.Wait();
            await using var db = Context();
            using var memory = new MemoryCache(new MemoryCacheOptions());
            try
            {
                await Publisher(db, memory).PublishAsync(versionId, Actor, "ФВ-2.9", CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        var first = Run();
        var second = Run();
        start.Set();
        var outcomes = await Task.WhenAll(first, second);

        var failures = outcomes.Where(o => o is not null).ToList();
        Assert.Single(failures);

        var rejected = Assert.IsType<DomainException>(failures[0]);
        Assert.Equal("ECR-TMPL-0409", rejected.ErrorCode);

        // Рівно одна подія публікації і стан Published: друга спроба нічого
        // не дописала.
        Assert.Equal(1, await PublicationEventsAsync(versionId));

        await using var check = Context();
        Assert.Equal(
            TemplateVersionStatus.Published,
            await check.TemplateVersions.Where(v => v.Id == versionId).Select(v => v.Status).SingleAsync());
    }

    private static PublishTemplateVersionHandler Publisher(EcrDbContext db, MemoryCache memory)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Actor);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }
                .Permission(PublishTemplateVersionHandler.Permission)
                .Permission("Template.View")
                .Build());

        return new PublishTemplateVersionHandler(
            new Repository<TemplateVersion, int>(db),
            HoldingLock(new TemplateVersionStore(db)),
            new RealFormulaEngine(),
            new CalculationBindingStore(db),
            new MetadataCache(memory, db),
            new UnitCatalog(db),
            access,
            user,
            new AuditWriter(db),
            new UnitOfWork(db),
            new TestClock(Now),
            new Ecr.Infrastructure.Reporting.ReportViewGenerator(db));
    }

    /// <summary>
    /// Справжнє сховище, але після взяття блоку версії тримає його 400 мс — час,
    /// за який паралельна публікація без блоку встигла б прочитати чернетку.
    /// </summary>
    private static ITemplateVersionStore HoldingLock(TemplateVersionStore real)
    {
        var spy = Substitute.For<ITemplateVersionStore>();

        spy.LockVersionForUpdateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var status = await real.LockVersionForUpdateAsync(call.ArgAt<int>(0), call.ArgAt<CancellationToken>(1));
                await Task.Delay(400);
                return status;
            });
        spy.GetWithStructureAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => real.GetWithStructureAsync(call.ArgAt<int>(0), call.ArgAt<CancellationToken>(1)));
        spy.ReplaceFormulaDependenciesAsync(
                Arg.Any<int>(), Arg.Any<IReadOnlyList<FormulaDependency>>(), Arg.Any<CancellationToken>())
            .Returns(call => real.ReplaceFormulaDependenciesAsync(
                call.ArgAt<int>(0), call.ArgAt<IReadOnlyList<FormulaDependency>>(1), call.ArgAt<CancellationToken>(2)));

        return spy;
    }

    private async Task<int> ArrangeVersionAsync()
    {
        await using var db = Context();

        var template = new Template(EcrCode.Create($"CCT{_tag}"), Text("Concurrency template"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        // Аркуш без таблиць — найменша структура, яку публікація пропускає.
        db.SheetDefs.Add(new SheetDef(version.Id, EcrCode.Create($"S{_tag}"), Text("Sheet"), 1));
        await db.SaveChangesAsync();

        return version.Id;
    }

    private async Task<int> PublicationEventsAsync(int versionId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM aud.PublicationEvent WHERE EntityType = N'TemplateVersion' AND EntityId = @id;";
        command.Parameters.AddWithValue("@id", versionId);

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(30))
            .Options);
}
