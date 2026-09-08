using Dapper;

namespace Trinetra.Federation.Storage.Repositories;

/// <summary>
/// The camera scope predicate, shared by every repository that reads or writes a camera.
/// </summary>
/// <remarks>
/// Both dimensions are ANDed and each is bypassed only by its own unscoped flag
/// (<c>CLAUDE.md</c> invariant 12): the organization dimension against
/// <c>&lt;alias&gt;.organization_unit_id</c>, the geographic dimension against
/// <c>&lt;alias&gt;.geographic_area_id</c>. A camera's <c>geographic_area_id</c> is <c>NOT NULL</c>,
/// so the geographic check never lapses.
/// </remarks>
internal static class CameraScope
{
    /// <summary>
    /// The dual-dimension predicate for a camera aliased <c>c</c>, parameterised on
    /// <c>@Perm</c> / <c>@UserId</c> / <c>@ApiKeyId</c> / <c>@UnscopedOrg</c> / <c>@UnscopedGeo</c>.
    /// </summary>
    public const string PredicateForC = """
        (@UnscopedOrg OR c.organization_unit_id IN (
            SELECT organization_unit_id FROM federation.authorized_org_units(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Perm)))
        AND (@UnscopedGeo OR c.geographic_area_id IN (
            SELECT geographic_area_id FROM federation.authorized_geographic_areas(
                p_user_id => @UserId, p_api_key_id => @ApiKeyId, p_permission => @Perm)))
        """;

    /// <summary>The scope parameters for <paramref name="permission"/>, plus an <c>id</c> parameter.</summary>
    public static DynamicParameters Args(Guid id, CallerContext caller, string permission)
    {
        var p = new DynamicParameters();
        p.Add("id", id);
        p.Add("UserId", caller.UserId);
        p.Add("ApiKeyId", caller.ApiKeyId);
        p.Add("Perm", permission);
        p.Add("UnscopedOrg", caller.IsUnscopedFor(permission));
        p.Add("UnscopedGeo", caller.IsUnscopedForGeography(permission));
        return p;
    }
}
