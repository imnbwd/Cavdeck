using Cavdeck.App.Services;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class CavdeckPathRegistrationTests
{
    [Fact]
    public void AddIsIdempotentAndPreservesExistingEntries()
    {
        var install = Path.Combine(@"C:\Users\Test", "AppData", "Local", "Cavdeck", "current");
        var original = @"C:\Windows\System32;C:\Tools";

        var first = CavdeckPathRegistration.UpdatePathValue(original, install, add: true);
        var second = CavdeckPathRegistration.UpdatePathValue(first, install, add: true);

        Assert.Equal(original + Path.PathSeparator + install, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void RemoveDeletesOnlyTheExactCavdeckEntry()
    {
        var install = Path.Combine(@"C:\Users\Test", "AppData", "Local", "Cavdeck", "current");
        var parent = Path.GetDirectoryName(install)!;
        var original = string.Join(Path.PathSeparator, @"C:\Tools", install + Path.DirectorySeparatorChar, parent);

        var updated = CavdeckPathRegistration.UpdatePathValue(original, install, add: false);

        Assert.Equal(string.Join(Path.PathSeparator, @"C:\Tools", parent), updated);
    }

    [Fact]
    public void ComparisonIgnoresWindowsPathCase()
    {
        var install = @"C:\Apps\Cavdeck\current";
        var original = @"c:\apps\Cavdeck\current;C:\Tools";

        var updated = CavdeckPathRegistration.UpdatePathValue(original, install, add: true);

        Assert.Equal(original, updated);
    }
}
