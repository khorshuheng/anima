namespace Anima.Dashboard.Service.Tests;

public sealed class DashboardRoutesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    public void Workflows_root_maps_to_the_workflow_list(string? remainder)
    {
        Assert.Equal("api/workflows", DashboardRoutes.Workflows(remainder));
    }

    [Fact]
    public void Workflows_keeps_id_subpath_and_query()
    {
        Assert.Equal(
            "api/workflows/42/questions?open=false",
            DashboardRoutes.Workflows("42/questions", "?open=false"));
    }

    [Fact]
    public void Graph_without_caps_targets_the_plain_snapshot()
    {
        Assert.Equal("api/graph", DashboardRoutes.Graph());
    }

    [Fact]
    public void Graph_forwards_only_the_caps_that_were_requested()
    {
        Assert.Equal("api/graph?maxNodes=500", DashboardRoutes.Graph(maxNodes: 500));
        Assert.Equal("api/graph?maxNodes=500&maxEdges=1000", DashboardRoutes.Graph(500, 1000));
    }

    [Fact]
    public void Graph_forwards_the_newest_window()
    {
        Assert.Equal(
            "api/graph?maxNodes=500&maxEdges=1000&newest=true",
            DashboardRoutes.Graph(500, 1000, newest: true));
        Assert.Equal("api/graph?newest=false", DashboardRoutes.Graph(newest: false));
    }

    [Fact]
    public void Graph_forwards_the_since_id_cursor()
    {
        Assert.Equal(
            "api/graph?maxNodes=500&newest=true&sinceId=58",
            DashboardRoutes.Graph(maxNodes: 500, newest: true, sinceId: 58));
    }

    [Fact]
    public void Bus_snapshot_targets_the_bus_diagnostics_endpoint()
    {
        Assert.Equal("api/bus/snapshot", DashboardRoutes.BusSnapshot());
    }

    [Fact]
    public void Bus_stream_forwards_the_topics_filter()
    {
        Assert.Equal("api/bus/stream", DashboardRoutes.BusStream());
        Assert.Equal("api/bus/stream?topics=anima.**", DashboardRoutes.BusStream("?topics=anima.**"));
    }
}
