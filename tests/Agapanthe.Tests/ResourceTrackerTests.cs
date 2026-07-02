using Agapanthe.Core;

namespace Agapanthe.Tests;

public class ResourceTrackerTests : IDisposable
{
    public ResourceTrackerTests()
    {
        ResourceTracker.Reset();
        ResourceTracker.Enabled = true;
    }

    public void Dispose() => ResourceTracker.Reset();

    [Fact]
    public void BalancedRegisterUnregister_ReportsNoLeak()
    {
        ResourceTracker.Register("GpuBuffer");
        ResourceTracker.Register("GpuBuffer");
        ResourceTracker.Unregister("GpuBuffer");
        ResourceTracker.Unregister("GpuBuffer");

        Assert.Equal(0, ResourceTracker.LiveCount("GpuBuffer"));
        Assert.True(ResourceTracker.Report());
    }

    [Fact]
    public void MissingUnregister_ReportsLeak()
    {
        ResourceTracker.Register("GpuTexture");

        Assert.Equal(1, ResourceTracker.LiveCount("GpuTexture"));
        Assert.False(ResourceTracker.Report());
    }

    [Fact]
    public void Disabled_TracksNothing()
    {
        ResourceTracker.Enabled = false;
        ResourceTracker.Register("GpuBuffer");

        Assert.Equal(0, ResourceTracker.LiveCount("GpuBuffer"));
        Assert.True(ResourceTracker.Report());
    }
}
