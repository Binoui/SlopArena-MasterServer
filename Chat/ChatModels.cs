namespace MasterServer.Chat;

// PlayerId is opaque text on the chat wire; it is the existing authenticated
// player identity, not a second account or a display-name lookup key.
public sealed record ChatPlayer(string PlayerId, string DisplayName, string SessionTag);

public sealed record ChatMessage(
    Guid MessageId,
    long Sequence,
    string Channel,
    Guid? ServerId,
    string? RecipientId,
    ChatPlayer Sender,
    string Text,
    DateTimeOffset SentAt);

public sealed record ChatPresence(ChatPlayer Player, bool Online);
public sealed record ServerChatState(Guid? ServerId, ChatMessage[] Messages);
public sealed record ChatSnapshot(ChatPlayer Self, ChatMessage[] GlobalMessages, ServerChatState Server);
public sealed record SetDisplayNameRequest(string DisplayName);

internal sealed record ChatDelivery(ChatMessage Message, string[] ConnectionIds);
