using Microsoft.AspNetCore.SignalR;

namespace MasterServer.Chat;

public sealed class ChatControlRateFilter(ChatService chat) : IHubFilter
{
    public ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        // Send attempts have their own shared guest budget. A rejected chat
        // send must not use the budget for leaving, selecting, or querying state.
        if (invocationContext.HubMethodName is not ("SendGlobal" or "SendServer" or "SendDirect"))
            chat.CheckControlRate(invocationContext.Context.ConnectionId);

        return next(invocationContext);
    }
}
