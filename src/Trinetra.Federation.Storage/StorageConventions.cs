using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Dapper;

namespace Trinetra.Federation.Storage;

/// <summary>
/// Global Dapper conventions for the federation schema.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL returns <c>snake_case</c> columns; the C# projections are <c>PascalCase</c>.
/// Dapper does <b>not</b> bridge that by default — and its failure mode is silent: unmapped
/// columns leave properties at their default value rather than throwing. A worker would claim
/// its targets, receive a list of blank <c>ConnectorTarget</c> records, and poll nothing at all
/// while reporting success.
/// </para>
/// <para>
/// Applied via <see cref="ModuleInitializerAttribute"/> rather than from DI composition
/// deliberately: this is process-global mutable state in Dapper, and any code path that opens a
/// connection without having gone through our DI setup — a test, a migration, a console tool —
/// would otherwise silently get the broken behaviour.
/// </para>
/// </remarks>
internal static class StorageConventions
{
    [ModuleInitializer]
    [SuppressMessage(
        "Usage", "CA2255:The ModuleInitializer attribute should not be used in libraries",
        Justification =
            "CA2255 protects consumers of a published package from surprising side effects. " +
            "This assembly ships only inside the Trinetra solution, never to third parties, " +
            "and the initializer sets one Dapper convention that every consumer requires. " +
            "Requiring an explicit call instead would reintroduce the silent-failure mode " +
            "this type exists to prevent.")]
    internal static void Initialize()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        // Dapper has no built-in mapping for DateOnly and throws NotSupportedException when one
        // is passed as a parameter. Npgsql maps it to `date` natively, so the handler only has to
        // hand Dapper a value it will accept.
        //
        // Registered here rather than converted at each call site: the alternative is every
        // caller remembering to write .ToDateTime(...), and the one that forgets fails at
        // runtime, in whichever background job happens to run first.
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
    }

    /// <summary>Maps <see cref="DateOnly"/> to a PostgreSQL <c>date</c>.</summary>
    /// <remarks>
    /// Calendar dates only — partition boundaries and retention cutoffs. This is not an exception
    /// to the <c>DateTimeOffset</c> rule: a partition covers a named day, not an instant, and
    /// attaching an offset to it would invent a precision the value does not have.
    /// </remarks>
    private sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, DateOnly value)
        {
            ArgumentNullException.ThrowIfNull(parameter);

            parameter.DbType = System.Data.DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => DateOnly.Parse(
                Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
                System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
