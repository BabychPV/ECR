// src/Ecr.Application/Calculations/ListMethodologyPublicationsHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Calculations;

/// <summary>
/// Журнал публікацій версій методології (<c>aud.PublicationEvent</c>, F-16).
/// </summary>
/// <remarks>
/// ⛔ Публікація методології — найнебезпечніша операція системи (ФВ-9.6), і
/// кожна пише в журнал причину й diff РЕЗУЛЬТАТІВ. Доти цей журнал не мав ні
/// маршруту, ні екрана: «хто, коли і навіщо змінив методологію» можна було
/// дізнатися лише <c>SELECT</c>-ом, а поле «By user» існувало тільки числом.
///
/// ⚠ Право — <c>Calculation.View</c>: той самий, хто бачить версії, має бачити,
/// як вони з'явилися.
/// </remarks>
public sealed class ListMethodologyPublicationsHandler(
    IMethodologyStore methodologies,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає журнал.</summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Публікації, найновіші першими; порожньо — жодної ще не було.</returns>
    public async Task<IReadOnlyList<MethodologyPublicationEntry>> HandleAsync(
        int methodologyId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        return await methodologies.ListPublicationsAsync(methodologyId, ct).ConfigureAwait(false);
    }
}
