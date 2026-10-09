using System.Globalization;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// AN-108 / P2-02: <c>GET /jobs/{id}</c> віддає <c>writtenCount</c> перерахунку формул із СИРОГО конверта,
/// щоб клієнт після перерахунку, який нічого не записав, не перечитував усі змонтовані зрізи документа.
/// </summary>
/// <remarks>Мутація: прибери гілку <c>NoneKey</c> або читання <c>written</c> — тести червоніють.</remarks>
public sealed class FormulaRecalcOutcomeTests
{
    private static string Done(string written) =>
        JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            FormulaRecalcOutcome.DoneKey,
            new Dictionary<string, string> { [FormulaRecalcOutcome.WrittenParam] = written }));

    private static string None() =>
        JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(FormulaRecalcOutcome.NoneKey));

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Завершений_перерахунок_віддає_записані_комірки(int written)
    {
        Assert.Equal(
            written,
            FormulaRecalcOutcome.WrittenCountOf("Succeeded", Done(written.ToString(CultureInfo.InvariantCulture))));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Змінених_комірок_немає_це_нуль()
    {
        Assert.Equal(0, FormulaRecalcOutcome.WrittenCountOf("Succeeded", None()));
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Failed")]
    [InlineData("Queued")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Незавершена_задача_невідомо(string state)
    {
        Assert.Null(FormulaRecalcOutcome.WrittenCountOf(state, Done("0")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Інша_задача_старий_текст_або_зіпсоване_число_невідомо()
    {
        Assert.Null(FormulaRecalcOutcome.WrittenCountOf("Succeeded", null));
        Assert.Null(FormulaRecalcOutcome.WrittenCountOf("Succeeded", "Перераховано 0 комірок"));
        Assert.Null(FormulaRecalcOutcome.WrittenCountOf("Succeeded", Done("x")));
        Assert.Null(FormulaRecalcOutcome.WrittenCountOf("Succeeded", Done("-1")));
        Assert.Null(FormulaRecalcOutcome.WrittenCountOf(
            "Succeeded",
            JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
                JobCompletionWarning.NotificationDoneKey,
                new Dictionary<string, string> { ["written"] = "0" }))));
    }
}
