using Microsoft.AspNetCore.SignalR;

namespace Exchange.API.Hubs;

public class MarketDataHub : Hub
{
    // Called automatically when a browser connects
    public override async Task OnConnectedAsync()
    {
        Console.WriteLine($"Client connected: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    // Called automatically when a browser disconnects
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Console.WriteLine($"Client disconnected: {Context.ConnectionId}");
        await base.OnDisconnectedAsync(exception);
    }

    // Called by the browser to subscribe to a trading pair
    // e.g. browser calls: connection.invoke("SubscribeToPair", "BTC/USD")
    public async Task SubscribeToPair(string tradingPair)
    {
        // Add this connection to the named group
        await Groups.AddToGroupAsync(Context.ConnectionId, tradingPair);
        Console.WriteLine($"Client {Context.ConnectionId} subscribed to {tradingPair}");

        // Confirm back to the subscribing client only
        await Clients.Caller.SendAsync("Subscribed", new
        {
            tradingPair,
            message = $"You are now subscribed to {tradingPair} updates"
        });
    }

    // Called by the browser to unsubscribe
    public async Task UnsubscribeFromPair(string tradingPair)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, tradingPair);
        await Clients.Caller.SendAsync("Unsubscribed", tradingPair);
    }
}