using System.Collections.Immutable;
using System.ComponentModel;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class PluginRowViewModel : INotifyPropertyChanged
{
    public PluginRowViewModel(PluginClosureReviewEntry entry)
    {
        Plugin = entry.Plugin;
        Order = entry.Order;
        Enabled = entry.Enabled;
        Exists = entry.Exists;
        Masters = entry.Masters;
        IsRequiredMaster = entry.RequiredMaster;
        isSelected = entry.Requested || entry.RequiredMaster;
        Health = !entry.Exists ? "Missing" : !entry.ReadSucceeded ? "Not reviewed" :
            entry.RequiredMaster ? "Required master" : "Ready";
    }

    public PluginName Plugin { get; }
    public string Name => Plugin.Value;
    public int Order { get; }
    public bool Enabled { get; }
    public string ActiveState => Enabled ? "Active" : "Inactive";
    public bool Exists { get; }
    public bool IsRequiredMaster { get; }
    public ImmutableArray<PluginName> Masters { get; }
    public int MasterCount => Masters.Length;
    public string Health { get; }

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
