using Silk.NET.Vulkan;
using Silk.NET.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Numerics;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VMASharp;

using Defragmentation;

public sealed unsafe class VulkanMemoryAllocator : IDisposable {
    private const long SmallHeapMaxSize = 1024L * 1024 * 1024;
    private const BufferUsageFlags UnknownBufferUsage = unchecked((BufferUsageFlags)uint.MaxValue);

    internal Vk VkApi { get; }

    public Device Device { get; }
    internal readonly Instance Instance;

    private readonly Version32 vulkanApiVersion;

    private readonly bool useExtMemoryBudget;
    private readonly bool useAmdDeviceCoherentMemory;
    internal readonly bool UseKhrBufferDeviceAddress;

    private readonly uint heapSizeLimitMask;

    private readonly PhysicalDeviceProperties physicalDeviceProperties;
    private PhysicalDeviceMemoryProperties memoryProperties;

    private readonly BlockList[] blockLists = new BlockList[Vk.MaxMemoryTypes]; //Default Pools

    private DedicatedAllocationHandler[] dedicatedAllocations = new DedicatedAllocationHandler[Vk.MaxMemoryTypes];

    private readonly long preferredLargeHeapBlockSize;
    private readonly PhysicalDevice physicalDevice;
    private int currentFrame;
    private uint gpuDefragmentationMemoryTypeBits = uint.MaxValue;

    private readonly ReaderWriterLockSlim poolsMutex = new();
    private readonly List<VulkanMemoryPool> pools = [];
    internal uint NextPoolId;

    internal readonly CurrentBudgetData Budget = new();

    public VulkanMemoryAllocator(in VulkanMemoryAllocatorCreateInfo createInfo) {
        this.VkApi = createInfo.VulkanAPIObject;

        this.Instance = createInfo.Instance;
        this.physicalDevice = createInfo.PhysicalDevice;
        this.Device = createInfo.LogicalDevice;

        this.vulkanApiVersion = createInfo.VulkanAPIVersion;

        if (this.vulkanApiVersion == 0) this.vulkanApiVersion = Vk.Version10;

        this.useExtMemoryBudget = (createInfo.Flags & AllocatorCreateFlags.ExtMemoryBudget) != 0;
        this.useAmdDeviceCoherentMemory = (createInfo.Flags & AllocatorCreateFlags.AMDDeviceCoherentMemory) != 0;
        this.UseKhrBufferDeviceAddress = (createInfo.Flags & AllocatorCreateFlags.BufferDeviceAddress) != 0;

        this.VkApi.GetPhysicalDeviceProperties(this.physicalDevice, out this.physicalDeviceProperties);
        this.VkApi.GetPhysicalDeviceMemoryProperties(this.physicalDevice, out this.memoryProperties);

        Debug.Assert(Helpers.IsPow2(Helpers.DebugAlignment));
        Debug.Assert(Helpers.IsPow2(Helpers.DebugMinBufferImageGranularity));
        Debug.Assert(Helpers.IsPow2((long)this.PhysicalDeviceProperties.Limits.BufferImageGranularity));
        Debug.Assert(Helpers.IsPow2((long)this.PhysicalDeviceProperties.Limits.NonCoherentAtomSize));

        this.preferredLargeHeapBlockSize = createInfo.PreferredLargeHeapBlockSize != 0 ? createInfo.PreferredLargeHeapBlockSize : 256L * 1024 * 1024;

        this.GlobalMemoryTypeBits = this.CalculateGlobalMemoryTypeBits();

        if (createInfo.HeapSizeLimits != null) {
            Span<MemoryHeap> memoryHeaps = MemoryMarshal.CreateSpan(ref this.MemoryHeaps.Element0, this.MemoryHeapCount);

            int heapLimitLength = Math.Min(createInfo.HeapSizeLimits.Length, (int)Vk.MaxMemoryHeaps);

            for (int heapIndex = 0; heapIndex < heapLimitLength; ++heapIndex) {
                long limit = createInfo.HeapSizeLimits[heapIndex];

                if (limit <= 0) continue;

                this.heapSizeLimitMask |= 1u << heapIndex;
                ref MemoryHeap heap = ref memoryHeaps[heapIndex];

                if ((ulong)limit < heap.Size) heap.Size = (ulong)limit;
            }
        }

        for (int memTypeIndex = 0; memTypeIndex < this.MemoryTypeCount; ++memTypeIndex) {
            long preferredBlockSize = this.CalcPreferredBlockSize(memTypeIndex);

            this.blockLists[memTypeIndex] =
                new BlockList(this, null, memTypeIndex, preferredBlockSize, 0, int.MaxValue, this.BufferImageGranularity, createInfo.FrameInUseCount, false, Helpers.DefaultMetaObjectCreate);

            ref DedicatedAllocationHandler alloc = ref this.dedicatedAllocations[memTypeIndex];

            alloc.Allocations = new List<Allocation>();
            alloc.Mutex = new ReaderWriterLockSlim();
        }

        if (this.useExtMemoryBudget) this.UpdateVulkanBudget();
    }

    public int CurrentFrameIndex { get; set; }

    internal long BufferImageGranularity => (long)Math.Max(1, this.PhysicalDeviceProperties.Limits.BufferImageGranularity);

    internal int MemoryHeapCount => (int)this.MemoryProperties.MemoryHeapCount;

    internal int MemoryTypeCount => (int)this.MemoryProperties.MemoryTypeCount;

    internal bool IsIntegratedGPU => this.PhysicalDeviceProperties.DeviceType == PhysicalDeviceType.IntegratedGpu;

    internal uint GlobalMemoryTypeBits { get; private set; }


    public void Dispose() {
        if (this.pools.Count != 0) throw new InvalidOperationException("");

        int i = this.MemoryTypeCount;

        while (i-- != 0) {
            if (this.dedicatedAllocations[i].Allocations.Count != 0) throw new InvalidOperationException("Unfreed dedicatedAllocations found");

            this.blockLists[i].Dispose();
        }
    }

    public ref readonly PhysicalDeviceProperties PhysicalDeviceProperties => ref this.physicalDeviceProperties;

    public ref readonly PhysicalDeviceMemoryProperties MemoryProperties => ref this.memoryProperties;

