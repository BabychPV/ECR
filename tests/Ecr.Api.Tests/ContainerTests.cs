// tests/Ecr.Api.Tests/ContainerTests.cs
using System.Reflection;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Контейнер уміє створити те, що застосунок від нього вимагатиме.
/// </summary>
/// <remarks>
/// ⚠ Файл з'явився після конкретного дефекту (`Q-077`), а не «про всяк
/// випадок». <c>IBackgroundJobScheduler</c> не був зареєстрований, через це не
/// резолвився <c>RecalculateDocumentHandler</c>, а через нього не створювався
/// <b>весь</b> <c>DocumentsController</c> — і <c>500</c> отримували всі його
/// ендпоінти, включно з поданням, якому черга не потрібна взагалі.
///
/// Перевірка контейнера при старті цього не ловить: вона звіряє **реєстрації**,
/// а контролери створюються ліниво, на першому запиті. Тому єдиний спосіб
/// побачити дірку заздалегідь — попросити контейнер побудувати кожен контролер
/// тут.
/// </remarks>
[Collection("SqlServer")]
public sealed class ContainerTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Кожен_контролер_створюється_контейнером()
    {
        using var app = new EcrApiFactory(sql);
        using var scope = app.Services.CreateScope();

        var controllers = typeof(Program).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && t.IsAssignableTo(typeof(ControllerBase)))
            .Where(t => !DeferredControllers.Contains(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(controllers);

        var broken = new List<string>();
        foreach (var controller in controllers)
        {
            try
            {
                ActivatorUtilities.CreateInstance(scope.ServiceProvider, controller);
            }
            catch (InvalidOperationException ex)
            {
                // Саме InvalidOperationException: так контейнер повідомляє про
                // незареєстровану залежність. Ловити все підряд означало б
                // ховати справжні помилки конструкторів.
                broken.Add($"{controller.Name}: {ex.Message}");
            }
        }

        Assert.Empty(broken);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Кожен_обробник_застосунку_резолвиться()
    {
        using var app = new EcrApiFactory(sql);
        using var scope = app.Services.CreateScope();

        // Обробники реєструються ПОІМЕННО (Assembly.Load за рядком заборонений
        // архітектурним правилом 8). Ціна — список поповнюють руками; цей тест
        // і є розплатою за забутий рядок.
        var handlers = typeof(Ecr.Application.DependencyInjection).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true, IsPublic: true }
                        && t.Name.EndsWith("Handler", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(handlers);

        var unresolved = handlers
            .Where(h => !DeferredHandlers.Contains(h.Name))
            .Where(h => scope.ServiceProvider.GetService(h) is null)
            .Select(h => h.Name)
            .ToList();

        Assert.Empty(unresolved);

        // Список відкладених має ЗМЕНШУВАТИСЯ. Обробник, який уже
        // зареєстрували, але забули прибрати звідси, знову робить пропуск
        // невидимим — рівно так і з'явився `Q-077`.
        var stale = DeferredHandlers
            .Where(name => handlers.Exists(h => h.Name == name)
                           && scope.ServiceProvider.GetService(
                               handlers.Find(h => h.Name == name)!) is not null)
            .ToList();

        Assert.Empty(stale);
    }

    /// <summary>Контролери, чиї обробники належать пізнішим етапам.</summary>
    /// <remarks>
    /// Це не «дозволені винятки», а розклад: кожен етап прибирає свій рядок,
    /// і порожній список означає, що застосунок піднімається цілком.
    /// </remarks>
    private static readonly HashSet<string> DeferredControllers = new(StringComparer.Ordinal)
    {
        // ⚠ Список ПОРОЖНІЙ: усі контролери створюються контейнером.
        // Кожен етап прибирав свій рядок; порожній список означає, що
        // застосунок піднімається цілком.
    };

    /// <summary>Обробники, реалізації яких належать пізнішим етапам.</summary>
    private static readonly HashSet<string> DeferredHandlers = new(StringComparer.Ordinal)
    {
        // ⚠ Список ПОРОЖНІЙ: усі обробники застосунку резолвляться. Останні
        // сім прибрано на Етапі 4 разом із реалізаціями довідників, одиниць і
        // розрахунків.
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Жоден_порт_застосунку_не_лишився_без_реалізації_мовчки()
    {
        // Порти, чиї реалізації належать пізнішим етапам. Список має
        // ЗМЕНШУВАТИСЯ: порт, який уже реалізували, але забули прибрати
        // звідси, знову робить пропуск невидимим.
        //
        // ⚠ Список ПОРОЖНІЙ: етапів попереду більше немає, і кожен порт
        // застосунку має реалізацію.
        string[] deferred = [];

        // ⚠ Збірки підвантажуються ЯВНО. `AppDomain.GetAssemblies()` бачить
        // лише те, що вже завантажив CLR, а завантажує він ліниво — на першу
        // згадку типу. Через це тест був чутливий до порядку виконання:
        // запущений окремо, він «не бачив» Ecr.Calculations і
        // Ecr.Adapters.PiAf і оголошував їхні порти нереалізованими.
        Type[] anchors =
        [
            typeof(Ecr.Infrastructure.Persistence.EcrDbContext),
            typeof(Ecr.Calculations.CalculationOrchestrator),
            typeof(Ecr.Adapters.PiAf.CollectionRunner),
            typeof(Ecr.Adapters.Excel.ExcelExporter),
            typeof(Ecr.Api.Auth.CurrentUser),
        ];

        Assert.All(anchors, t => Assert.NotNull(t.Assembly));

        var ports = typeof(Ecr.Application.Ports.IUserStore).Assembly
            .GetTypes()
            .Where(t => t.IsInterface && t.Name.StartsWith('I') && t.Namespace is not null
                        && (t.Namespace.EndsWith(".Ports", StringComparison.Ordinal)
                            || t.Namespace.EndsWith(".Security", StringComparison.Ordinal)))
            .Select(t => t.Name)
            .ToList();

        var implemented = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.StartsWith("Ecr.", StringComparison.Ordinal) == true)
            .SelectMany(SafeTypes)
            .Where(t => t is { IsAbstract: false, IsClass: true })
            .SelectMany(t => t.GetInterfaces())
            .Select(i => i.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = ports
            .Except(implemented, StringComparer.Ordinal)
            .Except(deferred, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Часткове завантаження краще за падіння тесту: нас цікавлять
            // типи, які вдалося прочитати.
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
