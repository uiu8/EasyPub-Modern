using System.Text.Json;
using System.Text.Json.Nodes;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class RepairModePreferenceTests
{
    [Theory]
    [InlineData(null, RepairLandingMode.TreeOnly)]
    [InlineData((int)RepairLandingMode.TreeOnly, RepairLandingMode.TreeOnly)]
    [InlineData((int)RepairLandingMode.EditSource, RepairLandingMode.EditSource)]
    [InlineData(987, RepairLandingMode.TreeOnly)]
    public async Task Missing_or_unknown_modes_are_safe_and_valid_stored_modes_survive(int? stored, RepairLandingMode expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "easypub-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        var json = JsonSerializer.SerializeToNode(EasyPubAppSettings.Default)!.AsObject();
        if (stored is { } value) json[nameof(EasyPubAppSettings.DefaultRepairLandingMode)] = value;
        else json.Remove(nameof(EasyPubAppSettings.DefaultRepairLandingMode));
        var original = json.ToJsonString();
        await File.WriteAllTextAsync(path, original);
        var store = new AppSettingsStore(path);

        Assert.Equal(expected, store.Load().DefaultRepairLandingMode);
        var loaded = await store.LoadAsync();
        Assert.Equal(expected, loaded.DefaultRepairLandingMode);
        Assert.Equal(original, await File.ReadAllTextAsync(path)); // Reading never rewrites old settings.
        await store.SaveAsync(loaded);
        Assert.Equal(expected, (await store.LoadAsync()).DefaultRepairLandingMode);
    }

    [Fact]
    public async Task A_new_installation_defaults_to_tree_only()
    {
        var store = new AppSettingsStore(Path.Combine(Path.GetTempPath(), "easypub-mode-" + Guid.NewGuid().ToString("N"), "settings.json"));
        Assert.Equal(RepairLandingMode.TreeOnly, EasyPubAppSettings.Default.DefaultRepairLandingMode);
        Assert.Equal(RepairLandingMode.TreeOnly, store.Load().DefaultRepairLandingMode);
        Assert.Equal(RepairLandingMode.TreeOnly, (await store.LoadAsync()).DefaultRepairLandingMode);
        Assert.False(File.Exists(store.StoragePath));
    }
}
