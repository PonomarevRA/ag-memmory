using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgMemory.Storage.LanceDb;

/// <summary>
/// Serializes adapter access across cooperating processes with a kernel-held, never-unlinked file handle.
/// Older clients do not acquire this lease and must reconnect before relying on cross-instance CAS safety.
/// Native multi-table operations remain non-atomic when a process fails midway through a commit.
/// </summary>
internal sealed class StorageLeaseGate(string storagePath, string leaseFileName = ".agmemory-store.lock")
{
    private static readonly Meter Meter = new("AgMemory.Storage.LanceDb", "1.0");
    private static readonly Histogram<double> WaitDuration = Meter.CreateHistogram<double>("agmemory.storage.gate.wait", "ms");
    private static readonly Histogram<double> HoldDuration = Meter.CreateHistogram<double>("agmemory.storage.gate.hold", "ms");
    // Active gates strongly retain a shared coordinator. The pool holds only weak references so
    // transient stores/scopes can be collected; periodic sweeps discard their path keys as well.
    private static readonly object CoordinatorSync = new();
    private static readonly Dictionary<string, WeakReference<SemaphoreSlim>> Coordinators = new(StringComparer.Ordinal);
    private static int _coordinatorLookups;
    private readonly SemaphoreSlim _local = GetCoordinator(Path.GetFullPath(Path.Combine(storagePath, leaseFileName)));
    private readonly string _leasePath = Path.Combine(Path.GetFullPath(storagePath), leaseFileName);
    private FileStream? _lease;
    private long _heldSince;
    private long _acquisitions;
    private long _waitTicks;
    private long _holdTicks;

    /// <summary>Atomically shares a live local queue and periodically removes dead weak entries without replacing an active gate or waiter.</summary>
    private static SemaphoreSlim GetCoordinator(string path)
    {
        lock (CoordinatorSync)
        {
            if (++_coordinatorLookups % 128 == 0)
            {
                var expired = new List<string>();
                foreach (var pair in Coordinators)
                    if (!pair.Value.TryGetTarget(out _)) expired.Add(pair.Key);
                foreach (var key in expired) Coordinators.Remove(key);
            }
            if (Coordinators.TryGetValue(path, out var reference) && reference.TryGetTarget(out var live)) return live;
            var coordinator = new SemaphoreSlim(1, 1);
            Coordinators[path] = new(coordinator);
            return coordinator;
        }
    }

    /// <summary>Attempts ownership without waiting; used only to avoid holding the primary lease behind a scope's existing builder.</summary>
    public bool TryWait()
    {
        if (!_local.Wait(0)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_leasePath)!);
            _lease = new FileStream(_leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _heldSince = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _acquisitions);
            return true;
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 35)
        {
            _local.Release();
            return false;
        }
        catch { _local.Release(); throw; }
    }

    /// <summary>Waits asynchronously for local ownership and the cross-process lease; cancellation never leaves a held local gate.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        await _local.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_leasePath)!);
            var backoffMilliseconds = 2;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _lease = new FileStream(_leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 35)
                {
                    // EAGAIN/EWOULDBLOCK are 11 on Linux and 35 on macOS; Windows uses sharing violation 32.
                    // Kernel ownership, not timestamps or PIDs, determines liveness. Never unlink this file.
                    await Task.Delay(backoffMilliseconds, cancellationToken).ConfigureAwait(false);
                    backoffMilliseconds = Math.Min(backoffMilliseconds * 2, 50);
                }
            }
            _heldSince = Stopwatch.GetTimestamp();
            var waited = _heldSince - started;
            Interlocked.Add(ref _waitTicks, waited);
            Interlocked.Increment(ref _acquisitions);
            WaitDuration.Record(waited * 1000d / Stopwatch.Frequency);
        }
        catch
        {
            _lease?.Dispose();
            _lease = null;
            _local.Release();
            throw;
        }
    }

    /// <summary>Releases the kernel lease before admitting the next local operation and records the completed hold interval.</summary>
    public void Release()
    {
        var held = Stopwatch.GetTimestamp() - _heldSince;
        Interlocked.Add(ref _holdTicks, held);
        _lease?.Dispose();
        _lease = null;
        _local.Release();
        HoldDuration.Record(held * 1000d / Stopwatch.Frequency);
    }

    public StorageGateStatistics Statistics => new(Interlocked.Read(ref _acquisitions),
        Interlocked.Read(ref _waitTicks) * 1000d / Stopwatch.Frequency,
        Interlocked.Read(ref _holdTicks) * 1000d / Stopwatch.Frequency);

}

/// <summary>Cumulative gate observations for this store instance; values contain no scope, path or record identifiers.</summary>
public sealed record StorageGateStatistics(long Acquisitions, double WaitMilliseconds, double HoldMilliseconds);
