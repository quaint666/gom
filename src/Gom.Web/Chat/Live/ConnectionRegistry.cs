using Microsoft.Extensions.Options;

namespace Gom.Web.Chat.Live;

/// <summary>Who is connected, by person. Bounded per person; knows how to end a person's or a session's sockets.</summary>
public sealed class ConnectionRegistry(IOptions<ChatOptions> options)
{
    /// <summary>The close code that tells a device its session is over: go to sign-in, do not reconnect.</summary>
    public const int SessionEnded = 4401;

    private readonly Dictionary<Guid, List<Connection>> _byUser = [];
    private readonly Lock _gate = new();

    public void Add(Connection connection)
    {
        List<Connection> evicted = [];
        lock (_gate)
        {
            if (!_byUser.TryGetValue(connection.UserId, out var list)) _byUser[connection.UserId] = list = [];
            while (list.Count >= options.Value.MaxSocketsPerUser) { evicted.Add(list[0]); list.RemoveAt(0); }
            list.Add(connection);
        }
        foreach (var old in evicted) old.Close(1008, "too many connections");
    }

    public void Remove(Connection connection)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(connection.UserId, out var list)) return;
            list.Remove(connection);
            if (list.Count == 0) _byUser.Remove(connection.UserId);
        }
    }

    public Connection[] For(Guid userId)
    {
        lock (_gate) return _byUser.TryGetValue(userId, out var list) ? [.. list] : [];
    }

    public int Count { get { lock (_gate) return _byUser.Values.Sum(l => l.Count); } }

    public void CloseSession(Guid sessionId)
    {
        Connection[] hit;
        lock (_gate) hit = [.. _byUser.Values.SelectMany(l => l).Where(c => c.SessionId == sessionId)];
        foreach (var c in hit) c.Close(SessionEnded, "session ended");
    }

    /// <summary>Ends every socket of a person, except those of one session (the device that is changing its own password).</summary>
    public void CloseUser(Guid userId, Guid? exceptSession = null)
    {
        foreach (var c in For(userId).Where(c => c.SessionId != exceptSession)) c.Close(SessionEnded, "session ended");
    }
}
