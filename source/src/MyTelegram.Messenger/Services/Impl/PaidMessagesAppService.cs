using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarsTransactions;

namespace MyTelegram.Messenger.Services.Impl;

public class PaidMessagesAppService(
    IMongoDatabase mongoDatabase,
    IPrivacyAppService privacyAppService,
    IContactAppService contactAppService)
    : IPaidMessagesAppService, ITransientDependency
{
    private const string ChannelSettingsCollection = "paid_message_channel_settings";
    private const string ExceptionsCollection = "paid_message_exceptions";
    private const string RevenueCollection = "paid_message_revenue";
    private const string BalanceCollection = "eventflow-userstarsbalancereadmodel";
    private const int PaidMessagesCommissionPermille = 850;

    public async Task<long> GetRequiredPaidStarsAsync(long senderUserId, Peer toPeer)
    {
        if (IsSelfOrSavedMessagesPeer(senderUserId, toPeer))
        {
            return 0;
        }

        if (toPeer.PeerType == PeerType.User)
        {
            if (senderUserId == toPeer.PeerId)
            {
                return 0;
            }

            var privacy = await privacyAppService.GetGlobalPrivacySettingsAsync(toPeer.PeerId);
            var paidStars = privacy?.NoncontactPeersPaidStars;
            if (!paidStars.HasValue || paidStars.Value <= 0)
            {
                return 0;
            }

            var contactType = await contactAppService.GetContactTypeAsync(senderUserId, toPeer.PeerId);
            var exemptByContact = contactType is ContactType.Mutual or ContactType.ContactOfTargetUser;
            if (exemptByContact)
            {
                return 0;
            }

            var exemptBySettings = await HasPaidMessageExceptionAsync(toPeer.PeerId, senderUserId, null);
            return exemptBySettings ? 0 : paidStars.Value;
        }

        if (toPeer.PeerType == PeerType.Channel)
        {
            var collection = mongoDatabase.GetCollection<BsonDocument>(ChannelSettingsCollection);
            var doc = await collection.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", toPeer.PeerId)).FirstOrDefaultAsync();
            if (doc == null || !doc.Contains("SendPaidMessagesStars") || doc["SendPaidMessagesStars"].IsBsonNull)
            {
                return 0;
            }

            var paidStars = doc["SendPaidMessagesStars"].IsInt64 ? doc["SendPaidMessagesStars"].AsInt64 : doc["SendPaidMessagesStars"].AsInt32;
            return paidStars > 0 ? paidStars : 0;
        }

        return 0;
    }

    public async Task ChargePaidMessagesAsync(long senderUserId, Peer toPeer, long? allowPaidStars, int messageCount)
    {
        if (messageCount <= 0)
        {
            return;
        }

        if (IsSelfOrSavedMessagesPeer(senderUserId, toPeer))
        {
            return;
        }

        var perMessageStars = await GetRequiredPaidStarsAsync(senderUserId, toPeer);
        if (perMessageStars <= 0)
        {
            return;
        }

        var totalRequired = checked(perMessageStars * messageCount);
        if (!allowPaidStars.HasValue || allowPaidStars.Value < totalRequired)
        {
            var amount = totalRequired > int.MaxValue ? int.MaxValue : (int)totalRequired;
            RpcErrors.RpcErrors403.AllowPaymentRequiredX.ThrowRpcError(amount);
        }

        var senderNewBalance = await DeductBalanceAsync(senderUserId, totalRequired);
        var now = DateTime.UtcNow.ToTimestamp();

        var senderTx = StarsTransactionStore.CreateTransactionDocument(
            ownerUserId: senderUserId,
            amount: -totalRequired,
            date: now,
            peerType: (int)toPeer.PeerType,
            peerId: toPeer.PeerId,
            title: "Paid messages",
            description: "Paid message sending",
            paidMessages: messageCount
        );
        await StarsTransactionStore.GetCollection(mongoDatabase).InsertOneAsync(senderTx);

        if (toPeer.PeerType == PeerType.User)
        {
            var receivedStars = totalRequired * PaidMessagesCommissionPermille / 1000;
            if (receivedStars > 0)
            {
                await AddBalanceAsync(toPeer.PeerId, receivedStars);
            }

            await AddRevenueAsync(toPeer.PeerId, senderUserId, null, receivedStars, totalRequired, messageCount);

            var receiverTx = StarsTransactionStore.CreateTransactionDocument(
                ownerUserId: toPeer.PeerId,
                amount: receivedStars,
                date: now,
                peerType: (int)PeerType.User,
                peerId: senderUserId,
                title: "Paid messages",
                description: "Paid message revenue",
                paidMessages: messageCount
            );
            await StarsTransactionStore.GetCollection(mongoDatabase).InsertOneAsync(receiverTx);
        }
    }

    public async Task<long> GetPaidMessagesRevenueAsync(long ownerUserId, long payerUserId, Peer? parentPeer = null)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(RevenueCollection);
        BsonValue scopeType = parentPeer == null ? BsonNull.Value : new BsonInt32((int)parentPeer.PeerType);
        BsonValue scopePeerId = parentPeer == null ? BsonNull.Value : new BsonInt64(parentPeer.PeerId);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("PayerUserId", payerUserId),
            Builders<BsonDocument>.Filter.Eq("ScopeType", scopeType),
            Builders<BsonDocument>.Filter.Eq("ScopePeerId", scopePeerId)
        );
        var doc = await collection.Find(filter).FirstOrDefaultAsync();
        if (doc == null || !doc.Contains("StarsAmount") || doc["StarsAmount"].IsBsonNull)
        {
            return 0;
        }

        return doc["StarsAmount"].IsInt64 ? doc["StarsAmount"].AsInt64 : doc["StarsAmount"].AsInt32;
    }

    private async Task<bool> HasPaidMessageExceptionAsync(long ownerUserId, long targetUserId, Peer? parentPeer)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(ExceptionsCollection);
        BsonValue scopeType = parentPeer == null ? BsonNull.Value : new BsonInt32((int)parentPeer.PeerType);
        BsonValue scopePeerId = parentPeer == null ? BsonNull.Value : new BsonInt64(parentPeer.PeerId);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("TargetUserId", targetUserId),
            Builders<BsonDocument>.Filter.Eq("ScopeType", scopeType),
            Builders<BsonDocument>.Filter.Eq("ScopePeerId", scopePeerId)
        );
        return await collection.Find(filter).AnyAsync();
    }

    private async Task<long> DeductBalanceAsync(long userId, long amount)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(BalanceCollection);
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var doc = await collection.Find(filter).FirstOrDefaultAsync();
        var currentBalance = 0L;
        if (doc != null && doc.Contains("Balance") && !doc["Balance"].IsBsonNull)
        {
            currentBalance = doc["Balance"].IsInt64 ? doc["Balance"].AsInt64 : doc["Balance"].AsInt32;
        }

        if (currentBalance < amount)
        {
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
        }

        var newBalance = currentBalance - amount;
        var update = Builders<BsonDocument>.Update
            .Set("Balance", newBalance)
            .Set("LastUpdated", DateTime.UtcNow);
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
        return newBalance;
    }

    private async Task AddBalanceAsync(long userId, long amount)
    {
        if (amount <= 0)
        {
            return;
        }

        var collection = mongoDatabase.GetCollection<BsonDocument>(BalanceCollection);
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var update = Builders<BsonDocument>.Update
            .Inc("Balance", amount)
            .Set("LastUpdated", DateTime.UtcNow);
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }

    private async Task AddRevenueAsync(long ownerUserId, long payerUserId, Peer? parentPeer, long starsAmount, long chargedStars, int messageCount)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(RevenueCollection);
        BsonValue scopeType = parentPeer == null ? BsonNull.Value : new BsonInt32((int)parentPeer.PeerType);
        BsonValue scopePeerId = parentPeer == null ? BsonNull.Value : new BsonInt64(parentPeer.PeerId);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("PayerUserId", payerUserId),
            Builders<BsonDocument>.Filter.Eq("ScopeType", scopeType),
            Builders<BsonDocument>.Filter.Eq("ScopePeerId", scopePeerId)
        );
        var update = Builders<BsonDocument>.Update
            .Set("OwnerUserId", ownerUserId)
            .Set("PayerUserId", payerUserId)
            .Set("ScopeType", scopeType)
            .Set("ScopePeerId", scopePeerId)
            .Inc("StarsAmount", starsAmount)
            .Inc("ChargedStars", chargedStars)
            .Inc("MessageCount", messageCount)
            .Set("UpdatedAt", DateTime.UtcNow.ToTimestamp());
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }

    private static bool IsSelfOrSavedMessagesPeer(long senderUserId, Peer toPeer)
    {
        if (toPeer.PeerType is PeerType.Self or PeerType.Empty)
        {
            return true;
        }

        return toPeer.PeerType == PeerType.User && toPeer.PeerId == senderUserId;
    }
}
