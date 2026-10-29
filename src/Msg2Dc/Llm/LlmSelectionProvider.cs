using MongoDB.Driver;
using Msg2Dc.Storage;

namespace Msg2Dc.Llm;

public sealed class LlmSelectionProvider
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly MongoStore _store;
    private readonly ILogger<LlmSelectionProvider> _log;
    private readonly object _gate = new();

    private LlmSelection? _current;
    private DateTime _loadedAtUtc = DateTime.MinValue;
    private bool _loaded;
    private long _version;

    public LlmSelectionProvider(MongoStore store, ILogger<LlmSelectionProvider> log)
    {
        _store = store;
        _log = log;
    }

    public LlmSelection? Current
    {
        get
        {
            lock (_gate)
            {
                return Clone(_current);
            }
        }
    }

    public async Task<LlmSelection?> GetAsync(CancellationToken ct)
    {
        long version;
        lock (_gate)
        {
            if (_loaded && DateTime.UtcNow - _loadedAtUtc < CacheLifetime)
            {
                return Clone(_current);
            }

            version = _version;
        }

        var settings = await _store.Settings
            .Find(Builders<SettingsDoc>.Filter.Empty)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        lock (_gate)
        {
            if (_version == version)
            {
                _current = Clone(settings?.Llm);
                _loaded = true;
                _loadedAtUtc = DateTime.UtcNow;
                _version++;
            }

            return Clone(_current);
        }
    }

    public LlmSelection? LoadBlocking()
    {
        try
        {
            var settings = _store.Settings
                .Find(Builders<SettingsDoc>.Filter.Empty)
                .FirstOrDefault();
            Store(settings?.Llm);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read LLM selection from settings");
        }

        return Current;
    }

    public void Set(LlmSelection selection) => Store(selection);

    public void Invalidate()
    {
        lock (_gate)
        {
            _version++;
            _loaded = false;
            _loadedAtUtc = DateTime.MinValue;
        }
    }

    private void Store(LlmSelection? selection)
    {
        lock (_gate)
        {
            _current = Clone(selection);
            _loaded = true;
            _loadedAtUtc = DateTime.UtcNow;
            _version++;
        }
    }

    private static LlmSelection? Clone(LlmSelection? selection) => selection is null
        ? null
        : new LlmSelection { Provider = selection.Provider ?? "", Model = selection.Model ?? "" };
}
