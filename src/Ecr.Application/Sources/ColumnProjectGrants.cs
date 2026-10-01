// src/Ecr.Application/Sources/ColumnProjectGrants.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;

namespace Ecr.Application.Sources;

/// <summary>
/// Мапінг на колонку шаблону пише в документи КОЖНОГО проєкту, що її використовує: на кожен потрібен
/// грант <c>Manage</c> (S3) — для мапінгу поля на колонку й прив'язки вікна рядка однаково.
/// </summary>
/// <remarks>
/// ⛔ Збір пише від імені integration writer, який має <c>Write</c> усюди, де немає явного <c>IsDeny</c>.
/// Без цієї перевірки <c>Integration.Manage</c> означало б «пиши довільні значення у відкриті періоди
/// будь-якого проєкту»: глобальне право каже «керує інтеграцією», грант — «саме цими проєктами» (Q-179).
/// <para>
/// ⚠ Колонку, якої не використовує жоден проєкт, мапити МОЖНА — мапінг нікого не зачіпає; вимога
/// глобального права закрила б налаштування нової версії шаблону до появи першого проєкту.
/// </para>
/// <para>
/// ⛔ S18: відмова називає <c>projectId</c> лише ВИДИМОГО проєкту (<see cref="AccessProfile.SeesDocumentsOf"/>).
/// Доти вона називала перший проєкт без гранта, хоч би й невидимий: перебором колонок можна було
/// зібрати номери проєктів, яких користувач не бачить. Невидимі проєкти дають відмову без номера
/// (<c>columnUsedInHiddenProjects</c>) — і лише тоді, коли видимих без гранта немає, щоб людина
/// спершу бачила те, що може виправити.
/// </para>
/// </remarks>
public static class ColumnProjectGrants
{
    /// <summary>Вимагає грант <c>Manage</c> на кожен проєкт, що використовує колонку.</summary>
    /// <param name="sources">Сховище збору (проєкти колонки).</param>
    /// <param name="profile">Профіль користувача.</param>
    /// <param name="columnDefId">Колонка шаблону.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="AccessDeniedException"><c>403 ECR-AUTH-0403</c>.</exception>
    public static async Task RequireManageAsync(
        ICollectionStore sources, AccessProfile profile, int columnDefId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(profile);

        var projects = await sources.FindProjectIdsUsingColumnAsync(columnDefId, ct).ConfigureAwait(false);
        var hidden = false;

        foreach (var projectId in projects.Order())
        {
            if (profile.LevelFor(ResourceKind.Project, projectId) >= GrantLevel.Manage)
            {
                continue;
            }

            if (!profile.SeesDocumentsOf(projectId))
            {
                hidden = true;
                continue;
            }

            throw new AccessDeniedException(
                "ECR-AUTH-0403",
                $"Немає гранта Manage на проєкт {projectId}, у який писав би мапінг.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectManageGrant",
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (hidden)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403",
                "Колонку використовують проєкти, на які немає гранта Manage.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0403.columnUsedInHiddenProjects" });
        }
    }
}
