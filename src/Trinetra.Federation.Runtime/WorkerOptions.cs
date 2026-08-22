using System.ComponentModel.DataAnnotations;
using Trinetra.Federation.Core.Model;

namespace Trinetra.Federation.Runtime;

/// <summary>Configuration for one connector worker process.</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// Stable identity for this worker. Defaults to host plus process id.
    /// </summary>
    /// <remarks>
    /// Must be unique across the fleet: two workers sharing an id would renew each other's
    /// leases and both poll the same VMS, which presents to the vendor as double the agreed
    /// request rate and to us as duplicated events.
    /// </remarks>
    public string WorkerId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";

    /// <summary>
    /// Upper bound on targets this process will hold.
    /// </summary>
    /// <remarks>
    /// Caps blast radius: without it a single worker starting first claims the entire estate,
    /// and its failure becomes a total outage instead of a partial one.
    /// </remarks>
    [Range(1, 1000)]
    public int MaxTargets { get; set; } = 100;

    /// <summary>
    /// How long a claim survives without renewal — and therefore the failover latency.
    /// </summary>
    /// <remarks>
    /// Too short and a GC pause or a slow query orphans live targets; too long and a crashed
    /// worker's sites go unpolled for that whole window. 45s is the default balance.
    /// </remarks>
    public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Renewal interval. Kept at a third of the TTL so two consecutive misses are
    /// survivable before ownership is lost.</summary>
    public TimeSpan RenewInterval => TimeSpan.FromTicks(LeaseTtl.Ticks / 3);

    /// <summary>How often to look for newly claimable targets.</summary>
    public TimeSpan ClaimInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Which isolation class this process serves. See <see cref="RuntimeClass"/> — a
    /// <see cref="RuntimeClass.Native"/> worker must never share a process with managed targets.
    /// </summary>
    public RuntimeClass RuntimeClass { get; set; } = RuntimeClass.Managed;

    /// <summary>Restricts claims to these vendors. Required for native workers, which are
    /// pinned to one vendor SDK per process.</summary>
    public IList<VendorKind> VendorFilter { get; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkerId))
        {
            throw new InvalidOperationException("Worker:WorkerId must be set.");
        }

        if (RuntimeClass == RuntimeClass.Native && VendorFilter.Count != 1)
        {
            throw new InvalidOperationException(
                "A Native runtime-class worker must pin exactly one vendor via Worker:VendorFilter. "
                + "Native SDK faults kill the process, so mixing vendors in one native worker "
                + "widens the blast radius to every vendor it holds.");
        }
    }
}
