using System.IO;
using System.Security.Cryptography;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

public sealed class UpdateSafetyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sat-update-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (string path, string sha) Installer()
    {
        string path = Path.Combine(_dir, "StandbyAndTimer_Setup_v9.9.9.exe");
        byte[] bytes = [0x4D, 0x5A, 1, 2, 3, 4, 5];
        File.WriteAllBytes(path, bytes);
        return (path, Convert.ToHexString(SHA256.HashData(bytes)));   // upper case on purpose
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc123")]                                                              // too short
    [InlineData("zz8bd2294ab9f0c4b9e7f02384ee410f4b9d5ab5de5b94cdac63f9ac807326327")] // not hex
    public void No_usable_hash_is_refused(string? sha) =>
        Assert.Throws<InvalidDataException>(() => UpdateService.NormalizeSha256(sha));

    [Fact]
    public async Task Download_without_a_hash_fails_before_any_request()
    {
        var service = new UpdateService();

        // Port 9 (discard) would fail with HttpRequestException if a request went out.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAsync("http://127.0.0.1:9/setup.exe", Path.Combine(_dir, "x.exe"), ""));
        Assert.False(File.Exists(Path.Combine(_dir, "x.exe")));
    }

    [Fact]
    public void Verified_installer_cannot_be_swapped_while_it_is_started()
    {
        var (path, sha) = Installer();

        using (UpdateService.OpenVerifiedInstaller(path, sha))
        {
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(path, [9, 9, 9]));
            Assert.ThrowsAny<Exception>(() => File.Move(path, path + ".old"));
            Assert.ThrowsAny<Exception>(() => File.Delete(path));
            // ...but it can still be read, as the loader does when it starts.
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(0x4D, reader.ReadByte());
        }

        File.Delete(path);   // released afterwards
    }

    [Fact]
    public void A_file_changed_after_download_is_not_started()
    {
        var (path, sha) = Installer();
        File.WriteAllBytes(path, [0x4D, 0x5A, 6, 6, 6]);   // swapped after the download check

        Assert.Throws<InvalidDataException>(() => UpdateService.OpenVerifiedInstaller(path, sha));
        File.Delete(path);   // the failed check did not keep the handle
    }

    [Fact]
    public void Installers_download_next_to_the_app_not_to_temp()
    {
        string dir = UpdateService.DownloadDirectory;

        Assert.StartsWith(AppContext.BaseDirectory, dir, StringComparison.OrdinalIgnoreCase);
        Assert.False(dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
    }
}
