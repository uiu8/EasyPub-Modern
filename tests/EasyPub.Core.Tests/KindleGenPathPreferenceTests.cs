using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class KindleGenPathPreferenceTests
{
    [Fact]
    public void Old_versioned_package_path_is_migrated_to_current_bundle()
    {
        using var fixture = new KindleGenFixture();
        var oldPath = fixture.CreateKindleGen("EasyPubModern-v0.9.0-win-x64");
        var currentPath = fixture.CreateKindleGen("EasyPubModern-v0.13.1-win-x64");

        var resolved = KindleGenPathPreference.ResolveForCurrentInstallation(
            oldPath,
            Path.GetDirectoryName(Path.GetDirectoryName(currentPath))!);

        Assert.Equal(currentPath, resolved);
    }

    [Fact]
    public void Existing_user_selected_external_path_is_preserved()
    {
        using var fixture = new KindleGenFixture();
        var customPath = fixture.CreateKindleGen("my-tools");
        var currentPath = fixture.CreateKindleGen("EasyPubModern-v0.13.1-win-x64");

        var resolved = KindleGenPathPreference.ResolveForCurrentInstallation(
            customPath,
            Path.GetDirectoryName(Path.GetDirectoryName(currentPath))!);

        Assert.Equal(customPath, resolved);
    }

    [Fact]
    public void Missing_saved_path_uses_current_bundle()
    {
        using var fixture = new KindleGenFixture();
        var currentPath = fixture.CreateKindleGen("EasyPubModern-v0.13.1-win-x64");

        var resolved = KindleGenPathPreference.ResolveForCurrentInstallation(
            Path.Combine(fixture.Root, "missing", "kindlegen_v2.9.exe"),
            Path.GetDirectoryName(Path.GetDirectoryName(currentPath))!);

        Assert.Equal(currentPath, resolved);
    }

    [Fact]
    public void Saved_path_is_retained_when_current_bundle_does_not_exist()
    {
        using var fixture = new KindleGenFixture();
        var customPath = fixture.CreateKindleGen("my-tools");
        var emptyApplicationDirectory = Path.Combine(fixture.Root, "unpacked-without-bin");
        Directory.CreateDirectory(emptyApplicationDirectory);

        var resolved = KindleGenPathPreference.ResolveForCurrentInstallation(customPath, emptyApplicationDirectory);

        Assert.Equal(customPath, resolved);
    }

    private sealed class KindleGenFixture : IDisposable
    {
        public KindleGenFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"easypub-kindlegen-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateKindleGen(string packageName)
        {
            var path = Path.Combine(Root, packageName, "bin", "kindlegen_v2.9.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "test");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
