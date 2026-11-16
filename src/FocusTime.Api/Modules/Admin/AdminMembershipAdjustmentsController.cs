using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using FocusTime.Api.Modules.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FocusTime.Api.Modules.Admin;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewAdminMembershipRequest(Guid OperationId, string Action, string? Plan, string? Scope,
    [param: Required, MaxLength(32)] string ReasonCode, [param: MaxLength(400)] string? Note);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApplyAdminMembershipRequest(Guid OperationId, bool AcknowledgeNoRefund = false);
public sealed record AdminEffectiveMembership(bool IsActive, string? Plan, Guid? GrantId,
    DateTimeOffset? StartsAtUtc, DateTimeOffset? ExpiresAtUtc)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsTrial { get; init; }
}
public sealed record AdminMembershipAdjustmentContext(Guid UserId, string Email, AdminEffectiveMembership Current,
    AdminEffectiveMembership AfterGiftRevocation, AdminEffectiveMembership? AfterAllRevocation,
    int GiftGrantCount, int ProtectedGrantCount, bool CanGrant, bool CanRevokeAll, string? ProtectionNote);
public sealed record AdminMembershipAdjustmentView(Guid UserId, Guid OperationId, string Action, string? Plan,
    string? Scope, string ReasonCode, string? Note, AdminEffectiveMembership Before, AdminEffectiveMembership After,
    int AffectedGrantCount, DateTimeOffset ExpiresAtUtc, bool Applied, long? Version);

[ApiController, Authorize(Policy = AdminAccess.Policy), Route("api/v1/admin/users/{userId:guid}/membership-adjustments"),
    ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminMembershipAdjustmentsController(AdminMembershipAdjustmentService adjustments) : ControllerBase
{
    [HttpGet]
    public Task<AdminMembershipAdjustmentContext> Context(Guid userId, CancellationToken ct) => adjustments.Context(userId, ct);

    [HttpPost("preview")]
    public Task<AdminMembershipAdjustmentView> Preview(Guid userId, PreviewAdminMembershipRequest request, CancellationToken ct)
    {
        RequireMutationHeader();
        return adjustments.Preview(userId, request, User, Authorization(), ct);
    }

    [HttpPost("apply")]
    public Task<AdminMembershipAdjustmentView> Apply(Guid userId, ApplyAdminMembershipRequest request, CancellationToken ct)
    {
        RequireMutationHeader();
        return adjustments.Apply(userId, request.OperationId, request.AcknowledgeNoRefund, User, Authorization(), ct);
    }

    private void RequireMutationHeader()
    {
        if (Request.Headers["X-FocusTime-Admin"] != "1") throw new AuthFault(403, "admin_header_required", "Administrator mutation header required.");
    }
    private string Authorization() => Request.Headers.Authorization.Count > 0
        ? Request.Headers.Authorization.ToString() : "Bearer " + Request.Cookies[AdminAccess.CookieName];
}
