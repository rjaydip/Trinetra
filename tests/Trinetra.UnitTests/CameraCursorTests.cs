using System.Reflection;
using Shouldly;
using Trinetra.Federation.Api.Endpoints;

namespace Trinetra.UnitTests;

/// <summary>
/// Finding 10-L6 — a malformed camera-list cursor is a named 400, not a silent restart.
/// Finding 10-M6 — the cursor now carries (id, code) together, not code alone, so paging is
/// stable when a retired camera's code has been reused by a live one.
/// </summary>
public sealed class CameraCursorTests
{
    // TryDecodeCursor is private, not internal (CameraEndpoints has no other reason to expose
    // it) — reflection keeps the test from forcing an accessibility change onto production code.
    private static readonly MethodInfo DecodeMethod = typeof(CameraEndpoints)
        .GetMethod("TryDecodeCursor", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(CameraEndpoints), "TryDecodeCursor");

    private static readonly MethodInfo EncodeMethod = typeof(CameraEndpoints)
        .GetMethod("EncodeCursor", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(CameraEndpoints), "EncodeCursor");

    private static (bool Ok, string? Code, Guid? Id) Decode(string? cursor)
    {
        var args = new object?[] { cursor, null, null };
        var ok = (bool)DecodeMethod.Invoke(null, args)!;
        return (ok, (string?)args[1], (Guid?)args[2]);
    }

    private static string Encode(string code, Guid id) =>
        (string)EncodeMethod.Invoke(null, [code, id])!;

    [Fact]
    public void NullCursor_DecodesToNoCursor()
    {
        var (ok, code, id) = Decode(null);

        ok.ShouldBeTrue();
        code.ShouldBeNull();
        id.ShouldBeNull();
    }

    [Fact]
    public void ValidCursor_RoundTrips()
    {
        var cameraId = Guid.NewGuid();
        var encoded = Encode("CAM-001", cameraId);

        var (ok, code, id) = Decode(encoded);

        ok.ShouldBeTrue();
        code.ShouldBe("CAM-001");
        id.ShouldBe(cameraId);
    }

    [Fact]
    public void CodeContainingPipe_StillRoundTrips()
    {
        // camera_code has no charset restriction — the id is encoded first specifically so a
        // '|' inside the code itself can't be mistaken for the id/code separator.
        var cameraId = Guid.NewGuid();
        var encoded = Encode("CAM|WEIRD|CODE", cameraId);

        var (ok, code, id) = Decode(encoded);

        ok.ShouldBeTrue();
        code.ShouldBe("CAM|WEIRD|CODE");
        id.ShouldBe(cameraId);
    }

    [Fact]
    public void MalformedCursor_FailsRatherThanRestartingSilently()
    {
        var (ok, code, id) = Decode("not-valid-base64!!");

        ok.ShouldBeFalse();
        code.ShouldBeNull();
        id.ShouldBeNull();
    }

    [Fact]
    public void CursorMissingId_FailsRatherThanRestartingSilently()
    {
        // The pre-10-M6 cursor shape (code only, no id) — must not be quietly accepted as valid.
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("CAM-001"));

        var (ok, code, id) = Decode(encoded);

        ok.ShouldBeFalse();
        code.ShouldBeNull();
        id.ShouldBeNull();
    }
}
