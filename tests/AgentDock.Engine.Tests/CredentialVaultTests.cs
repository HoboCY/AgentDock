using AgentDock.Core;
using Xunit;

namespace AgentDock.Engine.Tests;

/// <summary>Windows DPAPI vault tests; macOS coverage lives in <see cref="MacCredentialVaultTests"/>.</summary>
public sealed class WindowsVaultFactAttribute : FactAttribute
{
    public WindowsVaultFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "DPAPI vault tests run on Windows only.";
        }
    }
}

/// <summary>Windows DPAPI vault theory; macOS coverage lives in <see cref="MacCredentialVaultTests"/>.</summary>
public sealed class WindowsVaultTheoryAttribute : TheoryAttribute
{
    public WindowsVaultTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "DPAPI vault tests run on Windows only.";
        }
    }
}

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

    [WindowsVaultFact]
    public void EmptyVaultHasNoCredential()
    {
        Assert.False(_vault.IsConfigured);
        Assert.Null(_vault.Read());
    }

    [WindowsVaultFact]
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

    [WindowsVaultFact]
    public void SetReplacesTheExistingBlobWithoutTemporaryResidue()
    {
        _vault.Set("sk-first-fixture");
        _vault.Set("sk-second-fixture");

        Assert.True(_vault.IsConfigured);
        Assert.Equal("sk-second-fixture", _vault.Read());
        Assert.Equal([Path.Combine(_home, CredentialVault.FileName)], Directory.GetFiles(_home));
    }

    [WindowsVaultTheory]
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

    [WindowsVaultFact]
    public void CorruptedBlobFailsAsCredentialInvalid()
    {
        File.WriteAllBytes(Path.Combine(_home, CredentialVault.FileName), [1, 2, 3, 4, 5]);
        var error = Assert.Throws<DockException>(() => _vault.Read());
        Assert.Equal("credential-invalid", error.Code);
    }
}
