using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Trinetra.Federation.Api.Endpoints;

namespace Trinetra.UnitTests;

/// <summary>PR11b / finding 15-L2 — detection ingest validates its event type and confidence.</summary>
public sealed class DetectionSubmissionValidationTests
{
    [Theory]
    [InlineData("ANPR_DETECTED", 0.0)]
    [InlineData("ANPR_DETECTED", 1.0)]
    [InlineData("VEHICLE_DETECTED", 0.62)]
    public void Accepts_KnownTypeAndConfidenceInRange(string type, double confidence) =>
        DetectionEndpoints.ValidateSubmission(type, confidence).ShouldBeNull();

    [Theory]
    [InlineData("anpr_detected")]     // wrong case
    [InlineData("FACE_DETECTED")]     // not a value the worker produces
    [InlineData("")]
    [InlineData(null)]                // omitted from the body
    public void Rejects_UnknownOrMissingEventType(string? type)
    {
        var problem = DetectionEndpoints.ValidateSubmission(type, 0.9);

        problem.ShouldNotBeNull();
        problem!.ProblemDetails.Title.ShouldBe("Unknown event type");
        problem.StatusCode.ShouldBe(400);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Rejects_ConfidenceOutOfRange(double confidence)
    {
        var problem = DetectionEndpoints.ValidateSubmission("ANPR_DETECTED", confidence);

        problem.ShouldNotBeNull();
        problem!.ProblemDetails.Title.ShouldBe("Confidence out of range");
        problem.StatusCode.ShouldBe(400);
    }
}
