using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// PS-P1A (D-PS-3): <c>HDR.&lt;Lookup&gt;</c> давав Null, бо
/// <see cref="HeaderValueMapping.ToExpressionValue"/> ігнорував
/// <c>ValueRegistryEntryId</c>. Червоний до фіксу.
/// </summary>
public sealed class HeaderValueMappingTests
{
    [Fact]
    public void Lookup_header_value_is_registry_entry_id_as_number()
    {
        var value = HeaderValueMapping.ToExpressionValue(new DocumentHeaderValueData { ValueRegistryEntryId = 101 });

        Assert.False(value.IsNull);
        Assert.Equal(101m, value.AsNumber());
    }

    [Fact]
    public void Empty_and_missing_header_stay_null()
    {
        Assert.True(HeaderValueMapping.ToExpressionValue(null).IsNull);
        Assert.True(HeaderValueMapping.ToExpressionValue(DocumentHeaderValueData.Empty).IsNull);
    }
}
