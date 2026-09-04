// src/Ecr.Domain/Entities/Calculations/ScriptVersion.cs
using Ecr.Domain.Abstractions;

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

    /// <summary>Створює версію скрипта.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="sourceCode">Вихідний код.</param>
    /// <param name="contentHash">SHA-256 нормалізованого коду.</param>
    public ScriptVersion(int methodologyVersionId, string sourceCode, byte[] contentHash)
    {
        ArgumentNullException.ThrowIfNull(contentHash);

        MethodologyVersionId = methodologyVersionId;
        SourceCode = sourceCode;
        ContentHash = contentHash;
    }

    public int MethodologyVersionId { get; private set; }
    public string SourceCode { get; private set; } = null!;

    /// <summary>Коли скрипт скомпільовано; <c>null</c> — ще ні.</summary>
    /// <remarks>
    /// ⚠ Власного <c>Status</c> у скрипта немає (`calc`-частина `Q-027`), і це
    /// правильно: стан має ВЕРСІЯ МЕТОДОЛОГІЇ, якій він належить. Два стани на
    /// одну сутність розійшлися б, і питання «чи опубліковано» отримало б дві
    /// відповіді.
    /// </remarks>
    public DateTime? CompiledAt { get; private set; }

    /// <summary>Діагностики компілятора; порожньо — компіляція чиста.</summary>
    public string? CompilerDiagnostics { get; private set; }

    /// <summary>Чи пройшов останній прогін тестів. Без цього публікація неможлива.</summary>
    public bool HasGreenTest { get; private set; }

    /// <summary>Контрольна сума коду — те, за чим видно, що скрипт не змінювали.</summary>
    public byte[] ContentHash { get; private set; } = [];

    /// <summary>Фіксує результат компіляції.</summary>
    /// <param name="diagnostics">Діагностики; <c>null</c> — чисто.</param>
    /// <param name="utcNow">Час компіляції в UTC.</param>
    public void MarkCompiled(string? diagnostics, DateTime utcNow)
    {
        CompilerDiagnostics = diagnostics;
        CompiledAt = utcNow;
    }

    /// <summary>Фіксує результат прогону тестів.</summary>
    /// <param name="passed">Чи всі зелені.</param>
    public void MarkTested(bool passed) => HasGreenTest = passed;
}
