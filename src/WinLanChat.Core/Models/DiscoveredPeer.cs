namespace WinLanChat.Core.Models;

public sealed record DiscoveredPeer(Guid Id, string Name, string ComputerName, string Host, int Port)
{
    public string Address => $"{Host}:{Port}";
    public string Description => $"{ComputerName} · {Address}";
}
