using Kanban.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class PlcBatchReadPlanCacheTests
{
    [Fact]
    public void GetOrBuild_SameSignature_ReusesPlan()
    {
        var cache = new PlcBatchReadPlanCache();
        var buildCount = 0;
        IReadOnlyList<PlcBatchReadPlanGroup> Factory()
        {
            buildCount++;
            return [];
        }

        cache.GetOrBuild("same", Factory);
        cache.GetOrBuild("same", Factory);

        Assert.Equal(1, buildCount);
        Assert.Equal(1, cache.RebuildCount);
    }

    [Fact]
    public void GetOrBuild_ChangedSignature_RebuildsPlan()
    {
        var cache = new PlcBatchReadPlanCache();
        var buildCount = 0;
        IReadOnlyList<PlcBatchReadPlanGroup> Factory()
        {
            buildCount++;
            return [];
        }

        cache.GetOrBuild("first", Factory);
        cache.GetOrBuild("second", Factory);

        Assert.Equal(2, buildCount);
        Assert.Equal(2, cache.RebuildCount);
    }
}
