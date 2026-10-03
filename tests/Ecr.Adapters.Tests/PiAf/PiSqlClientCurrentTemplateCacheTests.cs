using Ecr.Adapters.PiAf;
using Ecr.Application.Ports;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// L4-09: <c>PiSqlClientDataSource.ReadCurrentAsync</c> читає шаблон елемента раз на елемент, а не на
/// кожен атрибут (кожне читання — окремий запит RTQP).
/// </summary>
/// <remarks>
/// ⚠ Живого RTQP у контурі немає (<c>OdbcDataReader</c> ззовні не зробиш), тому перевіряється сам цикл
/// шляхів (<c>ReadCurrentPathsAsync</c>) з підставними запитами — той самий код, що кличе
/// <c>ReadCurrentAsync</c>.
/// </remarks>
public sealed class PiSqlClientCurrentTemplateCacheTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Шаблон_читається_раз_на_елемент_а_не_на_атрибут()
    {
        var templateCalls = new List<string>();
        var pointCalls = new List<string>();

        var result = await PiSqlClientDataSource.ReadCurrentPathsAsync(
            ["Stack1|Capacity", "Stack1|Name", "Stack1|Height", "Stack2|Capacity", "Gone|Capacity", "Gone|Name"],
            element =>
            {
                templateCalls.Add(element);
                return Task.FromResult(element == "Gone" ? null : "StackTemplate");
            },
            (path, element, template, attribute) =>
            {
                pointCalls.Add(path);
                Assert.Equal("StackTemplate", template);
                return Task.FromResult<SourceDataPoint?>(new SourceDataPoint(path, Now, 1m, null, null, "Good"));
            });

        Assert.Equal(["Stack1", "Stack2", "Gone"], templateCalls);
        Assert.Equal(4, pointCalls.Count);
        Assert.Equal(4, result.Values.Count);

        // Відсутній елемент — відмова кожного його шляху, як і раніше, але без повторного запиту шаблону.
        Assert.Equal(["Gone|Capacity", "Gone|Name"], result.Failures.Select(f => f.SourcePath));
        Assert.All(result.Failures, f => Assert.Equal("err.ECR-INT-0404.sourcePathNotFound", f.MessageKey));
    }

    [Fact]
    public async Task Порожнє_значення_шляху_відмова_шляху_currentValueUnreadable()
    {
        var result = await PiSqlClientDataSource.ReadCurrentPathsAsync(
            ["Stack1|Capacity", "Stack1|Name"],
            _ => Task.FromResult<string?>("StackTemplate"),
            (path, _, _, _) => Task.FromResult(path.EndsWith("Name", StringComparison.Ordinal)
                ? null
                : new SourceDataPoint(path, Now, 1m, null, null, "Good")));

        Assert.Equal("Stack1|Capacity", Assert.Single(result.Values).SourcePath);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("ECR-INT-0503", failure.ErrorCode);
        Assert.Equal("err.ECR-INT-0503.currentValueUnreadable", failure.MessageKey);
    }
}
