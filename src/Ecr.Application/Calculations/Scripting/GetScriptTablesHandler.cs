// src/Ecr.Application/Calculations/Scripting/GetScriptTablesHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Calculations.Scripting;

/// <summary>Колонка живої таблиці для підказок редактора.</summary>
/// <param name="Code">Код колонки.</param>
/// <param name="DataType">Тип даних колонки.</param>
/// <param name="IsMonthColumn">Місячна колонка.</param>
/// <param name="MonthNumber">Номер місяця (1…12); <c>null</c> — не місячна.</param>
/// <param name="UnitCode">Код одиниці колонки; <c>null</c> — одиниці немає.</param>
public sealed record ScriptTableColumnDto(string Code, string DataType, bool IsMonthColumn, byte? MonthNumber, string? UnitCode);

/// <summary>Жива таблиця для підказок редактора.</summary>
/// <param name="Requested">Код із запиту (<c>Таблиця</c> або <c>Шаблон.Таблиця</c>).</param>
/// <param name="TemplateCode">Код шаблону.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="Columns">Колонки (без видалених).</param>
/// <param name="MonthAliases">Псевдоніми місячних входів таблиці.</param>
public sealed record ScriptTableDto(
    string Requested,
    string TemplateCode,
    string TableCode,
    IReadOnlyList<ScriptTableColumnDto> Columns,
    IReadOnlyList<string> MonthAliases);

/// <summary>
/// Живі таблиці за кодами — для підказок редактора після <c>row.</c> і <c>Alias.</c> (MS, план 6). Код із крапкою —
/// <c>Шаблон.Таблиця</c>. Код, якого немає серед живих таблиць, у відповіді відсутній.
/// </summary>
public sealed class GetScriptTablesHandler(
    IMethodologyStore methodologies, IUnitCatalog units, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право на читання.</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Найбільша кількість кодів у запиті.</summary>
    public const int MaxCodes = 50;

    /// <summary>Знаходить живі таблиці.</summary>
    /// <param name="codes">Коди через кому.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-CALC-0422</c> <c>scriptTablesCodesInvalid</c> — кодів немає чи їх задовго.</exception>
    public async Task<IReadOnlyList<ScriptTableDto>> HandleAsync(string? codes, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var requested = (codes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requested.Count == 0 || requested.Count > MaxCodes)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Потрібно від 1 до {MaxCodes} кодів таблиць через кому.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.scriptTablesCodesInvalid",
                    ["limit"] = MaxCodes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var catalog = await units.GetAsync(ct).ConfigureAwait(false);
        var unitCodes = catalog.Units.Values.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First().Code);

        var result = new List<ScriptTableDto>();
        foreach (var code in requested)
        {
            var dot = code.IndexOf('.', StringComparison.Ordinal);
            var (template, table) = dot > 0 ? (code[..dot], code[(dot + 1)..]) : ((string?)null, code);

            foreach (var live in await methodologies.FindLiveTablesByCodeAsync(table, template, ct).ConfigureAwait(false))
            {
                result.Add(new ScriptTableDto(
                    code,
                    live.TemplateCode,
                    live.TableCode,
                    [.. live.Columns.Select(c => new ScriptTableColumnDto(
                        c.Code,
                        c.DataType.ToString(),
                        c.IsMonthColumn,
                        c.MonthNumber,
                        c.UnitId is { } id ? unitCodes.GetValueOrDefault(id) : null))],
                    live.MonthAliases));
            }
        }

        return result;
    }
}
