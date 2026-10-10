// tests/Ecr.Api.Tests/FailingSecurityEventAuditWriter.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Api.Tests;

/// <summary>
/// Справжній <see cref="AuditWriter"/>, що падає лише на запису ПОДІЇ БЕЗПЕКИ заданого типу (F2-03).
/// </summary>
/// <remarks>
/// ⛔ Предмет: результатна подія журналу (хто видалив/переніс/змінив ключ) пишеться В ТІЙ САМІЙ
/// транзакції, що й зміна. Збій запису тоді відкочує зміну; запис ПІСЛЯ коміту лишав зміну в базі
/// без сліду (або, навпаки, слід без зміни). Інші виклики (комірки, структура, інші типи подій)
/// проходять до справжнього писача — тож відкат перевіряється справжнім відкатом СУБД.
/// </remarks>
internal sealed class FailingSecurityEventAuditWriter(IAuditWriter inner, string failingEventType) : IAuditWriter
{
    /// <summary>Текст внесеного збою.</summary>
    public const string Marker = "збій запису події безпеки (тест F2-03)";

    /// <summary>Фабрика застосунку, у якій подія типу <paramref name="eventType"/> не записується.</summary>
    /// <param name="baseApp">Базовий стенд.</param>
    /// <param name="eventType">Тип події безпеки.</param>
    /// <returns>Похідна фабрика; кличе її власник (<c>using</c>).</returns>
    public static WebApplicationFactory<Program> Install(EcrApiFactory baseApp, string eventType)
    {
        ArgumentNullException.ThrowIfNull(baseApp);

        return baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IAuditWriter>(sp => new FailingSecurityEventAuditWriter(
                ActivatorUtilities.CreateInstance<AuditWriter>(sp), eventType))));
    }

    public Task WriteCellChangesAsync(IReadOnlyList<CellChangeRecord> changes, CancellationToken ct)
        => inner.WriteCellChangesAsync(changes, ct);

    public Task WriteStructureChangeAsync(StructureChangeRecord change, CancellationToken ct)
        => inner.WriteStructureChangeAsync(change, ct);

    public Task WriteSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        return string.Equals(evt.EventType, failingEventType, StringComparison.Ordinal)
            ? throw new InvalidOperationException(Marker)
            : inner.WriteSecurityEventAsync(evt, ct);
    }

    public Task WriteSecurityEventsAsync(IReadOnlyList<SecurityEventRecord> events, CancellationToken ct)
        => inner.WriteSecurityEventsAsync(events, ct);

    public Task WriteIndependentSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct)
        => inner.WriteIndependentSecurityEventAsync(evt, ct);

    public Task WritePublicationEventAsync(PublicationEventRecord evt, CancellationToken ct)
        => inner.WritePublicationEventAsync(evt, ct);
}
