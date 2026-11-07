// src/Ecr.Application/Periods/CurrentPeriodSettingHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Application.Periods;

/// <summary>
/// Налаштування «поточний період» проєкту (✎ 2026-11-07 Period clarity, п. c): режим і що з нього виходить.
/// </summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="Mode"><c>Auto</c> — за датою (веде <c>PeriodStateJob</c>); <c>Pinned</c> — закріплено вручну з причиною.</param>
/// <param name="CurrentPeriodId">Чинний поточний період (підказка UI, не правило доступу, D-77); <c>null</c> — немає.</param>
/// <param name="CurrentPeriodKey">Його ключ.</param>
/// <param name="AutomaticPeriodId">Який період був би поточним у режимі <c>Auto</c> зараз; <c>null</c> — жодного.</param>
/// <param name="AutomaticPeriodKey">Його ключ.</param>
/// <param name="PinnedReason">Причина закріплення; лише в <c>Pinned</c>.</param>
/// <param name="ChangedAt">Коли поточний період або режим змінено востаннє, UTC.</param>
public sealed record CurrentPeriodSettingDto(
    int ProjectId,
    CurrentPeriodMode Mode,
    int? CurrentPeriodId,
    int? CurrentPeriodKey,
    int? AutomaticPeriodId,
    int? AutomaticPeriodKey,
    string? PinnedReason,
    DateTime? ChangedAt);

/// <summary>
/// Читання й зміна налаштування «поточний період» у налаштуваннях ПРОЄКТУ (✎ 2026-11-07, квіз людини п. c):
/// поточний період — автоматично за датою; закріплення вручну і «Повернути автоматичний» живуть тут, а не на сторінці
/// періодів. Механізм — той самий <see cref="SetCurrentPeriodHandler"/> (<c>D-77</c>: значення за замовчуванням,
/// ніколи не рішення про доступ).
/// </summary>
public sealed class CurrentPeriodSettingHandlers(
    IPeriodStore periods,
    SetCurrentPeriodHandler set,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    Domain.Services.PeriodStateCalculator calculator)
{
    /// <summary>Право перегляду налаштування: той самий читач, що бачить проєкт і його документи.</summary>
    public const string ViewPermission = "Document.View";

    /// <summary>Право зміни налаштування — те саме, що в <see cref="SetCurrentPeriodHandler.Permission"/>.</summary>
    public const string ConfigurePermission = "Period.Configure";

    /// <summary>Поточне налаштування проєкту.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<CurrentPeriodSettingDto> GetAsync(int projectId, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, ViewPermission, ct)
            .ConfigureAwait(false);

        // ⛔ S17: невидимий проєкт — та сама відповідь, що й неіснуючий.
        Projects.ProjectVisibility.RequireVisible(profile, projectId);
        Security.PermissionCheck.RequireIn(profile, ViewPermission, projectId);

        return await ReadAsync(projectId, ct).ConfigureAwait(false);
    }

    /// <summary>Встановлює режим: <c>Pinned</c> (період + причина) або <c>Auto</c> («Повернути автоматичний»).</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="pinnedPeriodId">Період для <c>Pinned</c>; для <c>Auto</c> ігнорується.</param>
    /// <param name="reason">Причина для <c>Pinned</c> (обов'язкова, аудит).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Право й грант — як у <see cref="SetCurrentPeriodHandler"/> (<c>Period.Configure</c> + Manage на проєкт).
    /// <c>Pinned</c> без періоду — <c>422 ECR-PRD-0422 periodNotInProject</c>.
    /// </remarks>
    public async Task<CurrentPeriodSettingDto> SetAsync(
        int projectId, CurrentPeriodMode mode, int? pinnedPeriodId, string? reason, CancellationToken ct)
    {
        // Перший рубіж — те саме право, що перевіряє `SetCurrentPeriodHandler` (там же видимість S17, грант Manage і
        // право в ЦЬОМУ проєкті, ФВ-6.14); тут — щоб маршрут налаштувань перевіряв своє право сам.
        var profile = await Security.PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, ConfigurePermission, ct)
            .ConfigureAwait(false);
        Projects.ProjectVisibility.RequireVisible(profile, projectId);
        Security.PermissionCheck.RequireIn(profile, ConfigurePermission, projectId);

        await set
            .HandleAsync(projectId, mode == CurrentPeriodMode.Pinned ? pinnedPeriodId ?? 0 : null, reason, ct)
            .ConfigureAwait(false);

        return await ReadAsync(projectId, ct).ConfigureAwait(false);
    }

    private async Task<CurrentPeriodSettingDto> ReadAsync(int projectId, CancellationToken ct)
    {
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw Projects.ProjectVisibility.NotFound(projectId);

        var current = project.Periods.FirstOrDefault(p => p.Id == project.CurrentPeriodId);
        var automatic = calculator.SelectCurrentPeriod(project.Periods);

        return new CurrentPeriodSettingDto(
            project.Id,
            project.CurrentPeriodMode,
            current?.Id,
            current?.PeriodKeyValue,
            automatic?.Id,
            automatic?.PeriodKeyValue,
            project.CurrentPeriodMode == CurrentPeriodMode.Pinned ? project.CurrentPeriodPinnedReason : null,
            project.CurrentPeriodChangedAt);
    }
}
