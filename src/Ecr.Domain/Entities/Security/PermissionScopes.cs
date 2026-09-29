// src/Ecr.Domain/Entities/Security/PermissionScopes.cs
namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Які функціональні права мають проєкт, а які — ні (ФВ-6.14).
/// </summary>
/// <remarks>
/// ⛔ Роль з областю дії дає свої функціональні права лише в проєктах
/// області (<c>AccessProfile.Has(code, projectId)</c>). Право з цього
/// переліку проєкту НЕ має (безпека, адміністрування, структура шаблонів,
/// довідники, методики, інтеграція, система, огляд усіх проєктів) — роль з
/// областю його не дає НІДЕ: «адміністратор лише проєкту A» над
/// користувачами всієї системи — це адміністратор усієї системи.
///
/// ⚠ Перелік — ГЛОБАЛЬНИХ, а не проєктних: нове право за замовчуванням
/// проєктне, і сторож <c>ProjectPermissionCheckTests</c> вимагатиме
/// перевіряти його з проєктом або явно записати сюди. Хибне «проєктне» дає
/// 403 оператору з областю (видно одразу), хибне «глобальне» — роль з
/// областю мовчки не отримує права, але не розширює доступ.
/// </remarks>
public static class PermissionScopes
{
    /// <summary>Права без проєкту: роль з областю їх не дає.</summary>
    public static readonly IReadOnlySet<string> Global = new HashSet<string>(StringComparer.Ordinal)
    {
        "Security.ManageUsers",
        "Security.ManageRoles",
        "Security.ViewAudit",
        "Security.Simulate",
        "System.ViewHealth",
        "System.RunJob",
        "System.ManageLocalization",
        "System.ManageNotifications",
        "Template.View",
        "Template.Edit",
        "Template.Publish",
        "Registry.View",
        "Registry.EditData",
        "Registry.EditDefinition",
        "Registry.Publish",
        "Uom.EditCatalog",
        "Integration.View",
        "Integration.Manage",
        "Integration.EditSchedule",
        "Calculation.EditFormula",
        "Calculation.EditConstant",
        "Calculation.EditRule",
        "Calculation.Publish",
        "Calculation.ManageRequiredInputs",
        "Report.EditDefinition",
        "Report.ViewCampaign",
    };

    /// <summary>Чи право без проєкту.</summary>
    /// <param name="code">Код права.</param>
    public static bool IsGlobal(string code) => Global.Contains(code);
}
