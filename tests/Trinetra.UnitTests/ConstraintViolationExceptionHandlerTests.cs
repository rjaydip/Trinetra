using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Shouldly;
using Trinetra.Federation.Api.Auth;

namespace Trinetra.UnitTests;

public sealed class ConstraintViolationExceptionHandlerTests
{
    private static PostgresException Pg(string sqlState) =>
        new("boom", "ERROR", "ERROR", sqlState);

    private static async Task<(bool Handled, int Status, string? Title)> RunAsync(Exception ex)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        var handled = await new ConstraintViolationExceptionHandler()
            .TryHandleAsync(ctx, ex, CancellationToken.None);

        string? title = null;
        if (handled)
        {
            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
            title = doc.RootElement.GetProperty("title").GetString();
        }

        return (handled, ctx.Response.StatusCode, title);
    }

    [Fact]
    public async Task StringDataRightTruncation_IsA400_NotA500()
    {
        // 22001: a value longer than its column. An endpoint that does not know a length limit
        // still gives the caller a 400 rather than a platform-fault 500 (finding 5-M9).
        var (handled, status, title) = await RunAsync(Pg(PostgresErrorCodes.StringDataRightTruncation));

        handled.ShouldBeTrue();
        status.ShouldBe(StatusCodes.Status400BadRequest);
        title.ShouldBe("Value too long");
    }

    [Theory]
    [InlineData("23505", StatusCodes.Status409Conflict)]
    [InlineData("23503", StatusCodes.Status400BadRequest)]
    [InlineData("23514", StatusCodes.Status400BadRequest)]
    public async Task KnownConstraintCodes_MapToTheirStatus(string sqlState, int expected)
    {
        var (handled, status, _) = await RunAsync(Pg(sqlState));

        handled.ShouldBeTrue();
        status.ShouldBe(expected);
    }

    [Fact]
    public async Task UnknownPostgresCode_IsLeftAsAFault()
    {
        var (handled, _, _) = await RunAsync(Pg("XX000"));

        handled.ShouldBeFalse();
    }

    [Fact]
    public async Task NonPostgresException_IsNotHandled()
    {
        var (handled, _, _) = await RunAsync(new InvalidOperationException("nope"));

        handled.ShouldBeFalse();
    }
}
