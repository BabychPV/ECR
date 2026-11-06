// tests/Ecr.Application.Tests/Sources/SourceEventMapDefaultScheduleTests.cs
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Q-2 (людина 06.11: частота синку EventFrame факела — «параметр налаштування в UI», типово щогодини): перший мапінг
/// подій сутності без розкладу заводить щогодинний розклад у тій самій транзакції і ставить його в планувальник.
/// </summary>
/// <remarks>
/// Мутація: прибрати виклик <c>AddDefaultScheduleAsync</c> у <c>CreateSourceEventMapHandler</c> — червоніє перший тест.
/// </remarks>
public sealed class SourceEventMapDefaultScheduleTests : SourceEventMapTestBase
{
    private readonly ICollectionScheduleStore _schedules = Substitute.For<ICollectionScheduleStore>();
    private readonly IBackgroundJobScheduler _scheduler = Substitute.For<IBackgroundJobScheduler>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "Q-2")]
    public async Task Перший_мапінг_без_розкладу_заводить_щогодинний_і_ставить_у_планувальник()
    {
        Scheduling("Flare_Auto", scheduleId: null);

        await Create().HandleAsync(Command(), CancellationToken.None);

        _schedules.Received(1).Add(Arg.Is<CollectionSchedule>(s =>
            s.SourceEntityId == EntityId && s.CronExpression == "0 0 * * * ?" && s.IsEnabled));
        await _scheduler.Received(1).ScheduleAsync<ICollectionJob>("0 0 * * * ?", Arg.Any<object?>(), Arg.Any<CancellationToken>());
        await Audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.Operation == CreateCollectionScheduleHandler.AuditOperation),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Flare_Auto", 3, RegistrySourceKind.External)] // розклад уже є — частоту людини не чіпаємо
    [InlineData("Flare_Auto_Day", null, RegistrySourceKind.External)] // Q-1: копія `_Day` не синхронізується
    [InlineData("Flare_Auto", null, RegistrySourceKind.Local)] // власна форма ECR — розклад заборонено (ФВ-12.8)
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "Q-2")]
    public async Task Розклад_не_заводиться_коли_він_є_для_копії_Day_і_для_власної_форми(
        string code, int? scheduleId, RegistrySourceKind kind)
    {
        Scheduling(code, scheduleId, kind);

        await Create().HandleAsync(Command(), CancellationToken.None);

        _schedules.DidNotReceiveWithAnyArgs().Add(default!);
        await _scheduler.DidNotReceiveWithAnyArgs().ScheduleAsync<ICollectionJob>(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "Q-2")]
    public async Task Відмова_планувальника_не_скасовує_мапінг_розклад_лишається_з_позначкою()
    {
        Scheduling("Flare_Auto", scheduleId: null);
        CollectionSchedule? added = null;
        _schedules.When(s => s.Add(Arg.Any<CollectionSchedule>())).Do(c => added = c.Arg<CollectionSchedule>());
        _scheduler.ScheduleAsync<ICollectionJob>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("quartz down"));

        var dto = await Create().HandleAsync(Command(), CancellationToken.None);

        Assert.Equal(77, dto.Id);
        Assert.NotNull(added);
        Assert.NotNull(added!.LastError);
    }

    private void Scheduling(string code, int? scheduleId, RegistrySourceKind kind = RegistrySourceKind.External)
        => _schedules.FindSourceEntityAsync(EntityId, Arg.Any<CancellationToken>())
            .Returns(new SourceEntityScheduling(code, null, scheduleId, 1, "PI", kind));

    private CreateSourceEventMapHandler Create()
        => new(Store, Sources, Access, User, Uow, Audit, Clock, groups: null, _schedules, new CollectionScheduleApplier(_scheduler));
}
