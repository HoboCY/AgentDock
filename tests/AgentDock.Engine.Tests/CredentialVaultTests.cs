using AgentDock.Core;
using Xunit;

namespace AgentDock.Engine.Tests;

public sealed class CredentialVaultTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "agentdock-vault-" + Guid.NewGuid().ToString("N"));
    private readonly CredentialVault _vault;

    public CredentialVaultTests()
    {
        Directory.CreateDirectory(_home);
        _vault = new CredentialVault(_home);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void EmptyVaultHasNoCredential()
    {
        Assert.False(_vault.IsConfigured);
        Assert.Null(_vault.Read());
    }

    [Fact]
    public void SetStoresProtectedBlobAndReadRoundTrips()
    {
        const string key = "sk-fixture-0123456789";
        _vault.Set(key);
        Assert.True(_vault.IsConfigured);
        Assert.Equal(key, _vault.Read());
        var blob = File.ReadAllBytes(Path.Combine(_home, CredentialVault.FileName));
        Assert.DoesNotContain(key, System.Text.Encoding.UTF8.GetString(blob));
        Assert.DoesNotContain(key, Convert.ToHexString(blob));
    }

    [Fact]
    public void SetReplacesTheExistingBlobWithoutTemporaryResidue()
    {
        _vault.Set("sk-first-fixture");
        _vault.Set("sk-second-fixture");

        Assert.True(_vault.IsConfigured);
        Assert.Equal("sk-second-fixture", _vault.Read());
        Assert.Equal([Path.Combine(_home, CredentialVault.FileName)], Directory.GetFiles(_home));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("has whitespace")]
    [InlineData("trailing ")]
    [InlineData(" leading")]
    public void InvalidKeysAreRejected(string key)
    {
        var error = Assert.Throws<DockException>(() => _vault.Set(key));
        Assert.Equal("credential-invalid", error.Code);
        Assert.False(_vault.IsConfigured);
    }

    [Fact]
    public void CorruptedBlobFailsAsCredentialInvalid()
    {
        File.WriteAllBytes(Path.Combine(_home, CredentialVault.FileName), [1, 2, 3, 4, 5]);
        var error = Assert.Throws<DockException>(() => _vault.Read());
        Assert.Equal("credential-invalid", error.Code);
    }
}
