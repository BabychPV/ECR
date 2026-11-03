using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;

namespace Ecr.Api.Startup;

/// <summary>
/// Прогрів метаданих моделі MVC для параметрів УСІХ дій контролерів — один раз, в одному потоці, до того, як
/// Kestrel прийме перший запит.
/// </summary>
/// <remarks>
/// ⛔ Причина — гонитва в самому ASP.NET Core (.NET 10), не в нашому коді. <c>ModelMetadata.GetHashCode()</c>
/// кешує хеш у полі <c>int? _hashCode</c> без синхронізації: два потоки, що вперше хешують ОДИН екземпляр
/// метаданих, можуть прочитати «розірване» значення (<c>HasValue</c> уже записано, саме число — ще ні). Перший
/// хеш параметра конструктора record-типу робить <c>ModelMetadata.CalculateRecordTypeConstructorDetails</c>,
/// кладучи параметр у словник <c>_parameterMapping</c>; з хибним хешем ключ лягає не в той кошик, і
/// <c>DefaultComplexObjectValidationStrategy.Enumerator.MoveNext</c> назавжди (до перезапуску!) кидає
/// <c>InvalidOperationException: No property found that maps to constructor parameter …</c> → 500 на кожен
/// запит до дії. Заразом там само прапор <c>_recordTypeConstructorDetailsCalculated</c> ставиться ДО запису
/// словника (вікно для NullReferenceException). 149 зі 150 тіл запитів у <c>Ecr.Api</c> — позиційні record-и.
///
/// Прогрів обходить граф метаданих кожного параметра (параметри конструктора, властивості, елементи колекцій),
/// хешує кожен вузол і проганяє штатний <see cref="IObjectModelValidator"/> на неініціалізованому екземплярі
/// кожного record-вузла — тим самим шляхом, що й <c>ParameterBinder</c>. Після цього всі ліниві поля, на яких
/// стоїть гонитва, уже обчислені, і конкурентні перші запити лише читають їх. Контракт (JSON/OpenAPI) не
/// змінюється. Помилка прогріву старт не валить — лише попередження в журнал.
/// </remarks>
public static partial class MvcModelMetadataWarmup
{
    /// <summary>Межа глибини обходу графа (захист від патологічних графів типів).</summary>
    private const int MaxDepth = 16;

    /// <summary>Підсумок прогріву.</summary>
    /// <param name="Actions">Дій контролерів.</param>
    /// <param name="Nodes">Вузлів метаданих прогріто (хеш, валідатори, властивості).</param>
    /// <param name="Records">Record-вузлів прогнано через валідатор.</param>
    /// <param name="Failures">Вузлів, на яких прогрів упав (див. попередження в журналі).</param>
    public sealed record Result(int Actions, int Nodes, int Records, int Failures);

