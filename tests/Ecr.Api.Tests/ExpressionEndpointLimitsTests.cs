using System.Reflection;
using Ecr.Api.Controllers;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Друга лінія захисту ендпоінта перевірки виразів: стеля тіла запиту.
/// </summary>
/// <remarks>
/// ⚠ Тест фіксує ОГОЛОШЕННЯ, а не роботу обмежувача: вимикає запит сам
/// ASP.NET Core, і перевіряти тут його власну поведінку означало б тестувати
/// фреймворк. Предмет перевірки — що ендпоінт стелю має і що число не
/// «попливло»; сама відмова від надто глибокого виразу доводиться поведінково
/// в <c>ExpressionDepthGuardTests</c>.
/// </remarks>
public sealed class ExpressionEndpointLimitsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Перевірка_виразу_оголошує_стелю_тіла_запиту_в_64_КіБ()
    {
        var action = typeof(ExpressionsController)
            .GetMethod(nameof(ExpressionsController.Validate), BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(action);

        // ⚠ Читаються ДАНІ атрибута, а не сам атрибут: властивість `Bytes` у
        // `RequestSizeLimitAttribute` не публічна, тож `GetCustomAttribute`
        // довів би лише наявність. Аргумент конструктора видно завжди — і це
        // саме те число, яке поїде в продуктив.
        var limit = action!.GetCustomAttributesData()
            .SingleOrDefault(a => a.AttributeType == typeof(RequestSizeLimitAttribute));

        Assert.NotNull(limit);

        // ⛔ Звірка з ЯВНИМ числом, а не лише з константою контролера: сама з
        // собою константа збіглася б за будь-якого значення.
        Assert.Equal(65536L, Convert.ToInt64(limit!.ConstructorArguments[0].Value, null));
        Assert.Equal(65536, ExpressionsController.MaxExpressionBodyBytes);
    }
}
