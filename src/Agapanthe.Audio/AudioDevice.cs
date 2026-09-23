using System.Diagnostics;
using Agapanthe.Core;
using Silk.NET.OpenAL;

namespace Agapanthe.Audio;

/// <summary>
/// The one OpenAL device/context/source-pool for a client host (spec D3/D7). <see cref="TryCreate"/> is the
/// only entry point and NEVER throws: any native failure (no hardware, no driver, OpenAL runtime absent)
/// degrades to <see cref="Supported"/> <see langword="false"/> rather than propagating — mirrors the
/// already-shipped <c>GraphicsDevice.SupportsGpuTimestamps</c>/<c>Renderer.GpuPassTimingsMs</c> capability-
/// detect-and-degrade pattern from UI-3. <see cref="Play"/> and <see cref="AudioLoader.Load"/> are always safe
/// to call — both are silent no-ops when unsupported.
/// <para>
/// <b>Single instance per process</b> (audit finding, engine-architect): <c>alcMakeContextCurrent</c> is
/// process-global OpenAL state. A second live instance would steal the current context out from under the
/// first, and the first's <see cref="Dispose"/> would then silently fail to tear down (the exact interaction
/// two of this file's own tests reproduced). <see cref="TryCreate"/> enforces this with a process-wide guard —
/// a second concurrent attempt degrades to <see cref="Supported"/> <see langword="false"/> rather than fighting
/// the first instance for the context.
/// </para>
/// </summary>
public sealed class AudioDevice : IDisposable
{
    private const int SourcePoolSize = 8; // spec D9 — fixed, pre-allocated, no per-call growth

    // Enforces the single-instance-per-process invariant above (Interlocked, not merely documented).
    private static int _liveInstanceCount;

    private readonly AL? _al;
    private readonly ALContext? _alc;
    private readonly unsafe Device* _alDevice;
    private readonly unsafe Context* _alContext;
    private readonly uint[] _sourcePool;
    private readonly List<uint> _bufferIds = [];
    private readonly bool _ownsLiveInstanceSlot;
    private int _nextSourceIndex;
    private bool _disposed;
    private bool _teardownFailed;

    public bool Supported { get; }

    /// <summary>Test-only observability (spec D11's regression test): <see langword="true"/> whenever the real
    /// <c>alcOpenDevice</c>/<c>alcCreateContext</c> path actually succeeded, REGARDLESS of
    /// <see cref="Supported"/> — proves <c>AGAPANTHE_AUDIO=0</c>/<paramref name="enabled"/><c>=false</c> ANDs the
    /// flag afterward rather than short-circuiting before ever touching AL.</summary>
    internal bool RealDeviceOpened => _sourcePool.Length > 0;

    private unsafe AudioDevice(
        AL? al, ALContext? alc, Device* device, Context* context, uint[] sourcePool, bool supported, bool ownsLiveInstanceSlot)
    {
        _al = al;
        _alc = alc;
        _alDevice = device;
        _alContext = context;
        _sourcePool = sourcePool;
        _ownsLiveInstanceSlot = ownsLiveInstanceSlot;
        Supported = supported;
    }