    public MemoryPropertyFlags GetMemoryTypeProperties(int memoryTypeIndex) => this.MemoryTypes[memoryTypeIndex].PropertyFlags;

    public int? FindMemoryTypeIndex(uint memoryTypeBits, in AllocationCreateInfo allocInfo) {
        memoryTypeBits &= this.GlobalMemoryTypeBits;

        if (allocInfo.MemoryTypeBits != 0) memoryTypeBits &= allocInfo.MemoryTypeBits;

        MemoryPropertyFlags requiredFlags = allocInfo.RequiredFlags, preferredFlags = allocInfo.PreferredFlags, notPreferredFlags = default;

        switch (allocInfo.Usage) {
            case MemoryUsage.Unknown:
                break;
            case MemoryUsage.GPU_Only:
                if (this.IsIntegratedGPU || (preferredFlags & MemoryPropertyFlags.HostVisibleBit) == 0) preferredFlags |= MemoryPropertyFlags.DeviceLocalBit;

                break;
            case MemoryUsage.CPU_Only:
                requiredFlags |= MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                break;
            case MemoryUsage.CPU_To_GPU:
                requiredFlags |= MemoryPropertyFlags.HostVisibleBit;
                if (!this.IsIntegratedGPU || (preferredFlags & MemoryPropertyFlags.HostVisibleBit) == 0) preferredFlags |= MemoryPropertyFlags.DeviceLocalBit;

                break;
            case MemoryUsage.GPU_To_CPU:
                requiredFlags |= MemoryPropertyFlags.HostVisibleBit;
                preferredFlags |= MemoryPropertyFlags.HostCachedBit;
                break;
            case MemoryUsage.CPU_Copy:
                notPreferredFlags |= MemoryPropertyFlags.DeviceLocalBit;
                break;
            case MemoryUsage.GPU_LazilyAllocated:
                requiredFlags |= MemoryPropertyFlags.LazilyAllocatedBit;
                break;
            default:
                throw new ArgumentException("Invalid Usage Flags");
        }

        if (((allocInfo.RequiredFlags | allocInfo.PreferredFlags) & (MemoryPropertyFlags.DeviceCoherentBitAmd | MemoryPropertyFlags.DeviceUncachedBitAmd)) == 0) notPreferredFlags |= MemoryPropertyFlags.DeviceCoherentBitAmd;

        int? memoryTypeIndex = null;
        int minCost = int.MaxValue;
        uint memTypeBit = 1;

        for (int memTypeIndex = 0; memTypeIndex < this.MemoryTypeCount; ++memTypeIndex, memTypeBit <<= 1) {
            if ((memTypeBit & memoryTypeBits) == 0)
                continue;

            MemoryPropertyFlags currFlags = this.MemoryTypes[memTypeIndex].PropertyFlags;

            if ((requiredFlags & ~currFlags) != 0)
                continue;

            int currCost = BitOperations.PopCount((uint)(preferredFlags & ~currFlags));

            currCost += BitOperations.PopCount((uint)(currFlags & notPreferredFlags));

            if (currCost < minCost) {
                if (currCost == 0) return memTypeIndex;

                memoryTypeIndex = memTypeIndex;
                minCost = currCost;
            }
        }

        return memoryTypeIndex;
    }

    public int? FindMemoryTypeIndexForBufferInfo(in BufferCreateInfo bufferInfo, in AllocationCreateInfo allocInfo) {
        Buffer buffer;
        fixed (BufferCreateInfo* pBufferInfo = &bufferInfo) {
            Result res = this.VkApi.CreateBuffer(this.Device, pBufferInfo, null, &buffer);

            if (res != Result.Success) throw new VulkanResultException(res);
        }

        MemoryRequirements memReq;
        this.VkApi.GetBufferMemoryRequirements(this.Device, buffer, &memReq);

        int? tmp = this.FindMemoryTypeIndex(memReq.MemoryTypeBits, in allocInfo);

        this.VkApi.DestroyBuffer(this.Device, buffer, null);

        return tmp;
    }

    public int? FindMemoryTypeIndexForImageInfo(in ImageCreateInfo imageInfo, in AllocationCreateInfo allocInfo) {
        Image image;
        fixed (ImageCreateInfo* pImageInfo = &imageInfo) {
            Result res = this.VkApi.CreateImage(this.Device, pImageInfo, null, &image);

            if (res != Result.Success) throw new VulkanResultException(res);
        }

        MemoryRequirements memReq;
        this.VkApi.GetImageMemoryRequirements(this.Device, image, &memReq);

        int? tmp = this.FindMemoryTypeIndex(memReq.MemoryTypeBits, in allocInfo);

        this.VkApi.DestroyImage(this.Device, image, null);

        return tmp;
    }

    /// <summary>
    /// Allocate a block of memory
    /// </summary>
    /// <param name="requirements">Memory Requirements for the allocation</param>
    /// <param name="createInfo">Allocation Creation information</param>
    /// <returns>An object representing the allocation</returns>
    public Allocation AllocateMemory(in MemoryRequirements requirements, in AllocationCreateInfo createInfo) {
        DedicatedAllocationInfo dedicatedInfo = DedicatedAllocationInfo.Default;

        return this.AllocateMemory(in requirements, in dedicatedInfo, in createInfo, SuballocationType.Unknown);
    }

    /// <summary>
    /// Allocate a block of memory with the memory requirements of <paramref name="buffer"/>,
    /// optionally binding it to the allocation
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="createInfo"></param>
    /// <param name="BindToBuffer">Whether to bind <paramref name="buffer"> to the allocation</param>
    /// <returns></returns>
    public Allocation AllocateMemoryForBuffer(Buffer buffer, in AllocationCreateInfo createInfo, bool BindToBuffer = false) {
        DedicatedAllocationInfo dedicatedInfo = DedicatedAllocationInfo.Default;

        dedicatedInfo.DedicatedBuffer = buffer;

        this.GetBufferMemoryRequirements(buffer, out MemoryRequirements memReq, out dedicatedInfo.RequiresDedicatedAllocation, out dedicatedInfo.PrefersDedicatedAllocation);

        Allocation alloc = this.AllocateMemory(in memReq, in dedicatedInfo, in createInfo, SuballocationType.Buffer);

        if (BindToBuffer) alloc.BindBufferMemory(buffer);

        return alloc;
    }

