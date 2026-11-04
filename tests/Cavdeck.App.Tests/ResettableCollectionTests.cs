using System.Collections.Specialized;
using Cavdeck.App.ViewModels;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ResettableCollectionTests
{
    [Fact]
    public void Replacing_a_page_notifies_the_view_once()
    {
        var rows = new ResettableCollection<int>();
        var changes = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, change) => changes.Add(change.Action);

        rows.ReplaceWith(Enumerable.Range(0, 200));

        Assert.Equal(200, rows.Count);
        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
    }
}
