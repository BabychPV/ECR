using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Дайджест сповіщень, що завершився <c>Succeeded</c> зі збоями й нулем
/// відправлених, дає похідний стан <c>SucceededWithErrors</c> (попередження)
/// без зміни автомата станів і без повторів (дублів у <c>NotificationOutbox</c>).
/// </summary>
public sealed class JobCompletionWarningTests
{
    private static string Done(int count, int sent, string key = JobCompletionWarning.NotificationDoneKey) =>
        JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            key,
            new Dictionary<string, string>
            {
                ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sent"] = sent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pending"] = "0",
            }));

    [Theory]
    [InlineData(3, 0, "Succeeded", "SucceededWithErrors")]
    [InlineData(3, 2, "Succeeded", null)]
    [InlineData(0, 0, "Succeeded", null)]
    [InlineData(3, 0, "Failed", null)]
    [InlineData(3, 0, "Running", null)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Попередження_лише_за_збоїв_без_жодної_відправки(int count, int sent, string state, string? expected)
    {
        Assert.Equal(expected, JobCompletionWarning.EffectiveStateOf(state, Done(count, sent)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Чужий_ключ_і_не_конверт_попередження_не_дають()
    {
        Assert.Null(JobCompletionWarning.EffectiveStateOf("Succeeded", Done(3, 0, "jobs.consistencyDone")));
        Assert.Null(JobCompletionWarning.EffectiveStateOf("Succeeded", "export-abc"));
        Assert.Null(JobCompletionWarning.EffectiveStateOf("Succeeded", null));
    }
}
