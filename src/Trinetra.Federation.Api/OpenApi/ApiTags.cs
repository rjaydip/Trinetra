namespace Trinetra.Federation.Api.OpenApi;

/// <summary>
/// The API's endpoint groups, and what each one is for.
/// </summary>
/// <remarks>
/// <para>
/// Tags are constants rather than literals at each call site because a typo'd tag string does not
/// fail — it silently creates a second, undescribed group in the document, and the endpoint
/// disappears from the section a reader was looking in. The descriptions live next to the names
/// so a group cannot be added without saying what it does.
/// </para>
/// <para>
/// Order matters: <see cref="Ordered"/> is the order Swagger UI renders the sections in, and it
/// follows the order an operator actually works through them — log in, model the estate, create
/// the people, connect the systems, read what comes back.
/// </para>
/// </remarks>
internal static class ApiTags
{
    public const string Authentication = "Authentication";
    public const string Organizations = "Organizations";
    public const string Geography = "Geography";
    public const string Users = "Users";
    public const string AccessControl = "Access control";
    public const string Vms = "VMS";
    public const string Credentials = "Credentials";
    public const string Events = "Events";

    /// <summary>Tag name to group description, in the order the document should present them.</summary>
    public static IReadOnlyList<(string Name, string Description)> Ordered { get; } =
    [
        (Authentication,
            "Obtaining and rotating a caller's own credentials for this API. `POST /auth/login` "
            + "returns the bearer token every other endpoint expects; it is the only route here "
            + "that is anonymous, and it is rate limited per source address. Machine callers use "
            + "an API key header instead and never touch this group. Managing *other* people's "
            + "accounts is under Users, not here."),

        (Organizations,
            "The organizational dimension of scope: organizations and the unit tree beneath them. "
            + "Every VMS target belongs to an organization unit, and a caller's grants over that "
            + "tree decide which targets they can see at all. This dimension is independent of "
            + "Geography and the two are ANDed — a caller scoped to a department still sees only "
            + "the districts they were granted."),

        (Geography,
            "The geographic dimension of scope: the area hierarchy (state, district, zone, ward) "
            + "and the physical sites that sit inside it. A site is where a VMS target is "
            + "installed. Read routes need `geography.read`; changing the hierarchy needs "
            + "`geography.manage`, because a reparent moves everything below it into a different "
            + "scope."),

        (Users,
            "Accounts, their profile and status, their group membership, and the effective "
            + "permission set that membership resolves to. Permissions are never granted to a "
            + "user directly — they come from access groups, so `GET /users/{id}/permissions` is "
            + "a computed view rather than something you can write to."),

        (AccessControl,
            "Access groups and the scope grants attached to them: the join between a role's "
            + "permissions and the slice of the organization and geography those permissions "
            + "apply over. `/roles` and `/permissions` are the reference data the UI builds its "
            + "pickers from. This is the group that decides what everyone else can do, so every "
            + "write here needs `group.manage` and is audited."),

        (Vms,
            "Connector targets — one row per VMS, NVR or camera gateway the platform federates — "
            + "plus everything the workers report back about them: health history, the probed "
            + "capability matrix, the discovered camera inventory, and connection tests against "
            + "the live device. Cameras are discovered, never registered one at a time: a target "
            + "is polled as a whole, because per-camera polling does not survive 80,000 cameras."),

        (Credentials,
            "Provisioning the credential a connector authenticates to a device with. **Write "
            + "only.** There is no route on this API that returns secret material, and no "
            + "response type that has ever carried it — only `/status`, which reports whether a "
            + "credential exists. Reading a value is reachable only from a worker that is about "
            + "to connect. Scoped through the target in the path, so a caller can only write a "
            + "credential for a target they can already reach."),

        (Events,
            "Queries over the normalized event stream in its hot PostgreSQL window. `from` and "
            + "`to` are required and may span at most seven days — the table is partitioned by "
            + "time and takes 100-400M rows a day, so an unbounded query cannot be served. Paging "
            + "is by opaque cursor, never offset. Wide historical and free-text search belongs to "
            + "OpenSearch and is not served here."),
    ];
}