    /// <summary>
    /// Allocate a block of memory with the memory requirements of <paramref name="image"/>,
    /// optionally binding it to the allocation
    /// </summary>
    /// <param name="image"></param>
    /// <param name="createInfo"></param>
    /// <param name="BindToImage">Whether to bind <paramref name="image"> to the allocation</param>
    /// <returns></returns>
    public Allocation AllocateMemoryForImage(Image image, in AllocationCreateInfo createInfo, bool BindToImage = false) {
        DedicatedAllocationInfo dedicatedInfo = DedicatedAllocationInfo.Default;

        dedicatedInfo.DedicatedImage = image;

        this.GetImageMemoryRequirements(image, out MemoryRequirements memReq, out dedicatedInfo.RequiresDedicatedAllocation, out dedicatedInfo.PrefersDedicatedAllocation);

        Allocation alloc = this.AllocateMemory(in memReq, in dedicatedInfo, in createInfo, SuballocationType.Image_Unknown);

        if (BindToImage) alloc.BindImageMemory(image);

        return alloc;
    }

    public Result CheckCorruption(uint memoryTypeBits) => throw new NotImplementedException();

    public Buffer CreateBuffer(in BufferCreateInfo bufferInfo, in AllocationCreateInfo allocInfo, out Allocation allocation) {
        Result res;
        Buffer buffer;

        Allocation? alloc = null;

        fixed (BufferCreateInfo* pInfo = &bufferInfo) {
            res = this.VkApi.CreateBuffer(this.Device, pInfo, null, &buffer);

            if (res < 0) throw new AllocationException("Buffer creation failed", res);
        }

        try {
            DedicatedAllocationInfo dedicatedInfo = default;

            dedicatedInfo.DedicatedBuffer = buffer;
            dedicatedInfo.DedicatedBufferUsage = bufferInfo.Usage;

            this.GetBufferMemoryRequirements(buffer, out MemoryRequirements memReq, out dedicatedInfo.RequiresDedicatedAllocation, out dedicatedInfo.PrefersDedicatedAllocation);

            alloc = this.AllocateMemory(in memReq, in dedicatedInfo, in allocInfo, SuballocationType.Buffer);
        } catch {
            this.VkApi.DestroyBuffer(this.Device, buffer, null);
            throw;
        }

        if ((allocInfo.Flags & AllocationCreateFlags.DontBind) == 0) {
            res = alloc.BindBufferMemory(buffer);

            if (res != Result.Success) {
                this.VkApi.DestroyBuffer(this.Device, buffer, null);
                alloc.Dispose();

                throw new AllocationException("Unable to bind memory to buffer", res);
            }
        }

        allocation = alloc;

        return buffer;
    }

    /// <summary>
    /// Create a vulkan image and allocate a block of memory to go with it.
    /// Unless specified otherwise with <seealso cref="AllocationCreateFlags.DontBind"/>, the Image will be bound to the memory for you.
    /// </summary>
    /// <param name="imageInfo">Information to create the image</param>
    /// <param name="allocInfo">Information to allocate memory for the image</param>
    /// <param name="allocation">The object corresponding to the allocation</param>
    /// <returns>The created image</returns>
    ///
    public Image CreateImage(in ImageCreateInfo imageInfo, in AllocationCreateInfo allocInfo, out Allocation allocation) {
        if (imageInfo.Extent.Width == 0 ||
            imageInfo.Extent.Height == 0 ||
            imageInfo.Extent.Depth == 0 ||
            imageInfo.MipLevels == 0 ||
            imageInfo.ArrayLayers == 0)
            throw new ArgumentException("Invalid Image Info");

        Result res;
        Image image;
        Allocation alloc;

        fixed (ImageCreateInfo* pInfo = &imageInfo) {
            res = this.VkApi.CreateImage(this.Device, pInfo, null, &image);

            if (res < 0) throw new AllocationException("Image creation failed", res);
        }

        SuballocationType suballocType = imageInfo.Tiling == ImageTiling.Optimal ? SuballocationType.Image_Optimal : SuballocationType.Image_Linear;

        try {
            alloc = this.AllocateMemoryForImage(image, allocInfo);
        } catch {
            this.VkApi.DestroyImage(this.Device, image, null);
            throw;
        }

        if ((allocInfo.Flags & AllocationCreateFlags.DontBind) == 0) {
            res = alloc.BindImageMemory(image);

            if (res != Result.Success) {
                this.VkApi.DestroyImage(this.Device, image, null);
                alloc.Dispose();

                throw new AllocationException("Unable to Bind memory to image", res);
            }
        }

        allocation = alloc;

        return image;
    }

    private ref PhysicalDeviceMemoryProperties.MemoryTypesBuffer MemoryTypes => ref this.memoryProperties.MemoryTypes;

    private ref PhysicalDeviceMemoryProperties.MemoryHeapsBuffer MemoryHeaps => ref this.memoryProperties.MemoryHeaps;

    internal int MemoryTypeIndexToHeapIndex(int typeIndex) {
        Debug.Assert(typeIndex < this.MemoryProperties.MemoryTypeCount);
        return (int)this.MemoryTypes[typeIndex].HeapIndex;
    }

    internal bool IsMemoryTypeNonCoherent(int memTypeIndex) => (this.MemoryTypes[memTypeIndex].PropertyFlags & (MemoryPropertyFlags.MemoryPropertyHostVisibleBit | MemoryPropertyFlags.MemoryPropertyHostCoherentBit)) == MemoryPropertyFlags.MemoryPropertyHostVisibleBit;

    internal long GetMemoryTypeMinAlignment(int memTypeIndex) => this.IsMemoryTypeNonCoherent(memTypeIndex) ? (long)Math.Max(1, this.PhysicalDeviceProperties.Limits.NonCoherentAtomSize) : 1;

