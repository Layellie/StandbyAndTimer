using System.Text.Json;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v2.3.1", "2.3.0",             true)]
    [InlineData("v2.3.1", "2.3.1",             false)]   // same version
    [InlineData("v2.3.1", "2.3.1.0",           false)]   // 4-part assembly form
    [InlineData("v2.3.0", "2.3.1",             false)]   // older tag
    [InlineData("v2.10.0", "2.9.0",            true)]    // numeric, not string, order
    [InlineData("V3.0.0", "2.9.9",             true)]
    [InlineData("v2.4.0", "2.4.0-beta1",       true)]    // final after its beta
    [InlineData("v2.4.0", "2.4.0-beta2+3f2a1c9", true)]  // SDK appends +commit
    [InlineData("v2.3.1", "2.4.0-beta1",       false)]   // beta of a newer version
    [InlineData("v2.4.0-beta2", "2.4.0-beta1", false)]   // never offer betas
    [InlineData("v2.3.1", "2.3.1+3f2a1c9",     false)]
    public void Newer_only_when_the_release_is_ahead(string tag, string current, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(tag, current));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("v2.x")]
    public void Unreadable_tag_is_never_newer(string tag) =>
        Assert.False(UpdateService.IsNewer(tag, "2.3.1"));
}

public class UpdateReleaseJsonTests
{
    private static JsonElement Release(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Setup_asset_is_the_exe_not_the_hash_file()
    {
        var r = Release("""
            { "assets": [
                { "name": "StandbyAndTimer_Setup_2.3.1.exe.sha256", "browser_download_url": "https://x/hash" },
                { "name": "StandbyAndTimer_Setup_2.3.1.exe",        "browser_download_url": "https://x/setup" } ] }
            """);

        Assert.Equal("https://x/setup", UpdateService.FindSetupAssetUrl(r));
    }

    [Fact]
    public void No_assets_means_no_setup_url() =>
        Assert.Equal("", UpdateService.FindSetupAssetUrl(Release("""{ "tag_name": "v2.3.1" }""")));

    [Theory]
    [InlineData("## Integrity\n\nSHA256: `23ea4f0f80da39e3ca550324214386f2a5f101540086e128f34515e0b164b1ab`")]
    [InlineData("sha-256 = 23EA4F0F80DA39E3CA550324214386F2A5F101540086E128F34515E0B164B1AB")]
    public void Body_hash_is_read_and_lowercased(string body) =>
        Assert.Equal("23ea4f0f80da39e3ca550324214386f2a5f101540086e128f34515e0b164b1ab",
                     UpdateService.Sha256FromBody(body));

    [Theory]
    [InlineData("no hash here")]
    [InlineData("SHA256: 23ea4f0f")]   // too short
    public void Body_without_a_full_hash_gives_nothing(string body) =>
        Assert.Equal("", UpdateService.Sha256FromBody(body));
}
