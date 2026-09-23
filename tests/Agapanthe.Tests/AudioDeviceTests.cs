using Agapanthe.Audio;

namespace Agapanthe.Tests;

/// <summary>
/// Audio-1 — <see cref="AudioDevice"/>/<see cref="AudioLoader"/>. Several of these tests are honestly
/// hardware-dependent (spec's stated, accepted limitation): a machine/CI runner with no real or software-
/// fallback OpenAL device still passes them, but trivially — nothing was ever allocated, so "zero leaks" and
/// "no allocation" hold vacuously. This mirrors <c>AGAPANTHE_GPU_TIMESTAMPS=0</c>'s own forced-off path never
/// exercising real GPU timestamp math either. Every test still runs on every machine; none is skipped.
/// </summary>
public sealed class AudioDeviceTests
{
    [Fact]
    public void TryCreate_NeverThrows()
    {
        var ex = Record.Exception(() =>
        {
            using var device = AudioDevice.TryCreate();
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Play_IsATrueNoOp_WhenForcedUnsupported()
    {
        using var device = AudioDevice.TryCreate(enabled: false);
        Assert.False(device.Supported);

        var before = GC.GetAllocatedBytesForCurrentThread();
        device.Play(default);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }

    [Fact]
    public void ForcedUnsupported_StillAttemptsTheRealOpenPath_NotAShortCircuit()
    {
        // Ground truth for this machine: does a real/software OpenAL device exist at all? Disposed immediately
        // (releasing the single-instance slot, see AudioDevice's class remarks) before creating the next one —
        // two live instances would otherwise fight over OpenAL's process-global current context.
        bool hasRealHardware;
        using (var probe = AudioDevice.TryCreate())
        {
            hasRealHardware = probe.RealDeviceOpened;
        }

        if (!hasRealHardware)
        {
            return; // inconclusive on this machine — the claim under test can't be observed either way here.
        }

        using var forced = AudioDevice.TryCreate(enabled: false);
        Assert.False(forced.Supported);
        Assert.True(forced.RealDeviceOpened); // the real path ran; only the flag was ANDed off afterward.
    }

    [Fact]
    public void SecondConcurrentInstance_DegradesInsteadOfStealingTheContext()
    {
        // Audit finding (engine-architect): alcMakeContextCurrent is process-global — a second live instance
        // must not fight the first for it. AudioDevice enforces single-instance-per-process explicitly.
        using var first = AudioDevice.TryCreate();
        if (!first.RealDeviceOpened)
        {
            return; // no real device on this machine — nothing to contend over.
        }

        using var second = AudioDevice.TryCreate();
        Assert.False(second.Supported);
        Assert.False(second.RealDeviceOpened); // never even attempted while `first` still holds the slot.

        // The first instance is unaffected by the second's (failed) attempt.
        Assert.True(first.Supported);
    }

    [Fact]
    public void AudioLoader_Load_ReturnsTheDefaultClip_WhenDeviceUnsupported_WithNoFilesystemOrAlAccess()
    {
        using var device = AudioDevice.TryCreate(enabled: false);

        // A path that does not exist — proves Load never even opens the file when unsupported.
        var clip = AudioLoader.Load(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.wav"), device);

        Assert.Equal(default, clip);
    }

    [Fact]
    public void AudioLoader_Load_FromStream_RoundTrips()
    {
        using var device = AudioDevice.TryCreate();
        using var stream = new MemoryStream();
        WavFormat.Write(stream, WavFormat.SineTone());
        stream.Position = 0;

        var clip = AudioLoader.Load(stream, device);

        // Supported => a real buffer id; unsupported => the default (no filesystem/AL access at all either way).
        Assert.Equal(device.Supported, !clip.Equals(default(AudioClip)));
    }

    [Fact]
    public void ReportLeaks_IsCleanAfterProperDisposal()
    {
        var device = AudioDevice.TryCreate();

        // Exercised for real only on a machine with an actual device (spec's stated limitation) — still
        // passes trivially otherwise, since nothing was ever allocated.
        if (device.Supported)
        {
            using var stream = new MemoryStream();
            WavFormat.Write(stream, WavFormat.SineTone());
            stream.Position = 0;
            var clip = AudioLoader.Load(stream, device);
            device.Play(clip);
        }

        device.Dispose();

        Assert.True(device.ReportLeaks());
    }

    [Fact]
    public void Play_IsZeroAllocAfterWarmup_WhenSupported()
    {
        using var device = AudioDevice.TryCreate();
        if (!device.Supported)
        {
            return; // hardware-dependent, spec's stated limitation.
        }

        using var stream = new MemoryStream();
        WavFormat.Write(stream, WavFormat.SineTone());
        stream.Position = 0;
        var clip = AudioLoader.Load(stream, device);

        for (var i = 0; i < 16; i++)
        {
            device.Play(clip); // warm up
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
        {
            device.Play(clip);
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Play_ThrowsAfterDispose()
    {
        var device = AudioDevice.TryCreate();
        device.Dispose();

        Assert.Throws<ObjectDisposedException>(() => device.Play(default));
    }
}