    /// <summary>
    /// Always attempts the real <c>alcOpenDevice</c>/<c>alcCreateContext</c> path first — <paramref name="enabled"/>
    /// <c>= false</c> ONLY ANDs <see cref="Supported"/> to <see langword="false"/> AFTERWARD (spec D11), never a
    /// short-circuit: a short-circuit would make "never throws" prove nothing about real AL failure handling.
    /// Mirrors <c>GraphicsDevice</c>'s own real-detect-then-AND shape for GPU timestamp support. Never reads the
    /// environment itself (audit finding, engine-architect — the S30 invariant "no path reads the environment
    /// outside <c>HostOptions</c>") — the caller (<c>AppHost</c>) passes <c>HostOptions.AudioEnabled</c>.
    /// </summary>
    public static unsafe AudioDevice TryCreate(bool enabled = true)
    {
        // The single-instance guard (see class remarks) — checked before touching AL at all, since a second
        // live instance cannot safely share the process-global current context with the first.
        if (Interlocked.CompareExchange(ref _liveInstanceCount, 1, 0) != 0)
        {
            Log.Warn("AudioDevice: a live instance already exists in this process (OpenAL's current context is " +
                "process-global) — this attempt degrades to unsupported rather than fighting it for the context.");
            return Unsupported(ownsLiveInstanceSlot: false);
        }

        var acquiredSlot = true;
        try
        {
            var alc = ALContext.GetApi(soft: true);
            var device = alc.OpenDevice(null);
            if (device is null)
            {
                Log.Warn("AudioDevice: alcOpenDevice returned no device — audio unavailable.");
                return ReleaseSlotAndReturnUnsupported(ref acquiredSlot);
            }

            var context = alc.CreateContext(device, null);
            if (context is null)
            {
                Log.Warn("AudioDevice: alcCreateContext failed — audio unavailable.");
                alc.CloseDevice(device);
                return ReleaseSlotAndReturnUnsupported(ref acquiredSlot);
            }

            if (!alc.MakeContextCurrent(context))
            {
                Log.Warn("AudioDevice: alcMakeContextCurrent failed — audio unavailable.");
                alc.DestroyContext(context);
                alc.CloseDevice(device);
                return ReleaseSlotAndReturnUnsupported(ref acquiredSlot);
            }

            var al = AL.GetApi(soft: true);
            var sourcePool = new uint[SourcePoolSize];
            var sourcesCreated = 0;
            try
            {
                for (var i = 0; i < sourcePool.Length; i++)
                {
                    sourcePool[i] = al.GenSource();
                    sourcesCreated++;
                }

                // Always-on (not [Conditional("DEBUG")]) — this is the one-time init check, it costs nothing
                // per-frame, and a silently-failed GenSource (id 0, error set) would otherwise leave a Supported
                // device with a useless pool (audit finding, csharp-lowlevel).
                var initError = al.GetError();
                if (initError != AudioError.NoError)
                {
                    throw new AudioException($"OpenAL error during source pool initialization: {initError}.");
                }
            }
            catch
            {
                // Partial pool: delete whatever sources were actually created before giving up (audit finding,
                // csharp-lowlevel — TryCreate previously leaked on any throw after context creation succeeded).
                for (var i = 0; i < sourcesCreated; i++)
                {
                    al.DeleteSource(sourcePool[i]);
                }

                alc.MakeContextCurrent(null);
                alc.DestroyContext(context);
                alc.CloseDevice(device);
                return ReleaseSlotAndReturnUnsupported(ref acquiredSlot);
            }

            Log.Info(enabled
                ? "AudioDevice: OpenAL device opened."
                : "AudioDevice: OpenAL device opened, but forced off (AGAPANTHE_AUDIO=0 / HostOptions.AudioEnabled=false).");
            return new AudioDevice(al, alc, device, context, sourcePool, supported: enabled, ownsLiveInstanceSlot: true);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            Log.Warn($"AudioDevice: audio unavailable — {ex.Message}");
            return ReleaseSlotAndReturnUnsupported(ref acquiredSlot);
        }

        static unsafe AudioDevice ReleaseSlotAndReturnUnsupported(ref bool acquiredSlot)
        {
            if (acquiredSlot)
            {
                Interlocked.Exchange(ref _liveInstanceCount, 0);
                acquiredSlot = false;
            }

            return Unsupported(ownsLiveInstanceSlot: false);
        }
    }

    private static unsafe AudioDevice Unsupported(bool ownsLiveInstanceSlot)
        => new(null, null, null, null, [], supported: false, ownsLiveInstanceSlot);

    /// <summary>Silent no-op if <see cref="Supported"/> is <see langword="false"/> or <paramref name="clip"/> is
    /// <see cref="AudioClip"/>'s default (no buffer) value — always safe to call.</summary>
    public unsafe void Play(AudioClip clip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Supported || clip.BufferId == 0)
        {
            return;
        }

        var sourceId = _sourcePool[_nextSourceIndex];
        _nextSourceIndex = (_nextSourceIndex + 1) % _sourcePool.Length;

