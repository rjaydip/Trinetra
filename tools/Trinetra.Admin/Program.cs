using Trinetra.Admin;
using Trinetra.Admin.Commands;

// Trinetra federation admin CLI.
//
// Provisions the configuration the platform reads — organizations, geography, users, access
// groups, credentials and connector targets — and verifies a target against real hardware.
//
// Speaks in human codes rather than UUIDs throughout, and goes through the same repositories the
// API uses so the two cannot disagree about what valid configuration looks like.

var options = Args.Parse(args);
var verbs = options.Positional;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Cancel the operation rather than killing the process, so an event watch or a stream probe
    // unwinds and releases its device connection cleanly.
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    if (verbs.Count == 0 || verbs[0] is "help" or "--help" or "-h")
    {
        PrintUsage();
        return 0;
    }

    var configuration = AdminServices.BuildConfiguration();

    // Key generation must work before anything is configured — it is how you get the key.
    if (verbs is ["key", "generate"])
    {
        return ProvisioningCommands.GenerateKey();
    }

    await using var db = AdminServices.CreateDataSource(configuration);

    return verbs switch
    {
        ["org", "add"]      => await ProvisioningCommands.AddOrganizationAsync(options, AdminServices.Organizations(db), db, cancellation.Token),
        ["org", "list"]     => await ProvisioningCommands.ListOrganizationsAsync(AdminServices.Organizations(db), cancellation.Token),

        ["unit", "add"]     => await ProvisioningCommands.AddUnitAsync(options, AdminServices.Organizations(db), db, cancellation.Token),
        ["unit", "list"]    => await ProvisioningCommands.ListUnitsAsync(AdminServices.Organizations(db), db, cancellation.Token),

        ["area", "add"]     => await ProvisioningCommands.AddAreaAsync(options, AdminServices.Geography(db), db, cancellation.Token),
        ["area", "list"]    => await ProvisioningCommands.ListAreasAsync(AdminServices.Geography(db), cancellation.Token),

        ["user", "add"]     => await ProvisioningCommands.AddUserAsync(options, AdminServices.Users(db), db, cancellation.Token),
        ["user", "list"]    => await ProvisioningCommands.ListUsersAsync(AdminServices.Users(db), cancellation.Token),
        ["user", "grant"]   => await ProvisioningCommands.GrantGroupAsync(options, AdminServices.Users(db), db, cancellation.Token),

        ["group", "add"]    => await ProvisioningCommands.AddGroupAsync(options, AdminServices.Groups(db), db, cancellation.Token),
        ["group", "list"]   => await ProvisioningCommands.ListGroupsAsync(AdminServices.Groups(db), cancellation.Token),
        ["role", "list"]    => await ProvisioningCommands.ListRolesAsync(AdminServices.Groups(db), cancellation.Token),

        ["secret", "set"]   => await SetSecretAsync(),

        ["target", "add"]   => await ProvisioningCommands.AddTargetAsync(options, AdminServices.Targets(db), db, cancellation.Token),
        ["target", "list"]  => await ProvisioningCommands.ListTargetsAsync(db, cancellation.Token),
        ["target", "test"]  => await RunTestAsync(),

        _ => Unknown(),
    };

    async Task<int> SetSecretAsync()
    {
        AdminServices.CreateEncryption(configuration);
        return await ProvisioningCommands.SetSecretAsync(options, AdminServices.Secrets(db), db, cancellation.Token);
    }

    async Task<int> RunTestAsync()
    {
        AdminServices.CreateEncryption(configuration);
        using var loggerFactory = AdminServices.CreateLoggerFactory(options.Has("verbose"));
        var factory = AdminServices.CreateAdapterFactory(db, loggerFactory);

        return await TestTargetCommand.RunAsync(
            options, db, factory, loggerFactory, cancellation.Token);
    }

    int Unknown()
    {
        Console.Error.WriteLine($"Unknown command: {string.Join(' ', verbs)}");
        PrintUsage();
        return 2;
    }
}
catch (CommandFailedException ex)
{
    // Expected failures: an operator needs the message, not a stack trace.
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Npgsql.PostgresException ex)
{
    // Handled before the connection case below: PostgresException derives from
    // NpgsqlException, so reporting it as a networking problem would send an operator to check
    // firewalls when the real answer is a missing schema or a constraint they violated.
    Console.Error.WriteLine($"Error: the database rejected the request. {ex.MessageText}");
    Console.Error.WriteLine($"  SQLSTATE {ex.SqlState}");

    if (ex.SqlState == "42P01")
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("  A federation table is missing. Start the API once to apply");
        Console.Error.WriteLine("  the migrations, then retry.");
    }

    return 1;
}
catch (Npgsql.NpgsqlException ex)
{
    Console.Error.WriteLine($"Error: could not reach the database. {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Check ConnectionStrings__Federation. Note that 'localhost' may");
    Console.Error.WriteLine("  resolve to IPv6 (::1) while a container publishes on IPv4 only —");
    Console.Error.WriteLine("  use 127.0.0.1 explicitly if the port is published by Docker.");
    return 1;
}

static void PrintUsage() => Console.WriteLine("""
    trinetra-admin — Trinetra platform administration

    Everything is referenced by its human code, never by UUID.

    SETUP
      key generate
          Generate a credential-encryption key. Store as Secrets__Key on every host.
          Never commit it; losing it makes stored camera passwords unrecoverable.

    ORGANIZATION  (who owns a camera)
      org add   --code POLICE --name "Police Department" [--type DEPARTMENT]
      org list
      unit add  --org POLICE --code AHM-CP --name "Ahmedabad Commissionerate"
                [--type COMMISSIONERATE] [--parent PD]
      unit list

    GEOGRAPHY  (where a camera is) — levels are yours to define
      area add  --code AHM --name Ahmedabad --type DISTRICT [--parent GJ] [--description …]
      area list

    ACCESS
      user add   --username rahul --name "Rahul" [--email …]     (prompts for password)
      user list
      user grant --username rahul --group AHM-CAM-OPS [--expires 2026-09-01]
      group add  --code AHM-CAM-OPS --role CAMERA_OPERATOR [--unit AHM-CP] [--area AHM]
      group list
      role list

    VMS INTEGRATION
      secret set --ref vault://vms/1 --username admin [--description …]
          Prompts for the password; never taken as a flag, because a flag lands in
          shell history and in the process list.

      target add  --code AHM-NVR-01 --unit AHM-CP --endpoint http://10.0.0.5
                  --vendor Onvif|HikvisionIsapi|DahuaCgi --credential vault://vms/1
                  [--area VILX] [--name …] [--expect-cameras 32] [--no-verify-tls]
          CP Plus and other Dahua OEM units use --vendor DahuaCgi.

      target list
      target test --code AHM-NVR-01 [--watch 30] [--skip-stream] [--verbose]
          Connects to the real device and reports credentials, capabilities, clock
          skew, inventory and stream reachability. Writes nothing.

    CONFIGURATION (environment)
      ConnectionStrings__Federation   PostgreSQL connection string
      Secrets__KeyId                  Identifier of the encryption key in use
      Secrets__Key                    Base64 key from 'key generate'
    """);
