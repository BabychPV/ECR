// src/Ecr.Application/Calculations/ListDerivedRequiredInputsHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations;

/// <summary>
/// ✎ D-578 (рішення людини 26.11): ВИВЕДЕНІ обов'язкові входи версії — поля, які читає її скрипт (<c>calc.MethodologyReadField</c>,
/// D-568), — для редактора методології (лише читання) разом із винятком методолога з переліку <c>required-inputs</c>
/// (Block / Warn / Optional), якщо він є. Право — те саме, що на перелік вимог.
/// </summary>
/// <remarks>
/// ⚠ Стани: <c>available</c> — перелік записано; <c>notRecorded</c> — запису немає (версію не дозаписано) і <c>notCompiled</c> —
/// скрипт не скомпілювався (маркер <c>*</c>): автоматичного переліку немає, діє лише перелік методолога (редактор показує
/// попередження); <c>notScript</c> — формульна версія (полів скрипта немає). Поля за ІНШИЙ місяць (<c>otherMonths</c>) — у
/// переліку з <c>inScope = false</c>: вимогою не стають (етап 2), як і поля інших таблиць.
/// </remarks>
public sealed class ListDerivedRequiredInputsHandler(
    IMethodologyStore methodologies,
    IMethodologyDraftStore drafts,
    ListMethodologyRequiredInputsHandler declared)
{
    /// <summary>Виведені вимоги версії.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<DerivedRequiredInputsDto> HandleAsync(int methodologyVersionId, CancellationToken ct)
    {
        // Право й існування версії — як у переліку вимог (той самий обробник).
        var manual = await declared.HandleAsync(methodologyVersionId, ct).ConfigureAwait(false);
        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false);
        if (version is null || version.Engine != MethodologyEngine.Script)
        {
            return new DerivedRequiredInputsDto(methodologyVersionId, DerivedRequiredInputsStatus.NotScript, []);
        }

        var fields = await methodologies.GetReadFieldsOfVersionsAsync([methodologyVersionId], ct).ConfigureAwait(false);
        if (fields.Count == 0)
        {
            return new DerivedRequiredInputsDto(methodologyVersionId, DerivedRequiredInputsStatus.NotRecorded, []);
        }

        if (fields.Any(f => f.FieldCode == MethodologyReadField.AllFields))
        {
            return new DerivedRequiredInputsDto(methodologyVersionId, DerivedRequiredInputsStatus.NotCompiled, []);
        }

        var exceptions = manual
            .Where(m => m.ColumnCode is not null)
            .GroupBy(m => m.ColumnCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Severity, StringComparer.OrdinalIgnoreCase);

        return new DerivedRequiredInputsDto(
            methodologyVersionId,
            DerivedRequiredInputsStatus.Available,
            [
                .. fields
                    .Where(f => f.FieldCode.Length > 0)
                    .OrderBy(f => f.FieldCode, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new DerivedRequiredInputDto(
                        f.FieldCode, f.OtherMonths, InScope: !f.OtherMonths,
                        exceptions.TryGetValue(f.FieldCode, out var severity) ? severity : null)),
            ]);
    }
}

/// <summary>✎ D-578: виведені обов'язкові входи версії.</summary>
/// <param name="MethodologyVersionId">Версія.</param>
/// <param name="Status">Стан переліку (<see cref="DerivedRequiredInputsStatus"/>).</param>
/// <param name="Fields">Поля, які читає скрипт, за кодом.</param>
public sealed record DerivedRequiredInputsDto(int MethodologyVersionId, string Status, IReadOnlyList<DerivedRequiredInputDto> Fields);

/// <summary>Поле, яке читає скрипт.</summary>
/// <param name="FieldCode">Код поля (колонки таблиці, до якої прив'язано методологію).</param>
/// <param name="OtherMonths">Читається за інший місяць.</param>
/// <param name="InScope">Стає вимогою (поточний місяць); <c>false</c> — етап 2.</param>
/// <param name="ExceptionSeverity">
/// Виняток методолога з переліку вимог: <c>Block</c>/<c>Warn</c>/<c>Optional</c>; <c>null</c> — виняток не заведено (вимога
/// діє як Block для подання).
/// </param>
public sealed record DerivedRequiredInputDto(string FieldCode, bool OtherMonths, bool InScope, RequiredInputSeverity? ExceptionSeverity);

/// <summary>Стани <see cref="DerivedRequiredInputsDto.Status"/>.</summary>
public static class DerivedRequiredInputsStatus
{
    /// <summary>Перелік записано.</summary>
    public const string Available = "available";

    /// <summary>Запису немає — автоматичного переліку немає.</summary>
    public const string NotRecorded = "notRecorded";

    /// <summary>Скрипт не скомпілювався (маркер «читає будь-що») — автоматичного переліку немає.</summary>
    public const string NotCompiled = "notCompiled";

    /// <summary>Формульна версія.</summary>
    public const string NotScript = "notScript";
}
