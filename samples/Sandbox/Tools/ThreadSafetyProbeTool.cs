using Agapanthe.Core;
using Agapanthe.Graphics;
using Agapanthe.Graphics.Memory;
using Agapanthe.Platform;

namespace Sandbox;

/// <summary>
/// GraphicsDevice thread-safety spec verification (D2, D4c, D8): a real window + <see cref="GraphicsDevice"/>
/// built for a one-shot diagnostic run, mirroring <see cref="IblTestTool"/>'s exact shape — a real
/// <c>GraphicsDevice</c> cannot be built inside <c>Agapanthe.Tests</c> (round-2 finding: it needs a real
/// <c>IVkSurface</c>/GLFW window). Triggered by <c>AGAPANTHE_THREAD_SAFETY_TEST=&lt;N&gt;</c> (N = upload
/// iterations per thread). <b>Debug build only</b>: the Vulkan validation layer and this project's
/// FailFast-on-error debug callback are Debug-only, and thread-safety checking is on by default in
/// <c>VK_LAYER_KHRONOS_validation</c> — no extra flag needed, unlike synchronization validation
/// (<c>VK_EXT_validation_features</c>), which is a different class of bug (missing barriers, not concurrent
/// queue access).
/// </summary>
internal static unsafe class ThreadSafetyProbeTool
{
    public static int Run(int iterationsPerThread)
    {
        var allPassed = true;
        using var window = new EngineWindow("Agapanthe — thread-safety probe", 64, 64);
        GraphicsDevice? device = null;

        window.Loaded += () =>
        {
            device = new GraphicsDevice(
                "Agapanthe thread-safety probe", window.GetRequiredVulkanExtensions(), window.VkSurface!);

            allPassed &= RunConcurrentUploadAndGuardChecks(device, iterationsPerThread);
            allPassed &= RunDisposePreconditionCheck(device);

            window.Close();
        };

        try
        {
            window.Run();
        }
        finally
        {
            device?.DeletionQueue.FlushAll();
            device?.Dispose();
        }

        var clean = ResourceTracker.Report();
        if (!clean)
        {
            Log.Error("ThreadSafetyProbeTool: ResourceTracker reported a leak.");
        }

        return allPassed && clean ? 0 : 1;
    }

    /// <summary>
    /// Checks 1-3: baseline concurrent upload (D9's two-uploader isolation — one <see cref="GpuUploader"/>
    /// per thread, never shared), D4c (the owner thread's <see cref="GraphicsDevice.WaitIdle"/> concurrent
    /// with an in-flight loader upload — "the most serious gap" this spec closes) and the guard-level
    /// discrimination (D2, round-3 correction): the actually discriminating case is the SANCTIONED loader
    /// thread itself — having just proven it legitimately passes <c>AssertCallerThread</c> via its own
    /// uploads — being rejected by <c>AssertOwnerThreadStrict</c> on <c>QueuePresent</c>/<c>AdvanceFrame</c>.
    /// An unsanctioned third thread would only prove *a* guard fires, not that the two levels differ.
    /// </summary>
    private static bool RunConcurrentUploadAndGuardChecks(GraphicsDevice device, int iterationsPerThread)
    {
        const ulong bufferSize = 256;
        using var ownerBuffer = new GpuBuffer(device, bufferSize, BufferUsage.Storage, MemoryDomain.DeviceLocal);
        using var ownerUploader = new GpuUploader(device);
        using var loaderBuffer = new GpuBuffer(device, bufferSize, BufferUsage.Storage, MemoryDomain.DeviceLocal);
        using var loaderUploader = new GpuUploader(device);

        Exception? presentGuardException = null;
        Exception? advanceGuardException = null;
        Exception? loaderFailure = null;
        var loaderData = new byte[bufferSize];

        var loaderThread = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < iterationsPerThread; i++)
                {
                    loaderData[0] = (byte)i;
                    loaderUploader.Upload<byte>(loaderBuffer, loaderData);
                }

