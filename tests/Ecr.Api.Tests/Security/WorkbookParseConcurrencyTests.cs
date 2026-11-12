// tests/Ecr.Api.Tests/Security/WorkbookParseConcurrencyTests.cs

using System.Reflection;
using System.Runtime.CompilerServices;
using Ecr.Api.Controllers;
using Ecr.Api.Security;
using Ecr.Application.Errors;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// S-3 (аудит 2026-11, L2): розбір книги <c>.xlsx</c> — повна модель ClosedXML у пам'яті процесу. Межа одночасних розборів
/// (<see cref="ImportPreviewGate"/>, AN-123) мусить стояти на КОЖНОМУ ендпоінті, що розбирає книгу, а не лише на перегляді
/// імпорту документа.
/// </summary>
public sealed class WorkbookParseConcurrencyTests
{
    /// <summary>Сім дій, що розбирають книгу: ім'я = «Контролер.Дія».</summary>
    public static TheoryData<string> Actions => new()
    {
        "AirLimits.Analyse",
        "AirLimits.Preview",
        "AirLimits.Apply",
        "NaeTree.AnalyseImport",
        "NaeTree.PreviewImport",
        "NaeTree.ApplyImport",
        "PermitTableImport.ReadWorkbook",
    };

    private static readonly Dictionary<string, Type> Map = new()
    {
        ["AirLimits"] = typeof(AirLimitsController),
        ["NaeTree"] = typeof(NaeTreeController),
        ["PermitTableImport"] = typeof(PermitTableImportController),
    };

    [Theory]
    [MemberData(nameof(Actions))]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Дія_розбору_книги_бере_межу_як_службу_з_FromServices(string action)
    {
        var method = Resolve(action);

        var gate = Assert.Single(method.GetParameters(), p => p.ParameterType == typeof(ImportPreviewGate));
        Assert.NotNull(gate.GetCustomAttribute<FromServicesAttribute>());
    }

    [Theory]
    [MemberData(nameof(Actions))]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Межа_вичерпана_дія_відповідає_429_не_торкаючись_розбору(string action)
    {
        using var gate = new ImportPreviewGate(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:RateLimit:ImportPreviewConcurrency"] = "1",
                ["Security:RateLimit:ImportPreviewQueueLimit"] = "0",
            })
            .Build());
        using var busy = await gate.EnterAsync(CancellationToken.None);

        var method = Resolve(action);

        // ⚠ Контролер без залежностей: до обробника розбору дія дійти не має права (інакше NullReferenceException, не 429).
        var controller = RuntimeHelpers.GetUninitializedObject(method.DeclaringType!);
        var args = method.GetParameters().Select(p => ArgumentFor(p, gate)).ToArray();

        // ⛔ Мутація: прибрати `EnterAsync` з дії — тут NullReferenceException обробника, не BusinessRuleException.
        var refusal = await Assert.ThrowsAsync<BusinessRuleException>(
            async () => await (dynamic)method.Invoke(controller, args)!);
        Assert.Equal("ECR-REQ-0429", refusal.ErrorCode);
    }

    private static MethodInfo Resolve(string action)
    {
        var parts = action.Split('.');
        var type = Map[parts[0]];
        return type.GetMethod(parts[1]) ?? throw new InvalidOperationException(action);
    }

    private static object? ArgumentFor(ParameterInfo p, ImportPreviewGate gate)
    {
        if (p.ParameterType == typeof(ImportPreviewGate))
        {
            return gate;
        }

        if (p.ParameterType == typeof(IFormFile))
        {
            return new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "book.xlsx");
        }

        if (p.ParameterType == typeof(CancellationToken))
        {
            return CancellationToken.None;
        }

        if (p.ParameterType == typeof(string))
        {
            return p.Name == "code" ? "T" : null;
        }

        return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
    }
}
