using System.Text.Json.Nodes;
using Ipx2Mqtt.Service;

namespace Ipx2Mqtt.Tests;

public class ConfigEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ipxedit_" + Guid.NewGuid().ToString("N"));

    public ConfigEditorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void YamlJson_RoundTripKeepsTypes()
    {
        var node = YamlJson.Parse("A: 1\nB: true\nC: \"65537\"\nD: 1.5\nE: text\nF: ~\nG: [1, two]\n")!;
        var back = YamlJson.Parse(YamlJson.ToYaml(node))!;

        Assert.Equal(1, (long)back["A"]!);
        Assert.True((bool)back["B"]!);
        Assert.Equal("65537", (string)back["C"]!);
        Assert.Equal(1.5, (double)back["D"]!);
        Assert.Equal("text", (string)back["E"]!);
        Assert.Equal("two", (string)back["G"]![1]!);
    }

    [Fact]
    public void YamlJson_QuotesStringsThatLookLikeOtherTypes()
    {
        var node = new JsonObject { ["Key"] = "65537", ["Flag"] = "true", ["Name"] = "Lumières: salon" };
        var back = YamlJson.Parse(YamlJson.ToYaml(node))!;

        Assert.Equal("65537", (string)back["Key"]!);
        Assert.Equal("true", (string)back["Flag"]!);
        Assert.Equal("Lumières: salon", (string)back["Name"]!);
    }

    [Fact]
    public void Save_WritesBothFilesAndKeepsBackup()
    {
        File.WriteAllText(Path.Combine(_dir, "config.yml"), "Mqtt:\n  Host: old\n  Password: secret\n");
        var editor = new ConfigEditor(_dir);

        // As received from the HTTP body: both nodes already have a parent.
        var body = JsonNode.Parse("""{"config":{"Mqtt":{"Host":"broker","Password":"********"}},"entities":[{"Key":"01-IPX.01","Name":"Cuisine","Area":"Cuisine"}]}""")!;
        var config = body["config"]!.AsObject();
        var entities = body["entities"]!.AsArray();

        Assert.Empty(editor.Save(config, entities));
        var text = File.ReadAllText(Path.Combine(_dir, "config.yml"));
        Assert.Contains("Host: broker", text);
        Assert.Contains("Password: secret", text); // mask resolved to the stored value
        Assert.True(File.Exists(Path.Combine(_dir, "config.yml.bak")));
        Assert.Contains("01-IPX.01", File.ReadAllText(Path.Combine(_dir, "entities.yml")));
    }

    [Fact]
    public void Read_MasksSecrets()
    {
        File.WriteAllText(Path.Combine(_dir, "config.yml"), "Mqtt:\n  Password: secret\nIpx:\n  ApiKey: \"\"\n");
        var data = new ConfigEditor(_dir).Read();

        Assert.Equal(ConfigEditor.Mask, (string)data["config"]!["Mqtt"]!["Password"]!);
        Assert.DoesNotContain("secret", data.ToJsonString());
    }

    [Fact]
    public void Save_RejectsInvalidEntitiesWithoutWriting()
    {
        var editor = new ConfigEditor(_dir);
        var errors = editor.Save(
            new JsonObject(),
            JsonNode.Parse("""[{"Type":"relay","Name":"X","Component":"bogus"}]""")!.AsArray());

        Assert.NotEmpty(errors);
        Assert.False(File.Exists(Path.Combine(_dir, "config.yml")));
    }
}
