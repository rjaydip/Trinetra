using Shouldly;
using Trinetra.Federation.Storage.Repositories;

namespace Trinetra.IntegrationTests;

/// <summary>
/// PR11a — <see cref="FederationQueryRepository.FederatedCameraExistsAsync"/> backs the fix for
/// finding 9-L2: an unknown <c>nativeCameraId</c> on <c>GET /vms/{id}/cameras/{nativeCameraId}/status-history</c>
/// must be a 404, not an empty 200.
/// </summary>
public sealed class FederationQueryTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private static readonly Guid TargetA = Guid.Parse("c9990000-0000-4000-8000-0000000000a1");
    private static readonly Guid TargetB = Guid.Parse("c9990000-0000-4000-8000-0000000000b2");

    public FederationQueryTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetTargetsAsync();
        await _fixture.ExecuteAsync($"""
            INSERT INTO federation.connector_target
                (id, code, organization_unit_id, display_name, vendor, endpoint, credential_reference)
            VALUES
                ('{TargetA}', 'FQ-A', '{PostgresFixture.PoliceUnit}', 'FQ target A',
                 'Onvif'::federation.vendor_kind, 'http://10.0.0.1', 'vault://a'),
                ('{TargetB}', 'FQ-B', '{PostgresFixture.PoliceUnit}', 'FQ target B',
                 'Onvif'::federation.vendor_kind, 'http://10.0.0.2', 'vault://b');

            INSERT INTO federation.federated_camera
                (target_id, native_camera_id, organization_unit_id)
            VALUES ('{TargetA}', 'CH01', '{PostgresFixture.PoliceUnit}');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private FederationQueryRepository Repo => new(_fixture.DataSource);

    [Fact]
    public async Task Exists_TrueForACameraOnThatTarget() =>
        (await Repo.FederatedCameraExistsAsync(TargetA, "CH01", CancellationToken.None)).ShouldBeTrue();

    [Fact]
    public async Task Exists_FalseForAnUnknownNativeId() =>
        (await Repo.FederatedCameraExistsAsync(TargetA, "CH99", CancellationToken.None)).ShouldBeFalse();

    [Fact]
    public async Task Exists_FalseWhenTheCameraIsOnADifferentTarget() =>
        (await Repo.FederatedCameraExistsAsync(TargetB, "CH01", CancellationToken.None)).ShouldBeFalse();
}
