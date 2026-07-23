using SqlSugar;

namespace Wcs.Core.Tests;

public class PersistenceIdTests
{
    [Fact]
    public void SnowflakeIds_AreUniqueUnderParallelLoad()
    {
        const int count = 100_000;
        var ids = new long[count];

        Parallel.For(0, count, index =>
        {
            ids[index] = SnowFlakeSingle.Instance.NextId();
        });

        Assert.All(ids, id => Assert.True(id > 0));
        Assert.Equal(count, ids.Distinct().Count());
    }
}
