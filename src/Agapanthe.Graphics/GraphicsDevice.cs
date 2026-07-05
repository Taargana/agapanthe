using Agapanthe.Core;
using Agapanthe.Graphics.Memory;
using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Agapanthe.Graphics;

/// <summary>
/// Facade over the Vulkan instance, physical device selection, logical device and
/// window surface. Owns the module-wide <see cref="DeletionQueue"/>. No Vulkan type
/// leaks through the public surface; <see cref="IVkSurface"/> is the one tolerated
/// boundary type for window-system integration.
/// </summary>
public sealed unsafe partial class GraphicsDevice : IDisposable
{
    /// <summary>Number of frames recorded ahead of the GPU (spec §3.3).</summary>
    public const int FramesInFlight = 2;

    private const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";
    // No Silk.NET wrapper class exposes these two names; the raw strings are the spec names.
    private const string PortabilityEnumerationExtensionName = "VK_KHR_portability_enumeration";
    private const string PortabilitySubsetExtensionName = "VK_KHR_portability_subset";

    private static readonly bool EnableValidation =
#if DEBUG
        true;
#else
        false;
#endif

    // Marshalled to native by the debug messenger; the static field keeps the
    // delegate (and its unmanaged thunk) alive for the messenger's whole lifetime.
    private static readonly DebugUtilsMessengerCallbackFunctionEXT DebugCallbackDelegate = DebugCallback;

    private readonly Vk _vk;
    private Instance _instance;
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;
    private KhrSurface? _khrSurface;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private KhrSwapchain? _khrSwapchain;
    private KhrDynamicRendering? _khrDynamicRendering;
    private KhrSynchronization2? _khrSynchronization2;
    private uint _instanceApiVersion;
    private bool _debugUtilsEnabled;
    private bool _useVulkan13Features;
    private bool _hasPortabilitySubset;
    private bool _samplerAnisotropyEnabled;
    private float _maxSamplerAnisotropy = 1f;
    private GpuAllocator? _allocator;
    private bool _disposed;

    public GraphicsDevice(string applicationName, string[] requiredInstanceExtensions, IVkSurface windowSurface)
    {
        ArgumentNullException.ThrowIfNull(applicationName);
        ArgumentNullException.ThrowIfNull(requiredInstanceExtensions);
        ArgumentNullException.ThrowIfNull(windowSurface);

        _vk = Vk.GetApi();
        try
        {
            CreateInstance(applicationName, requiredInstanceExtensions);
            CreateDebugMessenger();
            CreateSurface(windowSurface);
            SelectPhysicalDevice();
            CreateLogicalDevice();
            _allocator = new GpuAllocator(this);
        }
        catch
        {
            // Lazy allocator: nothing is bound yet, so disposing frees nothing — but keep the
            // ordering contract (allocator before the device it depends on).
            _allocator?.Dispose();
            _allocator = null;
            ReleaseResources();
            // Resources are already balanced; stop the finalizer reporting a phantom leak.
            GC.SuppressFinalize(this);
            throw;
        }
    }

