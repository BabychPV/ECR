// src/Ecr.Application/Calculations/Scripting/ScriptRegistryScopeReader.cs
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.Scripting;

namespace Ecr.Application.Calculations.Scripting;

/// <summary>
/// MS × B179: обсяги читання довідників за періодом для тексту скрипта (граф залежностей, інфраструктура не бачить
/// компілятор скриптів). Лише компіляція й аналіз — без бази.
/// </summary>
public static class ScriptRegistryScopeReader
{
    /// <summary>Обсяг кожного довідника скрипта; не компілюється → порожньо (викликач бере <see cref="PeriodScope.All"/>).</summary>
    /// <param name="scriptText">Текст скрипта версії.</param>
    /// <param name="numericMode">Числовий режим версії.</param>
    /// <param name="isLibrary">Методологія — бібліотека.</param>
    /// <param name="isModuleLevel">Версія рівня <c>Module</c>.</param>
    public static IReadOnlyDictionary<string, PeriodScope> Read(string scriptText, NumericMode numericMode, bool isLibrary, bool isModuleLevel)
    {
        ArgumentNullException.ThrowIfNull(scriptText);

        // ⚠ Семантичні діагностики скрипт не скасовують (він будується «як є»): обсяги потрібні й тоді; лише
        // синтаксична відмова (Script == null) лишає читання без обсягу.
        var compilation = ScriptCompiler.Compile(scriptText, new ScriptCompileOptions(numericMode, isLibrary, IsModuleLevel: isModuleLevel));
        return compilation.Script is { } script
            ? ScriptRegistryScopes.Collect(script)
            : new Dictionary<string, PeriodScope>();
    }
}
