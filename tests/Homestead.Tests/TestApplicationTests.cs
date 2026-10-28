using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class TestApplicationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Test_hosts_load_configuration_without_file_watchers(string environment)
    {
        using var data = new TemporaryData();
        using var app = new TestApplication(data.Path, environment);
        var configuration = (IConfigurationRoot)app.Services.GetRequiredService<IConfiguration>();
        var providers = configuration.Providers.OfType<FileConfigurationProvider>().ToArray();
        Assert.NotEmpty(providers);
        Assert.All(providers, provider => Assert.False(provider.Source.ReloadOnChange,
            $"Test configuration must not watch {provider.Source.Path}; many isolated hosts exhaust Linux inotify instances."));
        Assert.Equal(data.Path, configuration["HOMESTEAD_DATA_PATH"]);
    }
}
