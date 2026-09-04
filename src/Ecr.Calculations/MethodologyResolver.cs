using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>
/// Підбирає версію методології, чинну на дату періоду, і зіставляє її з
/// рядками документа за правилами (ФВ-13.3).
/// </summary>
public sealed class MethodologyResolver(Ecr.Infrastructure.Persistence.EcrDbContext db)
{
    /// <summary>Знаходить чинну версію методології на дату.</summary>
    public Task<MethodologyDescriptor?> ResolveVersionAsync(int methodologyId, DateOnly onDate, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: вибрати Published-версію з максимальним EffectiveFrom <= onDate. " +
            "Якщо жодної — це не порожній результат, а помилка конфігурації: " +
            "методологія прив'язана, але не має чинної версії.");

    /// <summary>Визначає, які рядки документа обробляє методологія.</summary>
    public Task<IReadOnlyList<string>> MatchRowsAsync(
        int methodologyVersionId, long tableInstanceId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: застосувати MethodologyRule.MatchJson (предикат по колонках рядка) у порядку " +
            "Priority; перший збіг виграє. Матриця покриття (ФВ-13.4) будується на тому самому " +
            "механізмі — вона показує, які рядки не закрилися жодним правилом.");
}
