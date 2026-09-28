using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace NpcManager.Desktop;

public sealed class BoundedProgressCollection :
    ReadOnlyObservableCollection<string>
{
    private readonly ObservableCollection<string> entries;
    private readonly int capacity;
    private readonly string omittedMarker;

    internal BoundedProgressCollection(int capacity, string omittedMarker)
        : this(new ObservableCollection<string>(), capacity, omittedMarker)
    {
    }

    private BoundedProgressCollection(
        ObservableCollection<string> entries,
        int capacity,
        string omittedMarker)
        : base(entries)
    {
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity,
                "Progress capacity must be at least two.");
        ArgumentNullException.ThrowIfNull(omittedMarker);
        this.entries = entries;
        this.capacity = capacity;
        this.omittedMarker = omittedMarker;
    }

    public new event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add => ((INotifyCollectionChanged)this).CollectionChanged += value;
        remove => ((INotifyCollectionChanged)this).CollectionChanged -= value;
    }

    internal void Add(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entries.Count < capacity)
        {
            entries.Add(entry);
            return;
        }

        while (entries.Count >= capacity - 1)
            entries.RemoveAt(0);
        entries.Insert(0, omittedMarker);
        entries.Add(entry);
    }

    internal void Clear() => entries.Clear();
}
