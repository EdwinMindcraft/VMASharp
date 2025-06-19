using System;
using Silk.NET.Core;
using Silk.NET.Vulkan;

#pragma warning disable CA1815

namespace VMASharp {
    public struct VulkanMemoryAllocatorCreateInfo {
        /// <summary>
        /// Flags for created allocator
        /// </summary>
        public AllocatorCreateFlags Flags;

        public Version32 VulkanAPIVersion;

        public Vk VulkanAPIObject;

        public Instance Instance;

        public PhysicalDevice PhysicalDevice;

        public Device LogicalDevice;

        public long PreferredLargeHeapBlockSize;

        public long[]? HeapSizeLimits;

        public int FrameInUseCount;

        public VulkanMemoryAllocatorCreateInfo(Version32 vulkanApiVersion,
            Vk vulkanApiObject,
            Instance instance, PhysicalDevice physicalDevice, Device logicalDevice,
            AllocatorCreateFlags flags = default,
            long preferredLargeHeapBlockSize = 0,
            long[]? heapSizeLimits = null,
            int frameInUseCount = 0) {
            this.Flags = flags;
            this.VulkanAPIVersion = vulkanApiVersion;
            this.VulkanAPIObject = vulkanApiObject;
            this.Instance = instance;
            this.PhysicalDevice = physicalDevice;
            this.LogicalDevice = logicalDevice;
            this.PreferredLargeHeapBlockSize = preferredLargeHeapBlockSize;
            this.HeapSizeLimits = heapSizeLimits;
            this.FrameInUseCount = frameInUseCount;
        }
    }

    public struct AllocationCreateInfo {
        public AllocationCreateFlags Flags;

        public AllocationStrategyFlags Strategy;

        public MemoryUsage Usage;

        public MemoryPropertyFlags RequiredFlags;

        public MemoryPropertyFlags PreferredFlags;

        public uint MemoryTypeBits;

        public VulkanMemoryPool? Pool;

        public object? UserData;

        public AllocationCreateInfo(AllocationCreateFlags flags = default,
            AllocationStrategyFlags strategy = default,
            MemoryUsage usage = default,
            MemoryPropertyFlags requiredFlags = default,
            MemoryPropertyFlags preferredFlags = default,
            uint memoryTypeBits = 0,
            VulkanMemoryPool? pool = null,
            object? userData = null) {
            this.Flags = flags;
            this.Strategy = strategy;
            this.Usage = usage;
            this.RequiredFlags = requiredFlags;
            this.PreferredFlags = preferredFlags;
            this.MemoryTypeBits = memoryTypeBits;
            this.Pool = pool;
            this.UserData = userData;
        }
    }

    public struct AllocationPoolCreateInfo {
        /// <summary>
        /// Memory type index to allocate from, non-optional
        /// </summary>
        public int MemoryTypeIndex;

        public PoolCreateFlags Flags;

        public long BlockSize;

        public int MinBlockCount;

        public int MaxBlockCount;

        public int FrameInUseCount;

        public Func<long, Metadata.IBlockMetadata>? AllocationAlgorithmCreate;

        public AllocationPoolCreateInfo(int memoryTypeIndex,
            PoolCreateFlags flags = 0,
            long blockSize = 0,
            int minBlockCount = 0,
            int maxBlockCount = 0,
            int frameInUseCount = 0,
            Func<long, Metadata.IBlockMetadata>? allocationAlgorithemCreate = null) {
            this.MemoryTypeIndex = memoryTypeIndex;
            this.Flags = flags;
            this.BlockSize = blockSize;
            this.MinBlockCount = minBlockCount;
            this.MaxBlockCount = maxBlockCount;
            this.FrameInUseCount = frameInUseCount;
            this.AllocationAlgorithmCreate = allocationAlgorithemCreate;
        }
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

        public readonly long CalcCost() {
            return this.SumItemSize + this.ItemsToMakeLostCount * LostAllocationCost;
        }
    }
}