using System.Text.Json;
using System.Text;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.FocusSync.Contracts;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Sessions;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.FocusSync;

public sealed class FocusSyncService(AppDbContext db, TimeProvider clock, AuthSecurity security)
{
    private async Task<LoginSession> LockSessionAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(x => x.Id == userId, ct);
        await AccountTransactions.LockAsync(db, user.Email!, ct);
        await db.Entry(user).ReloadAsync(ct);
        var now = clock.GetUtcNow();
        return await db.LoginSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId &&
            x.UserId == userId && x.RevokedAtUtc == null && x.ExpiresAtUtc > now && x.AccessExpiresAtUtc > now &&
            x.SecurityStamp == user.SecurityStamp, ct)
            ?? throw new AuthFault(401, "invalid_session", "Please sign in again.");
    }
    public async Task<SyncPushResponse> PushAsync(Guid userId, Guid sessionId, SyncPushRequest request, CancellationToken ct)
    {
        if (request.Operations.Any(x => x is null))
            throw new AuthFault(400, "validation_failed", "Operations must not contain null entries.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var session = await LockSessionAsync(userId, sessionId, ct);
        var state = await db.FocusSyncStates.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (state is null) { state = new() { UserId = userId }; db.Add(state); }
        var results = new List<SyncOperationResult>();
        foreach (var operation in request.Operations)
        {
            if (operation.OperationId == Guid.Empty || operation.RecordId == Guid.Empty)
            { results.Add(new(operation.OperationId, operation.RecordId, SyncOperationStatus.Rejected, null, "invalid_id")); continue; }
            var code = operation.BaseRevision is < 1 ? "invalid_revision" :
                operation.Kind == SyncOperationKind.Delete ? operation.Record is not null ? "delete_record_not_allowed" : null :
                operation.Record is null ? "record_required" : FocusPayloadCodec.Validate(operation.Record);
            var record = code is null && operation.Kind == SyncOperationKind.Upsert ? FocusPayloadCodec.Normalize(operation.Record!) : operation.Record;
            var payload = JsonSerializer.Serialize(record, FocusPayloadCodec.Json);
            var digest = AuthSecurity.Hash(JsonSerializer.Serialize(new
                { operation.RecordId, operation.Kind, operation.BaseRevision, Record = record }, FocusPayloadCodec.Json));
            var receipt = db.FocusSyncReceipts.Local.FirstOrDefault(x => x.UserId == userId && x.OperationId == operation.OperationId)
                ?? await db.FocusSyncReceipts.SingleOrDefaultAsync(x => x.UserId == userId && x.OperationId == operation.OperationId, ct);
            if (receipt is not null)
            {
                results.Add(receipt.RequestHash != digest
                    ? new(operation.OperationId, operation.RecordId, SyncOperationStatus.Conflict, null, "operation_id_reused")
                    : new(operation.OperationId, operation.RecordId,
                        receipt.Status == "Applied" ? SyncOperationStatus.Duplicate : Enum.Parse<SyncOperationStatus>(receipt.Status),
                        receipt.Revision, receipt.Code));
                continue;
            }
            var status = code is null ? SyncOperationStatus.Applied : SyncOperationStatus.Rejected;
            long? revision = null;
            if (code is null)
            {
                var existing = db.FocusRecords.Local.FirstOrDefault(x => x.UserId == userId && x.RecordId == operation.RecordId)
                    ?? await db.FocusRecords.SingleOrDefaultAsync(x => x.UserId == userId && x.RecordId == operation.RecordId, ct);
                var payloadHash = AuthSecurity.Hash(payload);
                if (operation.Kind == SyncOperationKind.Delete)
                {
                    // Completed history is immutable: delete wins, including delete-before-upload.
                    if (existing?.IsDeleted == true)
                    { status = SyncOperationStatus.Duplicate; revision = existing.Revision; }
                    else
                    {
                        revision = state.LastRevision = checked(state.LastRevision + 1);
                        if (existing is null)
                        {
                            existing = new() { UserId = userId, RecordId = operation.RecordId };
                            db.FocusRecords.Add(existing);
                        }
                        existing.SourceDeviceId = session.DeviceId;
                        existing.Revision = revision.Value;
                        existing.IsDeleted = true;
                        existing.PayloadJson = "{}";
                        existing.PayloadHash = AuthSecurity.Hash("null");
                        existing.ReceivedAtUtc = clock.GetUtcNow();
                        AppendChange(existing);
                    }
                }
                else if (existing is not null)
                {
                    revision = existing.Revision;
                    status = !existing.IsDeleted && existing.PayloadHash == payloadHash ? SyncOperationStatus.Duplicate : SyncOperationStatus.Conflict;
                    code = status == SyncOperationStatus.Conflict ? existing.IsDeleted ? "record_deleted" : "record_conflict" : null;
                }
                else if (operation.BaseRevision is not null)
                { status = SyncOperationStatus.Conflict; code = "revision_conflict"; }
                else
                {
                    revision = state.LastRevision = checked(state.LastRevision + 1);
                    var added = new FocusRecord { UserId = userId, RecordId = operation.RecordId, SourceDeviceId = session.DeviceId,
                        Revision = revision.Value, PayloadJson = payload, PayloadHash = payloadHash, ReceivedAtUtc = clock.GetUtcNow() };
                    db.FocusRecords.Add(added);
                    AppendChange(added);
                }
            }
            db.FocusSyncReceipts.Add(new() { UserId = userId, OperationId = operation.OperationId, RecordId = operation.RecordId,
                RequestHash = digest, Status = status.ToString(), Code = code, Revision = revision, ReceivedAtUtc = clock.GetUtcNow() });
            results.Add(new(operation.OperationId, operation.RecordId, status, revision, code));
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(results);
    }

    private void AppendChange(FocusRecord record) => db.FocusChanges.Add(new()
    {
        UserId = record.UserId, Revision = record.Revision, RecordId = record.RecordId,
        SourceDeviceId = record.SourceDeviceId, IsDeleted = record.IsDeleted,
        PayloadJson = record.PayloadJson, ReceivedAtUtc = record.ReceivedAtUtc
    });

    public async Task<SyncChangesResponse> ChangesAsync(Guid userId, Guid sessionId, string? cursor, int limit, CancellationToken ct)
    {
        var page = await ReadPageAsync(userId, sessionId, cursor, limit, false, ct);
        return new(page.Items, page.NextCursor!, page.HasMore);
    }

    public async Task<PageResponse<FocusRecordChange>> HistoryAsync(Guid userId, Guid sessionId, string? cursor, int limit, CancellationToken ct)
    {
        var page = await ReadPageAsync(userId, sessionId, cursor, limit, true, ct);
        return new(page.Items, page.HasMore ? page.NextCursor : null);
    }

    private async Task<(IReadOnlyList<FocusRecordChange> Items, string NextCursor, bool HasMore)> ReadPageAsync(
        Guid userId, Guid sessionId, string? cursor, int limit, bool history, CancellationToken ct)
    {
        if (limit is < 1 or > 200) throw new AuthFault(400, "validation_failed", "Page size must be between 1 and 200.");
        var position = FocusCursorCodec.Read(security, userId, history ? "history" : "changes", cursor);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSessionAsync(userId, sessionId, ct);
        var last = await db.FocusSyncStates.Where(x => x.UserId == userId).Select(x => x.LastRevision).SingleOrDefaultAsync(ct);
        var upper = position.UpperRevision ?? last;
        if (upper > last || position.Position > upper) throw FocusCursorCodec.Invalid();
        var query = db.FocusChanges.AsNoTracking().Where(x => x.UserId == userId && x.Revision > position.Position && x.Revision <= upper);
        if (history)
            query = query.Where(x => !x.IsDeleted && !db.FocusChanges.Any(newer => newer.UserId == userId &&
                newer.RecordId == x.RecordId && newer.Revision > x.Revision && newer.Revision <= upper));
        var fetched = await query.OrderBy(x => x.Revision).Take(limit + 1).ToListAsync(ct);
        var items = new List<FocusRecordChange>();
        var bytes = 0;
        foreach (var row in fetched.Take(limit))
        {
            // Leave room for IPC string escaping as well as the HTTP response envelope.
            var size = row.IsDeleted ? 256 : Encoding.UTF8.GetByteCount(row.PayloadJson) + 256;
            if (items.Count > 0 && bytes + size > 350000) break;
            items.Add(new(row.RecordId, row.SourceDeviceId, row.Revision, row.IsDeleted,
                row.IsDeleted ? null : JsonSerializer.Deserialize<FocusRecordPayload>(row.PayloadJson, FocusPayloadCodec.Json)));
            bytes += size;
        }
        var more = fetched.Count > items.Count;
        var next = new FocusCursor(userId, position.Purpose, more ? items[^1].ServerRevision : upper, more ? upper : null);
        await tx.CommitAsync(ct);
        return (items, FocusCursorCodec.Write(security, next), more);
    }
    public async Task<FocusRecordChange> GetAsync(Guid userId, Guid sessionId, Guid recordId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSessionAsync(userId, sessionId, ct);
        var row = await db.FocusRecords.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId && x.RecordId == recordId && !x.IsDeleted, ct)
            ?? throw new AuthFault(404, "not_found", "Focus record was not found.");
        var response = new FocusRecordChange(row.RecordId, row.SourceDeviceId, row.Revision, false,
            JsonSerializer.Deserialize<FocusRecordPayload>(row.PayloadJson, FocusPayloadCodec.Json));
        await tx.CommitAsync(ct);
        return response;
    }
}
