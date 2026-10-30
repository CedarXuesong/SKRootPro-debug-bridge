namespace SkpdbDesktop;

/// <summary>Leases device slots to logical contacts for the duration of each press.</summary>
public sealed class TouchSlotAllocator
{
    private sealed class Contact
    {
        public required TouchBatchEvent Position;
        public ushort? PhysicalSlot;
        public LinkedListNode<Contact>? Waiting;
    }

    private readonly Dictionary<ushort, Contact> _contacts = new();
    private readonly LinkedList<Contact> _waiting = new();
    private readonly bool[] _occupied;
    // Keep this history until the session ends: a lost UP acknowledgement must
    // still be followed by an emergency physical lift, even after local release.
    private readonly HashSet<ushort> _used = new();
    public int Capacity => _occupied.Length;
    public int ActiveCount { get; private set; }
    public int PendingCount => _waiting.Count;

    public TouchSlotAllocator(int slotMax)
    {
        if (slotMax < 0 || slotMax > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(slotMax));
        _occupied = new bool[slotMax + 1];
    }

    // Samplers produce logical contact IDs; only this class sees the actual
    // device capacity. Screen coordinates/rotation remain unchanged.
    public static TouchDeviceInfo SamplingDevice(TouchDeviceInfo device) =>
        device with { SlotMax = ushort.MaxValue };

    public ushort? GetPhysicalSlot(ushort logicalSlot) =>
        _contacts.TryGetValue(logicalSlot, out var contact) ? contact.PhysicalSlot : null;

    public List<TouchBatchEvent> Process(IEnumerable<TouchBatchEvent> input, bool promotePending = true)
    {
        var output = new List<TouchBatchEvent>();
        foreach (var item in input)
        {
            if (item.Action == 1)
            {
                // Duplicate DOWN cannot acquire a second lease or reset a finger.
                if (_contacts.ContainsKey(item.Slot)) continue;
                var contact = new Contact { Position = item };
                _contacts.Add(item.Slot, contact);
                if (_waiting.Count != 0 || !TryLease(contact, output))
                    contact.Waiting = _waiting.AddLast(contact);
            }
            else if (_contacts.TryGetValue(item.Slot, out var contact))
            {
                if (item.Action == 2)
                {
                    contact.Position = item;
                    if (contact.PhysicalSlot is { } slot) output.Add(item with { Slot = slot });
                }
                else if (item.Action == 3)
                {
                    if (contact.PhysicalSlot is { } slot)
                    {
                        output.Add(item with { Slot = slot });
                        _occupied[slot] = false;
                        ActiveCount--;
                    }
                    if (contact.Waiting is { } node) _waiting.Remove(node);
                    _contacts.Remove(item.Slot);
                }
            }
        }
        // Drain after all releases/cancellations in this sample. A short press
        // released while full must never reappear as a delayed ghost click.
        while (promotePending && _waiting.First is { } next && TryLease(next.Value, output))
        {
            next.Value.Waiting = null;
            _waiting.RemoveFirst();
        }
        return output;
    }

    private bool TryLease(Contact contact, List<TouchBatchEvent> output)
    {
        var preferred = contact.Position.Slot;
        var slot = preferred < Capacity && !_occupied[preferred] ? preferred : Array.IndexOf(_occupied, false);
        if (slot < 0) return false;
        contact.PhysicalSlot = (ushort)slot;
        _occupied[slot] = true;
        _used.Add((ushort)slot);
        ActiveCount++;
        output.Add(contact.Position with { Action = 1, Slot = (ushort)slot });
        return true;
    }

    public List<TouchBatchEvent> ReleaseAll(bool force = false)
    {
        var slots = force ? _used : _contacts.Values.Where(c => c.PhysicalSlot.HasValue)
            .Select(c => c.PhysicalSlot!.Value).ToHashSet();
        var output = slots.Order().Select(slot => new TouchBatchEvent(3, 0, 0, slot)).ToList();
        _contacts.Clear();
        _waiting.Clear();
        Array.Clear(_occupied);
        ActiveCount = 0;
        return output;
    }
}