    internal void GetBufferMemoryRequirements(Buffer buffer, out MemoryRequirements memReq, out bool requiresDedicatedAllocation, out bool prefersDedicatedAllocation) {
        BufferMemoryRequirementsInfo2 req = new() { SType = StructureType.BufferMemoryRequirementsInfo2, Buffer = buffer };

        MemoryDedicatedRequirements dedicatedRequirements = new() { SType = StructureType.MemoryDedicatedRequirements };

        MemoryRequirements2 memReq2 = new() { SType = StructureType.MemoryRequirements2, PNext = &dedicatedRequirements };

        this.VkApi.GetBufferMemoryRequirements2(this.Device, &req, &memReq2);

        memReq = memReq2.MemoryRequirements;
        requiresDedicatedAllocation = dedicatedRequirements.RequiresDedicatedAllocation;
        prefersDedicatedAllocation = dedicatedRequirements.PrefersDedicatedAllocation;
    }

    internal void GetImageMemoryRequirements(Image image, out MemoryRequirements memReq, out bool requiresDedicatedAllocation, out bool prefersDedicatedAllocation) {
        ImageMemoryRequirementsInfo2 req = new() { SType = StructureType.ImageMemoryRequirementsInfo2, Image = image };

        MemoryDedicatedRequirements dedicatedRequirements = new() { SType = StructureType.MemoryDedicatedRequirements };

        MemoryRequirements2 memReq2 = new() { SType = StructureType.MemoryRequirements2, PNext = &dedicatedRequirements };

        this.VkApi.GetImageMemoryRequirements2(this.Device, &req, &memReq2);

        memReq = memReq2.MemoryRequirements;
        requiresDedicatedAllocation = dedicatedRequirements.RequiresDedicatedAllocation;
        prefersDedicatedAllocation = dedicatedRequirements.PrefersDedicatedAllocation;
    }

    internal Allocation AllocateMemory(
            in MemoryRequirements memReq, in DedicatedAllocationInfo dedicatedInfo,
            in AllocationCreateInfo createInfo, SuballocationType suballocType
        ) {
        Debug.Assert(Helpers.IsPow2((long)memReq.Alignment));

        if (memReq.Size == 0)
            throw new ArgumentException("Allocation size cannot be 0");

        const AllocationCreateFlags CheckFlags1 = AllocationCreateFlags.DedicatedMemory | AllocationCreateFlags.NeverAllocate;
        const AllocationCreateFlags CheckFlags2 = AllocationCreateFlags.Mapped | AllocationCreateFlags.CanBecomeLost;

        if ((createInfo.Flags & CheckFlags1) == CheckFlags1)
            throw new ArgumentException("Specifying AllocationCreateFlags.DedicatedMemory with AllocationCreateFlags.NeverAllocate is invalid");
        else if ((createInfo.Flags & CheckFlags2) == CheckFlags2) throw new ArgumentException("Specifying AllocationCreateFlags.Mapped with AllocationCreateFlags.CanBecomeLost is invalid");

        if (dedicatedInfo.RequiresDedicatedAllocation) {
            if ((createInfo.Flags & AllocationCreateFlags.NeverAllocate) != 0) throw new AllocationException("AllocationCreateFlags.NeverAllocate specified while dedicated allocation required", Result.ErrorOutOfDeviceMemory);

            if (createInfo.Pool != null) throw new ArgumentException("Pool specified while dedicated allocation required");
        }

        if (createInfo.Pool != null && (createInfo.Flags & AllocationCreateFlags.DedicatedMemory) != 0) throw new ArgumentException("Specified AllocationCreateFlags.DedicatedMemory when createInfo.Pool is not null");

        if (createInfo.Pool != null) {
            int memoryTypeIndex = createInfo.Pool.BlockList.MemoryTypeIndex;
            long alignmentForPool = Math.Max((long)memReq.Alignment, this.GetMemoryTypeMinAlignment(memoryTypeIndex));

            AllocationCreateInfo infoForPool = createInfo;

            if ((createInfo.Flags & AllocationCreateFlags.Mapped) != 0 && (this.MemoryTypes[memoryTypeIndex].PropertyFlags & MemoryPropertyFlags.MemoryPropertyHostVisibleBit) == 0) infoForPool.Flags &= ~AllocationCreateFlags.Mapped;

            return createInfo.Pool.BlockList.Allocate(this.CurrentFrameIndex, (long)memReq.Size, alignmentForPool, infoForPool, suballocType);
        } else {
            uint memoryTypeBits = memReq.MemoryTypeBits;
            int? typeIndex = this.FindMemoryTypeIndex(memoryTypeBits, createInfo);

            if (typeIndex == null) throw new AllocationException("Unable to find suitable memory type for allocation", Result.ErrorFeatureNotPresent);

            long alignmentForType = Math.Max((long)memReq.Alignment, this.GetMemoryTypeMinAlignment((int)typeIndex));

            return this.AllocateMemoryOfType((long)memReq.Size, alignmentForType, in dedicatedInfo, in createInfo, (int)typeIndex, suballocType);
        }
    }

    public void FreeMemory(Allocation allocation) {
        if (allocation is null) throw new ArgumentNullException(nameof(allocation));

        if (allocation.TouchAllocation()) {
            if (allocation is BlockAllocation blockAlloc) {
                BlockList list;
                VulkanMemoryPool? pool = blockAlloc.Block.ParentPool;

                if (pool != null) {
                    list = pool.BlockList;
                } else {
                    list = this.blockLists[allocation.MemoryTypeIndex];
                    Debug.Assert(list != null);
                }

                list.Free(allocation);
            } else {
                DedicatedAllocation? dedicated = allocation as DedicatedAllocation;

                Debug.Assert(dedicated != null);

                this.FreeDedicatedMemory(dedicated);
            }
        }

        this.Budget.RemoveAllocation(this.MemoryTypeIndexToHeapIndex(allocation.MemoryTypeIndex), allocation.Size);
    }

