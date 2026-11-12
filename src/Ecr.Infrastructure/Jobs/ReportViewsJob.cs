// src/Ecr.Infrastructure/Jobs/ReportViewsJob.cs
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ P-4b (D-479): перегенерація вʼюх SSRS <c>rpt.v_*</c> шаблону у фоні після публікації його версії —
/// тим самим <see cref="IReportViewGenerator"/> (ті самі процедури), що доти кликала публікація в HTTP-запиті.
/// </summary>
/// <remarks>
/// ⛔ Ставиться ЗЛИТТЯМ на шаблон (<see cref="ReportViewsTarget"/>), тож payload визначається ціллю: задача бере ВСІ
/// опубліковані й застарілі версії шаблону, а не версію, що її поставила, — злиття двох публікацій не губить вʼюх жодної.
/// ⚠ Блоку версії задача не бере й нічого не пише в <c>cfg.*</c>: публікація, правка чернетки чи друга публікація
/// того самого шаблону під час прогону не чекають на неї.
/// ⚠ Збій видно двічі: генератор фіксує причину для картки <c>reportviews</c> (<see cref="IReportViewStatus"/>), а
/// задача кидає далі — у черзі вона <c>Failed</c> з причиною, і її можна повторити («Restart»). Картка «оновлюються»
/// знімається в будь-якому разі (<see cref="IReportViewStatus.Settled"/>) — якщо за прогін не з'явилась новіша
/// позначка (тоді чекає злитий повтор, і знімає вже він).
/// </remarks>
public sealed class ReportViewsJob(
    EcrDbContext db,
    IReportViewGenerator generator,
    IReportViewStatus? status = null) : IReportViewsJob
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var task = Parse(payload);

        // ⚠ Покоління позначки — ДО запиту версій: публікація, що закомітилась після цього рядка, ставить новіше
        // покоління і злитий повтор, тож Settled нижче картку «оновлюються» не зніме, доки повтор не відпрацює.
        var generation = status?.PendingGeneration(task.TemplateId) ?? 0;
        try
        {
            var versionIds = await db.TemplateVersions
                .AsNoTracking()
                // ⚠ Published і Deprecated — ті самі статуси, що бере процедура (05-rpt-views.sql: Status IN (1, 2), «застаріла
                // версія вʼюх НЕ втрачає»): версія, яку наступна публікація встигла зробити Deprecated до старту задачі, свої
                // вʼюхи однаково отримує. Процедура ідемпотентна (однаковий текст — без ALTER), тож повтор для старих версій дешевий.
                .Where(v => v.TemplateId == task.TemplateId
                    && (v.Status == TemplateVersionStatus.Published || v.Status == TemplateVersionStatus.Deprecated))
                .OrderBy(v => v.Id)
                .Select(v => v.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            // ⚠ Збій однієї версії не відміняє решти (одна задовга таблиця не лишає без вʼюх інші версії), але перший
            // кидається наприкінці — задача стає Failed, а не тихо Succeeded.
            Exception? first = null;
            for (var i = 0; i < versionIds.Count; i++)
            {
                await progress
                    .ReportAsync(i * 100 / versionIds.Count, $"template {task.TemplateId}, version {versionIds[i]}", ct)
                    .ConfigureAwait(false);
                try
                {
                    await generator.GenerateAsync(versionIds[i], ct).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Решта версій генерується далі; перша помилка кидається після циклу.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    first ??= ex;
                }
            }

            if (first is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
            }

            await progress.ReportAsync(100, null, ct).ConfigureAwait(false);
        }
        finally
        {
            status?.Settled(task.TemplateId, generation);
        }
    }

    /// <summary>Розбирає завдання черги.</summary>
    internal static ReportViewsTask Parse(object? payload)
    {
        if (payload is ReportViewsTask typed)
        {
            return typed;
        }

        var json = payload as string ?? System.Text.Json.JsonSerializer.Serialize(payload);

        return System.Text.Json.JsonSerializer.Deserialize<ReportViewsTask>(json, Options) is { TemplateId: > 0 } task
            ? task
            : throw new InvalidOperationException("Завдання перегенерації вʼюх не розбирається: немає шаблону.");
    }
}
