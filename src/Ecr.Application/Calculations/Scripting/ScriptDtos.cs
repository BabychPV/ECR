// src/Ecr.Application/Calculations/Scripting/ScriptDtos.cs
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Scripting;

namespace Ecr.Application.Calculations.Scripting;

/// <summary>Діагностика скрипта в API (MS, план 5.4).</summary>
/// <param name="Code">Код без префікса.</param>
/// <param name="MessageKey">Ключ каталогу: <c>script.&lt;code&gt;</c>.</param>
/// <param name="Params">Підстановки для перекладу.</param>
/// <param name="Line">Рядок з 1.</param>
/// <param name="Column">Колонка з 1.</param>
/// <param name="Length">Довжина підкресленого фрагмента.</param>
/// <param name="Message">Англійський запасний текст.</param>
public sealed record ScriptDiagnosticDto(
    string Code,
    string MessageKey,
    IReadOnlyDictionary<string, string> Params,
    int Line,
    int Column,
    int Length,
    string Message)
{
    /// <summary>Складає DTO з діагностики компілятора чи перевірки.</summary>
    /// <param name="diagnostic">Діагностика.</param>
    public static ScriptDiagnosticDto From(ScriptDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new ScriptDiagnosticDto(
            diagnostic.Code, diagnostic.MessageKey, diagnostic.Params, diagnostic.Line, diagnostic.Column,
            diagnostic.Length, diagnostic.Message);
    }
}

/// <summary>Таблиця, з якої скрипт читає: рядки (<c>rows from</c>) або комірки (<c>table Alias = …</c>).</summary>
/// <param name="Code">Код таблиці.</param>
/// <param name="TemplateCode">Код шаблону, якщо вказано (<c>Шаблон.Таблиця</c>).</param>
/// <param name="Alias">Псевдонім з оголошення <c>table</c>; <c>null</c> — таблиця з <c>rows from</c>.</param>
public sealed record ScriptOutlineTableDto(string Code, string? TemplateCode, string? Alias);

/// <summary>Поле рядка, яке читає скрипт.</summary>
/// <param name="Name">Ім'я поля (код колонки).</param>
/// <param name="Optional">Необов'язкове (<c>row.X?</c>).</param>
public sealed record ScriptOutlineInputDto(string Name, bool Optional);

/// <summary>Вихід скрипта.</summary>
/// <param name="Code">Код виходу.</param>
/// <param name="Unit">Код одиниці.</param>
/// <param name="PerSubstance">Вихід по речовині.</param>
public sealed record ScriptOutlineOutputDto(string Code, string Unit, bool PerSubstance);

/// <summary>Константа верхнього рівня.</summary>
/// <param name="Name">Ім'я.</param>
/// <param name="Kind"><c>scalar</c> або <c>map</c>.</param>
/// <param name="Line">Рядок оголошення.</param>
public sealed record ScriptOutlineConstantDto(string Name, string Kind, int Line);

/// <summary>Змінна верхнього рівня.</summary>
/// <param name="Name">Ім'я.</param>
/// <param name="Line">Рядок оголошення.</param>
public sealed record ScriptOutlineVariableDto(string Name, int Line);

/// <summary>Власна функція скрипта.</summary>
/// <param name="Name">Ім'я.</param>
/// <param name="Parameters">Імена параметрів.</param>
/// <param name="Line">Рядок оголошення.</param>
public sealed record ScriptOutlineFunctionDto(string Name, IReadOnlyList<string> Parameters, int Line);

