// src/Ecr.Application/Ports/IFlertSyncStatusReader.cs
namespace Ecr.Application.Ports;

/// <summary>Задача разового імпорту складу газу FLERT → ECR (<c>D-476</c>, D-NEW): маркер для постановки «Імпортувати з FLERT».</summary>
public interface IFlertCompositionSyncJob : IBackgroundJob;

/// <summary>Стан імпорту FLERT для сторінки «Склад газу» (<c>D-476</c>, D-NEW): останній прогін.</summary>
public interface IFlertSyncStatusReader
{
    /// <summary>Читає стан; прогонів ще не було — <see cref="FlertSyncStatusDto.LastRun"/> = <c>null</c>.</summary>
    /// <param name="ct">Скасування.</param>
    public Task<FlertSyncStatusDto> ReadAsync(CancellationToken ct);
}

/// <summary>Стан імпорту FLERT.</summary>
/// <param name="LastRun">Останній прогін; <c>null</c> — не було жодного.</param>
public sealed record FlertSyncStatusDto(FlertSyncRunDto? LastRun);

/// <summary>Один прогін синку FLERT (<c>itg.MaintenanceRun</c> <c>flert-composition-sync</c>).</summary>
/// <param name="StartedAt">Початок, UTC.</param>
/// <param name="FinishedAt">Кінець, UTC; <c>null</c> — ще виконується.</param>
/// <param name="Status"><c>Running</c>, <c>Succeeded</c>, <c>Degraded</c> або <c>Failed</c>.</param>
/// <param name="Reason">Чому знімок не застосовано (<c>disabled</c>, <c>emptyResult</c>, <c>deactivationLimit</c>…); <c>null</c> — застосовано.</param>
/// <param name="SourceRows">Рядків прочитано з FLERT.</param>
/// <param name="Added">Створено записів (усі довідники дзеркала).</param>
/// <param name="Updated">Змінено записів.</param>
/// <param name="Deactivated">Вимкнено записів.</param>
/// <param name="Reactivated">Увімкнено знову.</param>
/// <param name="IssueCount">Зауважень прогону.</param>
/// <param name="Message">Перше зауваження рівня «помилка» або текст збою; <c>null</c> — немає.</param>
public sealed record FlertSyncRunDto(
    DateTime StartedAt,
    DateTime? FinishedAt,
    string Status,
    string? Reason,
    int SourceRows,
    int Added,
    int Updated,
    int Deactivated,
    int Reactivated,
    int IssueCount,
    string? Message);
