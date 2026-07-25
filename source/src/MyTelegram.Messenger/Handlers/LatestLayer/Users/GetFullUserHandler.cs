using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.BotVerifications;
using MyTelegram.Messenger.Services.Impl;
using TUserFull = MyTelegram.Schema.Users.TUserFull;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Users;
/// <summary>
/// Returns extended user info by ID.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 USERNAME_OCCUPIED The provided username is already occupied.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/users.getFullUser"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetFullUserHandler(
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IUserConverterService userConverterService,
    ILayeredService<IPeerSettingsConverter> peerSettingsLayeredService,
    ILayeredService<IPeerNotifySettingsConverter> peerNotifySettingsLayeredService,
    IBlockCacheAppService blockCacheAppService,
    IAccessHashHelper accessHashHelper,
    IContactHelper contactHelper,
    IPeerSettingsAppService peerSettingsAppService,
    IPhotoAppService photoAppService,
    IUserAppService userAppService,
    IPrivacyAppService privacyAppService,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Users.RequestGetFullUser, MyTelegram.Schema.Users.IUserFull>
{
    protected override async Task<MyTelegram.Schema.Users.IUserFull> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Users.RequestGetFullUser obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Id);
        var selfUserId = input.UserId;
        var targetPeer = peerHelper.GetPeer(obj.Id, input.UserId);
        var targetUserId = targetPeer.PeerId;
        var userReadModel = await userAppService.GetAsync(targetPeer.PeerId);
        if (userReadModel == null)
        {
            //RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            throw new RpcException(RpcErrors.RpcErrors400.UserIdInvalid);
        }

        var photoReadModels = await photoAppService.GetPhotosAsync(userReadModel);
        var privacyReadModels = await privacyAppService.GetPrivacyListAsync(targetUserId);
        var contactReadModels = await queryProcessor.ProcessAsync(new GetContactListBySelfIdAndTargetUserIdQuery(input.UserId, targetUserId));
        var myContactReadModel = contactReadModels?.FirstOrDefault(p => p.SelfUserId == selfUserId && p.TargetUserId == targetUserId);
        var targetUserContactReadModel = contactReadModels?.FirstOrDefault(p => p.SelfUserId == targetUserId && p.TargetUserId == selfUserId);
        var peerNotifySettingsId = PeerNotifySettingsId.Create(selfUserId, targetPeer.PeerType, targetPeer.PeerId);
        var peerNotifySettingReadModel = await queryProcessor.ProcessAsync(new GetPeerNotifySettingsByIdQuery(peerNotifySettingsId.Value));
        var peerSettingReadModel = await peerSettingsAppService.GetPeerSettingsAsync(input.UserId, targetPeer.PeerId);
        var contactType = contactHelper.GetContactType(myContactReadModel, targetUserContactReadModel); // await contactAppService.GetContactTypeAsync(input.UserId, targetPeer.PeerId);
        var peerSettings = peerSettingsLayeredService.GetConverter(input.Layer).ToPeerSettings(input.UserId, targetPeer.PeerId, peerSettingReadModel, contactType);
        var peerNotifySettings = peerNotifySettingsLayeredService.GetConverter(input.Layer).ToPeerNotifySettings(peerNotifySettingReadModel?.NotifySettings ?? PeerNotifySettings.DefaultSettings);
        var userFull = userConverterService.ToUserFull(input, userReadModel, photoReadModels, contactReadModels, privacyReadModels, input.Layer);
        userFull.Settings = peerSettings;
        userFull.NotifySettings = peerNotifySettings;
        userFull.Blocked = await blockCacheAppService.IsBlockedAsync(input.UserId, targetPeer.PeerId);
        var user = userConverterService.ToUser(input, userReadModel, photoReadModels, myContactReadModel, targetUserContactReadModel, privacyReadModels, input.Layer);
        await SetPaidMessagesSettingsAsync(input.UserId, targetUserId, contactType, userReadModel, userFull, user);
        await SetPersonalChannelAsync(input, userReadModel, userFull);
        await SetCommonChatCountAsync(input, userReadModel, userFull);
        await SetStarGiftsCountAsync(targetUserId, userFull);
        await SetStarsRatingAsync(targetUserId, userFull);
        await SetBotVerificationAsync(targetUserId, userFull, user);
        return new TUserFull
        {
            Chats = [],
            FullUser = userFull,
            Users = new TVector<IUser>(user)
        };
    }

    private async Task SetStarGiftsCountAsync(long userId, IUserFull userFull)
    {
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        
        // Count ALL gifts owned by the user (for display_gifts_button)
        var allGiftsFilter = Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId);
        var totalCount = await savedGiftsCollection.CountDocumentsAsync(allGiftsFilter);

        if (totalCount > 0)
        {
            // Count saved (pinned to profile) gifts
            var savedFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                Builders<BsonDocument>.Filter.Eq("Saved", true)
            );
            var savedCount = await savedGiftsCollection.CountDocumentsAsync(savedFilter);

            // StargiftsCount shows the number of gifts visible on profile (saved ones)
            // But if user has any gifts, show the button
            userFull.StargiftsCount = (int)savedCount;
            userFull.DisplayGiftsButton = true;
        }
    }

    private async Task SetPaidMessagesSettingsAsync(long selfUserId, long targetUserId, ContactType contactType, IUserReadModel targetUserReadModel, IUserFull userFull, IUser user)
    {
        var paidStars = targetUserReadModel.GlobalPrivacySettings?.NoncontactPeersPaidStars;
        if (!paidStars.HasValue || paidStars.Value <= 0 || selfUserId == targetUserId)
        {
            return;
        }

        var exemptByContact = contactType is ContactType.Mutual or ContactType.ContactOfTargetUser;
        var isExempt = exemptByContact || await IsPaidMessageExceptionEnabledAsync(targetUserId, selfUserId);
        var actualStars = isExempt ? 0L : paidStars.Value;
        userFull.SendPaidMessagesStars = actualStars;

        if (userFull.Settings is MyTelegram.Schema.TPeerSettings peerSettings)
        {
            peerSettings.ChargePaidMessageStars = actualStars > 0 ? actualStars : null;
        }

        if (user is TUser layeredUser)
        {
            layeredUser.SendPaidMessagesStars = actualStars > 0 ? actualStars : null;
        }
    }

    private async Task<bool> IsPaidMessageExceptionEnabledAsync(long ownerUserId, long targetUserId)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>("paid_message_exceptions");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ScopeType", BsonNull.Value),
            Builders<BsonDocument>.Filter.Eq("ScopePeerId", BsonNull.Value),
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("TargetUserId", targetUserId)
        );
        return await collection.Find(filter).AnyAsync();
    }

    private async Task SetBotVerificationAsync(long userId, IUserFull userFull, IUser user)
    {
        var botVerification = await BotVerificationStore.GetBotVerificationAsync(mongoDatabase, PeerType.User, userId);
        if (botVerification == null)
        {
            return;
        }

        userFull.BotVerification = botVerification;
        if (user is TUser layeredUser)
        {
            layeredUser.BotVerificationIcon = botVerification.Icon;
        }
    }

    private async Task SetStarsRatingAsync(long userId, IUserFull userFull)
    {
        var ratingCollection = mongoDatabase.GetCollection<BsonDocument>("user_stars_rating");
        var doc = await ratingCollection.Find(Builders<BsonDocument>.Filter.Eq("UserId", userId)).FirstOrDefaultAsync();
        if (doc == null)
        {
            return;
        }

        var level = GetInt(doc, "Level");
        var currentLevelStars = GetLong(doc, "CurrentLevelStars");
        var stars = GetLong(doc, "Stars");
        var nextLevelStars = doc.Contains("NextLevelStars") && !doc["NextLevelStars"].IsBsonNull
            ? GetLong(doc, "NextLevelStars")
            : (long?)null;

        userFull.StarsRating = new TStarsRating
        {
            Level = level,
            CurrentLevelStars = currentLevelStars,
            Stars = stars,
            NextLevelStars = nextLevelStars
        };
    }

    private static int GetInt(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return 0;
        }

        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
        {
            return 0;
        }

        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    private async Task SetCommonChatCountAsync(IRequestInput input, IUserReadModel userReadModel, IUserFull userFull)
    {
        var count = await queryProcessor.ProcessAsync(new GetCommonChatCountQuery(input.UserId, userReadModel.UserId));
        userFull.CommonChatsCount = count;
    }

    private async Task SetPersonalChannelAsync(IRequestInput input, IUserReadModel userReadModel, IUserFull userFull)
    {
        if (userReadModel.PersonalChannelId.HasValue)
        {
            var channelTopMessageId = await queryProcessor.ProcessAsync(new GetChannelTopMessageIdQuery(userReadModel.PersonalChannelId.Value));
            if (channelTopMessageId.HasValue)
            {
                userFull.PersonalChannelId = userReadModel.PersonalChannelId;
                userFull.PersonalChannelMessage = channelTopMessageId.Value;
            }
        }
    }
}
