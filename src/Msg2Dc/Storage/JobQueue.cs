using MongoDB.Bson;
using MongoDB.Driver;

namespace Msg2Dc.Storage;

public sealed class JobQueue
{
    private readonly MongoStore _store;

    public JobQueue(MongoStore store) => _store = store;

    public Task EnqueueProcessAsync(string lineMessageId, TimeSpan delay, CancellationToken ct) =>
        _store.Jobs.InsertOneAsync(new JobDoc
        {
            Kind = "process_message",
            LineMessageId = lineMessageId,
            RunAfter = DateTime.UtcNow.Add(delay),
            CreatedAt = DateTime.UtcNow
        }, cancellationToken: ct);

    public Task EnqueueDeletePostsAsync(string lineMessageId, CancellationToken ct) =>
        _store.Jobs.InsertOneAsync(new JobDoc
        {
            Kind = "delete_posts",
            LineMessageId = lineMessageId,
            RunAfter = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        }, cancellationToken: ct);

    public async Task<JobDoc?> ClaimAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<JobDoc>.Filter.And(
            Builders<JobDoc>.Filter.Eq(j => j.Status, JobDoc.StatusPending),
            Builders<JobDoc>.Filter.Lte(j => j.RunAfter, now));

        var update = Builders<JobDoc>.Update
            .Set(j => j.Status, JobDoc.StatusRunning)
            .Set(j => j.LockedAt, now)
            .Inc(j => j.Attempts, 1);

        return await _store.Jobs.FindOneAndUpdateAsync(filter, update, new FindOneAndUpdateOptions<JobDoc>
        {
            Sort = Builders<JobDoc>.Sort.Ascending(j => j.RunAfter),
            ReturnDocument = ReturnDocument.After
        }, ct);
    }

    public Task CompleteAsync(ObjectId id, CancellationToken ct) =>
        _store.Jobs.UpdateOneAsync(
            Builders<JobDoc>.Filter.Eq(j => j.Id, id),
            Builders<JobDoc>.Update.Set(j => j.Status, JobDoc.StatusDone),
            cancellationToken: ct);

    public Task FailAsync(JobDoc job, string error, CancellationToken ct)
    {
        var dead = job.Attempts >= 5;
        var update = Builders<JobDoc>.Update
            .Set(j => j.Status, dead ? JobDoc.StatusDead : JobDoc.StatusPending)
            .Set(j => j.LastError, error)
            .Unset(j => j.LockedAt)
            .Set(j => j.RunAfter, DateTime.UtcNow.AddSeconds(Math.Pow(2, Math.Min(job.Attempts, 6)) * 5));

        return _store.Jobs.UpdateOneAsync(Builders<JobDoc>.Filter.Eq(j => j.Id, job.Id), update, cancellationToken: ct);
    }

    public Task RequeueStaleAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var filter = Builders<JobDoc>.Filter.And(
            Builders<JobDoc>.Filter.Eq(j => j.Status, JobDoc.StatusRunning),
            Builders<JobDoc>.Filter.Lt(j => j.LockedAt, cutoff));

        return _store.Jobs.UpdateManyAsync(filter,
            Builders<JobDoc>.Update.Set(j => j.Status, JobDoc.StatusPending).Unset(j => j.LockedAt),
            cancellationToken: ct);
    }

    public Task<long> PendingCountAsync(CancellationToken ct) =>
        _store.Jobs.CountDocumentsAsync(
            Builders<JobDoc>.Filter.In(j => j.Status, new[] { JobDoc.StatusPending, JobDoc.StatusRunning }),
            cancellationToken: ct);
}