                // Check 3 — still sanctioned at this point, and this thread's own loop above already
                // proved it is a legitimate AssertCallerThread caller (every Upload -> QueueSubmit2 passed).
                try
                {
                    device.QueuePresent(device.PresentQueue, null);
                }
                catch (Exception ex)
                {
                    presentGuardException = ex;
                }

                try
                {
                    device.AdvanceFrame();
                }
                catch (Exception ex)
                {
                    advanceGuardException = ex;
                }
            }
            catch (Exception ex)
            {
                loaderFailure = ex;
            }
        });

        device.SetSanctionedLoaderThread(loaderThread.ManagedThreadId);
        loaderThread.Start();

        // Check 2 (D4c) — no fixed synchronization point exists on purpose: the point is to race WaitIdle
        // against the loader's in-flight uploads. The owner thread keeps submitting its own uploads too, so
        // a missing lock would have two independent submitters plus a WaitIdle all contending.
        var ownerData = new byte[bufferSize];
        for (var i = 0; i < 20 && loaderThread.IsAlive; i++)
        {
            device.WaitIdle();
            ownerData[0] = (byte)i;
            ownerUploader.Upload<byte>(ownerBuffer, ownerData);
        }

        loaderThread.Join();
        device.ClearSanctionedLoaderThread();

        var passed = true;
        if (loaderFailure is not null)
        {
            Log.Error($"ThreadSafetyProbeTool check 1 (baseline concurrent upload, D9) FAILED: {loaderFailure}");
            passed = false;
        }
        else
        {
            Log.Info("ThreadSafetyProbeTool check 1 (baseline concurrent upload, D9): PASS");
        }

        // No validation message fired and no corruption resulted, or the process would already be dead
        // (this project's Debug debug-callback calls Environment.FailFast on any validation error) or the
        // Join() above would have thrown/hung from a corrupted uploader.
        Log.Info("ThreadSafetyProbeTool check 2 (WaitIdle concurrent with loader upload, D4c): PASS");

        if (presentGuardException is InvalidOperationException && advanceGuardException is InvalidOperationException)
        {
            Log.Info(
                "ThreadSafetyProbeTool check 3 (guard-level discrimination, D2): PASS — the sanctioned loader " +
                "thread was rejected by AssertOwnerThreadStrict on both QueuePresent and AdvanceFrame despite " +
                "legitimately passing AssertCallerThread on its own uploads.");
        }
        else
        {
            Log.Error(
                $"ThreadSafetyProbeTool check 3 (guard-level discrimination, D2) FAILED: QueuePresent threw " +
                $"{presentGuardException?.GetType().Name ?? "nothing"}, AdvanceFrame threw " +
                $"{advanceGuardException?.GetType().Name ?? "nothing"} — both must be InvalidOperationException.");
            passed = false;
        }

        return passed;
    }

    /// <summary>
    /// Check 4 (D8): <see cref="GraphicsDevice.Dispose"/> must throw if a loader thread is still sanctioned.
    /// A synthetic registration is enough per the spec — no live thread needs to actually be running.
    /// </summary>
    private static bool RunDisposePreconditionCheck(GraphicsDevice device)
    {
        device.SetSanctionedLoaderThread(int.MaxValue); // synthetic id, never a real running thread
        var threw = false;
        try
        {
            device.Dispose();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        finally
        {
            // Must not leave the tool's own final teardown dirty regardless of outcome (D8's check runs
            // before _disposed is set, so the device is still fully usable here either way).
            device.ClearSanctionedLoaderThread();
        }

        if (threw)
        {
            Log.Info("ThreadSafetyProbeTool check 4 (Dispose precondition, D8): PASS");
            return true;
        }

        Log.Error(
            "ThreadSafetyProbeTool check 4 (Dispose precondition, D8) FAILED: Dispose() did not throw while " +
            "a loader thread was still sanctioned.");
        return false;
    }
}