    public Stats CalculateStats() {
        Stats newStats = new();

        for (int i = 0; i < this.MemoryTypeCount; ++i) {
            BlockList? list = this.blockLists[i];

            Debug.Assert(list != null);

            list.AddStats(newStats);
        }

        this.poolsMutex.EnterReadLock();
        try {
            foreach (VulkanMemoryPool pool in this.pools) pool.BlockList.AddStats(newStats);
        } finally {
            this.poolsMutex.ExitReadLock();
        }

        for (int typeIndex = 0; typeIndex < this.MemoryTypeCount; ++typeIndex) {
            int heapIndex = this.MemoryTypeIndexToHeapIndex(typeIndex);

            ref DedicatedAllocationHandler handler = ref this.dedicatedAllocations[typeIndex];

            handler.Mutex.EnterReadLock();

            try {
                foreach (Allocation alloc in handler.Allocations) {
                    ((DedicatedAllocation)alloc).CalcStatsInfo(out StatInfo stat);

                    StatInfo.Add(ref newStats.Total, stat);
                    StatInfo.Add(ref newStats.MemoryType[typeIndex], stat);
                    StatInfo.Add(ref newStats.MemoryHeap[heapIndex], stat);
                }
            } finally {
                handler.Mutex.ExitReadLock();
            }
        }

        newStats.PostProcess();

        return newStats;
    }

    internal void GetBudget(int heapIndex, out AllocationBudget outBudget) {
        Unsafe.SkipInit(out outBudget);

        if ((uint)heapIndex >= Vk.MaxMemoryHeaps) throw new ArgumentOutOfRangeException(nameof(heapIndex));

        if (this.useExtMemoryBudget) {
            //Reworked to remove recursion
            if (this.Budget.OperationsSinceBudgetFetch >= 30) this.UpdateVulkanBudget(); //Outside of mutex lock

            this.Budget.BudgetMutex.EnterReadLock();
            try {
                ref CurrentBudgetData.InternalBudgetStruct heapBudget = ref this.Budget.BudgetData[heapIndex];

                outBudget.BlockBytes = heapBudget.BlockBytes;
                outBudget.AllocationBytes = heapBudget.AllocationBytes;

                if (heapBudget.VulkanUsage + outBudget.BlockBytes > heapBudget.BlockBytesAtBudgetFetch)
                    outBudget.Usage = heapBudget.VulkanUsage + outBudget.BlockBytes - heapBudget.BlockBytesAtBudgetFetch;
                else
                    outBudget.Usage = 0;

                outBudget.Budget = Math.Min(heapBudget.VulkanBudget, (long)this.MemoryHeaps[heapIndex].Size);
            } finally {
                this.Budget.BudgetMutex.ExitReadLock();
            }
        } else {
            ref CurrentBudgetData.InternalBudgetStruct heapBudget = ref this.Budget.BudgetData[heapIndex];

            outBudget.BlockBytes = heapBudget.BlockBytes;
            outBudget.AllocationBytes = heapBudget.AllocationBytes;

            outBudget.Usage = heapBudget.BlockBytes;
            outBudget.Budget = (long)(this.MemoryHeaps[heapIndex].Size * 8 / 10); //80% heuristics
        }
    }

    internal void GetBudget(int firstHeap, AllocationBudget[] outBudgets) {
        Debug.Assert(outBudgets != null && outBudgets.Length != 0);
        Array.Clear(outBudgets, 0, outBudgets.Length);

        if ((uint)(outBudgets.Length + firstHeap) >= Vk.MaxMemoryHeaps) throw new ArgumentOutOfRangeException();

        if (this.useExtMemoryBudget) {
            //Reworked to remove recursion
            if (this.Budget.OperationsSinceBudgetFetch >= 30) this.UpdateVulkanBudget(); //Outside of mutex lock

            this.Budget.BudgetMutex.EnterReadLock();
            try {
                for (int i = 0; i < outBudgets.Length; ++i) {
                    int heapIndex = i + firstHeap;
                    ref AllocationBudget outBudget = ref outBudgets[i];

                    ref CurrentBudgetData.InternalBudgetStruct heapBudget = ref this.Budget.BudgetData[heapIndex];

                    outBudget.BlockBytes = heapBudget.BlockBytes;
                    outBudget.AllocationBytes = heapBudget.AllocationBytes;

                    if (heapBudget.VulkanUsage + outBudget.BlockBytes > heapBudget.BlockBytesAtBudgetFetch)
                        outBudget.Usage = heapBudget.VulkanUsage + outBudget.BlockBytes - heapBudget.BlockBytesAtBudgetFetch;
                    else
                        outBudget.Usage = 0;

                    outBudget.Budget = Math.Min(heapBudget.VulkanBudget, (long)this.MemoryHeaps[heapIndex].Size);
                }
            } finally {
                this.Budget.BudgetMutex.ExitReadLock();
            }
        } else {
            for (int i = 0; i < outBudgets.Length; ++i) {
                int heapIndex = i + firstHeap;
                ref AllocationBudget outBudget = ref outBudgets[i];
                ref CurrentBudgetData.InternalBudgetStruct heapBudget = ref this.Budget.BudgetData[heapIndex];

                outBudget.BlockBytes = heapBudget.BlockBytes;
                outBudget.AllocationBytes = heapBudget.AllocationBytes;

                outBudget.Usage = heapBudget.BlockBytes;
                outBudget.Budget = (long)(this.MemoryHeaps[heapIndex].Size * 8 / 10); //80% heuristics
            }
        }
    }

    internal Result DefragmentationBegin(in DefragmentationInfo2 info, DefragmentationStats stats, DefragmentationContext context) => throw new NotImplementedException();

    internal Result DefragmentationEnd(DefragmentationContext context) => throw new NotImplementedException();

    internal Result DefragmentationPassBegin(ref DefragmentationPassMoveInfo[] passInfo, DefragmentationContext context) => throw new NotImplementedException();

    internal Result DefragmentationPassEnd(DefragmentationContext context) => throw new NotImplementedException();

    public VulkanMemoryPool CreatePool(in AllocationPoolCreateInfo createInfo) {
        AllocationPoolCreateInfo tmpCreateInfo = createInfo;

        if (tmpCreateInfo.MaxBlockCount == 0) tmpCreateInfo.MaxBlockCount = int.MaxValue;

        if (tmpCreateInfo.MinBlockCount > tmpCreateInfo.MaxBlockCount) throw new ArgumentException("Min block count is higher than max block count");

        if (tmpCreateInfo.MemoryTypeIndex >= this.MemoryTypeCount || ((1u << tmpCreateInfo.MemoryTypeIndex) & this.GlobalMemoryTypeBits) == 0) throw new ArgumentException("Invalid memory type index");

        long preferredBlockSize = this.CalcPreferredBlockSize(tmpCreateInfo.MemoryTypeIndex);

        VulkanMemoryPool pool = new(this, tmpCreateInfo, preferredBlockSize);

        this.poolsMutex.EnterWriteLock();
        try {
            this.pools.Add(pool);
        } finally {
            this.poolsMutex.ExitWriteLock();
        }

        return pool;
    }

