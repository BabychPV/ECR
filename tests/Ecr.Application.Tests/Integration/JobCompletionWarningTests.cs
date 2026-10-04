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

    /// <summary>
    /// CL-5: <c>failures</c> — збої для доставки без рядка «адресатів немає»; коли він є, рішення
    /// за ним, а не за <c>count</c>. Конверт без нього (до CL-5) читається за <c>count</c>, як раніше.
    /// </summary>
    [Theory]
    [InlineData(1, 0, 0, null)]
    [InlineData(3, 2, 0, "SucceededWithErrors")]
    [InlineData(3, 2, 1, null)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Лише_рядок_про_адресатів_не_попередження_а_збої_без_відправки_так(
        int count, int failures, int sent, string? expected)
    {
        var message = JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            JobCompletionWarning.NotificationDoneKey,
            new Dictionary<string, string>
            {
                ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [JobCompletionWarning.FailuresParam] = failures.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sent"] = sent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pending"] = "0",
            }));

        Assert.Equal(expected, JobCompletionWarning.EffectiveStateOf("Succeeded", message));
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
