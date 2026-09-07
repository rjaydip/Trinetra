using Shouldly;
using Trinetra.Federation.Api.Auth;
using Trinetra.Federation.Storage.Repositories;
using Trinetra.Federation.Storage.Security;

namespace Trinetra.UnitTests;

public sealed class PasswordChangeGuardTests
{
    private static PasswordHistoryEntry Entry(string password, DateTimeOffset setAt) =>
        new(PasswordHasher.Hash(password), setAt);

    [Fact]
    public void Check_RejectsAReusedPassword()
    {
        var history = new[] { Entry("old-one", DateTimeOffset.UtcNow.AddDays(-10)) };

        PasswordChangeGuard.Check("old-one", history, enforceMinimumAge: false).ShouldNotBeNull();
    }

    [Fact]
    public void Check_AllowsAFreshPassword()
    {
        var history = new[] { Entry("old-one", DateTimeOffset.UtcNow.AddDays(-10)) };

        PasswordChangeGuard.Check("brand-new", history, enforceMinimumAge: true).ShouldBeNull();
    }

    [Fact]
    public void Check_EnforceMinimumAge_RejectsWhenCurrentIsYoung()
    {
        var history = new[] { Entry("old-one", DateTimeOffset.UtcNow.AddHours(-1)) };

        PasswordChangeGuard.Check("brand-new", history, enforceMinimumAge: true).ShouldNotBeNull();
    }

    [Fact]
    public void Check_MinimumAgeOff_AllowsAYoungCurrent_ButStillEnforcesHistory()
    {
        // This is exactly the admin-reset contract: age not checked, reuse still is.
        var history = new[] { Entry("old-one", DateTimeOffset.UtcNow.AddMinutes(-1)) };

        PasswordChangeGuard.Check("brand-new", history, enforceMinimumAge: false).ShouldBeNull();
        PasswordChangeGuard.Check("old-one", history, enforceMinimumAge: false).ShouldNotBeNull();
    }

    [Fact]
    public void Check_NoHistory_NeverAgeLimited()
    {
        PasswordChangeGuard.Check("anything", [], enforceMinimumAge: true).ShouldBeNull();
    }
}