    ~GraphicsDevice()
    {
        // Only report when a native handle was actually acquired; ctor argument-validation
        // exceptions reach the finalizer with nothing registered (audit M2, finding 1).
        if (_instance.Handle != 0 || _device.Handle != 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(GraphicsDevice));
        }
    }

    /// <summary>Deferred-destruction queue for all GPU resources owned by this device (spec §3.2).</summary>
    public DeletionQueue DeletionQueue { get; } = new();

    /// <summary>
    /// The device's GPU memory allocator (spec §3.5). Buffers and images allocate through this from
    /// M3-04 onward; it is created after the logical device and torn down after the DeletionQueue is
    /// drained so deferred frees still have their backing blocks alive.
    /// </summary>
    public GpuAllocator Allocator =>
        _allocator ?? throw new InvalidOperationException("GraphicsDevice allocator is not available (device disposed or construction failed).");

    /// <summary>
    /// Authoritative render frame counter, advanced once per presented frame by the
    /// FrameRenderer. Resources disposed mid-loop stamp their destruction with this index so
    /// the DeletionQueue can defer it past the frames still in flight (spec §3.2.1).
    /// </summary>
    public long CurrentFrameIndex { get; private set; }

    /// <summary>Advances the frame counter. Called by the FrameRenderer after each present.</summary>
    internal void AdvanceFrame() => CurrentFrameIndex++;

    /// <summary>
    /// Non-capturing deferred destroy (spec §3.2.5, zero managed allocation on the hot path).
    /// <paramref name="destructor"/> must be a cached static delegate and <paramref name="payload"/>
    /// carries the raw handles by value, so no closure is allocated per Dispose. Preferred for any
    /// resource freed per-frame (textures, staging buffers).
    /// </summary>
    public void EnqueueDestroy(Action<GraphicsDevice, DeletionPayload> destructor, in DeletionPayload payload)
        => DeletionQueue.Enqueue(this, destructor, in payload, CurrentFrameIndex);

    /// <summary>
    /// Schedules a GPU destroy action to run once the current frame is out of flight
    /// (N + FramesInFlight). Allocates a closure/delegate — reserved for rare or shutdown-only
    /// teardown. Prefer <see cref="EnqueueDestroy(Action{GraphicsDevice, DeletionPayload}, in DeletionPayload)"/>
    /// on the per-frame hot path.
    /// </summary>
    public void EnqueueDestroy(Action destroy) => DeletionQueue.Enqueue(destroy, CurrentFrameIndex);

    /// <summary>Name of the selected physical device.</summary>
    public string AdapterName { get; private set; } = string.Empty;

    internal Vk Api => _vk;
    internal Instance Instance => _instance;
    internal PhysicalDevice PhysicalDevice => _physicalDevice;
    internal Device Device => _device;
    internal SurfaceKHR Surface => _surface;
    internal KhrSurface KhrSurface => _khrSurface!;
    internal KhrSwapchain KhrSwapchain => _khrSwapchain!;

    /// <summary>True when dynamicRendering/synchronization2 are enabled as Vulkan 1.3 core features
    /// (MoltenVK path); false when the KHR extensions are used (Vulkan 1.2 devices).</summary>
    internal bool HasVulkan13Core => _useVulkan13Features;

    /// <summary>True when the <c>samplerAnisotropy</c> device feature was supported and enabled at logical-device
    /// creation. When false, <see cref="Sampler"/> forces isotropic sampling regardless of the requested value.</summary>
    internal bool SamplerAnisotropyEnabled => _samplerAnisotropyEnabled;

    /// <summary>The device's <c>maxSamplerAnisotropy</c> limit; the ceiling a <see cref="Sampler"/> clamps to.
    /// <c>1</c> when anisotropy is unavailable.</summary>
    internal float MaxSamplerAnisotropy => _maxSamplerAnisotropy;

    /// <summary>Non-null only when <see cref="HasVulkan13Core"/> is false.</summary>
    internal KhrDynamicRendering? KhrDynamicRendering => _khrDynamicRendering;

    /// <summary>Non-null only when <see cref="HasVulkan13Core"/> is false.</summary>
    internal KhrSynchronization2? KhrSynchronization2 => _khrSynchronization2;

    internal Queue GraphicsQueue { get; private set; }
    internal Queue PresentQueue { get; private set; }
    internal uint GraphicsQueueFamily { get; private set; }
    internal uint PresentQueueFamily { get; private set; }

    /// <summary>Blocks until the GPU finished all submitted work.</summary>
    public void WaitIdle()
        => VkCheck.ThrowIfFailed(_vk.DeviceWaitIdle(_device), "vkDeviceWaitIdle");

    /// <summary>
    /// Records a one-shot command buffer through <paramref name="record"/> and submits it on the graphics
    /// queue, blocking on a fence until the GPU finishes. Generalises <see cref="GpuUploader"/>'s one-shot
    /// pattern so load-time batch work (IBL generation, M7-04) can drive a <see cref="CommandList"/> outside
    /// the frame loop, where <see cref="CommandList"/> otherwise only exists inside the per-frame draw callback.
    /// <para>
    /// <b>Synchronous — never on the hot path.</b> A transient command pool and fence are created per call,
    /// the work is submitted with synchronization2 (fence-only, no semaphores), the call blocks on that fence,
    /// then the pool and fence are destroyed before returning. Intended for load-time work only, where the
    /// per-call allocation and the full stall are acceptable (spec §3.2, same rationale as
    /// <see cref="GpuUploader"/>). A per-call pool is chosen over a persistent one on purpose: it adds no
    /// mutable state or teardown-ordering coupling to <see cref="GraphicsDevice"/>, and this is load-time.
    /// Not thread-safe (phase-1 single-threaded).
    /// </para>
    /// </summary>
    public void SubmitImmediate(Action<CommandList> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            // Transient: the pool backs a single one-shot buffer and is destroyed at the end of the call.
            Flags = CommandPoolCreateFlags.TransientBit,
            QueueFamilyIndex = GraphicsQueueFamily,
        };
        CommandPool pool;
        VkCheck.ThrowIfFailed(_vk.CreateCommandPool(_device, &poolInfo, null, &pool), "vkCreateCommandPool");
        ResourceTracker.Register("VkCommandPool");

        Fence fence = default;
        try
        {
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer cmd;
            VkCheck.ThrowIfFailed(_vk.AllocateCommandBuffers(_device, &allocInfo, &cmd), "vkAllocateCommandBuffers");

            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            Fence createdFence;
            VkCheck.ThrowIfFailed(_vk.CreateFence(_device, &fenceInfo, null, &createdFence), "vkCreateFence");
            fence = createdFence;
            ResourceTracker.Register("VkFence");

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            VkCheck.ThrowIfFailed(_vk.BeginCommandBuffer(cmd, &beginInfo), "vkBeginCommandBuffer");

            record(new CommandList(this, cmd));

            VkCheck.ThrowIfFailed(_vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");

            var cmdInfo = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = cmd };
            var submit = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                CommandBufferInfoCount = 1,
                PCommandBufferInfos = &cmdInfo,
            };
            // synchronization2 submit: no semaphores — the fence is the only synchronization, and the wait
            // below proves the GPU is done before the pool/fence are destroyed in the finally.
            QueueSubmit2(GraphicsQueue, &submit, fence);

            var waitFence = fence;
            VkCheck.ThrowIfFailed(_vk.WaitForFences(_device, 1, &waitFence, true, ulong.MaxValue), "vkWaitForFences");
        }
        finally
        {
            if (fence.Handle != 0)
            {
                _vk.DestroyFence(_device, fence, null);
                ResourceTracker.Unregister("VkFence");
            }

            // Destroys the allocated command buffer with the pool. Safe even if record() threw before submit:
            // DestroyCommandPool is valid on a buffer left in the recording or executable state.
            _vk.DestroyCommandPool(_device, pool, null);
            ResourceTracker.Unregister("VkCommandPool");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_device.Handle != 0)
        {
            WaitIdle();
        }

        // Drain deferred destroys first: resource teardown (M3-04/05) returns suballocations to the
        // allocator, which must still hold their backing blocks at this point.
        DeletionQueue.FlushAll();

