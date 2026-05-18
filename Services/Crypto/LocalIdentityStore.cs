using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeshChat.Services.Crypto;

public sealed class LocalIdentityStoreException : Exception
{
    public LocalIdentityStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record LocalIdentityPublicInfo(
    string Algorithm,
    string PublicKey,
    string Fingerprint);

public sealed class LocalIdentityStore
{
    private const string PublicIdentityAlgorithm = "ECDSA-P256-SHA256";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public LocalIdentityStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeshChat",
            "Data");
        IdentityKeyPath = Path.Combine(DataDirectory, "identity.key");
        IdentityJsonPath = Path.Combine(DataDirectory, "identity.json");
    }

    public string DataDirectory { get; }

    public string IdentityKeyPath { get; }

    public string IdentityJsonPath { get; }

    public LocalIdentity LoadOrCreate()
    {
        Directory.CreateDirectory(DataDirectory);

        if (!File.Exists(IdentityKeyPath))
        {
            var identity = LocalIdentity.Generate();
            Save(identity);
            return identity;
        }

        try
        {
            var protectedPrivateKey = File.ReadAllBytes(IdentityKeyPath);
            var privateKey = ProtectedData.Unprotect(
                protectedPrivateKey,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);

            var loaded = LocalIdentity.FromPkcs8PrivateKey(privateKey);
            WritePublicIdentity(loaded);
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new LocalIdentityStoreException(
                $"Local identity at '{IdentityKeyPath}' could not be loaded. Delete the corrupt identity files to generate a new identity.",
                ex);
        }
    }

    public LocalIdentityPublicInfo ReadPublicIdentity()
    {
        try
        {
            var json = File.ReadAllText(IdentityJsonPath);
            return JsonSerializer.Deserialize<LocalIdentityPublicInfo>(json, JsonOptions)
                ?? throw new JsonException("Public identity JSON was empty.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new LocalIdentityStoreException(
                $"Public identity at '{IdentityJsonPath}' could not be read.",
                ex);
        }
    }

    private void Save(LocalIdentity identity)
    {
        var privateKey = identity.ExportPrivateKey();
        var protectedPrivateKey = ProtectedData.Protect(
            privateKey,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        File.WriteAllBytes(IdentityKeyPath, protectedPrivateKey);
        WritePublicIdentity(identity);
    }

    private void WritePublicIdentity(LocalIdentity identity)
    {
        var publicInfo = new LocalIdentityPublicInfo(
            PublicIdentityAlgorithm,
            Convert.ToBase64String(identity.ExportPublicKey()),
            identity.Fingerprint);

        var json = JsonSerializer.Serialize(publicInfo, JsonOptions);
        File.WriteAllText(IdentityJsonPath, json);
    }
}
