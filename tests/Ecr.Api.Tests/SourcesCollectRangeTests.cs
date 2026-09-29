// tests/Ecr.Api.Tests/SourcesCollectRangeTests.cs
using System.Text.Json;
using Ecr.Api.Controllers;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Аудит 2026-09-28, B7: ручний збір (<c>POST /api/v1/sources/{id}/collect</c>)
/// ставить задачу з проміжком у UTC, хоч би з яким <see cref="DateTime.Kind"/>
/// прийшли межі, і відмовляє на порожньому чи перевернутому проміжку.
/// </summary>
/// <remarks>
/// ⚠ Задача збору несе ОДИН проміжок (<see cref="CollectionTask"/>): з нього
/// <c>CollectionRunner</c> і запитує джерело, і пише покриття. Тож проміжок у
/// задачі в UTC — це і є «запит і покриття за тим самим UTC-проміжком»: до фіксу
/// PiWebApi перераховував <c>Local</c>/<c>Unspecified</c> у UTC за поясом
/// сервера (<c>ToUniversalTime</c>), а покриття лягало за сирими значеннями.
/// <para>
/// Тіло запиту розбирається тим самим <see cref="JsonSerializerDefaults.Web"/>,
/// що й у ASP.NET Core, — щоб <c>Kind</c> був саме тим, який дає справжній
/// конвеєр, а не вигаданим у тесті.
/// </para>
/// <para>
/// Мутація: у <c>SourcesController.Collect</c> передавати в обробник
/// <c>request.FromUtc</c>/<c>request.ToUtc</c> без нормалізації і прибрати
/// перевірку <c>from &gt;= to</c> → червоні всі три тести.
/// </para>
/// </remarks>
public sealed class SourcesCollectRangeTests
{
    private const int EntityId = 41;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-11.3")]
    public async Task Межі_без_зони_ставляться_в_задачу_як_UTC_без_зсуву()
    {
        var request = Parse("""{ "fromUtc": "2026-08-01T00:00:00", "toUtc": "2026-08-02T00:00:00" }""");
        Assert.Equal(DateTimeKind.Unspecified, request.FromUtc.Kind);

        var task = await CollectAsync(request);

        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), task.FromUtc);
        Assert.Equal(new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc), task.ToUtc);
        Assert.Equal(DateTimeKind.Utc, task.FromUtc!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, task.ToUtc!.Value.Kind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-11.3")]
    public async Task Межі_зі_зміщенням_ставляться_в_задачу_як_той_самий_момент_у_UTC()
    {
        var request = Parse("""{ "fromUtc": "2026-08-01T05:00:00+05:00", "toUtc": "2026-08-02T05:00:00+05:00" }""");
        Assert.Equal(DateTimeKind.Local, request.FromUtc.Kind);

        var task = await CollectAsync(request);

        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), task.FromUtc);
        Assert.Equal(new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc), task.ToUtc);
        Assert.Equal(DateTimeKind.Utc, task.FromUtc!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, task.ToUtc!.Value.Kind);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-11.3")]
    [InlineData("2026-08-02T00:00:00Z", "2026-08-01T00:00:00Z")]
    [InlineData("2026-08-01T00:00:00Z", "2026-08-01T00:00:00Z")]
    // Той самий момент різними записами: 05:00+05:00 = 00:00Z — проміжок порожній.
    [InlineData("2026-08-01T05:00:00+05:00", "2026-08-01T00:00:00")]
    public async Task Порожній_чи_перевернутий_проміжок_422_і_задачі_немає(string from, string to)
    {
        var jobs = Jobs();
        var controller = Controller(jobs);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => controller.Collect(EntityId, Parse($$"""{ "fromUtc": "{{from}}", "toUtc": "{{to}}" }"""), CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.collectionRunRange", error.Details!["messageKey"]);
        Assert.Empty(jobs.ReceivedCalls());
    }

    private static CollectRequest Parse(string json)
        => JsonSerializer.Deserialize<CollectRequest>(json, Web)!;

    private static async Task<CollectionTask> CollectAsync(CollectRequest request)
    {
        var jobs = Jobs();

        var result = await Controller(jobs).Collect(EntityId, request, CancellationToken.None);
        Assert.IsType<AcceptedResult>(result);

        var call = Assert.Single(jobs.ReceivedCalls());
        return Assert.IsType<CollectionTask>(call.GetArguments()[0]);
    }

    private static IBackgroundJobScheduler Jobs()
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueAsync<ICollectionJob>(Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("collect-job");
        return jobs;
    }

    /// <summary>Справжній <see cref="CollectFromSourceHandler"/>; підмінено сховище, чергу й доступ.</summary>
    private static SourcesController Controller(IBackgroundJobScheduler jobs)
    {
        var store = Substitute.For<ICollectionStore>();
        store.FindSourceEntityAsync(EntityId, Arg.Any<CancellationToken>())
             .Returns(new SourceEntity(1, "ENT-B7", RegistrySourceKind.External));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(7);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(7, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 7 }.Permission(CollectFromSourceHandler.Permission).Build());

        return new SourcesController(
            list: null!,
            new CollectFromSourceHandler(store, jobs, access, user),
            preview: null!,
            create: null!,
            bindRegistry: null!);
    }
}
