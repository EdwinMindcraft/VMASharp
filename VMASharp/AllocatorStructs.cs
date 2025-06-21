using System;
using Silk.NET.Core;
using Silk.NET.Vulkan;

#pragma warning disable CA1815

namespace VMASharp;

public struct VulkanMemoryAllocatorCreateInfo(
        Version32 vulkanApiVersion,
        Vk vulkanApiObject,
        Instance instance,
        PhysicalDevice physicalDevice,
        Device logicalDevice,
        AllocatorCreateFlags flags = default,
        long preferredLargeHeapBlockSize = 0,
        long[]? heapSizeLimits = null,
        int frameInUseCount = 0
    ) {
    /// <summary>
    /// Flags for created allocator
    /// </summary>
    public AllocatorCreateFlags Flags = flags;

    public Version32 VulkanAPIVersion = vulkanApiVersion;
    public Vk VulkanAPIObject = vulkanApiObject;
    public Instance Instance = instance;
    public PhysicalDevice PhysicalDevice = physicalDevice;
    public Device LogicalDevice = logicalDevice;
    public long PreferredLargeHeapBlockSize = preferredLargeHeapBlockSize;
    public long[]? HeapSizeLimits = heapSizeLimits;
    public int FrameInUseCount = frameInUseCount;
}

public struct AllocationCreateInfo(
        AllocationCreateFlags flags = default,
        AllocationStrategyFlags strategy = default,
        MemoryUsage usage = default,
        MemoryPropertyFlags requiredFlags = default,
        MemoryPropertyFlags preferredFlags = default,
        uint memoryTypeBits = 0,
        VulkanMemoryPool? pool = null,
        object? userData = null
    ) {
    public AllocationCreateFlags Flags = flags;
    public AllocationStrategyFlags Strategy = strategy;
    public MemoryUsage Usage = usage;
    public MemoryPropertyFlags RequiredFlags = requiredFlags;
    public MemoryPropertyFlags PreferredFlags = preferredFlags;
    public uint MemoryTypeBits = memoryTypeBits;
    public VulkanMemoryPool? Pool = pool;
    public object? UserData = userData;
}

public struct AllocationPoolCreateInfo(
        int memoryTypeIndex,
        PoolCreateFlags flags = 0,
        long blockSize = 0,
        int minBlockCount = 0,
        int maxBlockCount = 0,
        int frameInUseCount = 0,
        Func<long, Metadata.IBlockMetadata>? allocationAlgorithemCreate = null
    ) {
    /// <summary>
    /// Memory type index to allocate from, non-optional
    /// </summary>
    public int MemoryTypeIndex = memoryTypeIndex;

    public PoolCreateFlags Flags = flags;
    public long BlockSize = blockSize;
    public int MinBlockCount = minBlockCount;
    public int MaxBlockCount = maxBlockCount;
    public int FrameInUseCount = frameInUseCount;

    public Func<long, Metadata.IBlockMetadata>? AllocationAlgorithmCreate = allocationAlgorithemCreate;
}

public struct AllocationContext {
    public int CurrentFrame, FrameInUseCount;
    public long BufferImageGranularity;
    public long AllocationSize;
    public long AllocationAlignment;
    public AllocationStrategyFlags Strategy;
    public SuballocationType SuballocationType;
    public bool CanMakeOtherLost;

    public AllocationContext(int currentFrame, int framesInUse, long bufferImageGranularity, long allocationSize, long allocationAlignment, AllocationStrategyFlags strategy, SuballocationType suballocType, bool canMakeOtherLost) {
        this.CurrentFrame = currentFrame;
        this.FrameInUseCount = framesInUse;
        this.BufferImageGranularity = bufferImageGranularity;
        this.AllocationSize = allocationSize;
        this.AllocationAlignment = allocationAlignment;
        this.Strategy = strategy;
        this.SuballocationType = suballocType;
        this.CanMakeOtherLost = canMakeOtherLost;
    }
}

public struct AllocationRequest {
    public const long LostAllocationCost = 1048576;

    public long Offset, SumFreeSize, SumItemSize;
    public long ItemsToMakeLostCount;

    public object Item;

    public object CustomData;

    public AllocationRequestType Type;

    public readonly long CalcCost() => this.SumItemSize + this.ItemsToMakeLostCount * LostAllocationCost;
}