        // Stop first: the pool round-robins, so a slot may already be playing an older one-shot when reused.
        _al!.SourceStop(sourceId);
        _al.SetSourceProperty(sourceId, SourceInteger.Buffer, (int)clip.BufferId);
        _al.SourcePlay(sourceId);
        CheckAlError(_al, nameof(Play));
    }

    /// <summary>Decodes <paramref name="data"/> and uploads it to a new OpenAL buffer — silently returns
    /// <see cref="AudioClip"/>'s default (no buffer) value if <see cref="Supported"/> is <see langword="false"/>
    /// (spec D10's stated limitation: no real allocation happens in the unsupported case, which is why the
    /// leak-tracking test below is only load-bearing on a machine with real/software audio). Only mono/stereo
    /// 8/16-bit PCM can be represented as an OpenAL buffer format — anything else throws
    /// <see cref="AudioException"/> (a real content/format mismatch, not a missing-device condition).</summary>
    internal unsafe AudioClip UploadClip(in WavFormat.WavData data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Supported)
        {
            return default;
        }

        var format = MapFormat(data.Channels, data.BitsPerSample);
        var bufferId = _al!.GenBuffer();
        try
        {
            fixed (byte* pcm = data.Pcm)
            {
                _al.BufferData(bufferId, format, pcm, data.Pcm.Length, data.SampleRate);
            }

            CheckAlError(_al, nameof(UploadClip));
        }
        catch
        {
            // Audit finding (csharp-lowlevel): the id must not be lost on a failed upload — GenBuffer already
            // allocated it natively, so it is deleted here rather than silently orphaned.
            _al.DeleteBuffer(bufferId);
            throw;
        }

        _bufferIds.Add(bufferId);
        return new AudioClip(bufferId);
    }

    private static BufferFormat MapFormat(short channels, short bitsPerSample) => (channels, bitsPerSample) switch
    {
        (1, 8) => BufferFormat.Mono8,
        (1, 16) => BufferFormat.Mono16,
        (2, 8) => BufferFormat.Stereo8,
        (2, 16) => BufferFormat.Stereo16,
        _ => throw new AudioException(
            $"Unsupported channel/bit-depth combination for OpenAL upload: {channels}ch/{bitsPerSample}-bit " +
            "(only mono/stereo 8/16-bit are supported)."),
    };

    /// <summary>
    /// <see langword="true"/> if every OpenAL buffer/source this device ever created was actually verified
    /// deleted by <see cref="Dispose"/> (spec D10, tightened post-audit) — a minimal Audio-scoped analog of the
    /// Vulkan-side <c>ResourceTracker.Report()</c> (same role: called once, ANDed into the SAME <c>clean</c> flag
    /// that already drives <c>AppHost</c>'s process exit code). Must be called AFTER <see cref="Dispose"/>.
    /// <para>
    /// <b>Verified, not merely counted</b> (audit finding, csharp-lowlevel — the original version cleared its
    /// tracking lists unconditionally in <see cref="Dispose"/>, so this always returned <see langword="true"/>
    /// even when a delete genuinely failed, e.g. deleting a buffer still attached to a source). <see cref="Dispose"/>
    /// now calls <c>AL.IsBuffer</c>/<c>AL.IsSource</c> right after each delete and records a failure in
    /// <see cref="_teardownFailed"/> if the native object is still alive.
    /// </para>
    /// </summary>
    public bool ReportLeaks() => !_teardownFailed;

    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_al is not null)
        {
            // Detach each buffer from its source and stop playback BEFORE deleting buffers — OpenAL refuses to
            // delete a buffer still bound to a source (AL_INVALID_OPERATION), and a source keeps its AL_BUFFER
            // set after a one-shot finishes playing (audit finding, csharp-lowlevel — confirmed by mutation: the
            // previous buffers-before-sources order leaked every buffer that had ever been played).
            foreach (var sourceId in _sourcePool)
            {
                _al.SourceStop(sourceId);
                _al.SetSourceProperty(sourceId, SourceInteger.Buffer, 0);
            }

            foreach (var sourceId in _sourcePool)
            {
                _al.DeleteSource(sourceId);
                if (_al.IsSource(sourceId))
                {
                    _teardownFailed = true;
                    Log.Error($"AudioDevice: source {sourceId} was not released by DeleteSource — leaking.");
                }
            }

            foreach (var bufferId in _bufferIds)
            {
                _al.DeleteBuffer(bufferId);
                if (_al.IsBuffer(bufferId))
                {
                    _teardownFailed = true;
                    Log.Error($"AudioDevice: buffer {bufferId} was not released by DeleteBuffer — leaking.");
                }
            }

            _bufferIds.Clear();
        }

        if (_alc is not null)
        {
            // Does NOT unconditionally clear the process-global current context (audit finding, engine-architect
            // — doing so would break any OTHER live AudioDevice's later AL calls). The single-instance guard
            // above means there is nothing else to protect in production; this order still tears down cleanly.
            if (_alContext is not null)
            {
                _alc.MakeContextCurrent(null);
                _alc.DestroyContext(_alContext);
            }

            if (_alDevice is not null)
            {
                _alc.CloseDevice(_alDevice);
            }
        }

        if (_ownsLiveInstanceSlot)
        {
            Interlocked.Exchange(ref _liveInstanceCount, 0);
        }
    }

    /// <summary>
    /// Mirrors the project's locked Vulkan discipline ("tout message de validation layer = bug") for OpenAL
    /// (spec D8) — Debug-only, same posture as the Vulkan validation layers themselves (`AGAPANTHE_SYNC_VALIDATION`,
    /// on by default in Debug, an opt-out dev-tooling cost rather than an always-on correctness assert). Not used
    /// for the leak gate itself (see <see cref="Dispose"/>'s always-on <c>IsBuffer</c>/<c>IsSource</c> checks).
    /// </summary>
    [Conditional("DEBUG")]
    private static void CheckAlError(AL al, string context)
    {
        var error = al.GetError();
        if (error != AudioError.NoError)
        {
            throw new AudioException($"OpenAL error during {context}: {error}.");
        }
    }
}
