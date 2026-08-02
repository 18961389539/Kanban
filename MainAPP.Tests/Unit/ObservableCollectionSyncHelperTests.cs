using System.Collections.ObjectModel;
using MainAPP.Helpers;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public sealed class ObservableCollectionSyncHelperTests
{
    private sealed class Item
    {
        public Item(string id) => Id = id;

        public string Id { get; }
    }

    [Fact]
    public void Sync_RemovesMissingItemsAndAppendsNewItems()
    {
        var retained = new Item("retained");
        var removed = new Item("removed");
        var added = new Item("added");
        var target = new ObservableCollection<Item> { retained, removed };

        ObservableCollectionSyncHelper.Sync(target, new List<Item> { retained, added });

        Assert.Equal(new[] { retained, added }, target);
    }

    [Fact]
    public void Sync_PreservesReferencesForUnchangedPositions()
    {
        var first = new Item("first");
        var second = new Item("second");
        var target = new ObservableCollection<Item> { first, second };

        ObservableCollectionSyncHelper.Sync(target, new List<Item> { first, second });

        Assert.Same(first, target[0]);
        Assert.Same(second, target[1]);
    }

    [Fact]
    public void Sync_ReplacesItemsWhenOrderChanges()
    {
        var first = new Item("first");
        var second = new Item("second");
        var target = new ObservableCollection<Item> { first, second };

        ObservableCollectionSyncHelper.Sync(target, new List<Item> { second, first });

        Assert.Same(second, target[0]);
        Assert.Same(first, target[1]);
    }

    [Fact]
    public void Sync_EmptySourceClearsTarget()
    {
        var target = new ObservableCollection<Item> { new("one"), new("two") };

        ObservableCollectionSyncHelper.Sync(target, new List<Item>());

        Assert.Empty(target);
    }
}
