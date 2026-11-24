// tests/Ecr.Api.Tests/DocumentRecalculationWait.cs
using Ecr.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Очікування, доки власний перерахунок документа (і каскад до нього) закінчиться — перед поданням чи затвердженням у
/// наскрізних тестах, що щойно писали в аркуш (PATCH комірок, застосування імпорту).
/// </summary>
/// <remarks>
/// ⛔ Подання й затвердження свідомо відмовляють <c>422 ECR-SUB-4221</c>, поки жива задача перерахунку чи формул документа
/// (b11, <c>SourceCascadePendingCheck</c>, ключ <c>ownRecalculationPending</c>). Кожен PATCH ставить таку задачу у
/// фоновий воркер хоста тесту, тож «PATCH → одразу submit/approve» — гонитва з цим воротами: під навантаженням CI
/// задача ще жива, і тест червоніє, хоча продукт поводиться правильно (CI-прогін 45442024228, api-8).
/// <para>
/// Чекаємо на СТАН тими самими воротами, що їх питає продукт (<see cref="ISourceCascadePendingGate"/>): не сон на
/// вгад і не повтор на 422. Межа (3 хв, як у <see cref="JobQueueWait"/>) — лише запобіжник від зависання; ворота самі
/// не послаблюються — поведінку відмови тримають <c>SubmitApproveCascadePendingHttpTests</c> і
/// <c>WorkflowTransactionTests.CascadePending</c>.
/// </para>
/// </remarks>
internal static class DocumentRecalculationWait
{
    /// <summary>Чекає, доки ворота перерахунку документа за період не скажуть «нічого не чекає».</summary>
    /// <param name="app">Хост тесту (його воркер виконує задачі).</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період аркуша.</param>
    /// <returns>Завдання.</returns>
    public static async Task UntilSettledAsync(EcrApiFactory app, long documentId, int periodKey)
    {
        ArgumentNullException.ThrowIfNull(app);
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            CascadePendingReason reason;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                reason = await scope.ServiceProvider.GetRequiredService<ISourceCascadePendingGate>()
                    .GetPendingReasonAsync(documentId, periodKey, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (reason == CascadePendingReason.None)
            {
                return;
            }

            Assert.True(
                DateTime.UtcNow < deadline,
                $"перерахунок документа {documentId} за {periodKey} не завершився за 3 хв ({reason})\n{app.ErrorsText}");
            await Task.Delay(100).ConfigureAwait(false);
        }
    }
}
