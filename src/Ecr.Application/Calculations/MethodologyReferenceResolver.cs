// src/Ecr.Application/Calculations/MethodologyReferenceResolver.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Calculations;

/// <summary>
/// Резолвінг <c>!Name</c> **через межу методології** (директива ПК-1 №05,
/// поправка 10).
/// </summary>
/// <remarks>
/// ⛔ Методологія не замкнена, і це не рідкісний випадок: 149 посилань із
/// <c>HSE400</c> і 116 з <c>Flert</c> ведуть у <c>Common</c>, а
/// <c>ECW_C09_02_01</c> — одна формула — потрібна п'яти методологіям. Правило
/// «<c>!</c> — це формула ТІЄЇ САМОЇ версії» (02b §3.4) відхилило б усі 265 як
/// нерезолвлені посилання.
/// <para>
/// ⚠ <c>Kind = Library</c> тут не перевіряється навмисно. Спільність — це
/// оголошений імпорт, а не природа методології; ворота «посилатися можна лише
/// на бібліотеку» заборонили б наявні посилання на <c>ECW_C09_02_01</c>.
/// </para>
/// </remarks>
public static class MethodologyReferenceResolver
{
    /// <summary>
    /// Знаходить, куди веде <c>!Name</c>: спершу у власній версії, потім в
    /// оголошених імпортах.
    /// </summary>
    /// <param name="name">Ім'я після <c>!</c>.</param>
    /// <param name="localFormulas">Формули цієї версії: код → ідентифікатор.</param>
    /// <param name="imports">Оголошені імпорти, розв'язані на дату періоду.</param>
    /// <returns>Куди веде посилання, або чому не веде нікуди.</returns>
    /// <exception cref="ArgumentNullException">Будь-який аргумент — <c>null</c>.</exception>
    /// <remarks>
    /// ⛔ Своє виграє над імпортованим, і це **не** неоднозначність: формула з
    /// тим самим кодом у власній версії — навмисне перекриття бібліотечної, і
    /// саме так методологія відходить від спільної поведінки, не форкаючи
    /// <c>Common</c>.
    ///
    /// ⛔ А от збіг МІЖ ДВОМА імпортами — помилка публікації. Узяти «перший за
    /// списком» означало б, що число залежить від порядку рядків у
    /// <c>calc.MethodologyImport</c> і змінюється від переіндексації; саме тому
    /// в оголошення імпорту не додано порядку.
    ///
    /// ⚠ Порівняння без урахування регістру — узгоджено з рештою резолвінгу
    /// символів у публікації (<c>PublishMethodologyHandler</c>): два різні
    /// правила для <c>!Name</c> розійшлися б на першому ж імені у змішаному
    /// регістрі.
    /// </remarks>
    public static MethodologyReference Resolve(
        string name,
        IReadOnlyDictionary<string, int> localFormulas,
        IReadOnlyList<MethodologyLibrary> imports)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(localFormulas);
        ArgumentNullException.ThrowIfNull(imports);

        if (localFormulas.TryGetValue(name, out var formulaId))
        {
            return new MethodologyReference(
                MethodologyReferenceOutcome.Local, formulaId, null, []);
        }

        var matches = imports
            .Where(library => library.FormulaCodes.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => new MethodologyReference(MethodologyReferenceOutcome.NotFound, null, null, []),
            1 => new MethodologyReference(
                MethodologyReferenceOutcome.Imported, null, matches[0].MethodologyId, []),
            _ => new MethodologyReference(
                MethodologyReferenceOutcome.Ambiguous,
                null,
                null,
                matches.Select(m => m.MethodologyCode).Order(StringComparer.Ordinal).ToList()),
        };
    }
}

/// <summary>Чим скінчився резолвінг <c>!Name</c>.</summary>
public enum MethodologyReferenceOutcome : byte
{
    /// <summary>Формула цієї самої версії.</summary>
    Local = 0,

    /// <summary>Формула однієї з оголошених бібліотек.</summary>
    Imported = 1,

    /// <summary>Ніде не знайдено.</summary>
    NotFound = 2,

    /// <summary>Знайдено у двох і більше бібліотеках — помилка публікації.</summary>
    Ambiguous = 3
}

/// <summary>Куди веде <c>!Name</c>.</summary>
/// <param name="Outcome">Результат резолвінгу.</param>
/// <param name="FormulaId">Формула цієї версії; заповнено лише для <c>Local</c>.</param>
/// <param name="MethodologyId">
/// Методологія-джерело; заповнено лише для <c>Imported</c> — це і є ребро
/// <c>calc.MethodologyDependency</c>.
/// </param>
/// <param name="Candidates">Коди методологій-претендентів; непорожньо лише для <c>Ambiguous</c>.</param>
public sealed record MethodologyReference(
    MethodologyReferenceOutcome Outcome,
    int? FormulaId,
    int? MethodologyId,
    IReadOnlyList<string> Candidates);
