namespace LLG.Core;

public sealed class UserCredentials
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public bool IsComplete => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password);
    public override string ToString() => "[saved credentials]";
}

public sealed class SubscriptionSnapshot
{
    public string Username { get; init; } = "";
    public string Yaml { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    public override string ToString() => "[subscription snapshot]";
}

public sealed class SavedState
{
    public int Version { get; init; } = 1;
    public UserCredentials Credentials { get; init; } = new();
    public SubscriptionSnapshot? Snapshot { get; init; }

    public SavedState WithCredentials(UserCredentials credentials) => new()
    {
        Credentials = credentials,
        Snapshot = string.Equals(Credentials.Username, credentials.Username, StringComparison.Ordinal)
            ? Snapshot : null
    };

    public SavedState WithSnapshot(SubscriptionSnapshot snapshot)
    {
        if (!string.Equals(Credentials.Username, snapshot.Username, StringComparison.Ordinal))
            throw new InvalidOperationException("节点列表与当前账号不匹配，请重新更新。");
        return new() { Credentials = Credentials, Snapshot = snapshot };
    }

    public override string ToString() => "[encrypted user state]";
}

public sealed record ConversionResult(string Links, int Count, int VlessCount, int AnyTlsCount);

public sealed class UserVisibleException(string message, Exception? inner = null) : Exception(message, inner);
