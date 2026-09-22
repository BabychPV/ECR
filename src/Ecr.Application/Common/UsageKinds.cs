// src/Ecr.Application/Common/UsageKinds.cs
using System.Reflection;

namespace Ecr.Application.Common;

/// <summary>
/// Види залежних об'єктів у <see cref="UsageItemDto.Kind"/> — єдиний перелік
/// для обох «де використано» (<c>BE-15</c> одиниці, <c>BE-24</c> довідники).
/// </summary>
/// <remarks>
/// ⚠ Рядки, а не enum: <c>JsonStringEnumConverter</c> тут пише імена членів як
/// є (<c>TemplateColumn</c>), а на дроті вже живуть camelCase-значення, на які
/// спирається клієнт. Сирих літералів видів у сховищах бути не повинно —
/// це стереже <c>UsageKindsTests</c>, і на цьому переліку клієнт будує
/// сторожа «кожен вид має ключ каталогу».
/// </remarks>
public static class UsageKinds
{
    /// <summary>Колонка шаблону (довідник: <c>Lookup</c>; одиниця: <c>UnitId</c>).</summary>
    public const string TemplateColumn = "templateColumn";

    /// <summary>Поле довідника.</summary>
    public const string RegistryField = "registryField";

    /// <summary>Речовина методики — посилається на запис довідника.</summary>
    public const string MethodologySubstance = "methodologySubstance";

    /// <summary>Сутність джерела, прив'язана до довідника.</summary>
    public const string SourceEntity = "sourceEntity";

    /// <summary>Константа методики.</summary>
    public const string MethodologyConstant = "methodologyConstant";

    /// <summary>Формула методики (одиниця виходу).</summary>
    public const string MethodologyFormula = "methodologyFormula";

    /// <summary>Вихід методики.</summary>
    public const string MethodologyOutput = "methodologyOutput";

    /// <summary>Мапінг поля джерела (одиниця джерела або цілі).</summary>
    public const string FieldMap = "fieldMap";

    /// <summary>Правило перерахунку між одиницями.</summary>
    public const string UnitConversion = "unitConversion";

    /// <summary>Похідна одиниця (чисельник або знаменник).</summary>
    public const string DerivedUnit = "derivedUnit";

    /// <summary>Базова одиниця розмірності.</summary>
    public const string DimensionBase = "dimensionBase";

    /// <summary>Таблиця даних — один рядок на таблицю, без підрахунку.</summary>
    public const string Data = "data";

    /// <summary>Формула шаблону, що читає колонку (<c>cfg.FormulaDependency</c>).</summary>
    public const string TemplateFormula = "templateFormula";

    /// <summary>Прив'язка методології: колонка-приймач або вхід прив'язки.</summary>
    public const string CalculationBinding = "calculationBinding";

    /// <summary>Правило методики, чий <c>MatchJson</c> має ключем колонку.</summary>
    public const string MethodologyRule = "methodologyRule";

    /// <summary>Обов'язковий вхід версії методики.</summary>
    public const string MethodologyRequiredInput = "methodologyRequiredInput";

    /// <summary>Усі види — рефлексією, щоб новий член не треба було дописувати вдруге.</summary>
    public static IReadOnlyList<string> All { get; } =
        [.. typeof(UsageKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];
}
