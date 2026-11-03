using System.Text.Json;
using System.Threading.Channels;

namespace FactorioModStudio.Server;

/// <summary>Fans server-side events (file changes, mod list changes) out to every connected browser tab.</summary>
internal sealed class ChangeFeed
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly List<Channel<string>> _subscribers = new();

    public Subscription Subscribe()
    {
        // A slow tab must never block the watcher: keep only the newest events.
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_gate) _subscribers.Add(channel);
        return new Subscription(this, channel);
    }

    public void Publish(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        lock (_gate)
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(json);
    }

    private void Remove(Channel<string> channel)
    {
        lock (_gate) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    public int SubscriberCount
    {
        get { lock (_gate) return _subscribers.Count; }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly ChangeFeed _feed;
        private readonly Channel<string> _channel;

        internal Subscription(ChangeFeed feed, Channel<string> channel)
        {
            _feed = feed;
            _channel = channel;
        }

        public ChannelReader<string> Reader => _channel.Reader;

        public void Dispose() => _feed.Remove(_channel);
    }
}
