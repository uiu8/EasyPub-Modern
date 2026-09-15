using System.Collections.ObjectModel;

namespace EasyPub.Desktop;

// A display-only projection: edits, saving and source ranges use Roots/Children.
// Incremental updates keep unaffected WPF containers instead of resetting the view.
public sealed class ChapterDisplayCollection : ObservableCollection<ChapterTreeNode>
{
    internal void Synchronize(IEnumerable<ChapterTreeNode> source)
    {
        var desired = source.Where(n => n.IsReviewVisible).ToArray();
        if (this.SequenceEqual(desired)) return;
        // A filter toggle can replace hundreds of rows. One Reset notification is
        // substantially cheaper than hundreds of Add/Move/Remove notifications;
        // TreeView virtualization then creates only the rows in the viewport.
        Items.Clear();
        foreach (var node in desired) Items.Add(node);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
