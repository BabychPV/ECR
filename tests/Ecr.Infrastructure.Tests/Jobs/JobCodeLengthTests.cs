// tests/Ecr.Infrastructure.Tests/Jobs/JobCodeLengthTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Ім'я КОЖНОГО типу задачі мусить уміщатися в <c>itg.JobProgress.JobCode</c>.
/// </summary>
/// <remarks>
/// ⛔ У стовпець лягає <c>typeof(TJob).FullName</c>
/// (<see cref="QuartzJobScheduler"/>, постановка в чергу і запис прогресу), а
/// стовпець — <c>nvarchar(64)</c>. Задача з довшим іменем або в глибшому
/// просторі імен зриває не власний прогін, а <c>StartAsync</c>: застосунок не
/// підіймається взагалі, ще до першого рядка роботи. Ціна помилки тут —
/// розгортання, тож ловити її мусить тест, а не продакшн.
/// <para>
/// ⚠ Перебираються обидві збірки, де задачі й живуть: маркери
/// (<c>Ecr.Application.Ports</c>, бо <c>Ecr.Application</c> не бачить
/// реалізацій) і самі реалізації (<c>Ecr.Infrastructure.Jobs</c>, там їх
/// реєструє DI). Списком типів сторож не користується навмисно: список
/// поповнюють руками, а забутий рядок — це рівно той дефект, який тут і
/// ловлять.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class JobCodeLengthTests(SqlServerFixture sql)
{
    /// <summary>Усі типи, які можна підставити як <c>TJob</c>.</summary>
    private static List<Type> JobTypes()
        => new[] { typeof(IBackgroundJob).Assembly, typeof(QuartzJobScheduler).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(t => typeof(IBackgroundJob).IsAssignableFrom(t) && t != typeof(IBackgroundJob))
            .OrderByDescending(t => (t.FullName ?? t.Name).Length)
            .ThenBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-12.4")]
    public void Повне_ім_я_кожного_типу_задачі_вміщається_в_JobCode()
    {
        var jobs = JobTypes();

        // ⚠ Сторож, який нічого не знайшов, зелений завжди. Обидві збірки
        // мусять дати типи: маркер із `Ecr.Application` і реалізацію з
        // `Ecr.Infrastructure`.
        Assert.Contains(typeof(IMaterializeCollectedDataJob), jobs);
        Assert.Contains(typeof(MaterializeCollectedDataJob), jobs);

        // ⛔ Літерал 64, а не `GetMaxLength()`: межа тут — вимога стовпця, і
        // вона мусить упасти, якщо стовпець «підвищать» міграцією, не
        // перевіривши, чи справді всі імена змінилися. Що літерал і стовпець —
        // одне число, стереже тест нижче.
        var overflowing = jobs
            .Where(t => (t.FullName ?? t.Name).Length > 64)
            .Select(t => $"{t.FullName} ({(t.FullName ?? t.Name).Length})")
            .ToList();

        var longest = jobs[0].FullName ?? jobs[0].Name;

        // Запас малий, і саме тому сторож існує: тип, чиє ім'я на десяток
        // літер довше за найдовше чинне, уже зриває старт застосунку.
        Assert.True(
            overflowing.Count == 0,
            $"itg.JobProgress.JobCode — nvarchar(64), а ці типи задач у нього не влазять: "
            + $"{string.Join(", ", overflowing)}. Назвіть тип коротше або перенесіть у коротший "
            + "простір імен — піднімати стовпець міграцією означає лишити ту саму пастку далі. "
            + $"Найдовше чинне ім'я — {longest} ({longest.Length}), запас {64 - longest.Length}.");
    }

    /// <summary>
    /// Межа стовпця і межа сторожа — одне й те саме число.
    /// </summary>
    /// <remarks>
    /// ⚠ Той самий прийом, що й у <c>QuartzJobFailureMessageTests</c> для
    /// <c>Message</c>/<c>Error</c>:
    /// сторож вище тримає літерал, а звідки взялося саме це число — видно
    /// лише тут, із моделі EF. Розбіжність («звузили стовпець міграцією») не
    /// ловить ніщо, крім цього твердження.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Межа_стовпця_JobCode_і_межа_сторожа_це_одне_й_те_саме_число()
    {
        using var db = sql.CreateContext();
        var entity = db.Model.FindEntityType(typeof(JobProgress))!;

        Assert.Equal(64, entity.FindProperty(nameof(JobProgress.JobCode))!.GetMaxLength());
    }
}