    internal void DestroyPool(VulkanMemoryPool pool) {
        this.poolsMutex.EnterWriteLock();
        try {
            bool success = this.pools.Remove(pool);
            Debug.Assert(success, "Pool not found in allocator");
        } finally {
            this.poolsMutex.ExitWriteLock();
        }
    }

    internal void GetPoolStats(VulkanMemoryPool pool, out PoolStats stats) {
        pool.BlockList.GetPoolStats(out stats);
    }

    internal int MakePoolAllocationsLost(VulkanMemoryPool pool) => pool.BlockList.MakePoolAllocationsLost(this.currentFrame);

    internal Result CheckPoolCorruption(VulkanMemoryPool pool) => throw new NotImplementedException();

    internal Allocation CreateLostAllocation() => throw new NotImplementedException();

    internal Result AllocateVulkanMemory(in MemoryAllocateInfo allocInfo, out DeviceMemory memory) {
        int heapIndex = this.MemoryTypeIndexToHeapIndex((int)allocInfo.MemoryTypeIndex);
        ref CurrentBudgetData.InternalBudgetStruct budgetData = ref this.Budget.BudgetData[heapIndex];

        if ((this.heapSizeLimitMask & (1u << heapIndex)) != 0) {
            long heapSize, blockBytes, blockBytesAfterAlloc;

            heapSize = (long)this.MemoryHeaps[heapIndex].Size;

            do {
                blockBytes = budgetData.BlockBytes;
                blockBytesAfterAlloc = blockBytes + (long)allocInfo.AllocationSize;

                if (blockBytesAfterAlloc > heapSize) throw new AllocationException("Budget limit reached for heap index " + heapIndex, Result.ErrorOutOfDeviceMemory);
            } while (Interlocked.CompareExchange(ref budgetData.BlockBytes, blockBytesAfterAlloc, blockBytes) != blockBytes);
        } else {
            Interlocked.Add(ref budgetData.BlockBytes, (long)allocInfo.AllocationSize);
        }

        fixed (MemoryAllocateInfo* pInfo = &allocInfo)
        fixed (DeviceMemory* pMemory = &memory) {
            Result res = this.VkApi.AllocateMemory(this.Device, pInfo, null, pMemory);

            if (res == Result.Success)
                Interlocked.Increment(ref this.Budget.OperationsSinceBudgetFetch);
            else
                Interlocked.Add(ref budgetData.BlockBytes, -(long)allocInfo.AllocationSize);

            return res;
        }
    }

    internal void FreeVulkanMemory(int memoryType, long size, DeviceMemory memory) {
        this.VkApi.FreeMemory(this.Device, memory, null);

        Interlocked.Add(ref this.Budget.BudgetData[this.MemoryTypeIndexToHeapIndex(memoryType)].BlockBytes, -size);
    }

    internal Result BindVulkanBuffer(Buffer buffer, DeviceMemory memory, long offset, void* pNext) {
        if (pNext != null) {
            BindBufferMemoryInfo info = new(pNext: pNext, buffer: buffer, memory: memory, memoryOffset: (ulong)offset);

            return this.VkApi.BindBufferMemory2(this.Device, 1, &info);
        } else {
            return this.VkApi.BindBufferMemory(this.Device, buffer, memory, (ulong)offset);
        }
    }

    internal Result BindVulkanImage(Image image, DeviceMemory memory, long offset, void* pNext) {
        if (pNext != default) {
            BindImageMemoryInfo info = new() {
                SType = StructureType.BindBufferMemoryInfo,
                PNext = pNext,
                Image = image,
                Memory = memory,
                MemoryOffset = (ulong)offset
            };

            return this.VkApi.BindImageMemory2(this.Device, 1, &info);
        } else {
            return this.VkApi.BindImageMemory(this.Device, image, memory, (ulong)offset);
        }
    }

    internal void FillAllocation(Allocation allocation, byte pattern) {
        if (Helpers.DebugInitializeAllocations && !allocation.CanBecomeLost &&
            (this.MemoryTypes[allocation.MemoryTypeIndex].PropertyFlags & MemoryPropertyFlags.MemoryPropertyHostVisibleBit) != 0) {
            IntPtr pData = allocation.Map();

            Unsafe.InitBlockUnaligned(ref *(byte*)pData, pattern, (uint)allocation.Size);

            this.FlushOrInvalidateAllocation(allocation, 0, long.MaxValue, CacheOperation.Flush);

            allocation.Unmap();
        }
    }

    internal uint GetGPUDefragmentationMemoryTypeBits() {
        uint memTypeBits = this.gpuDefragmentationMemoryTypeBits;
        if (memTypeBits == uint.MaxValue) {
            memTypeBits = this.CalculateGpuDefragmentationMemoryTypeBits();
            this.gpuDefragmentationMemoryTypeBits = memTypeBits;
        }

        return memTypeBits;
    }

    private long CalcPreferredBlockSize(int memTypeIndex) {
        int heapIndex = this.MemoryTypeIndexToHeapIndex(memTypeIndex);

        Debug.Assert((uint)heapIndex < Vk.MaxMemoryHeaps);

        long heapSize = (long)this.MemoryHeaps[heapIndex].Size;

        return Helpers.AlignUp(heapSize <= SmallHeapMaxSize ? heapSize / 8 : this.preferredLargeHeapBlockSize, 32);
    }

