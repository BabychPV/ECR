// src/Ecr.Domain/Entities/Calculations/ScriptVersion.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Версія скрипта рівня 2. **Виконання в першому релізі не будується**
/// (D-105): сутність існує, щоб модель була повною і щоб додавання рівня 2
/// пізніше не ламало схему, але рушія до неї немає.
/// </summary>
/// <remarks>
/// <see cref="HasGreenTest"/> — умова публікації (ФВ-9.12, ФВ-13.7): без
/// зеленого тесту версія не публікується, і це перевіряється, а не мається на
/// увазі.
/// </remarks>
public sealed class ScriptVersion : Entity<int>
{
    private ScriptVersion() { }

    public ScriptVersion(int methodologyVersionId, string sourceCode)
    {
        MethodologyVersionId = methodologyVersionId;
        SourceCode = sourceCode;
        Status = TemplateVersionStatus.Draft;
    }

    public int MethodologyVersionId { get; private set; }
    public string SourceCode { get; private set; } = null!;
    public TemplateVersionStatus Status { get; private set; }

    /// <summary>Чи пройшов останній прогін тестів. Без цього публікація неможлива.</summary>
    public bool HasGreenTest { get; private set; }

    public DateTime? LastTestedAt { get; private set; }
}
