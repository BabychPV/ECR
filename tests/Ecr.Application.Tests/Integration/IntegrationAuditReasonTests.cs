using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Причина зміни налаштувань збору — конверт <c>integrationAudit.*</c>, а не українська фраза
/// (P3 живого проходу екрана джерел: англійський інтерфейс журналу структурних змін бачив українську).
/// </summary>
public sealed class IntegrationAuditReasonTests
{
    [Fact]
    public void Причина_кодується_конвертом_з_ключем_і_параметрами_інваріантною_культурою()
    {
        var raw = IntegrationConfigAudit.Reason(
            "integrationAudit.sourceEntityBound", ("entity", "FLD-1"), ("registry", (int?)12345));

        Assert.True(JobProgressMessageCodec.TryDecode(raw, out var envelope));
        Assert.Equal("integrationAudit.sourceEntityBound", envelope.Key);
        Assert.Equal("FLD-1", envelope.Params!["entity"]);
        Assert.Equal("12345", envelope.Params!["registry"]);
        Assert.DoesNotContain("Сутність", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Причина_без_параметрів_не_несе_порожнього_p()
    {
        var raw = IntegrationConfigAudit.Reason("integrationAudit.sourceEntityUnbound");

        Assert.Equal("{\"k\":\"integrationAudit.sourceEntityUnbound\"}", raw);
    }
}