    /// <summary>Прогріває метадані параметрів усіх дій контролерів.</summary>
    /// <param name="services">Кореневий постачальник сервісів застосунку.</param>
    /// <param name="logger">Журнал старту.</param>
    public static Result Warm(IServiceProvider services, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return WarmAll(services, logger);
        }
#pragma warning disable CA1031 // прогрів — оптимізація; старт через нього не падає
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNodeFailed(logger, "(усі дії)", ex.GetType().Name, ex.Message);
            return new Result(0, 0, 0, 1);
        }
    }

    private static Result WarmAll(IServiceProvider services, ILogger logger)
    {
        var actions = services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
        var metadataProvider = services.GetRequiredService<IModelMetadataProvider>();
        var validator = services.GetRequiredService<IObjectModelValidator>();

        using var scope = services.CreateScope();
        var seen = new HashSet<ModelMetadata>(ReferenceEqualityComparer.Instance);
        var validated = new HashSet<ModelMetadata>(ReferenceEqualityComparer.Instance);
        var failures = 0;

        foreach (var action in actions)
        {
            var roots = new List<ModelMetadata>();
            foreach (var parameter in action.Parameters)
            {
                if (Root(metadataProvider, parameter, containerType: null, logger, ref failures) is { } root)
                {
                    roots.Add(root);
                }
            }

            var controllerType = (action as ControllerActionDescriptor)?.ControllerTypeInfo.AsType();
            foreach (var property in action.BoundProperties)
            {
                if (Root(metadataProvider, property, controllerType, logger, ref failures) is { } root)
                {
                    roots.Add(root);
                }
            }

            foreach (var root in roots)
            {
                var records = new List<ModelMetadata>();
                Walk(root, seen, records, 0, logger, ref failures);
                foreach (var record in records)
                {
                    if (validated.Add(record))
                    {
                        ValidateOnce(validator, scope.ServiceProvider, action, record, logger, ref failures);
                    }
                }
            }
        }

        return new Result(actions.Count, seen.Count, validated.Count, failures);
    }

    // Той самий вузол, що бере ControllerBinderDelegateProvider: метадані ПАРАМЕТРА (за ParameterInfo) або
    // властивості контролера (за типом контролера й іменем).
    private static ModelMetadata? Root(
        IModelMetadataProvider provider, ParameterDescriptor parameter, Type? containerType, ILogger logger,
        ref int failures)
    {
        try
        {
            if (containerType is not null)
            {
                return provider.GetMetadataForProperty(containerType, parameter.Name);
            }

            return provider is ModelMetadataProvider typed && parameter is ControllerParameterDescriptor controller
                ? typed.GetMetadataForParameter(controller.ParameterInfo, controller.ParameterType)
                : provider.GetMetadataForType(parameter.ParameterType);
        }
#pragma warning disable CA1031 // прогрів — оптимізація; старт через нього не падає
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures++;
            LogNodeFailed(logger, parameter.ParameterType.FullName ?? parameter.Name, ex.GetType().Name, ex.Message);
            return null;
        }
    }

    private static void Walk(
        ModelMetadata node, HashSet<ModelMetadata> seen, List<ModelMetadata> records, int depth, ILogger logger,
        ref int failures)
    {
        if (depth > MaxDepth || !seen.Add(node))
        {
            return;
        }

        try
        {
            // Кешує ModelMetadata._hashCode в одному потоці — це і є вада, яку закриває прогрів.
            _ = node.GetHashCode();
            _ = node.ValidatorMetadata;
            _ = node.HasValidators;
            _ = node.ValidateChildren;
            _ = node.PropertyValidationFilter;

            // Як ValidationVisitor: дітей вузла з ValidateChildren = false (CancellationToken, Stream, IFormFile…) не обходить.
            if (!node.ValidateChildren)
            {
                return;
            }

            if (node.IsEnumerableType && node.ElementMetadata is { } element)
            {
                Walk(element, seen, records, depth + 1, logger, ref failures);
            }

            if (!node.IsComplexType)
            {
                return;
            }

            if (node.BoundConstructor is { } constructor)
            {
                _ = constructor.GetHashCode();
                foreach (var parameter in constructor.BoundConstructorParameters ?? [])
                {
                    Walk(parameter, seen, records, depth + 1, logger, ref failures);
                }

                if (!node.ModelType.IsAbstract)
                {
                    records.Add(node);
                }
            }

            foreach (var property in node.Properties)
            {
                Walk(property, seen, records, depth + 1, logger, ref failures);
            }
        }
#pragma warning disable CA1031 // прогрів — оптимізація; старт через нього не падає
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures++;
            LogNodeFailed(logger, node.ModelType.FullName ?? node.ModelType.Name, ex.GetType().Name, ex.Message);
        }
    }

    // Штатна валідація неініціалізованого екземпляра: рахує відображення «параметр конструктора → властивість»
    // (CalculateRecordTypeConstructorDetails) і кеш валідаторів — у тому самому вузлі, що й ParameterBinder.
    private static void ValidateOnce(
        IObjectModelValidator validator, IServiceProvider requestServices, ActionDescriptor action,
        ModelMetadata record, ILogger logger, ref int failures)
    {
        try
        {
            var model = RuntimeHelpers.GetUninitializedObject(record.ModelType);
            var context = new ActionContext(
                new DefaultHttpContext { RequestServices = requestServices }, new RouteData(), action,
                new ModelStateDictionary());

            if (validator is ObjectModelValidator typed)
            {
                typed.Validate(context, new ValidationStateDictionary(), string.Empty, model, record);
            }
            else
            {
                validator.Validate(context, new ValidationStateDictionary(), string.Empty, model);
            }
        }
#pragma warning disable CA1031 // прогрів — оптимізація; старт через нього не падає
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures++;
            LogNodeFailed(logger, record.ModelType.FullName ?? record.ModelType.Name, ex.GetType().Name, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Старт: прогрів метаданих MVC для {Type} не вдався ({Error}: {Message}). Перші одночасні запити до "
            + "дії з цим типом можуть впасти з 500 (гонитва ModelMetadata.GetHashCode).")]
    private static partial void LogNodeFailed(ILogger logger, string type, string error, string message);
}
