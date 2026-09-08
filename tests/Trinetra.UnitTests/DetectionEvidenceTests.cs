using System.Text;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Trinetra.Federation.Api.Endpoints;

namespace Trinetra.UnitTests;

public sealed class DetectionEvidenceTests
{
    [Fact]
    public void RequestSizeLimitAttribute_IsHonouredMetadata()
    {
        // The detection-ingest route relies on RequestSizeLimitAttribute being read as
        // IRequestSizeLimitMetadata by ApplyRequestSizeLimitAsync. Fail loudly if a framework
        // change ever severs that link.
        IRequestSizeLimitMetadata metadata = new RequestSizeLimitAttribute(4321);
        metadata.MaxRequestBodySize.ShouldBe(4321);
    }

    private const int Max = 1024;   // 1 KB limit for these tests

    [Fact]
    public void TryDecode_ValidSmallSnapshot_Decodes()
    {
        var payload = Encoding.UTF8.GetBytes("a pretend JPEG");
        var base64 = Convert.ToBase64String(payload);

        DetectionEvidence.TryDecode(base64, Max, out var bytes, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        bytes.ShouldBe(payload);
    }

    [Fact]
    public void TryDecode_Empty_IsRejected()
    {
        DetectionEvidence.TryDecode("", Max, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void TryDecode_Malformed_IsRejectedNotThrown()
    {
        // Would be a FormatException from Convert.FromBase64String.
        DetectionEvidence.TryDecode("not base64 !!!", Max, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void TryDecode_OversizedString_IsRejectedWithoutDecoding()
    {
        // 10 MB of base64 against a 1 KB limit — rejected on the string length, before any
        // decode buffer is touched.
        var huge = new string('A', 10 * 1024 * 1024);

        DetectionEvidence.TryDecode(huge, Max, out var bytes, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("larger than");   // rejected on string length, not after a decode
        bytes.ShouldBeEmpty();
    }

    [Fact]
    public void TryDecode_DecodesToJustOverTheLimit_IsRejected()
    {
        var justOver = new byte[Max + 64];
        var base64 = Convert.ToBase64String(justOver);

        DetectionEvidence.TryDecode(base64, Max, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void TryDecode_ExactlyAtTheLimit_Decodes()
    {
        var atLimit = new byte[Max];
        Random.Shared.NextBytes(atLimit);
        var base64 = Convert.ToBase64String(atLimit);

        DetectionEvidence.TryDecode(base64, Max, out var bytes, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        bytes.Length.ShouldBe(Max);
    }
}
