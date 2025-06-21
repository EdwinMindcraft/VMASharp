namespace VMASharp;

internal struct Suballocation {
    public long Offset, Size;
    public BlockAllocation? Allocation;
    public SuballocationType Type;

    public Suballocation(long offset, long size, SuballocationType type = SuballocationType.Free, BlockAllocation? alloc = null) {
        this.Offset = offset;
        this.Size = size;
        this.Allocation = alloc;
        this.Type = type;
    }
}
