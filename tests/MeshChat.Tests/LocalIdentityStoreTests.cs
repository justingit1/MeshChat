using MeshChat.Services.Crypto;

namespace MeshChat.Tests;

public sealed class LocalIdentityStoreTests
{
    [Fact]
    public void FirstLoad_CreatesIdentity()
    {
        using var testDirectory = TempIdentityDirectory.Create();
        var store = new LocalIdentityStore(testDirectory.Path);

        using var identity = store.LoadOrCreate();

        Assert.False(string.IsNullOrWhiteSpace(identity.Fingerprint));
        Assert.True(File.Exists(store.IdentityKeyPath));
        Assert.True(File.Exists(store.IdentityJsonPath));
    }

    [Fact]
    public void SecondLoad_ReturnsSameFingerprint()
    {
        using var testDirectory = TempIdentityDirectory.Create();
        var store = new LocalIdentityStore(testDirectory.Path);

        using var first = store.LoadOrCreate();
        var firstFingerprint = first.Fingerprint;
        using var second = store.LoadOrCreate();

        Assert.Equal(firstFingerprint, second.Fingerprint);
    }

    [Fact]
    public void PublicIdentityExport_IsStable()
    {
        using var testDirectory = TempIdentityDirectory.Create();
        var store = new LocalIdentityStore(testDirectory.Path);

        using var first = store.LoadOrCreate();
        var firstExport = File.ReadAllText(store.IdentityJsonPath);
        using var second = store.LoadOrCreate();
        var secondExport = File.ReadAllText(store.IdentityJsonPath);
        var publicInfo = store.ReadPublicIdentity();

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(firstExport, secondExport);
        Assert.Equal("ECDSA-P256-SHA256", publicInfo.Algorithm);
        Assert.Equal(first.Fingerprint, publicInfo.Fingerprint);
        Assert.False(string.IsNullOrWhiteSpace(publicInfo.PublicKey));
    }

    [Fact]
    public void MissingIdentityKey_GeneratesNewIdentity()
    {
        using var testDirectory = TempIdentityDirectory.Create();
        var store = new LocalIdentityStore(testDirectory.Path);

        using var first = store.LoadOrCreate();
        var firstFingerprint = first.Fingerprint;
        File.Delete(store.IdentityKeyPath);
        using var second = store.LoadOrCreate();

        Assert.NotEqual(firstFingerprint, second.Fingerprint);
        Assert.True(File.Exists(store.IdentityKeyPath));
        Assert.True(File.Exists(store.IdentityJsonPath));
    }

    [Fact]
    public void MissingPublicIdentityExport_IsRecreatedFromPrivateIdentity()
    {
        using var testDirectory = TempIdentityDirectory.Create();
        var store = new LocalIdentityStore(testDirectory.Path);

        using var first = store.LoadOrCreate();
        var firstFingerprint = first.Fingerprint;
        File.Delete(store.IdentityJsonPath);
        using var second = store.LoadOrCreate();
        var publicInfo = store.ReadPublicIdentity();

        Assert.Equal(firstFingerprint, second.Fingerprint);
        Assert.Equal(firstFingerprint, publicInfo.Fingerprint);
        Assert.True(File.Exists(store.IdentityJsonPath));
    }

    [Fact]
    public void CorruptIdentityKey_ThrowsControlledError()
    {
        using var testDirectory = TempIdentityDirectory.Create();
        var store = new LocalIdentityStore(testDirectory.Path);
        Directory.CreateDirectory(testDirectory.Path);
        File.WriteAllBytes(store.IdentityKeyPath, [0x01, 0x02, 0x03]);

        var exception = Assert.Throws<LocalIdentityStoreException>(() => store.LoadOrCreate());

        Assert.Contains("could not be loaded", exception.Message);
    }

    private sealed class TempIdentityDirectory : IDisposable
    {
        private TempIdentityDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempIdentityDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "MeshChat.Tests",
                Guid.NewGuid().ToString("N"));

            return new TempIdentityDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
