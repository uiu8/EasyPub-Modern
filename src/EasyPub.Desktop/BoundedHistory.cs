namespace EasyPub.Desktop;

internal sealed class BoundedHistory<T>
{
    private readonly int _capacity;
    private readonly LinkedList<T> _items = [];

    public BoundedHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Count => _items.Count;

    public void Push(T item)
    {
        _items.AddLast(item);
        if (_items.Count > _capacity) _items.RemoveFirst();
    }

    public bool TryPeek(out T item)
    {
        if (_items.Last is null)
        {
            item = default!;
            return false;
        }
        item = _items.Last.Value;
        return true;
    }

    public bool TryPop(out T item)
    {
        if (!TryPeek(out item)) return false;
        _items.RemoveLast();
        return true;
    }
}
