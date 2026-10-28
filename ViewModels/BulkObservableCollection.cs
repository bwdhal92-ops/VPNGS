using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FlareVpn.ViewModels;

/// <summary>Observable collection that publishes one Reset after replacing a batch.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var replacement = items.ToList();

        Items.Clear();
        foreach (var item in replacement) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
