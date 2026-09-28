using System.Collections.ObjectModel;
using System.ComponentModel;
using NpcManager.Application;

namespace NpcManager.Desktop;

/// <summary>
/// Strongly typed branch-or-leaf adapter for the animation TreeView. A leaf
/// carries the immutable application clip; branches can never be accepted.
/// </summary>
public sealed class AnimationTreeItemViewModel : INotifyPropertyChanged
{
    private bool isExpanded;
    private bool isSelected;

    private AnimationTreeItemViewModel(
        string name,
        PreviewAnimationListItem? clip,
        bool expand)
    {
        Name = name;
        Clip = clip;
        isExpanded = expand;
        Children = [];
    }

    public string Name { get; }

    public PreviewAnimationListItem? Clip { get; }

    public bool IsLeaf => Clip is not null;

    public ObservableCollection<AnimationTreeItemViewModel> Children { get; }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (isExpanded == value) return;
            isExpanded = value;
            PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string AutomationName => IsLeaf && Clip is not null
        ? $"{Clip.ClipName}; {Clip.Path}"
        : Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static AnimationTreeItemViewModel FromBranch(
        PreviewAnimationTreeBranch branch,
        bool expandAll,
        bool isRoot = true)
    {
        var item = new AnimationTreeItemViewModel(
            $"{branch.Name} ({branch.ClipCount})",
            null,
            expandAll || isRoot);
        foreach (PreviewAnimationTreeBranch child in branch.Children)
            item.Children.Add(FromBranch(
                child, expandAll, isRoot: false));
        foreach (PreviewAnimationTreeLeaf leaf in branch.Leaves)
        {
            item.Children.Add(new AnimationTreeItemViewModel(
                leaf.Label, leaf.Clip, expand: false));
        }
        return item;
    }

    internal bool TrySelectPath(
        string path,
        out AnimationTreeItemViewModel? selected)
    {
        if (Clip is not null && string.Equals(
                Clip.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            IsSelected = true;
            selected = this;
            return true;
        }

        foreach (AnimationTreeItemViewModel child in Children)
        {
            if (!child.TrySelectPath(path, out selected)) continue;
            IsExpanded = true;
            return true;
        }

        selected = null;
        return false;
    }
}
