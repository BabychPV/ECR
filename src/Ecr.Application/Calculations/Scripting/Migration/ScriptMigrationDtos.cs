// src/Ecr.Application/Calculations/Scripting/Migration/ScriptMigrationDtos.cs
namespace Ecr.Application.Calculations.Scripting.Migration;

/// <summary>Одна версія в пакеті, який випускає <c>pack</c> інструмента перенесення.</summary>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="Version">Номер версії.</param>
/// <param name="EffectiveFrom">Дата чинності (<c>yyyy-MM-dd</c>); <c>null</c> — не звіряється.</param>
/// <param name="Text">Текст скрипта.</param>
/// <param name="SourceFingerprint">Відбиток джерела (SHA-256, hex) на момент <c>pack</c>.</param>
public sealed record ScriptPackageEntryDto(
    string MethodologyCode, string Version, string? EffectiveFrom, string Text, string SourceFingerprint);

/// <summary>Пакет <c>package.json</c> — тіло <c>POST …/script-migration/apply</c>.</summary>
/// <param name="Format">Формат пакета (<c>ecr-methodology-script-package</c>); <c>null</c> — не звіряється.</param>
/// <param name="FormatVersion">Версія формату; <c>null</c> — не звіряється.</param>
/// <param name="Entries">Версії пакета.</param>
public sealed record ApplyScriptPackageRequest(
    string? Format, int? FormatVersion, IReadOnlyList<ScriptPackageEntryDto>? Entries);

/// <summary>Підсумок щодо однієї версії пакета.</summary>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="Version">Номер версії.</param>
/// <param name="VersionId">Версія в базі; <c>null</c> — не знайдено.</param>
/// <param name="Status"><c>applied</c> — записано; <c>wouldApply</c> — сухий прогін, записалося б; <c>refused</c> — відмова.</param>
/// <param name="Reason">Ключ причини відмови (<c>scriptMigration.refused.*</c>); <c>null</c> — відмови немає.</param>
/// <param name="Message">Пояснення відмови англійською.</param>
/// <param name="Details">Подробиці відмови (діагностики, відбитки); <c>null</c> — немає.</param>
public sealed record ScriptMigrationEntryResultDto(
    string MethodologyCode,
    string Version,
    int? VersionId,
    string Status,
    string? Reason,
    string? Message,
    IReadOnlyList<string>? Details);

/// <summary>Відповідь <c>POST …/script-migration/apply</c>.</summary>
/// <param name="DryRun">Сухий прогін: нічого не записано.</param>
/// <param name="Applied">Скільки записано (або записалося б).</param>
/// <param name="Refused">Скільки відмов.</param>
/// <param name="Entries">Підсумок за версіями — у порядку пакета.</param>
public sealed record ApplyScriptPackageResponse(
    bool DryRun, int Applied, int Refused, IReadOnlyList<ScriptMigrationEntryResultDto> Entries);

/// <summary>Тіло <c>POST …/script-migration/switch</c>.</summary>
/// <param name="VersionIds">Версії групи.</param>
/// <param name="Engine"><c>Script</c> або <c>Formulas</c>.</param>
/// <param name="Reason">Причина; обов'язкова, потрапляє в журнал.</param>
public sealed record SwitchScriptEngineRequest(IReadOnlyList<int>? VersionIds, string? Engine, string? Reason);

/// <summary>Версія, перемкнена групою.</summary>
/// <param name="VersionId">Версія.</param>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="Version">Номер версії.</param>
public sealed record SwitchedVersionDto(int VersionId, string MethodologyCode, string Version);

/// <summary>Відповідь <c>POST …/script-migration/switch</c>.</summary>
/// <param name="Engine">Рушій, на який перемкнуто.</param>
/// <param name="Versions">Перемкнені версії.</param>
public sealed record SwitchScriptEngineResponse(string Engine, IReadOnlyList<SwitchedVersionDto> Versions);
