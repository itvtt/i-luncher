namespace TerminalNotes.Services;

/// <summary>Eight fixed positions. Capacity changes visibility, never session ownership.</summary>
public sealed class TerminalSlotLayout
{
    private readonly string?[] _slots = new string?[8];
    public int Capacity { get; private set; } = 1;
    public IReadOnlyList<string?> Slots { get; }

    public TerminalSlotLayout() => Slots = Array.AsReadOnly(_slots);

    public void SetCapacity(int capacity)
    {
        if (capacity is not (1 or 2 or 3 or 4 or 6 or 8))
            throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    public int IndexOf(string project) =>
        Array.FindIndex(_slots, value => string.Equals(value, project, StringComparison.OrdinalIgnoreCase));

    public void Assign(string project, int targetSlot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        if (targetSlot < 0 || targetSlot >= Capacity) throw new ArgumentOutOfRangeException(nameof(targetSlot));
        int previous = IndexOf(project);
        if (previous == targetSlot) return;
        if (previous >= 0) _slots[previous] = _slots[targetSlot];
        _slots[targetSlot] = project;
    }

    public bool CanEnsureVisibleWithoutReplacing(string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        int existing = IndexOf(project);
        if (existing >= 0) return existing < Capacity;
        return Array.FindIndex(_slots, 0, Capacity, value => value is null) >= 0;
    }

    public int EnsureVisible(string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        int existing = IndexOf(project);
        if (existing >= 0 && existing < Capacity) return existing;
        int target = Array.FindIndex(_slots, 0, Capacity, value => value is null);
        if (target < 0) target = 0;
        Assign(project, target);
        return target;
    }

    public void Remove(string project)
    {
        int index = IndexOf(project);
        if (index >= 0) _slots[index] = null;
    }

    public int Compact()
    {
        int next = 0;
        for (int index = 0; index < _slots.Length; index++)
        {
            if (_slots[index] is not string project) continue;
            if (next != index)
            {
                _slots[next] = project;
                _slots[index] = null;
            }
            next++;
        }
        return next;
    }

    public void Rename(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        int index = IndexOf(source);
        if (index < 0) return;
        int occupied = IndexOf(destination);
        if (occupied >= 0 && occupied != index) throw new InvalidOperationException("이미 배치된 프로젝트입니다.");
        _slots[index] = destination;
    }
}
