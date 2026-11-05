// src/Ecr.Application/Documents/EditAssignments/DelegatorAuthority.cs
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Application.Documents.EditAssignments;

/// <summary>
/// P2-2 (аудит 25.10): чи делегатор ЩЕ має право тримати надане ним надання — ті самі правила, за якими воно видається
/// (<c>CanManage</c> і власний рівень на області), для обчислення чинності надання на боці виконавця.
/// </summary>
/// <remarks>
/// ⛔ Чисті функції над профілем делегатора. Рядок передачі не видаляється: надання лише перестає діяти, поки делегатор
/// не відновить права.
/// </remarks>
public static class DelegatorAuthority
{
    /// <summary>Чи може делегатор керувати передачами документа (Manage проєкту або автор з Write).</summary>
    /// <param name="delegator">Профіль делегатора.</param>
    /// <param name="projectId">Проєкт документа.</param>
    /// <param name="ownerUserId">Автор документа.</param>
    public static bool MayManage(AccessProfile delegator, int projectId, int ownerUserId)
        => EditAssignmentAccess.CanManage(delegator, projectId, ownerUserId, projectReadOnly: false);

    /// <summary>
    /// Чи сягає власний рівень делегатора на області <paramref name="level"/>; області, якої вже немає в шаблоні
    /// документа, — ні.
    /// </summary>
    /// <param name="delegator">Профіль делегатора.</param>
    /// <param name="projectId">Проєкт документа.</param>
    /// <param name="snapshot">Знімок шаблону документа.</param>
    /// <param name="slice">Область надання.</param>
    /// <param name="level">Виданий рівень.</param>
    public static bool OwnLevelReaches(
        AccessProfile delegator, int projectId, TemplateVersionSnapshot snapshot, EditAssignmentSlice slice, GrantLevel level)
    {
        try
        {
            return EditAssignmentAccess.OwnLevelOver(delegator, projectId, snapshot, slice) >= level;
        }
        catch (BusinessRuleException)
        {
            return false;
        }
    }

    /// <summary>
    /// PM-EDITASSIGN-P3 (a): чи може делегатор сам редагувати кожен аркуш області Write-передачі — те саме правило
    /// «у межах власних прав», що й під час видачі (<c>SheetsBeyondOwnRights</c>); області, якої вже немає, — ні.
    /// </summary>
    /// <param name="delegator">Профіль делегатора.</param>
    /// <param name="projectId">Проєкт документа.</param>
    /// <param name="snapshot">Знімок шаблону документа.</param>
    /// <param name="slice">Область передачі.</param>
    public static bool WriteWithinOwnRights(
        AccessProfile delegator, int projectId, TemplateVersionSnapshot snapshot, EditAssignmentSlice slice)
    {
        try
        {
            return !EditAssignmentAccess.SheetsBeyondOwnRights(delegator, projectId, snapshot, slice).Exceeds;
        }
        catch (BusinessRuleException)
        {
            return false;
        }
    }
}
