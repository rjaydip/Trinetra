using Trinetra.Federation.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Security;

namespace Trinetra.Federation.Api.Auth;

/// <summary>
/// Creates the bootstrap administrator on first start.
/// </summary>
/// <remarks>
/// Someone has to exist before anyone can log in and create anyone else. This is that account,
/// and the only one the platform creates for itself.
/// </remarks>
public sealed partial class AdminSeeder
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly UserRepository _users;
    private readonly AccessGroupRepository _groups;
    private readonly AuthOptions _options;
    private readonly ILogger<AdminSeeder> _logger;

    public AdminSeeder(
        NpgsqlDataSource dataSource,
        UserRepository users,
        AccessGroupRepository groups,
        IOptions<AuthOptions> options,
        ILogger<AdminSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataSource = dataSource;
        _users = users;
        _groups = groups;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        var seed = _options.SeedAdmin;

        // Refusing to start beats starting with a guessable administrator on a system holding an
        // entire estate's camera credentials. A weak bootstrap account is the kind of thing that
        // survives into production precisely because nothing ever complained about it.
        if (!PasswordHasher.IsAcceptable(seed.Password, out var reason))
        {
            throw new InvalidOperationException(
                $"Auth:SeedAdmin:Password is not acceptable: {reason} "
                + "Supply it via the Auth__SeedAdmin__Password environment variable. "
                + "The API will not start without a usable bootstrap credential.");
        }

        if (await _users.ExistsAsync(seed.Username, ct))
        {
            // Never updated. Otherwise every deployment silently resets a password the operator
            // had already rotated, quietly restoring the bootstrap credential.
            LogAlreadyPresent(_logger, seed.Username);
            return;
        }

        var password = PasswordHasher.Hash(seed.Password);

        await using var work = await UnitOfWork.BeginAsync(_dataSource, ct);

        var userId = await _users.CreateAsync(
            seed.Username, seed.DisplayName, email: null, password,
            // Forces rotation on first login: the configured value is a bootstrap credential,
            // not a permanent one.
            mustChangePassword: true, isSystem: true, work, ct);

        // Same transaction as the user above. A bootstrap admin that exists without its group
        // holds no permissions at all, and it is never re-seeded -- so a failure between the two
        // statements leaves the platform with no way in short of direct database access.
        var groupId = await _groups.EnsurePlatformAdminGroupAsync(work, ct);

        await _users.AssignGroupAsync(userId, groupId, assignedBy: null, expiresAt: null, work, ct);

        await work.CommitAsync(ct);

        LogSeeded(_logger, seed.Username);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Seeded bootstrap administrator '{Username}'. It must change its password on "
                + "first login, and the value in configuration should then be removed.")]
    private static partial void LogSeeded(ILogger logger, string username);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Administrator '{Username}' already exists; leaving its password untouched")]
    private static partial void LogAlreadyPresent(ILogger logger, string username);
}