    private Allocation AllocateMemoryOfType(
            long size, long alignment, in DedicatedAllocationInfo dedicatedInfo, in AllocationCreateInfo createInfo,
            int memoryTypeIndex, SuballocationType suballocType
        ) {
        AllocationCreateInfo finalCreateInfo = createInfo;

        if ((finalCreateInfo.Flags & AllocationCreateFlags.Mapped) != 0 && (this.MemoryTypes[memoryTypeIndex].PropertyFlags & MemoryPropertyFlags.MemoryPropertyHostVisibleBit) == 0) finalCreateInfo.Flags &= ~AllocationCreateFlags.Mapped;

        if (finalCreateInfo.Usage == MemoryUsage.GPU_LazilyAllocated) finalCreateInfo.Flags |= AllocationCreateFlags.DedicatedMemory;

        BlockList blockList = this.blockLists[memoryTypeIndex];

        long preferredBlockSize = blockList.PreferredBlockSize;
        bool preferDedicatedMemory = dedicatedInfo.RequiresDedicatedAllocation | dedicatedInfo.PrefersDedicatedAllocation || size > preferredBlockSize / 2;

        if (preferDedicatedMemory && (finalCreateInfo.Flags & AllocationCreateFlags.NeverAllocate) == 0 && finalCreateInfo.Pool == null) finalCreateInfo.Flags |= AllocationCreateFlags.DedicatedMemory;

        Exception? blockAllocException = null;

        if ((finalCreateInfo.Flags & AllocationCreateFlags.DedicatedMemory) == 0)
            try {
                return blockList.Allocate(this.CurrentFrameIndex, size, alignment, finalCreateInfo, suballocType);
            } catch (Exception e) {
                blockAllocException = e;
            }

        //Try a dedicated allocation if a block allocation failed, or if specified as a dedicated allocation
        if ((finalCreateInfo.Flags & AllocationCreateFlags.NeverAllocate) != 0) throw new AllocationException("Block List allocation failed, and `AllocationCreateFlags.NeverAllocate` specified", blockAllocException);

        return this.AllocateDedicatedMemory(
            size, suballocType, memoryTypeIndex,
            (finalCreateInfo.Flags & AllocationCreateFlags.WithinBudget) != 0,
            (finalCreateInfo.Flags & AllocationCreateFlags.Mapped) != 0,
            finalCreateInfo.UserData, in dedicatedInfo
        );
    }

    private Allocation AllocateDedicatedMemoryPage(
            long size, SuballocationType suballocType, int memTypeIndex, in MemoryAllocateInfo allocInfo, bool map,
            object? userData
        ) {
        Result res = this.AllocateVulkanMemory(in allocInfo, out DeviceMemory memory);

        if (res < 0) throw new AllocationException("Dedicated memory allocation Failed", res);

        IntPtr mappedData = default;
        if (map) {
            res = this.VkApi.MapMemory(this.Device, memory, 0, Vk.WholeSize, 0, (void**)&mappedData);

            if (res < 0) {
                this.FreeVulkanMemory(memTypeIndex, size, memory);

                throw new AllocationException("Unable to map dedicated allocation", res);
            }
        }

        DedicatedAllocation allocation = new(this, memTypeIndex, memory, suballocType, mappedData, size) { UserData = userData };

        this.Budget.AddAllocation(this.MemoryTypeIndexToHeapIndex(memTypeIndex), size);

        this.FillAllocation(allocation, Helpers.AllocationFillPattern_Created);

        return allocation;
    }

    private Allocation AllocateDedicatedMemory(long size, SuballocationType suballocType, int memTypeIndex, bool withinBudget, bool map, object? userData, in DedicatedAllocationInfo dedicatedInfo) {
        int heapIndex = this.MemoryTypeIndexToHeapIndex(memTypeIndex);

        if (withinBudget) {
            this.GetBudget(heapIndex, out AllocationBudget budget);
            if (budget.Usage + size > budget.Budget) throw new AllocationException("Memory Budget limit reached for heap index " + heapIndex, Result.ErrorOutOfDeviceMemory);
        }

        MemoryAllocateInfo allocInfo = new() { SType = StructureType.MemoryAllocateInfo, MemoryTypeIndex = (uint)memTypeIndex, AllocationSize = (ulong)size };

        Debug.Assert(!(dedicatedInfo.DedicatedBuffer.Handle != default && dedicatedInfo.DedicatedImage.Handle != default), "dedicated buffer and dedicated image were both specified");

        MemoryDedicatedAllocateInfo dedicatedAllocInfo = new(StructureType.MemoryDedicatedAllocateInfo);

        if (dedicatedInfo.DedicatedBuffer.Handle != default) {
            dedicatedAllocInfo.Buffer = dedicatedInfo.DedicatedBuffer;
            allocInfo.PNext = &dedicatedAllocInfo;
        } else if (dedicatedInfo.DedicatedImage.Handle != default) {
            dedicatedAllocInfo.Image = dedicatedInfo.DedicatedImage;
            allocInfo.PNext = &dedicatedAllocInfo;
        }

        MemoryAllocateFlagsInfoKHR allocFlagsInfo = new(StructureType.MemoryAllocateFlagsInfoKhr);
        if (this.UseKhrBufferDeviceAddress) {
            bool canContainBufferWithDeviceAddress = true;

            if (dedicatedInfo.DedicatedBuffer.Handle != default)
                canContainBufferWithDeviceAddress = dedicatedInfo.DedicatedBufferUsage == UnknownBufferUsage
                                                    || (dedicatedInfo.DedicatedBufferUsage & BufferUsageFlags.BufferUsageShaderDeviceAddressBitKhr) != 0;
            else if (dedicatedInfo.DedicatedImage.Handle != default) canContainBufferWithDeviceAddress = false;

            if (canContainBufferWithDeviceAddress) {
                allocFlagsInfo.Flags = MemoryAllocateFlags.MemoryAllocateDeviceAddressBit;
                allocFlagsInfo.PNext = allocInfo.PNext;
                allocInfo.PNext = &allocFlagsInfo;
            }
        }

        Allocation alloc = this.AllocateDedicatedMemoryPage(size, suballocType, memTypeIndex, in allocInfo, map, userData);

        //Register made allocations
        ref DedicatedAllocationHandler handler = ref this.dedicatedAllocations[memTypeIndex];

        handler.Mutex.EnterWriteLock();
        try {
            handler.Allocations.InsertSorted(alloc, (alloc1, alloc2) => alloc1.Offset.CompareTo(alloc2.Offset));
        } finally {
            handler.Mutex.ExitWriteLock();
        }

        return alloc;
    }

