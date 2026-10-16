using AgentDock.Core;
using Xunit;

namespace AgentDock.Engine.Tests;

/// <summary>
/// macOS Keychain vault probes. They create real current-user generic-password
/// items, so they are opt-in: set AGENTDOCK_KEYCHAIN_TESTS=1 on macOS and run
/// from a logged-in session with the login keychain unlocked.
/// </summary>
public sealed class MacKeychainFactAttribute : FactAttribute
{
    public MacKeychainFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "macOS Keychain vault tests require macOS.";
        }
        else if (Environment.GetEnvironmentVariable("AGENTDOCK_KEYCHAIN_TESTS") != "1")
        {
            Skip = "Set AGENTDOCK_KEYCHAIN_TESTS=1 on macOS to run real Keychain probes.";
        }
    }
}

/// <summary>
/// Every test uses a synthetic key and a unique temporary data home, so the
/// service/account pair can only address the item this test created. Cleanup
/// refuses to delete anything that does not match that unique test home under
/// the process temp root, and it never touches the real user's vault items.
/// </summary>
public sealed class MacCredentialVaultTests : IDisposable
{
    private readonly List<string> _homes = [];

    private string NewHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "agentdock-keychain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        _homes.Add(home);
        return home;
    }

    private static void DeleteTestItem(string home)
    {
        var identity = MacKeychain.Identity(home);
        var testRoot = ProjectPaths.Canonicalize(Path.GetTempPath()) + Path.DirectorySeparatorChar;
        if (identity.Service != MacKeychain.ServiceName ||
            !ProjectPaths.Canonicalize(identity.Account).StartsWith(testRoot, StringComparison.Ordinal))
        {
            // A derivation bug must never turn cleanup into a delete of a real user item.
            return;
        }
        MacKeychain.Delete(identity);
    }

    public void Dispose()
    {
        foreach (var home in _homes)
        {
            try
            {
                DeleteTestItem(home);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Keychain cleanup is best effort; the test result already ran.
            }
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [MacKeychainFact]
    public void MissingItemReportsNotConfiguredAndReadsNull()
    {
        var vault = new CredentialVault(NewHome());

        Assert.False(vault.IsConfigured);
        Assert.Null(vault.Read());
    }

    [MacKeychainFact]
    public void SetStoresKeychainItemAndReadRoundTrips()
    {
        var home = NewHome();
        var vault = new CredentialVault(home);
        const string key = "sk-mac-fixture-0123456789";

        vault.Set(key);

        Assert.True(vault.IsConfigured);
        Assert.Equal(key, vault.Read());
        Assert.False(File.Exists(Path.Combine(home, CredentialVault.FileName)));
        Assert.Empty(Directory.GetFileSystemEntries(home));
    }

    [MacKeychainFact]
    public void SetReplacesTheExistingKeychainItem()
    {
        var vault = new CredentialVault(NewHome());

        vault.Set("sk-mac-fixture-first");
        vault.Set("sk-mac-fixture-second");

        Assert.True(vault.IsConfigured);
        Assert.Equal("sk-mac-fixture-second", vault.Read());
    }

    [MacKeychainFact]
    public void DistinctHomesKeepIndependentItems()
    {
        var first = new CredentialVault(NewHome());
        var second = new CredentialVault(NewHome());

        first.Set("sk-mac-fixture-first-home");
        second.Set("sk-mac-fixture-second-home");
        Assert.Equal("sk-mac-fixture-first-home", first.Read());
        Assert.Equal("sk-mac-fixture-second-home", second.Read());

        second.Set("sk-mac-fixture-second-home-updated");
        Assert.Equal("sk-mac-fixture-first-home", first.Read());
        Assert.Equal("sk-mac-fixture-second-home-updated", second.Read());
        Assert.True(first.IsConfigured);
        Assert.True(second.IsConfigured);
    }

    [MacKeychainFact]
    public void EquivalentHomeSpellingsAddressTheSameItem()
    {
        var home = NewHome();
        new CredentialVault(home).Set("sk-mac-fixture-canonical");

        var alias = new CredentialVault(home + Path.DirectorySeparatorChar);

        Assert.True(alias.IsConfigured);
        Assert.Equal("sk-mac-fixture-canonical", alias.Read());
        var upper = home.ToUpperInvariant();
        if (Directory.Exists(upper))
        {
            var sameDirectory = new CredentialVault(upper);
            Assert.True(sameDirectory.IsConfigured);
            Assert.Equal("sk-mac-fixture-canonical", sameDirectory.Read());
        }
    }

    [MacKeychainFact]
    public void InvalidKeysAreRejectedWithoutStoring()
    {
        var vault = new CredentialVault(NewHome());

        var error = Assert.Throws<DockException>(() => vault.Set("has whitespace"));

        Assert.Equal("credential-invalid", error.Code);
        Assert.False(vault.IsConfigured);
    }
}
