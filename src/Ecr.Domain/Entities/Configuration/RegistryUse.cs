// src/Ecr.Domain/Entities/Configuration/RegistryUse.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Ребро «хто посилається на довідник»: формула шаблону, формула версії
/// методології або правило іншого довідника читає довідник
/// <see cref="RegistryDefId"/> — цілком чи одне його поле
/// (FEATURE-REGISTRY-TABLES §3.2, міграція <c>RK04RegistryUseAndRunAsOf</c>).
/// </summary>
/// <remarks>
/// <para>
/// Ребра — похідні дані: публікація джерела (RT-23b, RT-24) переписує їх
/// повністю, руками вони не редагуються. Звідси й відсутність мутаторів.
/// </para>
/// <para>
/// ⚠ <see cref="SourceId"/> поліморфний, як <see cref="FormulaDependency.SourceKind"/>:
/// на що він вказує, визначає <see cref="SourceKind"/>, тому зовнішнього
/// ключа на джерело немає. Вид закритий переліком і в базі
/// (<c>CK_RegUse_Kind</c>): ребро невідомого виду не читав би жоден
/// споживач — ні «Де використано», ні завантажувач знімка прогону, — і
/// довідник виглядав би невикористаним, хоча формула на нього посилається.
/// </para>
/// <para>
/// Створюється лише фабриками: кожна задає свій вид і рівно ті посилання, що
/// йому належать, тож суперечливого ребра (правило з кодом формули) не
/// буває.
/// </para>
/// </remarks>
public sealed class RegistryUse : Entity<long>
{
    /// <summary>Джерело — формула шаблону; <see cref="SourceId"/> = <c>cfg.FormulaDef.Id</c>.</summary>
    public const byte TemplateFormulaSource = 0;

    /// <summary>
    /// Джерело — формула версії методології; <see cref="SourceId"/> =
    /// <c>calc.MethodologyVersion.Id</c>, формула — <see cref="FormulaCode"/>.
    /// </summary>
    public const byte MethodologyVersionSource = 1;

    /// <summary>
    /// Джерело — правило довідника; <see cref="SourceId"/> =
    /// <c>cfg.RegistryRuleDef.Id</c>. Саме за цим видом збереження дочірнього
    /// запису композиції знаходить батьків, чиї правила його агрегують (§5.10).
    /// </summary>
    public const byte RegistryRuleSource = 2;

    /// <summary>Найдовший код формули — як <c>nvarchar(64)</c> у схемі.</summary>
    public const int FormulaCodeMaxLength = 64;

    /// <summary>Найдовший шлях поля — як <c>nvarchar(400)</c> у схемі.</summary>
    public const int FieldPathMaxLength = 400;

    private RegistryUse() { }

    private RegistryUse(byte sourceKind, int sourceId, string? formulaCode, int registryDefId, string? fieldPath)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(registryDefId);

        SourceKind = sourceKind;
        SourceId = sourceId;
        FormulaCode = formulaCode;
        RegistryDefId = registryDefId;
        FieldPath = NormalizeFieldPath(fieldPath);
    }

    /// <summary>Використання довідника формулою шаблону.</summary>
    /// <param name="formulaDefId">Формула шаблону (<c>cfg.FormulaDef.Id</c>).</param>
    /// <param name="registryDefId">Довідник, який вона читає.</param>
    /// <param name="fieldPath">Шлях поля (<c>COMPONENT.MW</c>); <c>null</c> — довідник цілком.</param>
    public static RegistryUse ForTemplateFormula(int formulaDefId, int registryDefId, string? fieldPath)
        => new(TemplateFormulaSource, formulaDefId, formulaCode: null, registryDefId, fieldPath);

    /// <summary>Використання довідника формулою версії методології.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="formulaCode">Код формули в межах версії — обов'язковий.</param>
    /// <param name="registryDefId">Довідник, який вона читає.</param>
    /// <param name="fieldPath">Шлях поля; <c>null</c> — довідник цілком (<c>REGFIND</c>, агрегат).</param>
    /// <remarks>
    /// ⚠ Код формули обов'язковий саме тут: у версії методології формул багато,
    /// і без коду «Де використано» могло б показати лише версію, а не формулу,
    /// яку треба відкрити.
    /// </remarks>
    public static RegistryUse ForMethodologyFormula(
        int methodologyVersionId, string formulaCode, int registryDefId, string? fieldPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formulaCode);
        var code = formulaCode.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(code.Length, FormulaCodeMaxLength, nameof(formulaCode));

        return new(MethodologyVersionSource, methodologyVersionId, code, registryDefId, fieldPath);
    }

    /// <summary>Використання довідника правилом іншого (або того самого) довідника.</summary>
    /// <param name="registryRuleDefId">Правило (<c>cfg.RegistryRuleDef.Id</c>).</param>
    /// <param name="registryDefId">Довідник, який правило читає.</param>
    /// <param name="fieldPath">Шлях поля; <c>null</c> — довідник цілком.</param>
    public static RegistryUse ForRegistryRule(int registryRuleDefId, int registryDefId, string? fieldPath)
        => new(RegistryRuleSource, registryRuleDefId, formulaCode: null, registryDefId, fieldPath);

    /// <summary>0 — формула шаблону, 1 — версія методології, 2 — правило довідника.</summary>
    public byte SourceKind { get; private set; }

    /// <summary>Id джерела; на що саме вказує — визначає <see cref="SourceKind"/>.</summary>
    public int SourceId { get; private set; }

    /// <summary>Код формули в межах версії методології; для інших видів — <c>null</c>.</summary>
    public string? FormulaCode { get; private set; }

    /// <summary>Довідник, який використовується.</summary>
    public int RegistryDefId { get; private set; }

    /// <summary>
    /// Шлях поля (<c>COMPONENT.MW</c>); <c>null</c> — довідник цілком
    /// (<c>REGFIND</c>, агрегат).
    /// </summary>
    public string? FieldPath { get; private set; }

    // Порожній рядок і null означають одне — «довідник цілком»; два записи
    // того самого факту дали б два різні ребра в «Де використано».
    private static string? NormalizeFieldPath(string? fieldPath)
    {
        if (string.IsNullOrWhiteSpace(fieldPath))
        {
            return null;
        }

        var path = fieldPath.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(path.Length, FieldPathMaxLength, nameof(fieldPath));
        return path;
    }
}
