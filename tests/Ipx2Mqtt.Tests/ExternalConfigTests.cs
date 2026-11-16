using Ipx2Mqtt.Ipx;
using Ipx2Mqtt.Service;
using Microsoft.Extensions.Configuration;

namespace Ipx2Mqtt.Tests;

public class ExternalConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipxcfg_" + Guid.NewGuid().ToString("N"));

    public ExternalConfigTests() => Directory.CreateDirectory(Path.Combine(_root, "config", "sub"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Locate_FindsConfigFolderInParents()
    {
        var file = Path.Combine(_root, "config", ExternalConfig.FileName);
        File.WriteAllText(file, "{}");

        Assert.Equal(file, ExternalConfig.Locate(null, Path.Combine(_root, "config", "sub")));
    }

    [Fact]
    public void Locate_ExplicitMissingFileFails() =>
        Assert.Throws<FileNotFoundException>(() => ExternalConfig.Locate(Path.Combine(_root, "nope.json")));

    [Fact]
    public void FileInAppsettingsFormat_BindsLabels()
    {
        var file = Path.Combine(_root, "x.json");
        File.WriteAllText(file, "{ \"Ipx\": { \"Labels\": { \"04-8R.05\": \"Lumières Z3\" } } }");

        var options = new ConfigurationBuilder().AddJsonFile(file).Build().GetSection("Ipx").Get<IpxOptions>()!;

        Assert.Equal("Lumières Z3", options.Labels["04-8R.05"]);
    }
}
