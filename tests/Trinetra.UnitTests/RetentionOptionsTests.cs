using Shouldly;
using Trinetra.Federation.Api.Auth;

namespace Trinetra.UnitTests;

public sealed class RetentionOptionsTests
{
    private static RetentionOptions Valid() => new(); // all defaults pass Validate()

    [Fact]
    public void Validate_RejectsSubMonthAuthAudit()
    {
        var o = Valid();
        o.AuthAuditMonths = 0;

        Should.Throw<InvalidOperationException>(o.Validate).Message.ShouldContain("AuthAuditMonths");
    }

    [Fact]
    public void Validate_Passes_WithDefaultAuthAudit() => Valid().Validate(); // 24, no throw

    [Fact]
    public void CutoffsFrom_DerivesAuthAuditCutoff()
    {
        var o = Valid();
        o.AuthAuditMonths = 24;
        var now = new DateTimeOffset(2026, 09, 07, 0, 0, 0, TimeSpan.Zero);

        o.CutoffsFrom(now).AuthAudit.ShouldBe(new DateOnly(2024, 09, 07));
    }
}
