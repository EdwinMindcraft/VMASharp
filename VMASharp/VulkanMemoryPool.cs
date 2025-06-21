using System;
using System.Threading;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace VMASharp;

public sealed class VulkanMemoryPool : IDisposable {
    public VulkanMemoryAllocator Allocator { get; }

    private Vk VkApi => this.Allocator.VkApi;


    public string Name { get; set; }

    internal uint ID { get; }

    internal readonly BlockList BlockList;

    internal VulkanMemoryPool(VulkanMemoryAllocator allocator, in AllocationPoolCreateInfo poolInfo, long preferredBlockSize) {
        this.Allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));

        ref int tmpRef = ref Unsafe.As<uint, int>(ref allocator.NextPoolId);

        this.ID = (uint)Interlocked.Increment(ref tmpRef);

        if (this.ID == 0)
            throw new OverflowException();

        this.BlockList = new BlockList(
            allocator,
            this,
            poolInfo.MemoryTypeIndex,
            poolInfo.BlockSize != 0 ? poolInfo.BlockSize : preferredBlockSize,
            poolInfo.MinBlockCount,
            poolInfo.MaxBlockCount,
            (poolInfo.Flags & PoolCreateFlags.IgnoreBufferImageGranularity) != 0 ? 1 : allocator.BufferImageGranularity,
            poolInfo.FrameInUseCount,
            poolInfo.BlockSize != 0,
            poolInfo.AllocationAlgorithmCreate ?? Helpers.DefaultMetaObjectCreate
        );

        this.BlockList.CreateMinBlocks();
    }

    public void Dispose() {
        this.Allocator.DestroyPool(this);
    }

    public int MakeAllocationsLost() => this.Allocator.MakePoolAllocationsLost(this);

    public Result CheckForCorruption() => this.Allocator.CheckPoolCorruption(this);

    public void GetPoolStats(out PoolStats stats) {
        this.Allocator.GetPoolStats(this, out stats);
    }
}
