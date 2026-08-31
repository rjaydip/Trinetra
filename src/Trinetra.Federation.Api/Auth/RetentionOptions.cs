namespace Trinetra.Federation.Api.Auth;

/// <summary>How long each high-volume table is kept before its partitions are dropped.</summary>
/// <remarks>
/// <para>
/// These periods are a <b>policy decision, not a technical one</b>. Event and video metadata from
/// public CCTV is subject to statutory retention rules, and the lawful period differs by state,
/// by department and by the purpose the cameras serve. The defaults here are a starting point
/// sized for disk, not legal advice — confirm them against the retention policy the deployment
/// is actually bound by before going live.
/// </para>
/// <para>
/// Every value is validated at startup. Retention deletes irreversibly, so a typo that reads as
/// "keep for 3 days" must not be discovered from a disk graph three days later.
/// </para>
/// </remarks>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>
    /// Whether retention runs at all. When off, partitions accumulate forever.
    /// </summary>
    /// <remarks>
    /// Exists so an operator can deliberately pause deletion during an investigation. It is not
    /// a safe long-term setting: <c>federation_event</c> takes 100-400M rows/day, so leaving this
    /// off is a disk-full outage on a schedule. Startup logs a warning while it is off.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>UTC hour at which the daily pass runs. Default 03:00.</summary>
    /// <remarks>
    /// Dropping a partition takes an ACCESS EXCLUSIVE lock on the parent table, so it is put in
    /// the quietest window rather than run on startup.
    /// </remarks>
    public int RunAtUtcHour { get; set; } = 3;

    /// <summary>Days of normalised events retained.</summary>
    public int EventDays { get; set; } = 90;

    /// <summary>Days of connector health history retained.</summary>
    /// <remarks>
    /// Shorter than events by default: health is operational telemetry for diagnosing a target
    /// that is misbehaving now, and nobody investigates last quarter's latency.
    /// </remarks>
    public int HealthDays { get; set; } = 30;

    /// <summary>Days of per-camera status transitions retained.</summary>
    /// <remarks>
    /// Longer than connector health, because these are the rows an incident review reads: "the
    /// camera covering that junction went Unreachable at 02:14" is a question asked weeks after
    /// the fact, whereas connector latency is only interesting while a target is misbehaving now.
    /// Affordable at this length precisely because only genuine transitions are stored —
    /// federated_camera.status_changed_at keeps the current status readable regardless of what
    /// this drops.
    /// </remarks>
    public int CameraStatusDays { get; set; } = 90;

    /// <summary>Months of audit history retained.</summary>
    /// <remarks>
    /// Far longer than everything else, and deliberately so. <c>config_audit</c> and
    /// <c>credential_access_log</c> are the record of who changed what and who read which
    /// credential — the evidence an audit or an incident review depends on, and the one category
    /// of data whose value increases with age.
    /// </remarks>
    public int AuditMonths { get; set; } = 84;

    /// <summary>Days of completed connection-test results retained.</summary>
    public int ConnectionTestDays { get; set; } = 30;

    /// <summary>Days of unparseable event payloads retained.</summary>
    /// <remarks>
    /// The dead letter table exists so a normalisation bug can be fixed and the payloads
    /// replayed. That window is what this bounds: beyond it the source VMS has almost certainly
    /// aged the event out too, so the payload can no longer be reconciled against anything.
    /// </remarks>
    public int DeadLetterDays { get; set; } = 90;

    /// <summary>The lowest retention any table may be configured to.</summary>
    /// <remarks>
    /// A floor rather than a warning. The realistic failure is a misplaced decimal or a value
    /// meant for one table pasted into another, and the result is silent, immediate and
    /// permanent data loss the moment the daily pass runs.
    /// </remarks>
    public const int MinimumDays = 7;

    /// <summary>Rejects settings that would destroy data unintentionally.</summary>
    public void Validate()
    {
        if (RunAtUtcHour is < 0 or > 23)
        {
            throw new InvalidOperationException(
                $"Retention:RunAtUtcHour is {RunAtUtcHour}. It must be an hour of the day, 0-23.");
        }

        RequireDays(nameof(EventDays), EventDays);
        RequireDays(nameof(HealthDays), HealthDays);
        RequireDays(nameof(CameraStatusDays), CameraStatusDays);
        RequireDays(nameof(ConnectionTestDays), ConnectionTestDays);
        RequireDays(nameof(DeadLetterDays), DeadLetterDays);

        if (AuditMonths < 1)
        {
            throw new InvalidOperationException(
                $"Retention:AuditMonths is {AuditMonths}. Audit history is the record of who "
                + "changed what and who read which credential; it must be retained for at least "
                + "one month, and most deployments are obliged to keep it for years.");
        }
    }

    private static void RequireDays(string name, int value)
    {
        if (value < MinimumDays)
        {
            throw new InvalidOperationException(
                $"Retention:{name} is {value}. The minimum is {MinimumDays} days.\n\n"
                + "  Retention drops partitions, which is immediate and irreversible — there is "
                + "no recycle bin and no undo.\n\n"
                + "  If a shorter period is genuinely required by policy, raise the floor "
                + $"deliberately in {nameof(RetentionOptions)}.{nameof(MinimumDays)} rather than "
                + "working around it here, so the decision is recorded in code review.");
        }
    }

    /// <summary>The cutoff dates this configuration implies, evaluated in UTC.</summary>
    /// <remarks>
    /// Dates, not timestamps, because the partitions are daily and monthly. UTC because
    /// departments' clocks and the server's local zone both drift; a retention boundary that
    /// moved with the host timezone would drop a different partition set after a DST change.
    /// </remarks>
    public RetentionCutoffs CutoffsFrom(DateTimeOffset utcNow)
    {
        var today = DateOnly.FromDateTime(utcNow.UtcDateTime);

        return new RetentionCutoffs(
            Events: today.AddDays(-EventDays),
            Health: today.AddDays(-HealthDays),
            CameraStatus: today.AddDays(-CameraStatusDays),
            Audit: today.AddMonths(-AuditMonths),
            ConnectionTests: utcNow.AddDays(-ConnectionTestDays),
            DeadLetter: utcNow.AddDays(-DeadLetterDays));
    }
}

/// <summary>Resolved retention boundaries for one pass.</summary>
public readonly record struct RetentionCutoffs(
    DateOnly Events,
    DateOnly Health,
    DateOnly CameraStatus,
    DateOnly Audit,
    DateTimeOffset ConnectionTests,
    DateTimeOffset DeadLetter);
