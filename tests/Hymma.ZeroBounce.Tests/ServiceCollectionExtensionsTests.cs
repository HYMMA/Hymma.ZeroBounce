using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hymma.ZeroBounce.Tests;

public class ServiceCollectionExtensionsTests
{
    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    [Fact]
    public void AddZeroBounce_WithApiKey_ResolvesClient()
    {
        var services = NewServices();
        services.AddZeroBounce("test_key");

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IZeroBounceClient>();
        var options = provider.GetRequiredService<IOptions<ZeroBounceOptions>>().Value;

        Assert.IsType<ZeroBounceClient>(client);
        Assert.Equal("test_key", options.ApiKey);
        Assert.Equal(ZeroBounceOptions.DefaultBaseUrl, options.BaseUrl);
        Assert.Equal(30, options.TimeoutSeconds);
        Assert.Equal(120, options.BatchTimeoutSeconds);
        Assert.False(options.ThrowOnError);
        Assert.False(options.IncludeRawResponse);
    }

    [Fact]
    public void AddZeroBounce_WithAction_AppliesEveryOption()
    {
        var services = NewServices();
        services.AddZeroBounce(o =>
        {
            o.ApiKey = "test_key";
            o.BaseUrl = ZeroBounceOptions.EuBaseUrl;
            o.TimeoutSeconds = 10;
            o.BatchTimeoutSeconds = 60;
            o.ThrowOnError = true;
            o.IncludeRawResponse = true;
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ZeroBounceOptions>>().Value;

        Assert.Equal(ZeroBounceOptions.EuBaseUrl, options.BaseUrl);
        Assert.Equal(10, options.TimeoutSeconds);
        Assert.Equal(60, options.BatchTimeoutSeconds);
        Assert.True(options.ThrowOnError);
        Assert.True(options.IncludeRawResponse);
        Assert.NotNull(provider.GetRequiredService<IZeroBounceClient>());
    }

    [Fact]
    public void AddZeroBounce_WithConfiguration_BindsTheZeroBounceSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZeroBounce:ApiKey"] = "from_config",
                ["ZeroBounce:TimeoutSeconds"] = "15",
                ["ZeroBounce:BaseUrl"] = ZeroBounceOptions.UsBaseUrl
            })
            .Build();
        var services = NewServices();
        services.AddZeroBounce(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ZeroBounceOptions>>().Value;

        Assert.Equal("from_config", options.ApiKey);
        Assert.Equal(15, options.TimeoutSeconds);
        Assert.Equal(ZeroBounceOptions.UsBaseUrl, options.BaseUrl);
        Assert.NotNull(provider.GetRequiredService<IZeroBounceClient>());
    }

    [Fact]
    public void AddZeroBounce_MissingApiKey_FailsLoudlyOnResolve()
    {
        var services = NewServices();
        services.AddZeroBounce(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        Assert.Throws<ArgumentException>(() => provider.GetRequiredService<IZeroBounceClient>());
    }

    [Fact]
    public void AddZeroBounce_ReturnsSameCollectionForChaining()
    {
        var services = NewServices();

        Assert.Same(services, services.AddZeroBounce("test_key"));
    }
}