#if DEBUG
        // Spec §6 M3: memory stats visible at shutdown, before the blocks are freed.
        _allocator?.LogStats();
#endif

        // Then free the blocks (vkFreeMemory) — the device is still alive here, before ReleaseResources
        // destroys it.
        _allocator?.Dispose();
        _allocator = null;

        ReleaseResources();
        GC.SuppressFinalize(this);
    }

    private void CreateInstance(string applicationName, string[] requiredExtensions)
    {
        uint loaderVersion = 0;
        VkCheck.ThrowIfFailed(_vk.EnumerateInstanceVersion(&loaderVersion), "vkEnumerateInstanceVersion");
        if (loaderVersion < (uint)Vk.Version12)
        {
            throw new GraphicsException(
                $"Vulkan loader reports {VersionToString(loaderVersion)}; the engine baseline is 1.2 (spec §2).");
        }

        // Baseline is 1.2 (spec §2). When the loader allows it we declare 1.3 so
        // dynamicRendering/synchronization2 can be enabled as core features instead of
        // KHR extensions — required on MoltenVK, whose devices report 1.3+ and where
        // chaining Vulkan13Features under a declared 1.2 apiVersion is a validation error.
        _instanceApiVersion = loaderVersion >= (uint)Vk.Version13 ? (uint)Vk.Version13 : (uint)Vk.Version12;

        var availableLayers = GetInstanceLayerNames();
        var layers = new List<string>();
        if (EnableValidation)
        {
            if (availableLayers.Contains(ValidationLayerName))
            {
                layers.Add(ValidationLayerName);
            }
            else
            {
                Log.Warn($"GraphicsDevice: {ValidationLayerName} not installed; running without validation.");
            }
        }

        var availableExtensions = GetInstanceExtensionNames();
        var extensions = new List<string>(requiredExtensions);
        var flags = InstanceCreateFlags.None;
        if (availableExtensions.Contains(PortabilityEnumerationExtensionName))
        {
            // Without this, recent loaders hide MoltenVK (a non-conformant portability
            // implementation) and vkCreateInstance fails with INCOMPATIBLE_DRIVER (spec §2).
            extensions.Add(PortabilityEnumerationExtensionName);
            flags |= InstanceCreateFlags.EnumeratePortabilityBitKhr;
        }

        _debugUtilsEnabled = EnableValidation && availableExtensions.Contains(ExtDebugUtils.ExtensionName);
        if (_debugUtilsEnabled)
        {
            extensions.Add(ExtDebugUtils.ExtensionName);
        }

        var appName = (byte*)SilkMarshal.StringToPtr(applicationName);
        var engineName = (byte*)SilkMarshal.StringToPtr("Agapanthe");
        var layerNames = layers.Count > 0 ? (byte**)SilkMarshal.StringArrayToPtr(layers) : null;
        var extensionNames = extensions.Count > 0 ? (byte**)SilkMarshal.StringArrayToPtr(extensions) : null;
        try
        {
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApplicationVersion = Vk.MakeVersion(0, 1, 0),
                PEngineName = engineName,
                EngineVersion = Vk.MakeVersion(0, 1, 0),
                ApiVersion = _instanceApiVersion,
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                Flags = flags,
                PApplicationInfo = &appInfo,
                EnabledLayerCount = (uint)layers.Count,
                PpEnabledLayerNames = layerNames,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = extensionNames,
            };

            // Chained so vkCreateInstance/vkDestroyInstance themselves are covered by the callback.
            var debugCreateInfo = MakeDebugMessengerCreateInfo();
            if (_debugUtilsEnabled)
            {
                createInfo.PNext = &debugCreateInfo;
            }

            Instance instance;
            VkCheck.ThrowIfFailed(_vk.CreateInstance(&createInfo, null, &instance), "vkCreateInstance");
            _instance = instance;
            ResourceTracker.Register("VkInstance");
            Log.Info($"GraphicsDevice: instance created (apiVersion {VersionToString(_instanceApiVersion)}, " +
                     $"layers [{string.Join(", ", layers)}], extensions [{string.Join(", ", extensions)}]).");
        }
        finally
        {
            SilkMarshal.Free((nint)appName);
            SilkMarshal.Free((nint)engineName);
            if (layerNames != null)
            {
                SilkMarshal.Free((nint)layerNames);
            }

            if (extensionNames != null)
            {
                SilkMarshal.Free((nint)extensionNames);
            }
        }
    }

    private void CreateDebugMessenger()
    {
        if (!_debugUtilsEnabled)
        {
            return;
        }

        if (!_vk.TryGetInstanceExtension(_instance, out ExtDebugUtils debugUtils))
        {
            Log.Warn("GraphicsDevice: VK_EXT_debug_utils enabled but functions not loadable.");
            return;
        }

        _debugUtils = debugUtils;
        var createInfo = MakeDebugMessengerCreateInfo();
        DebugUtilsMessengerEXT messenger;
        VkCheck.ThrowIfFailed(
            _debugUtils.CreateDebugUtilsMessenger(_instance, &createInfo, null, &messenger),
            "vkCreateDebugUtilsMessengerEXT");
        _debugMessenger = messenger;
        ResourceTracker.Register("VkDebugMessenger");
    }

    private void CreateSurface(IVkSurface windowSurface)
    {
        if (!_vk.TryGetInstanceExtension(_instance, out KhrSurface khrSurface))
        {
            throw new GraphicsException("VK_KHR_surface functions not loadable; was the extension requested by the window?");
        }

        _khrSurface = khrSurface;
        var handle = windowSurface.Create<AllocationCallbacks>(new VkHandle(_instance.Handle), null);
        _surface = new SurfaceKHR(handle.Handle);
        ResourceTracker.Register("VkSurface");
    }

    private void SelectPhysicalDevice()
    {
        uint count = 0;
        VkCheck.ThrowIfFailed(_vk.EnumeratePhysicalDevices(_instance, &count, null), "vkEnumeratePhysicalDevices");
        if (count == 0)
        {
            throw new GraphicsException("No Vulkan physical device found.");
        }

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
        {
            VkCheck.ThrowIfFailed(_vk.EnumeratePhysicalDevices(_instance, &count, p), "vkEnumeratePhysicalDevices");
        }

        var bestScore = -1;
        DeviceCandidate best = default;
        foreach (var device in devices)
        {
            if (EvaluateDevice(device, out var candidate) && candidate.Score > bestScore)
            {
                bestScore = candidate.Score;
                best = candidate;
            }
        }

        if (bestScore < 0)
        {
            throw new GraphicsException(
                "No physical device supports the required queues (graphics + present), swapchain, dynamic rendering and synchronization2.");
        }

        _physicalDevice = best.Device;
        GraphicsQueueFamily = best.GraphicsFamily;
        PresentQueueFamily = best.PresentFamily;
        _useVulkan13Features = best.UseVulkan13Features;
        _hasPortabilitySubset = best.HasPortabilitySubset;
        AdapterName = best.Name;
        Log.Info($"GraphicsDevice: selected '{best.Name}' (graphics family {best.GraphicsFamily}, " +
                 $"present family {best.PresentFamily}, 1.3 core features: {best.UseVulkan13Features}, " +
                 $"portability subset: {best.HasPortabilitySubset}).");
    }

    private bool EvaluateDevice(PhysicalDevice device, out DeviceCandidate candidate)
    {
        candidate = default;
        var props = _vk.GetPhysicalDeviceProperties(device);
        var name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "<unknown>";

        if (!TryFindQueueFamilies(device, out var graphicsFamily, out var presentFamily))
        {
            return false;
        }

        var extensions = GetDeviceExtensionNames(device);
        if (!extensions.Contains(KhrSwapchain.ExtensionName))
        {
            return false;
        }

        // Feature route: core 1.3 when the effective version allows it, KHR extensions otherwise.
        var effectiveVersion = Math.Min(_instanceApiVersion, props.ApiVersion);
        var useVulkan13 = effectiveVersion >= (uint)Vk.Version13;
        if (!useVulkan13 &&
            (!extensions.Contains(Silk.NET.Vulkan.Extensions.KHR.KhrDynamicRendering.ExtensionName) ||
             !extensions.Contains(Silk.NET.Vulkan.Extensions.KHR.KhrSynchronization2.ExtensionName)))
        {
            return false;
        }

        if (!SupportsRequiredFeatures(device, useVulkan13))
        {
            return false;
        }

        var score = props.DeviceType switch
        {
            PhysicalDeviceType.DiscreteGpu => 1000,
            PhysicalDeviceType.IntegratedGpu => 100,
            _ => 10,
        };

        candidate = new DeviceCandidate(
            device, graphicsFamily, presentFamily, score, useVulkan13,
            extensions.Contains(PortabilitySubsetExtensionName), name);
        return true;
    }

    private bool TryFindQueueFamilies(PhysicalDevice device, out uint graphicsFamily, out uint presentFamily)
    {
        graphicsFamily = 0;
        presentFamily = 0;

        uint familyCount = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, null);
        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* p = families)
        {
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, p);
        }

        uint? graphics = null;
        uint? present = null;
        for (uint i = 0; i < families.Length; i++)
        {
            var supportsGraphics = (families[i].QueueFlags & QueueFlags.GraphicsBit) != 0;
            Bool32 supportsPresent = false;
            VkCheck.ThrowIfFailed(
                KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, _surface, &supportsPresent),
                "vkGetPhysicalDeviceSurfaceSupportKHR");

            if (supportsGraphics && supportsPresent)
            {
                // A single family for both avoids concurrent sharing on the swapchain.
                graphicsFamily = i;
                presentFamily = i;
                return true;
            }

            graphics ??= supportsGraphics ? i : null;
            present ??= supportsPresent ? i : null;
        }

        if (graphics is null || present is null)
        {
            return false;
        }

        graphicsFamily = graphics.Value;
        presentFamily = present.Value;
        return true;
    }

    private bool SupportsRequiredFeatures(PhysicalDevice device, bool useVulkan13)
    {
        if (useVulkan13)
        {
            var vk13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
            var features2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &vk13 };
            _vk.GetPhysicalDeviceFeatures2(device, &features2);
            return vk13.DynamicRendering && vk13.Synchronization2;
        }

        var dynamicRendering = new PhysicalDeviceDynamicRenderingFeatures { SType = StructureType.PhysicalDeviceDynamicRenderingFeatures };
        var synchronization2 = new PhysicalDeviceSynchronization2Features
        {
            SType = StructureType.PhysicalDeviceSynchronization2Features,
            PNext = &dynamicRendering,
        };
        var query = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &synchronization2 };
        _vk.GetPhysicalDeviceFeatures2(device, &query);
        return dynamicRendering.DynamicRendering && synchronization2.Synchronization2;
    }

    private void CreateLogicalDevice()
    {
        var queuePriority = 1f;
        var uniqueFamilyCount = GraphicsQueueFamily == PresentQueueFamily ? 1 : 2;
        var queueInfos = stackalloc DeviceQueueCreateInfo[2];
        queueInfos[0] = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = GraphicsQueueFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority,
        };
        queueInfos[1] = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = PresentQueueFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority,
        };

        var extensions = new List<string> { KhrSwapchain.ExtensionName };
        if (!_useVulkan13Features)
        {
            extensions.Add(Silk.NET.Vulkan.Extensions.KHR.KhrDynamicRendering.ExtensionName);
            extensions.Add(Silk.NET.Vulkan.Extensions.KHR.KhrSynchronization2.ExtensionName);
        }

        if (_hasPortabilitySubset)
        {
            // Mandatory: a device exposing VK_KHR_portability_subset requires it enabled (spec §2).
            extensions.Add(PortabilitySubsetExtensionName);
        }

        var vk13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true,
        };
        var dynamicRendering = new PhysicalDeviceDynamicRenderingFeatures
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
            DynamicRendering = true,
        };
        var synchronization2 = new PhysicalDeviceSynchronization2Features
        {
            SType = StructureType.PhysicalDeviceSynchronization2Features,
            Synchronization2 = true,
            PNext = &dynamicRendering,
        };
        // Anisotropic filtering: enable when the hardware has it (using it in a sampler without
        // the feature is a VUID). Core 1.0 feature — lives in features2.Features, not a pNext.
        _vk.GetPhysicalDeviceFeatures(_physicalDevice, out var supportedFeatures);
        _samplerAnisotropyEnabled = supportedFeatures.SamplerAnisotropy;
        _vk.GetPhysicalDeviceProperties(_physicalDevice, out var deviceProperties);
        _maxSamplerAnisotropy = deviceProperties.Limits.MaxSamplerAnisotropy;

        var features2 = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = _useVulkan13Features ? &vk13 : (void*)&synchronization2,
            Features = new PhysicalDeviceFeatures { SamplerAnisotropy = _samplerAnisotropyEnabled },
        };

        var extensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);
        try
        {
            var createInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &features2,
                QueueCreateInfoCount = (uint)uniqueFamilyCount,
                PQueueCreateInfos = queueInfos,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = extensionNames,
            };

            Device device;
            VkCheck.ThrowIfFailed(_vk.CreateDevice(_physicalDevice, &createInfo, null, &device), "vkCreateDevice");
            _device = device;
            ResourceTracker.Register("VkDevice");
        }
        finally
        {
            SilkMarshal.Free((nint)extensionNames);
        }

        GraphicsQueue = _vk.GetDeviceQueue(_device, GraphicsQueueFamily, 0);
        PresentQueue = _vk.GetDeviceQueue(_device, PresentQueueFamily, 0);

        if (!_vk.TryGetDeviceExtension(_instance, _device, out KhrSwapchain khrSwapchain))
        {
            throw new GraphicsException("VK_KHR_swapchain functions not loadable.");
        }

        _khrSwapchain = khrSwapchain;

        if (!_useVulkan13Features)
        {
            if (_vk.TryGetDeviceExtension(_instance, _device, out KhrDynamicRendering khrDynamicRendering))
            {
                _khrDynamicRendering = khrDynamicRendering;
            }

            if (_vk.TryGetDeviceExtension(_instance, _device, out KhrSynchronization2 khrSynchronization2))
            {
                _khrSynchronization2 = khrSynchronization2;
            }
        }

        Log.Info($"GraphicsDevice: logical device created (extensions [{string.Join(", ", extensions)}]).");
    }

    private void ReleaseResources()
    {
        if (_device.Handle != 0)
        {
            _vk.DestroyDevice(_device, null);
            _device = default;
            ResourceTracker.Unregister("VkDevice");
        }

        _khrSwapchain?.Dispose();
        _khrSwapchain = null;
        _khrDynamicRendering?.Dispose();
        _khrDynamicRendering = null;
        _khrSynchronization2?.Dispose();
        _khrSynchronization2 = null;

        if (_surface.Handle != 0)
        {
            _khrSurface!.DestroySurface(_instance, _surface, null);
            _surface = default;
            ResourceTracker.Unregister("VkSurface");
        }

        _khrSurface?.Dispose();
        _khrSurface = null;

        if (_debugMessenger.Handle != 0)
        {
            _debugUtils!.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            _debugMessenger = default;
            ResourceTracker.Unregister("VkDebugMessenger");
        }

        _debugUtils?.Dispose();
        _debugUtils = null;

        if (_instance.Handle != 0)
        {
            _vk.DestroyInstance(_instance, null);
            _instance = default;
            ResourceTracker.Unregister("VkInstance");
        }

        _vk.Dispose();
    }

    private static DebugUtilsMessengerCreateInfoEXT MakeDebugMessengerCreateInfo()
        => new()
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt
                              | DebugUtilsMessageSeverityFlagsEXT.InfoBitExt
                              | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                              | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                          | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                          | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = DebugCallbackDelegate,
        };

    private static uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        var message = SilkMarshal.PtrToString((nint)data->PMessage) ?? "<no message>";
        if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
        {
            Log.Error($"[Vulkan] {message}");
#if DEBUG
            // Fail fast (spec §4). This runs on a native (loader) call stack, so throwing a
            // managed exception across the boundary is undefined; FailFast crashes cleanly
            // with a dump instead.
            Environment.FailFast($"Vulkan validation error: {message}");
#endif
        }
        else if ((severity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0)
        {
            Log.Warn($"[Vulkan] {message}");
        }
        else
        {
            Log.Debug($"[Vulkan] {message}");
        }

        return 0;
    }

    private HashSet<string> GetInstanceLayerNames()
    {
        uint count = 0;
        VkCheck.ThrowIfFailed(_vk.EnumerateInstanceLayerProperties(&count, null), "vkEnumerateInstanceLayerProperties");
        var layers = new LayerProperties[count];
        fixed (LayerProperties* p = layers)
        {
            VkCheck.ThrowIfFailed(_vk.EnumerateInstanceLayerProperties(&count, p), "vkEnumerateInstanceLayerProperties");
        }

        var names = new HashSet<string>(layers.Length, StringComparer.Ordinal);
        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            if (SilkMarshal.PtrToString((nint)layer.LayerName) is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private HashSet<string> GetInstanceExtensionNames()
    {
        uint count = 0;
        VkCheck.ThrowIfFailed(
            _vk.EnumerateInstanceExtensionProperties((byte*)null, &count, null),
            "vkEnumerateInstanceExtensionProperties");
        var extensions = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = extensions)
        {
            VkCheck.ThrowIfFailed(
                _vk.EnumerateInstanceExtensionProperties((byte*)null, &count, p),
                "vkEnumerateInstanceExtensionProperties");
        }

        return ExtensionNames(extensions);
    }

    private HashSet<string> GetDeviceExtensionNames(PhysicalDevice device)
    {
        uint count = 0;
        VkCheck.ThrowIfFailed(
            _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null),
            "vkEnumerateDeviceExtensionProperties");
        var extensions = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = extensions)
        {
            VkCheck.ThrowIfFailed(
                _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, p),
                "vkEnumerateDeviceExtensionProperties");
        }

        return ExtensionNames(extensions);
    }

    private static HashSet<string> ExtensionNames(ExtensionProperties[] extensions)
    {
        var names = new HashSet<string>(extensions.Length, StringComparer.Ordinal);
        for (var i = 0; i < extensions.Length; i++)
        {
            var extension = extensions[i];
            if (SilkMarshal.PtrToString((nint)extension.ExtensionName) is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string VersionToString(uint version)
    {
        var v = (Version32)version;
        return $"{v.Major}.{v.Minor}.{v.Patch}";
    }

    private readonly record struct DeviceCandidate(
        PhysicalDevice Device,
        uint GraphicsFamily,
        uint PresentFamily,
        int Score,
        bool UseVulkan13Features,
        bool HasPortabilitySubset,
        string Name);
}
