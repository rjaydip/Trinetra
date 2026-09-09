using Shouldly;
using Trinetra.Federation.Api.Contracts;

namespace Trinetra.UnitTests;

public sealed class PaginationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(7, true)]
    public void Enabled_TracksThePageParameter(int? page, bool expected) =>
        new PageQuery(page, null).Enabled.ShouldBe(expected);

    [Fact]
    public void PageSize_DefaultsTo50_AndIsClampedToTheMax()
    {
        new PageQuery(1, null).ResolvedPageSize(1000).ShouldBe(50);
        new PageQuery(1, 5000).ResolvedPageSize(1000).ShouldBe(1000);
        new PageQuery(1, 0).ResolvedPageSize(1000).ShouldBe(1);
    }

    [Fact]
    public void Offset_IsZeroWhenNotPaginated_AndPageRelativeOtherwise()
    {
        new PageQuery(null, 20).Offset(1000).ShouldBe(0);
        new PageQuery(1, 20).Offset(1000).ShouldBe(0);
        new PageQuery(3, 20).Offset(1000).ShouldBe(40);
    }

    [Fact]
    public void Limit_IsTheHardCapWhenNotPaginated_ThePageSizeOtherwise()
    {
        new PageQuery(null, null).Limit(5000, 1000).ShouldBe(5000);
        new PageQuery(2, 25).Limit(5000, 1000).ShouldBe(25);
    }

    [Fact]
    public void PageResult_ComputesTotalPages()
    {
        new PageResult<int>([], 1, 50, 0).TotalPages.ShouldBe(0);
        new PageResult<int>([], 1, 50, 50).TotalPages.ShouldBe(1);
        new PageResult<int>([], 1, 50, 51).TotalPages.ShouldBe(2);
        new PageResult<int>([], 1, 50, 347).TotalPages.ShouldBe(7);
    }
}
