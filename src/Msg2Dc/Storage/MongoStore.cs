using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Msg2Dc.Configuration;

namespace Msg2Dc.Storage;

public sealed class MongoStore
{
    private readonly IMongoDatabase _db;

    public MongoStore(IOptions<MongoOptions> options)
    {
        var settings = MongoClientSettings.FromConnectionString(options.Value.ConnectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(8);
        settings.ConnectTimeout = TimeSpan.FromSeconds(8);
        var client = new MongoClient(settings);
        _db = client.GetDatabase(options.Value.Database);
    }

    public IMongoCollection<GroupDoc> Groups => _db.GetCollection<GroupDoc>("groups");
    public IMongoCollection<MessageDoc> Messages => _db.GetCollection<MessageDoc>("messages");
    public IMongoCollection<ItemDoc> Items => _db.GetCollection<ItemDoc>("items");
    public IMongoCollection<PostDoc> Posts => _db.GetCollection<PostDoc>("posts");
    public IMongoCollection<SubjectDoc> Subjects => _db.GetCollection<SubjectDoc>("subjects");
    public IMongoCollection<SubscriptionDoc> Subscriptions => _db.GetCollection<SubscriptionDoc>("subscriptions");
    public IMongoCollection<SenderDoc> Senders => _db.GetCollection<SenderDoc>("senders");
    public IMongoCollection<SettingsDoc> Settings => _db.GetCollection<SettingsDoc>("settings");
    public IMongoCollection<JobDoc> Jobs => _db.GetCollection<JobDoc>("jobs");
    public IMongoCollection<LlmLogDoc> LlmLogs => _db.GetCollection<LlmLogDoc>("llmLogs");
    public IMongoCollection<SystemStateDoc> SystemStates => _db.GetCollection<SystemStateDoc>("systemState");

    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        await Groups.Indexes.CreateOneAsync(
            new CreateIndexModel<GroupDoc>(Builders<GroupDoc>.IndexKeys.Ascending(g => g.LineGroupId),
                new CreateIndexOptions { Unique = true }), cancellationToken: ct);

        await Messages.Indexes.CreateOneAsync(
            new CreateIndexModel<MessageDoc>(Builders<MessageDoc>.IndexKeys.Ascending(m => m.WebhookEventId),
                new CreateIndexOptions { Unique = true }), cancellationToken: ct);

        await Messages.Indexes.CreateOneAsync(
            new CreateIndexModel<MessageDoc>(Builders<MessageDoc>.IndexKeys
                .Ascending(m => m.LineGroupId).Ascending(m => m.SenderUserId).Ascending(m => m.SentAt)),
            cancellationToken: ct);

        await Messages.Indexes.CreateOneAsync(
            new CreateIndexModel<MessageDoc>(Builders<MessageDoc>.IndexKeys.Ascending(m => m.LineMessageId)),
            cancellationToken: ct);

        await Items.Indexes.CreateOneAsync(
            new CreateIndexModel<ItemDoc>(Builders<ItemDoc>.IndexKeys
                .Ascending(i => i.SubjectName).Descending(i => i.CreatedAt)), cancellationToken: ct);

        await Items.Indexes.CreateOneAsync(
            new CreateIndexModel<ItemDoc>(Builders<ItemDoc>.IndexKeys.Ascending(i => i.SearchTokens)),
            cancellationToken: ct);

        await Items.Indexes.CreateOneAsync(
            new CreateIndexModel<ItemDoc>(Builders<ItemDoc>.IndexKeys.Ascending(i => i.LineMessageId)),
            cancellationToken: ct);

        await Posts.Indexes.CreateOneAsync(
            new CreateIndexModel<PostDoc>(Builders<PostDoc>.IndexKeys.Ascending(p => p.LineMessageId)),
            cancellationToken: ct);

        await Subjects.Indexes.CreateOneAsync(
            new CreateIndexModel<SubjectDoc>(Builders<SubjectDoc>.IndexKeys.Ascending(s => s.Name),
                new CreateIndexOptions { Unique = true }), cancellationToken: ct);

        await Subscriptions.Indexes.CreateOneAsync(
            new CreateIndexModel<SubscriptionDoc>(Builders<SubscriptionDoc>.IndexKeys
                .Ascending(s => s.ChannelId).Ascending(s => s.SubjectName),
                new CreateIndexOptions { Unique = true }), cancellationToken: ct);

        await Subscriptions.Indexes.CreateOneAsync(
            new CreateIndexModel<SubscriptionDoc>(Builders<SubscriptionDoc>.IndexKeys
                .Ascending(s => s.GuildId).Ascending(s => s.SubjectName)), cancellationToken: ct);

        await Senders.Indexes.CreateOneAsync(
            new CreateIndexModel<SenderDoc>(Builders<SenderDoc>.IndexKeys
                .Ascending(s => s.LineUserId).Ascending(s => s.LineGroupId),
                new CreateIndexOptions { Unique = true }), cancellationToken: ct);

        await Jobs.Indexes.CreateOneAsync(
            new CreateIndexModel<JobDoc>(Builders<JobDoc>.IndexKeys
                .Ascending(j => j.Status).Ascending(j => j.RunAfter)), cancellationToken: ct);

        await SystemStates.Indexes.CreateOneAsync(
            new CreateIndexModel<SystemStateDoc>(Builders<SystemStateDoc>.IndexKeys.Ascending(s => s.Key),
                new CreateIndexOptions { Unique = true }), cancellationToken: ct);
    }

    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task DropDatabaseAsync(CancellationToken ct) =>
        _db.Client.DropDatabaseAsync(_db.DatabaseNamespace.DatabaseName, ct);
}
