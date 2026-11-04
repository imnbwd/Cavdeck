using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Cavdeck.App.ViewModels;

// WPF only needs one reset when a filtered or sorted page is replaced.
// Per-row notifications force repeated layout work on the dispatcher.
public sealed class ResettableCollection<T> : ObservableCollection<T>
{
    public void ReplaceWith(IEnumerable<T> items)
    {
        var snapshot = items.ToList();
        Items.Clear();
        foreach (var item in snapshot)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
