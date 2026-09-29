// tests/Ecr.Architecture.Tests/ExternalRegistryManualEditGuardTests.cs
using System.Reflection;
using Ecr.Application.Registries;
using Ecr.TestKit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D-211: кожен, хто змінює записи довідника, кличе <see cref="ExternalRegistryGuard"/> — записи
/// External-довідника вручну не змінюються (master — AF, D-49).
/// </summary>
/// <remarks>
/// ⚠ Чому IL, а не текст джерел: виклик writer'а буває в приватному помічнику чи лямбді транзакції,
/// а сторожі по тексту в цьому репозиторії вже червоніли на коментарі й переносі рядка. Тут
/// машини станів і замикання згортаються до типу верхнього рівня (як у
/// <see cref="ProjectPermissionCheckTests"/>), і правило — «тип, що пише записи, кличе гард».
/// <para>
/// «Пише записи» — будь-який виклик <see cref="WriteSinks"/>: методи
/// <see cref="RegistryEntryWriter"/>, що змінюють записи чи значення, і мутатори
/// <c>RegistryEntry</c> (видалення, вікно чинності, батько, назва). Правило суворіше за «публічні
/// обробники Application»: скануються Application, Infrastructure, Api і Adapters.Excel, тож новий
/// писач у будь-якому з них без гарду — червоний.
/// </para>
/// </remarks>
public sealed class ExternalRegistryManualEditGuardTests
{
    /// <summary>Виклики, що змінюють записи довідника: тип і метод.</summary>
    private static readonly (string Type, string Method)[] WriteSinks =
    [
        ("Ecr.Application.Registries.RegistryEntryWriter", "WriteAsync"),
        ("Ecr.Application.Registries.RegistryEntryWriter", "UpdateAsync"),
        ("Ecr.Application.Registries.RegistryEntryWriter", "ApplyValuesAsync"),
        ("Ecr.Application.Registries.RegistryEntryWriter", "SaveEntryAsync"),
        ("Ecr.Application.Registries.RegistryEntryWriter", "SaveBatchAsync"),
        ("Ecr.Application.Registries.RegistryEntryWriter", "AddEntry"),
        ("Ecr.Domain.Entities.Dictionaries.RegistryEntry", "SoftDelete"),
        ("Ecr.Domain.Entities.Dictionaries.RegistryEntry", "SetValidity"),
        ("Ecr.Domain.Entities.Dictionaries.RegistryEntry", "SetParent"),
        ("Ecr.Domain.Entities.Dictionaries.RegistryEntry", "Rename"),
    ];

    /// <summary>Сам гард.</summary>
    private const string GuardType = "Ecr.Application.Registries.ExternalRegistryGuard";

