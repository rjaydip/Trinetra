using System.Text.Json;
using Shouldly;
using Trinetra.Federation.Api.Contracts;

namespace Trinetra.UnitTests;

/// <summary>
/// Locks the wire shape of the inventory-freshness fields added to <see cref="VmsResponse"/> and
/// the new <see cref="PollInventoryResponse"/> — the frontend needs
/// <c>lastInventoryPollAt</c>/<c>lastInventoryCameraCount</c> to show "inventory last refreshed
/// Xh ago" without a second call to <c>/health</c>, and a camelCase drift here would silently
/// break that without failing anything server-side.
/// </summary>
public sealed class VmsResponseSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void VmsResponse_SerializesInventoryFreshnessFields_CamelCase()
    {
        var response = new VmsResponse(
            Id: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Code: "TGT-1",
            OrganizationUnitId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            GeographicAreaId: null,
            DisplayName: "Test target",
            Vendor: "Onvif",
            RuntimeClass: "Managed",
            Endpoint: "http://10.0.0.1",
            CredentialReference: "vault://x",
            VerifyTls: true,
            State: "Active",
            ExpectedCameraCount: 128,
            LastInventoryPollAt: DateTimeOffset.Parse("2026-09-13T00:00:00Z"),
            LastInventoryCameraCount: 120);

        var json = JsonSerializer.Serialize(response, Options);

        json.ShouldContain("\"lastInventoryPollAt\":\"2026-09-13T00:00:00");
        json.ShouldContain("\"lastInventoryCameraCount\":120");
    }

    [Fact]
    public void VmsResponse_WithNoInventoryPollYet_SerializesFreshnessFieldsAsNull()
    {
        var response = new VmsResponse(
            Id: Guid.NewGuid(), Code: "TGT-2",
            OrganizationUnitId: Guid.NewGuid(), GeographicAreaId: null,
            DisplayName: "New target", Vendor: "Onvif", RuntimeClass: "Managed",
            Endpoint: "http://10.0.0.2", CredentialReference: "vault://y",
            VerifyTls: true, State: "Active", ExpectedCameraCount: null,
            LastInventoryPollAt: null, LastInventoryCameraCount: null);

        var json = JsonSerializer.Serialize(response, Options);

        json.ShouldContain("\"lastInventoryPollAt\":null");
        json.ShouldContain("\"lastInventoryCameraCount\":null");
    }

    [Fact]
    public void PollInventoryResponse_Serializes_TargetIdRequestedAtAndCircuitOpen()
    {
        var response = new PollInventoryResponse(
            TargetId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            RequestedAt: DateTimeOffset.Parse("2026-09-13T12:00:00Z"),
            CircuitOpen: true);

        var json = JsonSerializer.Serialize(response, Options);

        json.ShouldContain("\"targetId\":\"33333333-3333-3333-3333-333333333333\"");
        json.ShouldContain("\"requestedAt\":\"2026-09-13T12:00:00");
        json.ShouldContain("\"circuitOpen\":true");
    }
}
