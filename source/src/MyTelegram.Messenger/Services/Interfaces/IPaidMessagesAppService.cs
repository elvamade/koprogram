namespace MyTelegram.Messenger.Services.Interfaces;

public interface IPaidMessagesAppService
{
    Task<long> GetRequiredPaidStarsAsync(long senderUserId, Peer toPeer);

    Task ChargePaidMessagesAsync(long senderUserId, Peer toPeer, long? allowPaidStars, int messageCount);

    Task<long> GetPaidMessagesRevenueAsync(long ownerUserId, long payerUserId, Peer? parentPeer = null);
}
