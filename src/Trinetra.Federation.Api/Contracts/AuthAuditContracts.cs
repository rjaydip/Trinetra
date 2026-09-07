using System.Text.Json;

namespace Trinetra.Federation.Api.Contracts;

/// <summary>One authentication-audit record.</summary>
public sealed record AuthAuditItem(
    DateTimeOffset OccurredAt,
    long Id,
    string EventType,
    string Outcome,
    Guid? UserId,
    Guid? ApiKeyId,
    string? PresentedUsername,
    string? SourceAddress,
    string? UserAgent,
    string? Jti,
    JsonElement? Detail);

/// <summary>A newest-first page of the authentication audit trail.</summary>
public sealed record AuthAuditPage(IReadOnlyList<AuthAuditItem> Items, string? Cursor);
