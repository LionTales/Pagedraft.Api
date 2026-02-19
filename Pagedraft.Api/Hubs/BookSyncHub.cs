using Microsoft.AspNetCore.SignalR;

namespace Pagedraft.Api.Hubs;

public class BookSyncHub : Hub
{
    public async Task JoinBook(Guid bookId) => await Groups.AddToGroupAsync(Context.ConnectionId, $"book:{bookId}");

    public async Task LeaveBook(Guid bookId) => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"book:{bookId}");
}