    private void FreeDedicatedMemory(DedicatedAllocation allocation) {
        ref DedicatedAllocationHandler handler = ref this.dedicatedAllocations[allocation.MemoryTypeIndex];

        handler.Mutex.EnterWriteLock();

        try {
            bool success = handler.Allocations.Remove(allocation);

            Debug.Assert(success);
        } finally {
            handler.Mutex.ExitWriteLock();
        }

        this.FreeVulkanMemory(allocation.MemoryTypeIndex, allocation.Size, allocation.DeviceMemory);
    }

    private uint CalculateGpuDefragmentationMemoryTypeBits() => throw new NotImplementedException();

    private const uint AMDVendorID = 0x1002;

    private uint CalculateGlobalMemoryTypeBits() {
        Debug.Assert(this.MemoryTypeCount > 0);

        uint memoryTypeBits = uint.MaxValue;

        if (this.PhysicalDeviceProperties.VendorID == AMDVendorID && !this.useAmdDeviceCoherentMemory)
            // Exclude memory types that have VK_MEMORY_PROPERTY_DEVICE_COHERENT_BIT_AMD.
            for (int index = 0; index < this.MemoryTypeCount; ++index)
                if ((this.MemoryTypes[index].PropertyFlags & MemoryPropertyFlags.MemoryPropertyDeviceCoherentBitAmd) != 0)
                    memoryTypeBits &= ~(1u << index);

        return memoryTypeBits;
    }

    private void UpdateVulkanBudget() {
        Debug.Assert(this.useExtMemoryBudget);

        PhysicalDeviceMemoryBudgetPropertiesEXT budgetProps = new(StructureType.PhysicalDeviceMemoryBudgetPropertiesExt);

        PhysicalDeviceMemoryProperties2 memProps = new(StructureType.PhysicalDeviceMemoryProperties2, &budgetProps);

        this.VkApi.GetPhysicalDeviceMemoryProperties2(this.physicalDevice, &memProps);

        this.Budget.BudgetMutex.EnterWriteLock();
        try {
            for (int i = 0; i < this.MemoryHeapCount; ++i) {
                ref CurrentBudgetData.InternalBudgetStruct data = ref this.Budget.BudgetData[i];

                data.VulkanUsage = (long)budgetProps.HeapUsage[i];
                data.VulkanBudget = (long)budgetProps.HeapBudget[i];

                data.BlockBytesAtBudgetFetch = data.BlockBytes;

                // Some bugged drivers return the budget incorrectly, e.g. 0 or much bigger than heap size.

                ref MemoryHeap heap = ref this.MemoryHeaps[i];

                if (data.VulkanBudget == 0)
                    data.VulkanBudget = (long)(heap.Size * 8 / 10);
                else if ((ulong)data.VulkanBudget > heap.Size) data.VulkanBudget = (long)heap.Size;

                if (data.VulkanUsage == 0 && data.BlockBytesAtBudgetFetch > 0) data.VulkanUsage = data.BlockBytesAtBudgetFetch;
            }

            this.Budget.OperationsSinceBudgetFetch = 0;
        } finally {
            this.Budget.BudgetMutex.ExitWriteLock();
        }
    }

    internal Result FlushOrInvalidateAllocation(Allocation allocation, long offset, long size, CacheOperation op) {
        int memTypeIndex = allocation.MemoryTypeIndex;
        if (size > 0 && this.IsMemoryTypeNonCoherent(memTypeIndex)) {
            long allocSize = allocation.Size;

            Debug.Assert((ulong)offset <= (ulong)allocSize);

            long nonCoherentAtomSize = (long)this.physicalDeviceProperties.Limits.NonCoherentAtomSize;

            MappedMemoryRange memRange = new(memory: allocation.DeviceMemory);

            if (allocation is BlockAllocation blockAlloc) {
                memRange.Offset = (ulong)Helpers.AlignDown(offset, nonCoherentAtomSize);

                if (size == long.MaxValue)
                    size = allocSize - offset;
                else
                    Debug.Assert(offset + size <= allocSize);

                memRange.Size = (ulong)Helpers.AlignUp(size + (offset - (long)memRange.Offset), nonCoherentAtomSize);

                long allocOffset = blockAlloc.Offset;

                Debug.Assert(allocOffset % nonCoherentAtomSize == 0);

                long blockSize = blockAlloc.Block.MetaData.Size;

                memRange.Offset += (ulong)allocOffset;
                memRange.Size = Math.Min(memRange.Size, (ulong)blockSize - memRange.Offset);
            } else if (allocation is DedicatedAllocation) {
                memRange.Offset = (ulong)Helpers.AlignDown(offset, nonCoherentAtomSize);

                if (size == long.MaxValue) {
                    memRange.Size = (ulong)allocSize - memRange.Offset;
                } else {
                    Debug.Assert(offset + size <= allocSize);

                    memRange.Size = (ulong)Helpers.AlignUp(size + (offset - (long)memRange.Offset), nonCoherentAtomSize);
                }
            } else {
                Debug.Assert(false);
                throw new ArgumentException("allocation type is not BlockAllocation or DedicatedAllocation");
            }

            switch (op) {
                case CacheOperation.Flush:
                    return this.VkApi.FlushMappedMemoryRanges(this.Device, 1, &memRange);
                case CacheOperation.Invalidate:
                    return this.VkApi.InvalidateMappedMemoryRanges(this.Device, 1, &memRange);
                default:
                    Debug.Assert(false);
                    throw new ArgumentException("Invalid Cache Operation value", nameof(op));
            }
        }

        return Result.Success;
    }

    internal struct DedicatedAllocationHandler {
        public List<Allocation> Allocations;
        public ReaderWriterLockSlim Mutex;
    }

    internal struct DedicatedAllocationInfo {
        public Buffer DedicatedBuffer;
        public Image DedicatedImage;
        public BufferUsageFlags DedicatedBufferUsage; //uint.MaxValue when unknown
        public bool RequiresDedicatedAllocation;
        public bool PrefersDedicatedAllocation;

        public static readonly DedicatedAllocationInfo Default = new() { DedicatedBufferUsage = unchecked((BufferUsageFlags)uint.MaxValue) };
    }
}
