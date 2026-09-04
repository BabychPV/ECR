// src/Ecr.Application/Calculations/RunCalculationHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Calculations;

/// <summary>
/// Прогін розрахунку. **Бюджет повного року — 10 хвилин** (ПРД-13, D-63)
/// проти 20 у чинній системі.
/// </summary>
/// <remarks>
/// Це вимога, а не результат заміру: профіль по модулях (`J-1`) показує,
/// звідки взяти різницю. Тому <c>ModulesProfileJson</c> заповнюється завжди,
/// а не лише в діагностичному режимі.
/// </remarks>
public sealed class RunCalculationHandler(
    ICalculationModule module,
    IUnitOfWork uow,
    IBackgroundJobScheduler jobs,
    ICurrentUser currentUser,
    IClock clock)
{
    public Task<long> HandleAsync(int projectId, int? periodKey, int? triggeredByUserId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) ⚠ ЗАКРИТІ ПЕРІОДИ автоматично не перераховувати НІКОЛИ " +
            "   (ФВ-9.7): без окремого погодження → ECR-CALC-4221;\n" +
            "2) ⚠ зріз із IsSubmitted = 1 не перераховується взагалі (ФВ-9.17): " +
            "   потреба змінити подану цифру закривається Reopen, а не перерахунком;\n" +
            "3) версія методології — за датою періоду, не за 'поточною' (ФВ-9.3);\n" +
            "4) порядок формул — топологічний, узятий із публікації, не будувати " +
            "   граф щоразу (ФВ-9.4);\n" +
            "5) NumericMode версії задає МОМЕНТ округлення: Legacy — після кожної " +
            "   операції, Strict — на виході (ФВ-9.16a);\n" +
            "6) паралелізм по незалежних гілках графа; ізоляція від інтерактивного " +
            "   піку обов'язкова — 10 хвилин перерахунку не мають з'їсти p95 операторів;\n" +
            "7) заповнити ModulesProfileJson;\n" +
            "8) перемикання IsCurrent — ОДНА транзакція (ФВ-9.11).");
}
