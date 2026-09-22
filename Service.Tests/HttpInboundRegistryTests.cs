namespace Service.Tests;

public sealed class HttpInboundRegistryTests
{
    [Fact]
    public void TryGetRoute_UnknownKey_ReturnsFalse()
    {
        var registry = new HttpInboundRegistry();

        var found = registry.TryGetRoute("unknown", out var route);

        Assert.False(found);
        Assert.Null(route);
    }

    [Fact]
    public void RegisterRoute_ThenTryGetRoute_ReturnsRegisteredRoute()
    {
        var registry = new HttpInboundRegistry();
        var route = new FakeHttpInboundRoute();

        using var registration = registry.RegisterRoute("orders", route);
        var found = registry.TryGetRoute("orders", out var resolved);

        Assert.True(found);
        Assert.Same(route, resolved);
    }

    [Fact]
    public void RegisterRoute_IsCaseInsensitiveOnRouteKey()
    {
        var registry = new HttpInboundRegistry();
        var route = new FakeHttpInboundRoute();

        using var registration = registry.RegisterRoute("Orders", route);

        Assert.True(registry.TryGetRoute("orders", out _));
    }

    [Fact]
    public void RegisterRoute_SameKeyTwice_LatestRegistrationWins()
    {
        var registry = new HttpInboundRegistry();
        var first = new FakeHttpInboundRoute();
        var second = new FakeHttpInboundRoute();

        using var firstRegistration = registry.RegisterRoute("orders", first);
        using var secondRegistration = registry.RegisterRoute("orders", second);

        registry.TryGetRoute("orders", out var resolved);
        Assert.Same(second, resolved);
    }

    [Fact]
    public void DisposingRegistration_RemovesRoute()
    {
        var registry = new HttpInboundRegistry();
        var route = new FakeHttpInboundRoute();
        var registration = registry.RegisterRoute("orders", route);

        registration.Dispose();

        Assert.False(registry.TryGetRoute("orders", out _));
    }

    private sealed class FakeHttpInboundRoute : IHttpInboundRoute
    {
        public void Enqueue(HttpInboundRequest request) => throw new NotSupportedException();

        public Task<HttpInboundReply?> EnqueueAndAwaitReplyAsync(HttpInboundRequest request, TimeSpan timeout, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public bool TryCompleteReply(string correlationId, HttpInboundReply reply) => throw new NotSupportedException();
    }
}
