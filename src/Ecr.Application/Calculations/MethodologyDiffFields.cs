// src/Ecr.Application/Calculations/MethodologyDiffFields.cs
using System.Reflection;

namespace Ecr.Application.Calculations;

/// <summary>
/// Поля в <c>MethodologyDiffItemDto.ChangedFields</c> — єдиний перелік для
/// <see cref="CompareMethodologyVersionsHandler"/> (<c>BE-25</c>).
/// </summary>
/// <remarks>
/// ⚠ Значення на дроті вже читає клієнт, і на цьому переліку він будує сторожа
/// «кожне поле має ключ каталогу». Сирих літералів полів в обробнику бути не
/// повинно — це стереже <c>MethodologyDiffFieldsTests</c>.
/// </remarks>
public static class MethodologyDiffFields
{
    /// <summary>Формула: вираз.</summary>
    public const string Expression = "expression";

    /// <summary>Формула: тип результату.</summary>
    public const string ResultType = "resultType";

    /// <summary>Формула: одиниця виходу.</summary>
    public const string OutputUnitId = "outputUnitId";

    /// <summary>Формула: перелік аргументів.</summary>
    public const string ArgumentsCsv = "argumentsCsv";

    /// <summary>Константа: вид (число чи текст).</summary>
    public const string Kind = "kind";

    /// <summary>Константа: числове значення.</summary>
    public const string Value = "value";

    /// <summary>Константа: текстове значення.</summary>
    public const string TextValue = "textValue";

    /// <summary>Константа: одиниця.</summary>
    public const string UnitId = "unitId";

    /// <summary>Константа: кінець дії.</summary>
    public const string ValidTo = "validTo";

    /// <summary>Константа: джерело значення.</summary>
    public const string Source = "source";

    /// <summary>Тест: вхідні дані.</summary>
    public const string InputJson = "inputJson";

    /// <summary>Тест: очікуваний результат.</summary>
    public const string ExpectedJson = "expectedJson";

    /// <summary>Тест: допуск.</summary>
    public const string Tolerance = "tolerance";

    /// <summary>Усі поля — рефлексією, щоб новий член не треба було дописувати вдруге.</summary>
    public static IReadOnlyList<string> All { get; } =
        [.. typeof(MethodologyDiffFields)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];
}