    /// <summary>Типи, що пишуть записи без гарду свідомо, — з причиною.</summary>
    /// <remarks>
    /// ⚠ <c>BindRegistryExternalKeyHandler</c>/<c>UnbindRegistryExternalKeyHandler</c> (прив'язка
    /// ключів — не правка даних) станом на 2026-09-29 в коді немає; з'являться — пишуть
    /// <c>dic.RegistryExternalKey</c>, а не записи, і сюди не потраплять. Якщо все ж кликатимуть
    /// writer — додати сюди з причиною.
    /// </remarks>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["Ecr.Infrastructure.Jobs.RegistrySyncJob"] =
            "Синк із зовнішнього джерела — і є шлях master-даних AF у External/Hybrid-довідник (D-49, S7).",
    };

    /// <summary>Типи, що містять самі стоки (writer), — не писачі, а точка запису.</summary>
    private static readonly HashSet<string> SinkOwners = new(StringComparer.Ordinal)
    {
        "Ecr.Application.Registries.RegistryEntryWriter",
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "D-211")]
    public void Кожен_писач_записів_довідника_кличе_гард_External()
    {
        var violations = Violations(Calls().ToList());

        Assert.True(
            violations.Count == 0,
            "Тип змінює записи довідника, але не кличе ExternalRegistryGuard.EnsureManualEditAllowed (D-211). "
            + "Поклич гард першою перевіркою після того, як відомий довідник, або — якщо це шлях master-даних — "
            + "допиши тип у Allowed з причиною:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "D-211")]
    public void Виняток_не_приховує_зробленого()
    {
        var calls = Calls().ToList();
        var writers = calls.Where(c => c.Kind == CallKind.Write).Select(c => c.Owner).ToHashSet(StringComparer.Ordinal);

        var stale = Allowed.Keys.Where(k => !writers.Contains(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "Винятки без запису — прибери: " + string.Join(", ", stale));

        // Виняток, який таки кличе гард, — більше не виняток.
        var guarded = calls.Where(c => c.Kind == CallKind.Guard).Select(c => c.Owner).ToHashSet(StringComparer.Ordinal);
        var needless = Allowed.Keys.Where(guarded.Contains).ToList();
        Assert.True(needless.Count == 0, "Виняток уже кличе гард — прибери: " + string.Join(", ", needless));
    }

    /// <summary>Сито бачить відомих писачів — інакше зламаний пошук дав би зелений тест.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "D-211")]
    public void Сито_бачить_писачів_і_гард()
    {
        var calls = Calls().ToList();
        string[] expected =
        [
            "Ecr.Application.Registries.UpsertRegistryEntryHandler",
            "Ecr.Application.Registries.ImportRegistryEntriesHandler",
            "Ecr.Application.Registries.Rows.RegistryBatchHandler",
            "Ecr.Application.Registries.DeleteRegistryEntryHandler",
            "Ecr.Application.Registries.SetEntryValidityHandler",
            "Ecr.Infrastructure.Jobs.RegistrySyncJob",
        ];

        foreach (var owner in expected)
        {
            Assert.Contains(calls, c => c.Owner == owner && c.Kind == CallKind.Write);
        }

        Assert.Contains(calls, c => c.Owner == "Ecr.Application.Registries.UpsertRegistryEntryHandler" && c.Kind == CallKind.Guard);
    }

    /// <summary>
    /// Мутаційний доказ самого правила: ті самі дані сканування без гарду в одному обробнику — червоні.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "D-211")]
    public void Правило_червоніє_без_гарду_в_обробнику()
    {
        const string owner = "Ecr.Application.Registries.SetEntryValidityHandler";
        var withoutGuard = Calls().Where(c => !(c.Owner == owner && c.Kind == CallKind.Guard)).ToList();

        Assert.Contains(Violations(withoutGuard), v => v.Contains(owner, StringComparison.Ordinal));
    }

    private static List<string> Violations(IReadOnlyList<Call> calls)
    {
        var guarded = calls.Where(c => c.Kind == CallKind.Guard).Select(c => c.Owner).ToHashSet(StringComparer.Ordinal);

        return [.. calls
            .Where(c => c.Kind == CallKind.Write && !guarded.Contains(c.Owner) && !Allowed.ContainsKey(c.Owner))
            .GroupBy(c => c.Owner, StringComparer.Ordinal)
            .Select(g => $"  {g.Key}: {string.Join(", ", g.Select(c => c.Target).Distinct().Order(StringComparer.Ordinal))}")
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Вид виклику.</summary>
    public enum CallKind
    {
        /// <summary>Зміна записів довідника.</summary>
        Write,

        /// <summary>Гард D-211.</summary>
        Guard,
    }

    /// <summary>Один виклик у IL.</summary>
    /// <param name="Owner">Тип верхнього рівня (замикання й машини станів згорнуто).</param>
    /// <param name="Target">Викликаний метод (<c>Тип::метод</c>).</param>
    /// <param name="Kind">Вид.</param>
    public sealed record Call(string Owner, string Target, CallKind Kind);

    private static IEnumerable<Call> Calls()
    {
        Assembly[] assemblies =
        [
            typeof(RegistryEntryWriter).Assembly,
            typeof(Ecr.Infrastructure.Persistence.EcrDbContext).Assembly,
            typeof(Ecr.Api.Controllers.SecurityController).Assembly,
            typeof(Ecr.Adapters.Excel.ExcelImporter).Assembly,
        ];

        foreach (var assembly in assemblies)
        {
            using var module = ModuleDefinition.ReadModule(assembly.Location);

            foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
            {
                var owner = Owner(method.DeclaringType);
                if (SinkOwners.Contains(owner) || owner == GuardType)
                {
                    continue;
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Ldftn or Code.Ldvirtftn)
                        || instruction.Operand is not MethodReference target)
                    {
                        continue;
                    }

                    var type = target.DeclaringType.FullName;
                    var name = $"{type}::{target.Name}";

                    if (type == GuardType && target.Name == nameof(ExternalRegistryGuard.EnsureManualEditAllowed))
                    {
                        yield return new Call(owner, name, CallKind.Guard);
                    }
                    else if (WriteSinks.Any(s => s.Type == type && s.Method == target.Name))
                    {
                        yield return new Call(owner, name, CallKind.Write);
                    }
                }
            }
        }
    }

    /// <summary>Тип верхнього рівня (крізь машини станів і замикання).</summary>
    private static string Owner(TypeDefinition type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type.FullName;
    }
}
