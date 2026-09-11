using System.Reflection;
using Shouldly;
using Trinetra.Federation.Api.Endpoints;

namespace Trinetra.UnitTests;

/// <summary>Finding 10-L6 — a malformed camera-list cursor is a named 400, not a silent restart.</summary>
public sealed class CameraCursorTests
{
    // TryDecodeCursor is private, not internal (CameraEndpoints has no other reason to expose
    // it) — reflection keeps the test from forcing an accessibility change onto production code.
    private static readonly MethodInfo Method = typeof(CameraEndpoints)
        .GetMethod("TryDecodeCursor", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(CameraEndpoints), "TryDecodeCursor");

    private static (bool Ok, string? Code) Invoke(string? cursor)
    {
        var args = new object?[] { cursor, null };
        var ok = (bool)Method.Invoke(null, args)!;
        return (ok, (string?)args[1]);
    }

    [Fact]
    public void NullCursor_DecodesToNoCursor()
    {
        var (ok, code) = Invoke(null);

        ok.ShouldBeTrue();
        code.ShouldBeNull();
    }

    [Fact]
    public void ValidCursor_RoundTrips()
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("CAM-001"));

        var (ok, code) = Invoke(encoded);

        ok.ShouldBeTrue();
        code.ShouldBe("CAM-001");
    }

    [Fact]
    public void MalformedCursor_FailsRatherThanRestartingSilently()
    {
        var (ok, code) = Invoke("not-valid-base64!!");

        ok.ShouldBeFalse();
        code.ShouldBeNull();
    }
}
