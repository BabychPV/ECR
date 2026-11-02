// src/Ecr.Domain/Entities/Calculations/MethodologyCategory.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Категорія констант версії методології (<c>calc.MethodologyCategory</c>): перелік, з якого вибирається
/// <see cref="MethodologyConstant.Category"/> (план CONST-CATEGORY, рішення людини 29.10).
/// </summary>
/// <remarks>
/// ⛔ Код — КЛЮЧ ЗІСТАВЛЕННЯ, а не підпис: його повертає правило категорії (<c>calc.CategoryRule</c>), і за ним
/// резолвер (<c>ConstantResolver</c>) звужує константу (порівняння ПОРЯДКОВЕ, з регістром). Тому коду не
/// перейменовують і не прибирають, поки на нього посилається бодай одна константа: описка тут — не помилка,
/// а тихо мертва константа, замість якої рахує спільна. Перейменування — нова категорія + перенос констант.
/// <para>
/// ⚠ <see cref="MethodologyConstant.Category"/> лишається рядком без зовнішнього ключа: резолвер, імпорт пакета
/// і клон працюють, як і раніше, а перелік лише ОБМЕЖУЄ запис константи.
/// </para>
/// </remarks>
public sealed class MethodologyCategory : Entity<int>
{
    /// <summary>Межа коду — та сама, що в <c>calc.MethodologyConstant.Category</c>.</summary>
    public const int MaxCodeLength = 64;

    /// <summary>Межа опису.</summary>
    public const int MaxDescriptionLength = 400;

    private MethodologyCategory() { }

    /// <summary>Заводить категорію; вхід — <c>MethodologyVersion.AddCategory</c> (чернетка) або клон/міграція.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="code">Код як його повертає правило категорії (регістр зберігається).</param>
    /// <param name="ordinal">Порядок у переліку.</param>
    public MethodologyCategory(int methodologyVersionId, string code, int ordinal)
    {
        MethodologyVersionId = methodologyVersionId;
        Code = code;
        Ordinal = ordinal;
    }

    /// <summary>Версія методології.</summary>
    public int MethodologyVersionId { get; private set; }

    /// <summary>Код категорії — значення <see cref="MethodologyConstant.Category"/>.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Пояснення для методолога; <c>null</c> — немає.</summary>
    public string? Description { get; private set; }

    /// <summary>Порядок у переліку.</summary>
    public int Ordinal { get; private set; }

    /// <summary>Виведена з ужитку: нова константа її не вибирає.</summary>
    public bool IsRetired { get; private set; }

    /// <summary>Опис, порядок і стан; вхід — <c>MethodologyVersion.EditCategory</c> або клон.</summary>
    /// <param name="description">Опис; <c>null</c> — зняти.</param>
    /// <param name="ordinal">Порядок.</param>
    /// <param name="isRetired">Виведена з ужитку.</param>
    public void Describe(string? description, int ordinal, bool isRetired)
    {
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Ordinal = ordinal;
        IsRetired = isRetired;
    }
}
