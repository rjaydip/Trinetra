using Dapper;
using Npgsql;
using Trinetra.Federation.Core.Model;
using Trinetra.Federation.Storage;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Secrets;
using Trinetra.Federation.Storage.Security;

namespace Trinetra.Admin.Commands;

/// <summary>
/// Provisioning from the terminal.
/// </summary>
/// <remarks>
/// Goes through the same repositories the API uses, so the two surfaces cannot disagree about
/// what a valid organization unit or connector target is. The CLI adds only one thing: it speaks
/// in codes rather than UUIDs, because that is what an operator has in front of them.
/// </remarks>
internal static class ProvisioningCommands
{
    /// <summary>The CLI acts locally with full rights — see <see cref="CallerContext.System"/>.</summary>
    private static CallerContext Caller => CallerContext.System($"admin-cli@{Environment.MachineName}");

    /// <summary>
    /// Records a CLI mutation in the same audit table the API writes to.
    /// </summary>
    /// <remarks>
    /// The CLI can do everything the API can, so it has to be equally auditable. An audit trail
    /// with a hole where terminal access sits answers "who changed this" only for the changes
    /// somebody chose to make the visible way.
    /// </remarks>

    // ---- Keys --------------------------------------------------------------

    public static int GenerateKey()
    {
        Out.Info(SecretEncryption.GenerateKeyBase64());
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "Store this as Secrets__Key on every worker and API host. Keep it OUT of");
        Console.Error.WriteLine(
            "appsettings.json and out of source control: anyone holding it can decrypt every");
        Console.Error.WriteLine(
            "camera password in the estate, and losing it makes them unrecoverable.");
        return 0;
    }

    // ---- Organizations -----------------------------------------------------

    public static async Task<int> AddOrganizationAsync(
        Args args, OrganizationRepository repo, NpgsqlDataSource db, CancellationToken ct)
    {
        var code = args.Require("code");

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var id = await repo.UpsertAsync(new Organization
        {
            Code = code,
            Name = args.Require("name"),
            OrganizationType = args.Get("type") ?? "DEPARTMENT",
            Description = args.Get("description"),
        }, Caller, work, ct);

        await work.AuditAsync(Caller, "create", "organization", id.ToString(), before: null, new { code }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);
        Out.Ok($"Organization {code}");
        return 0;
    }

    public static async Task<int> ListOrganizationsAsync(OrganizationRepository repo, CancellationToken ct)
    {
        var orgs = await repo.ListAsync(Caller, ct);

        if (orgs.Count == 0)
        {
            Out.Warn("No organizations configured. Add one with: org add --code … --name …");
            return 0;
        }

        Out.Info($"{"CODE",-16} {"TYPE",-16} {"STATUS",-10} NAME");
        foreach (var o in orgs)
        {
            Out.Info($"{o.Code,-16} {o.OrganizationType,-16} {o.Status,-10} {o.Name}");
        }

        return 0;
    }

    public static async Task<int> AddUnitAsync(
        Args args, OrganizationRepository repo, NpgsqlDataSource db, CancellationToken ct)
    {
        var code = args.Require("code");
        var organizationId = await Lookup.OrganizationAsync(db, args.Require("org"), ct);
        var parentId = await Lookup.OptionalUnitAsync(db, args.Get("parent"), ct);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var unitId = await repo.UpsertUnitAsync(new OrganizationUnit
        {
            OrganizationId = organizationId,
            ParentUnitId = parentId,
            Code = code,
            Name = args.Require("name"),
            UnitType = args.Get("type") ?? "UNIT",
        }, Caller, work, ct);

        await work.AuditAsync(Caller, "create", "organization_unit", unitId.ToString(),
            before: null, after: new { code, parent = args.Get("parent") }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        Out.Ok($"Organization unit {code}"
             + (parentId is null ? " (root)" : $" under {args.Get("parent")}"));
        return 0;
    }

    public static async Task<int> ListUnitsAsync(OrganizationRepository repo, NpgsqlDataSource db, CancellationToken ct)
    {
        var units = (await repo.ListUnitsAsync(null, Caller, ct)).ToList();

        if (units.Count == 0)
        {
            Out.Warn("No organization units configured.");
            return 0;
        }

        // Rendered as a tree: a flat list of units whose parents are UUIDs is unreadable, and
        // the shape is the thing an operator is usually checking.
        var byParent = units.ToLookup(u => u.ParentUnitId);
        Out.Info($"{"CODE",-18} {"TYPE",-18} NAME");
        Print(byParent, null, 0);
        return 0;

        static void Print(ILookup<Guid?, OrganizationUnit> lookup, Guid? parent, int depth)
        {
            foreach (var u in lookup[parent].OrderBy(u => u.Code, StringComparer.Ordinal))
            {
                Out.Info($"{new string(' ', depth * 2)}{u.Code,-18} {u.UnitType,-18} {u.Name}");
                Print(lookup, u.Id, depth + 1);
            }
        }
    }

    // ---- Geography ---------------------------------------------------------

    public static async Task<int> AddAreaAsync(
        Args args, GeographyRepository repo, NpgsqlDataSource db, CancellationToken ct)
    {
        var code = args.Require("code");
        var parentId = await Lookup.OptionalAreaAsync(db, args.Get("parent"), ct);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var areaId = await repo.UpsertAreaAsync(new GeographicArea
        {
            ParentAreaId = parentId,
            Code = code,
            Name = args.Require("name"),
            // Operator-defined. No enum, so a deployment using Zone/Sector/Block needs no change.
            AreaType = args.Require("type"),
        }, Caller, work, ct);

        await work.AuditAsync(Caller, "create", "geographic_area", areaId.ToString(),
            before: null, after: new { code, parent = args.Get("parent") }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        Out.Ok($"Geographic area {code}"
             + (parentId is null ? " (root)" : $" under {args.Get("parent")}"));
        return 0;
    }

    public static async Task<int> ListAreasAsync(GeographyRepository repo, CancellationToken ct)
    {
        var areas = (await repo.ListAreasAsync(null, false, Caller, ct)).ToList();

        if (areas.Count == 0)
        {
            Out.Warn("No geographic areas configured.");
            return 0;
        }

        var byParent = areas.ToLookup(a => a.ParentAreaId);
        Out.Info($"{"CODE",-18} {"TYPE",-14} NAME");
        Print(byParent, null, 0);
        return 0;

        static void Print(ILookup<Guid?, GeographicArea> lookup, Guid? parent, int depth)
        {
            foreach (var a in lookup[parent].OrderBy(a => a.Code, StringComparer.Ordinal))
            {
                Out.Info($"{new string(' ', depth * 2)}{a.Code,-18} {a.AreaType,-14} {a.Name}");
                Print(lookup, a.Id, depth + 1);
            }
        }
    }

    public static async Task<int> AddSiteAsync(
        Args args, GeographyRepository repo, NpgsqlDataSource db, CancellationToken ct)
    {
        var code = args.Require("code");
        var areaId = await Lookup.GeographicAreaAsync(db, args.Require("area"), ct);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var siteId = await repo.UpsertSiteAsync(new Site
        {
            Code = code,
            Name = args.Require("name"),
            GeographicAreaId = areaId,
            SiteType = args.Get("type"),
            Address = args.Get("address"),
            Latitude = ParseCoordinate(args.Get("lat"), "lat", -90, 90),
            Longitude = ParseCoordinate(args.Get("lon"), "lon", -180, 180),
        }, Caller, work, ct);

        await work.AuditAsync(Caller, "create", "site", siteId.ToString(),
            before: null, after: new { code, area = args.Require("area") }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        Out.Ok($"Site {code} in {args.Require("area")}");
        return 0;
    }

    public static async Task<int> ListSitesAsync(GeographyRepository repo, CancellationToken ct)
    {
        var sites = await repo.ListSitesAsync(null, Caller, ct);

        if (sites.Count == 0)
        {
            Out.Warn("No sites configured.");
            return 0;
        }

        Out.Info($"{"CODE",-20} {"TYPE",-14} {"LAT",10} {"LON",11}  NAME");
        foreach (var s in sites)
        {
            Out.Info($"{s.Code,-20} {s.SiteType ?? "-",-14} "
                   + $"{s.Latitude?.ToString("F5", System.Globalization.CultureInfo.InvariantCulture) ?? "-",10} "
                   + $"{s.Longitude?.ToString("F5", System.Globalization.CultureInfo.InvariantCulture) ?? "-",11}  {s.Name}");
        }

        return 0;
    }

    // ---- Secrets -----------------------------------------------------------

    public static async Task<int> SetSecretAsync(
        Args args, SecretWriter secrets, NpgsqlDataSource db, CancellationToken ct)
    {
        var reference = args.Require("ref");

        // Prompted, never a flag: a password on the command line lands in shell history and in
        // the process list, where any local user can read it.
        var password = Out.ReadSecret("Password: ");

        if (string.IsNullOrEmpty(password))
        {
            throw new CommandFailedException("No password was entered.");
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        await secrets.WriteAsync(
            reference, args.Get("username"), password, args.Get("token"),
            args.Get("description"), Caller, work, ct);

        await work.CommitAsync(ct);

        Out.Ok($"Secret {reference} stored");
        return 0;
    }

    // ---- Connector targets -------------------------------------------------

    public static async Task<int> AddTargetAsync(
        Args args, ConnectorTargetRepository repo, NpgsqlDataSource db, CancellationToken ct)
    {
        var vendorRaw = args.Require("vendor");

        if (!Enum.TryParse<VendorKind>(vendorRaw, ignoreCase: true, out var vendor))
        {
            throw new CommandFailedException(
                $"Unknown vendor '{vendorRaw}'. Valid: {string.Join(", ", Enum.GetNames<VendorKind>())}. "
                + "CP Plus and other Dahua OEM units use DahuaCgi.");
        }

        var code = args.Require("code");
        var unitId = await Lookup.OrganizationUnitAsync(db, args.Require("unit"), ct);
        var siteId = await Lookup.OptionalSiteAsync(db, args.Get("site"), ct);
        var verifyTls = !args.Has("no-verify-tls");

        if (!verifyTls)
        {
            Out.Warn("TLS verification disabled for this target. Recorded per target, and the "
                   + "accepted certificate is logged on every connection.");
        }

        if (siteId is null)
        {
            // Not fatal, but worth saying: without a site the target has no place in the
            // geographic hierarchy, so geographically-scoped users will not see it.
            Out.Warn("No --site given. This target cannot be reached by geographic scope.");
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var targetId = await repo.UpsertAsync(new ConnectorTarget
        {
            Id = Guid.Empty,
            Code = code,
            OrganizationUnitId = unitId,
            SiteId = siteId,
            DisplayName = args.Get("name") ?? code,
            Vendor = vendor,
            Endpoint = args.Require("endpoint"),
            CredentialReference = args.Require("credential"),
            VerifyTls = verifyTls,
            ExpectedCameraCount = int.TryParse(args.Get("expect-cameras"), out var expected)
                ? expected : null,
        }, Caller, work, ct);

        await work.AuditAsync(Caller, "create", "connector_target", targetId.ToString(), before: null, new
        {
            code,
            unit = args.Require("unit"),
            site = args.Get("site"),
            vendor = vendor.ToString(),
            endpoint = args.Require("endpoint"),
            credentialReference = args.Require("credential"),
            verifyTls,
        }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        Out.Ok($"Target {code} → {vendor} at {args.Require("endpoint")}");
        Out.Info($"  Verify it with: trinetra-admin target test --code {code}");
        return 0;
    }

    public static async Task<int> ListTargetsAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);

        var rows = (await connection.QueryAsync(new CommandDefinition("""
            SELECT t.code, t.vendor::text AS vendor, t.state::text AS state,
                   ou.code AS unit_code, s.code AS site_code, t.endpoint, t.leased_by,
                   (SELECT count(*) FROM federation.federated_camera c WHERE c.target_id = t.id) AS cameras,
                   -- Bounded to the recent window: connector_health is partitioned by day, so
                   -- an unbounded correlated subquery probes every partition once per target.
                   -- It is also more honest -- a target whose worker died months ago showed its
                   -- last-known 'Healthy' forever, and now shows nothing.
                   (SELECT h.status::text FROM federation.connector_health h
                     WHERE h.target_id = t.id
                       AND h.checked_at >= now() - interval '1 hour'
                     ORDER BY h.checked_at DESC LIMIT 1) AS health
            FROM federation.connector_target t
            JOIN federation.organization_units ou ON ou.id = t.organization_unit_id
            LEFT JOIN federation.sites s ON s.id = t.site_id
            ORDER BY t.code;
            """, cancellationToken: ct))).ToList();

        if (rows.Count == 0)
        {
            Out.Warn("No connector targets configured.");
            return 0;
        }

        Out.Info($"{"CODE",-16} {"VENDOR",-16} {"UNIT",-12} {"SITE",-12} "
               + $"{"STATE",-11} {"HEALTH",-11} {"CAMS",5}  ENDPOINT");

        foreach (var r in rows)
        {
            var owner = r.leased_by is null ? "" : $"  (worker {r.leased_by})";
            Out.Info($"{r.code,-16} {r.vendor,-16} {r.unit_code,-12} {r.site_code ?? "-",-12} "
                   + $"{r.state,-11} {(string?)r.health ?? "-",-11} {r.cameras,5}  {r.endpoint}{owner}");
        }

        return 0;
    }

    // ---- Users -------------------------------------------------------------

    public static async Task<int> AddUserAsync(Args args, UserRepository users, NpgsqlDataSource db, CancellationToken ct)
    {
        var username = args.Require("username");

        if (await users.ExistsAsync(username, ct))
        {
            throw new CommandFailedException($"A user named '{username}' already exists.");
        }

        var password = Out.ReadSecret("Password: ");

        if (!PasswordHasher.IsAcceptable(password, out var reason))
        {
            throw new CommandFailedException(reason!);
        }

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        await users.CreateAsync(
            username, args.Get("name") ?? username, args.Get("email"),
            PasswordHasher.Hash(password),
            // Forced, so the person who created the account never knows the working credential.
            mustChangePassword: true, isSystem: false, work, ct);

        await work.AuditAsync(Caller, "create", "user", username, before: null, new { username, name = args.Get("name") }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        Out.Ok($"User {username} created. They must change this password at first login.");
        Out.Info($"  Grant access with: trinetra-admin user grant --username {username} --group <code>");
        return 0;
    }

    public static async Task<int> ListUsersAsync(UserRepository users, CancellationToken ct)
    {
        var all = await users.ListAsync(ct);

        if (all.Count == 0)
        {
            Out.Warn("No users configured.");
            return 0;
        }

        Out.Info($"{"USERNAME",-20} {"STATUS",-10} {"PW CHANGE",-11} {"LAST LOGIN",-22} NAME");
        foreach (var u in all)
        {
            var lastLogin = u.LastLoginAt?.ToString("u") ?? "never";
            Out.Info($"{u.Username,-20} {u.Status,-10} {(u.MustChangePassword ? "required" : "-"),-11} "
                   + $"{lastLogin,-22} {u.DisplayName}");
        }

        return 0;
    }

    public static async Task<int> GrantGroupAsync(
        Args args, UserRepository users, NpgsqlDataSource db, CancellationToken ct)
    {
        var username = args.Require("username");
        var groupCode = args.Require("group");

        await using var connection = await db.OpenConnectionAsync(ct);

        var userId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM federation.platform_users WHERE lower(username) = lower(@username);",
            new { username }, cancellationToken: ct))
            ?? throw new CommandFailedException($"No user '{username}'.");

        var groupId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM federation.access_groups WHERE code = @groupCode;", new { groupCode }, cancellationToken: ct))
            ?? throw new CommandFailedException(
                $"No access group '{groupCode}'. List them with: trinetra-admin group list");

        DateTimeOffset? expiresAt = DateTimeOffset.TryParse(
            args.Get("expires"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        await users.AssignGroupAsync(userId, groupId, assignedBy: null, expiresAt, work, ct);

        await work.AuditAsync(Caller, "update", "user_group", userId.ToString(),
            before: null, after: new { username, group = groupCode, expiresAt }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        Out.Ok($"{username} added to {groupCode}"
             + (expiresAt is null ? "" : $", expiring {expiresAt:u}"));
        return 0;
    }

    // ---- Access groups -----------------------------------------------------

    public static async Task<int> AddGroupAsync(
        Args args, AccessGroupRepository repo, NpgsqlDataSource db, CancellationToken ct)
    {
        var code = args.Require("code");
        var roleId = await Lookup.RoleAsync(db, args.Require("role"), ct);

        await using var work = await UnitOfWork.BeginAsync(db, ct);

        var groupId = await repo.UpsertAsync(new AccessGroup
        {
            Code = code,
            Name = args.Get("name") ?? code,
            Description = args.Get("description"),
            RoleId = roleId,
            // Active from the CLI: someone at a terminal is deliberately provisioning, not
            // drafting for later review.
            Status = "ACTIVE",
        }, actorId: null, work, ct);

        // Scope is optional at creation but almost always wanted: a group with no organization
        // scope reaches the entire estate.
        if (args.Get("unit") is { } unitCode)
        {
            var unitId = await Lookup.OrganizationUnitAsync(db, unitCode, ct);
            await repo.AddScopeAsync(groupId, "ORGANIZATION", unitId, null, null, null,
                $"Organization scope: {unitCode}", work, ct);
            Out.Info($"  Scoped to organization unit {unitCode}");
        }

        if (args.Get("area") is { } areaCode)
        {
            var areaId = await Lookup.GeographicAreaAsync(db, areaCode, ct);
            await repo.AddScopeAsync(groupId, "GEOGRAPHY", null, areaId, null, null,
                $"Geographic scope: {areaCode}", work, ct);
            Out.Info($"  Scoped to geographic area {areaCode}");
        }

        if (args.Get("unit") is null && args.Get("area") is null)
        {
            Out.Warn("No --unit or --area given: this group is UNSCOPED and reaches the whole "
                   + "estate. That is rarely what you want for anything but administrators.");
        }

        await work.AuditAsync(Caller, "create", "access_group", groupId.ToString(), before: null, new
        {
            code, role = args.Require("role"), unit = args.Get("unit"), area = args.Get("area"),
        }, organizationUnitId: null, ct);
        await work.CommitAsync(ct);

        Out.Ok($"Access group {code} with role {args.Require("role")}");
        return 0;
    }

    public static async Task<int> ListGroupsAsync(AccessGroupRepository repo, CancellationToken ct)
    {
        var groups = await repo.ListAsync(ct);

        if (groups.Count == 0)
        {
            Out.Warn("No access groups configured.");
            return 0;
        }

        Out.Info($"{"CODE",-20} {"ROLE",-22} {"STATUS",-10} {"MEMBERS",8}  SCOPES");
        foreach (var g in groups)
        {
            var scopes = g.Scopes.Count == 0
                ? "unscoped (entire estate)"
                : string.Join(", ", g.Scopes.Select(s => s.ScopeType.ToLowerInvariant()));

            Out.Info($"{g.Code,-20} {g.RoleCode,-22} {g.Status,-10} {g.MemberCount,8}  {scopes}");
        }

        return 0;
    }

    public static async Task<int> ListRolesAsync(AccessGroupRepository repo, CancellationToken ct)
    {
        var roles = await repo.ListRolesAsync(ct);

        Out.Info($"{"CODE",-22} {"PERMS",6}  NAME");
        foreach (var r in roles)
        {
            var permissions = await repo.GetRolePermissionsAsync(r.Id, ct);
            Out.Info($"{r.Code,-22} {permissions.Count,6}  {r.Name}");
        }

        return 0;
    }

    private static double? ParseCoordinate(string? raw, string flag, double min, double max)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new CommandFailedException($"--{flag} '{raw}' is not a number.");
        }

        if (value < min || value > max)
        {
            throw new CommandFailedException($"--{flag} must be between {min} and {max}.");
        }

        return value;
    }
}