/// <summary>Структура скрипта для редактора: що читає, що пише, що оголошено.</summary>
/// <param name="Tables">Таблиці (<c>rows from</c> без псевдоніма, <c>table</c> із псевдонімом).</param>
/// <param name="Inputs">Поля рядка.</param>
/// <param name="Outputs">Виходи.</param>
/// <param name="Substances">Коди речовин.</param>
/// <param name="Imports">Імпортовані бібліотеки.</param>
/// <param name="Constants">Константи.</param>
/// <param name="Variables">Змінні верхнього рівня.</param>
/// <param name="Functions">Власні функції.</param>
public sealed record ScriptOutlineDto(
    IReadOnlyList<ScriptOutlineTableDto> Tables,
    IReadOnlyList<ScriptOutlineInputDto> Inputs,
    IReadOnlyList<ScriptOutlineOutputDto> Outputs,
    IReadOnlyList<string> Substances,
    IReadOnlyList<string> Imports,
    IReadOnlyList<ScriptOutlineConstantDto> Constants,
    IReadOnlyList<ScriptOutlineVariableDto> Variables,
    IReadOnlyList<ScriptOutlineFunctionDto> Functions)
{
    /// <summary>Порожня структура — коли скрипт не вдалося розібрати.</summary>
    public static ScriptOutlineDto Empty { get; } = new([], [], [], [], [], [], [], []);

    /// <summary>Будує структуру зі скомпільованого скрипта; <c>null</c> — порожню.</summary>
    /// <param name="script">Скрипт.</param>
    public static ScriptOutlineDto From(CompiledScript? script)
    {
        if (script is null)
        {
            return Empty;
        }

        var tables = new List<ScriptOutlineTableDto>();
        foreach (var reference in script.Rows.SelectMany(r => r.Tables))
        {
            var item = new ScriptOutlineTableDto(reference.TableCode, reference.TemplateCode, null);
            if (!tables.Contains(item))
            {
                tables.Add(item);
            }
        }

        tables.AddRange(script.Tables.Select(t => new ScriptOutlineTableDto(t.Value.TableCode, t.Value.TemplateCode, t.Key)));

        return new ScriptOutlineDto(
            tables,
            [.. script.RowFields.Select(f => new ScriptOutlineInputDto(f.Name, f.IsOptional))],
            [.. script.Outputs.Select(o => new ScriptOutlineOutputDto(o.Code, o.UnitCode, o.IsPerSubstance))],
            [.. script.Substances],
            [.. script.Imports],
            [.. script.Members.Where(m => m.Kind == MemberKind.Const)
                .Select(m => new ScriptOutlineConstantDto(m.Name, m.IsMap ? "map" : "scalar", m.Line))],
            [.. script.Members.Where(m => m.Kind == MemberKind.Var).Select(m => new ScriptOutlineVariableDto(m.Name, m.Line))],
            [.. script.Members.Where(m => m.Kind == MemberKind.Function)
                .Select(m => new ScriptOutlineFunctionDto(
                    m.Name,
                    script.Functions.TryGetValue(m.Name, out var f) ? [.. f.Parameters] : [],
                    m.Line))]);
    }
}

/// <summary>Скрипт версії для редактора (<c>GET</c>/<c>PUT …/script</c>).</summary>
/// <param name="Engine">Рушій версії.</param>
/// <param name="Text">Текст скрипта (порожній, якщо його ще немає).</param>
/// <param name="Hash">SHA-256 тексту, hex малими літерами; порожній рядок — тексту ще немає.</param>
/// <param name="IsDraft">Версію можна правити (чернетка).</param>
/// <param name="Diagnostics">Діагностики компіляції й оточення.</param>
/// <param name="Outline">Структура скрипта.</param>
public sealed record MethodologyScriptDto(
    MethodologyEngine Engine,
    string Text,
    string Hash,
    bool IsDraft,
    IReadOnlyList<ScriptDiagnosticDto> Diagnostics,
    ScriptOutlineDto Outline);

/// <summary>Результат перевірки тексту без збереження.</summary>
/// <param name="Diagnostics">Діагностики.</param>
/// <param name="Outline">Структура.</param>
public sealed record ScriptValidationDto(IReadOnlyList<ScriptDiagnosticDto> Diagnostics, ScriptOutlineDto Outline);

/// <summary>Відповідь перегляду на документі.</summary>
/// <param name="Tables">Таблиці з рядками й значеннями; порожньо, якщо текст має діагностики.</param>
/// <param name="Diagnostics">Діагностики тексту (перегляд виконується лише без них).</param>
public sealed record ScriptPreviewDto(
    IReadOnlyList<ScriptPreviewTable> Tables, IReadOnlyList<ScriptDiagnosticDto> Diagnostics);

/// <summary>Результат форматування літерала константи.</summary>
/// <param name="Text">Канонічний друк; <c>null</c>, якщо літерал не розібрано.</param>
/// <param name="Kind"><c>scalar</c> або <c>map</c>; <c>null</c> — не розібрано.</param>
/// <param name="Diagnostics">Діагностики розбору (позиції — у вхідному тексті).</param>
public sealed record FormatConstDto(string? Text, string? Kind, IReadOnlyList<ScriptDiagnosticDto> Diagnostics);

/// <summary>Тіло <c>PUT …/script</c>.</summary>
/// <param name="Text">Новий текст.</param>
/// <param name="ExpectedHash">Хеш тексту, з якого редактор виходив; не збігається — <c>409 scriptChanged</c>.</param>
public sealed record SaveMethodologyScriptRequest(string Text, string? ExpectedHash = null);

/// <summary>Тіло <c>POST …/script/validate</c> і <c>…/format-const</c>.</summary>
/// <param name="Text">Текст скрипта (для <c>format-const</c> — літерал константи).</param>
public sealed record ScriptTextRequest(string Text);

/// <summary>Тіло <c>POST …/script/preview</c>.</summary>
/// <param name="Text">Текст; порожній — збережений текст версії.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="TableInstanceId">Лише цей екземпляр таблиці.</param>
/// <param name="MaxRows">Скільки рядків з кожної таблиці (1…200, типово 50).</param>
public sealed record PreviewMethodologyScriptRequest(
    string? Text, long DocumentId, int PeriodKey, long? TableInstanceId = null, int? MaxRows = null);
