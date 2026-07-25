using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.StarsTransactions;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class UpgradeStarGiftHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IIdGenerator idGenerator,
    IUserConverterService userConverterService,
    IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestUpgradeStarGift, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestUpgradeStarGift obj)
    {
        return await ProcessUpgradeAsync(
            mongoDatabase,
            peerHelper,
            idGenerator,
            userConverterService,
            objectMessageSender,
            input,
            obj.Stargift,
            obj.KeepOriginalDetails,
            chargeUpgrade: false);
    }

    internal static async Task<TUpdates> ProcessUpgradeAsync(
        IMongoDatabase mongoDatabase,
        IPeerHelper peerHelper,
        IIdGenerator idGenerator,
        IUserConverterService userConverterService,
        IObjectMessageSender objectMessageSender,
        IRequestInput input,
        object stargift,
        bool keepOriginalDetails,
        bool chargeUpgrade)
    {
        var userId = input.UserId;
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var documentsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        FilterDefinition<BsonDocument> filter;
        BsonDocument? savedGiftDoc = null;

        switch (stargift)
        {
            case TInputSavedStarGiftUser userGift:
                filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                    Builders<BsonDocument>.Filter.Eq("MsgId", userGift.MsgId)
                );
                savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
                if (savedGiftDoc == null)
                {
                    filter = Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                        Builders<BsonDocument>.Filter.Eq("SavedId", (long)userGift.MsgId)
                    );
                    savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
                }
                break;

            case TInputSavedStarGiftChat chatGift:
                var chatPeer = peerHelper.GetPeer(chatGift.Peer, userId);
                if (chatGift.SavedId == 0)
                    RpcErrors.RpcErrors400.SavedIdEmpty.ThrowRpcError();
                filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", chatPeer.PeerId),
                    Builders<BsonDocument>.Filter.Eq("SavedId", chatGift.SavedId)
                );
                savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
                break;

            case TInputSavedStarGiftSlug slugGift:
                filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
                    Builders<BsonDocument>.Filter.Eq("Slug", slugGift.Slug)
                );
                savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
                break;

            default:
                RpcErrors.RpcErrors400.StargiftPeerInvalid.ThrowRpcError();
                return new TUpdates();
        }

        if (savedGiftDoc == null)
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();

        var ownerUserId = GetLong(savedGiftDoc!, "OwnerUserId");
        if (ownerUserId != userId)
            RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();

        if (savedGiftDoc!.GetValue("Converted", false).AsBoolean)
            RpcErrors.RpcErrors400.StargiftAlreadyConverted.ThrowRpcError();

        var giftId = GetLong(savedGiftDoc, "GiftId");
        var giftDoc = await giftsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).FirstOrDefaultAsync();

        if (giftDoc == null)
            RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();

        var isAlreadyUpgraded = savedGiftDoc.GetValue("Upgraded", false).AsBoolean;
        var craftRequiredCount = GetCraftRequiredCount(giftDoc!);
        if (isAlreadyUpgraded && craftRequiredCount <= 1)
            RpcErrors.RpcErrors400.StargiftAlreadyUpgraded.ThrowRpcError();

        await StarGiftUpgradeStateHelper.SyncCanUpgradeAsync(savedGiftsCollection, savedGiftDoc, giftDoc);

        if (!isAlreadyUpgraded && !savedGiftDoc.GetValue("CanUpgrade", false).AsBoolean)
            RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();

        var craftBurnSavedIds = await ResolveCraftBurnSavedIdsAsync(
            savedGiftsCollection,
            savedGiftDoc,
            userId,
            giftId,
            craftRequiredCount,
            isAlreadyUpgraded);

        var upgradeStars = GetNullableLong(savedGiftDoc, "UpgradeStars");
        if (!upgradeStars.HasValue)
        {
            upgradeStars = GetNullableLong(giftDoc, "UpgradeStars");
        }

        // Craft upgrades consume multiple gifts and don't require star payment.
        if (craftRequiredCount > 1)
        {
            upgradeStars = 0;
        }

        if (!upgradeStars.HasValue)
            RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();

        var upgradeAlreadyPrepaid = StarGiftUpgradeStateHelper.IsUpgradeAlreadyPrepaid(savedGiftDoc);
        long? chargedUpgradeStars = null;
        long? newBalance = null;

        if (upgradeStars.Value > 0)
        {
            if (upgradeAlreadyPrepaid)
            {
                if (chargeUpgrade)
                {
                    RpcErrors.RpcErrors400.NoPaymentNeeded.ThrowRpcError();
                }
            }
            else
            {
                if (!chargeUpgrade)
                {
                    RpcErrors.RpcErrors400.PaymentRequired.ThrowRpcError();
                }

                var balanceCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userstarsbalancereadmodel");
                var balanceFilter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
                var balanceDoc = await balanceCollection.Find(balanceFilter).FirstOrDefaultAsync();

                long currentBalance = 0;
                if (balanceDoc != null && balanceDoc.Contains("Balance"))
                {
                    currentBalance = balanceDoc["Balance"].IsInt64 ? balanceDoc["Balance"].AsInt64 : balanceDoc["Balance"].AsInt32;
                }

                if (currentBalance < upgradeStars.Value)
                {
                    RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
                }

                newBalance = currentBalance - upgradeStars.Value;
                if (balanceDoc != null)
                {
                    var updateBalance = Builders<BsonDocument>.Update
                        .Set("Balance", newBalance.Value)
                        .Set("LastUpdated", DateTime.UtcNow);
                    await balanceCollection.UpdateOneAsync(balanceFilter, updateBalance);
                }
                else
                {
                    var newBalanceDoc = new BsonDocument
                    {
                        { "UserId", userId },
                        { "Balance", newBalance.Value },
                        { "LastUpdated", DateTime.UtcNow }
                    };
                    await balanceCollection.InsertOneAsync(newBalanceDoc);
                }

                chargedUpgradeStars = upgradeStars.Value;
            }
        }
        else if (chargeUpgrade)
        {
            RpcErrors.RpcErrors400.NoPaymentNeeded.ThrowRpcError();
        }

        if (chargedUpgradeStars.HasValue)
        {
            var upgradeTitle = GetNullableString(giftDoc!, "Title");
            var upgradeTransaction = StarsTransactionStore.CreateTransactionDocument(
                userId,
                -chargedUpgradeStars.Value,
                now,
                (int)PeerType.User,
                userId,
                giftId: giftId,
                title: upgradeTitle ?? "Star Gift",
                description: "Gift upgrade",
                stargiftUpgrade: true
            );
            await StarsTransactionStore.GetCollection(mongoDatabase).InsertOneAsync(upgradeTransaction);
        }

        var giftNum = await GetNextGiftNumAsync(mongoDatabase, giftId);
        var giftTitle = GetNullableString(giftDoc!, "Title") ?? "Gift";
        var slug = GenerateUniqueSlug(giftTitle, giftNum);

        var (modelAttr, patternAttr, backdropAttr) = await SelectRandomAttributesAsync(mongoDatabase, giftId, giftDoc, documentsCollection);

        var attributes = new TVector<IStarGiftAttribute>();
        if (modelAttr != null) attributes.Add(modelAttr);
        if (patternAttr != null) attributes.Add(patternAttr);
        if (backdropAttr != null) attributes.Add(backdropAttr);

        if (keepOriginalDetails)
        {
            var origFromUserId = GetNullableLong(savedGiftDoc, "FromUserId");
            var giftDate = GetInt(savedGiftDoc, "Date");
            var message = GetNullableString(savedGiftDoc, "Message");

            var originalDetails = new TStarGiftAttributeOriginalDetails
            {
                RecipientId = new TPeerUser { UserId = ownerUserId },
                Date = giftDate
            };
            if (origFromUserId.HasValue)
                originalDetails.SenderId = new TPeerUser { UserId = origFromUserId.Value };
            if (!string.IsNullOrEmpty(message))
                originalDetails.Message = new TTextWithEntities { Text = message, Entities = [] };
            attributes.Add(originalDetails);
        }

        var availabilityTotal = GetNullableInt(giftDoc!, "AvailabilityTotal") ?? 0;

        var updateGift = Builders<BsonDocument>.Update
            .Set("Upgraded", true)
            .Set("UpgradedDate", now)
            .Set("Slug", slug)
            .Set("GiftNum", giftNum)
            .Set("CanUpgrade", false)
            .Set("PrepaidUpgradeHash", BsonNull.Value)
            .Set("PrepaidUpgrade", false)
            .Set("UpgradeSeparate", false)
            .Set("UpgradeStars", BsonNull.Value)
            .Set("PrepaidDate", BsonNull.Value)
            .Set("PrepaidKeepOriginalDetails", BsonNull.Value)
            .Set("CanTransferAt", now)
            .Set("CanResellAt", now)
            .Set("CanExportAt", now + 86400 * 30)
            .Set("Crafted", craftBurnSavedIds.Count > 0)
            .Set("CraftBurnedCount", craftBurnSavedIds.Count)
            .Set("KeepOriginalDetails", keepOriginalDetails);

        if (modelAttr != null)
            updateGift = updateGift.Set("ModelName", modelAttr.Name);
        if (patternAttr != null)
            updateGift = updateGift.Set("PatternName", patternAttr.Name);
        if (backdropAttr != null)
            updateGift = updateGift
                .Set("BackdropId", backdropAttr.BackdropId)
                .Set("BackdropName", backdropAttr.Name)
                .Set("BackdropCenterColor", backdropAttr.CenterColor)
                .Set("BackdropEdgeColor", backdropAttr.EdgeColor)
                .Set("BackdropPatternColor", backdropAttr.PatternColor)
                .Set("BackdropTextColor", backdropAttr.TextColor);

        await savedGiftsCollection.UpdateOneAsync(filter, updateGift);

        if (craftBurnSavedIds.Count > 0)
        {
            var burnFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
                Builders<BsonDocument>.Filter.In("SavedId", craftBurnSavedIds)
            );
            var burnUpdate = Builders<BsonDocument>.Update
                .Set("Converted", true)
                .Set("ConvertedDate", now)
                .Set("BurnedByCraft", true)
                .Set("BurnedByCraftSavedId", GetLong(savedGiftDoc, "SavedId"))
                .Set("CanUpgrade", false)
                .Set("PrepaidUpgradeHash", BsonNull.Value)
                .Set("PrepaidUpgrade", false)
                .Set("UpgradeSeparate", false)
                .Set("UpgradeStars", BsonNull.Value)
                .Set("PrepaidDate", BsonNull.Value)
                .Set("PrepaidKeepOriginalDetails", BsonNull.Value);
            await savedGiftsCollection.UpdateManyAsync(burnFilter, burnUpdate);
        }

        var stickerId = GetLong(giftDoc, "StickerId");
        var stickerDoc = await documentsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("DocumentId", stickerId)
        ).FirstOrDefaultAsync();

        IDocument sticker = stickerDoc != null ? ConvertDocument(stickerDoc) : new TDocumentEmpty { Id = stickerId };

        var msgId = GetInt(savedGiftDoc, "MsgId");
        var giftFromUserId = GetNullableLong(savedGiftDoc, "FromUserId");

        if (msgId > 0 && giftFromUserId.HasValue && giftFromUserId.Value != ownerUserId)
        {
            var starsValue = GetNullableLong(giftDoc, "Stars") ?? 0;
            var convertStarsValue = GetNullableLong(giftDoc, "ConvertStars") ?? 0;
            var giftForAction = new TStarGift
            {
                Id = giftId,
                Limited = giftDoc.GetValue("Limited", false).AsBoolean,
                SoldOut = giftDoc.GetValue("SoldOut", false).AsBoolean,
                Birthday = giftDoc.GetValue("Birthday", false).AsBoolean,
                Sticker = sticker,
                Stars = starsValue,
                ConvertStars = convertStarsValue,
                AvailabilityRemains = GetNullableInt(giftDoc, "AvailabilityRemains"),
                AvailabilityTotal = GetNullableInt(giftDoc, "AvailabilityTotal"),
                UpgradeStars = GetNullableLong(giftDoc, "UpgradeStars"),
                Title = GetNullableString(giftDoc, "Title")
            };

            var messagesCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
            var senderSavedId = GetLong(savedGiftDoc, "SavedId");
            var senderMsgFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", giftFromUserId.Value),
                Builders<BsonDocument>.Filter.Eq("ToPeerId", ownerUserId),
                Builders<BsonDocument>.Filter.Eq("MessageId", msgId),
                Builders<BsonDocument>.Filter.Eq("MessageActionType", (int)MessageActionType.StarGift)
            );
            if (senderSavedId > 0)
            {
                senderMsgFilter = Builders<BsonDocument>.Filter.And(
                    senderMsgFilter,
                    Builders<BsonDocument>.Filter.Eq("SavedId", senderSavedId)
                );
            }

            var senderMsgDoc = await messagesCollection.Find(senderMsgFilter).FirstOrDefaultAsync();

            if (senderMsgDoc != null)
            {
                var senderMsgId = GetInt(senderMsgDoc, "MessageId");

                var senderUpdatedAction = new TMessageActionStarGift
                {
                    NameHidden = savedGiftDoc.GetValue("NameHidden", false).AsBoolean,
                    Saved = savedGiftDoc.GetValue("Saved", false).AsBoolean,
                    Converted = false,
                    Upgraded = true,
                    Refunded = false,
                    CanUpgrade = false,
                    Gift = giftForAction,
                    ConvertStars = GetNullableLong(savedGiftDoc, "ConvertStars") ?? 0,
                    UpgradeStars = GetNullableLong(savedGiftDoc, "UpgradeStars"),
                    FromId = new TPeerUser { UserId = giftFromUserId.Value },
                    Peer = new TPeerUser { UserId = ownerUserId },
                    SavedId = GetLong(savedGiftDoc, "SavedId"),
                    UpgradeMsgId = msgId
                };

                var senderPts = await idGenerator.NextIdAsync(IdType.Pts, giftFromUserId.Value);
                var editMessageUpdate = new TUpdateEditMessage
                {
                    Message = new TMessageService
                    {
                        Id = senderMsgId,
                        FromId = new TPeerUser { UserId = giftFromUserId.Value },
                        PeerId = new TPeerUser { UserId = ownerUserId },
                        Date = GetInt(senderMsgDoc, "Date"),
                        Out = true,
                        Action = senderUpdatedAction
                    },
                    Pts = senderPts,
                    PtsCount = 1
                };

                var senderUserList = await userConverterService.GetUserListAsync(input, [giftFromUserId.Value, ownerUserId], true, true, input.Layer);
                var senderUsers = new TVector<IUser>();
                foreach (var u in senderUserList) senderUsers.Add(u);

                var senderUpdates = new TUpdates
                {
                    Updates = [editMessageUpdate],
                    Users = senderUsers,
                    Chats = [],
                    Date = now,
                    Seq = 0
                };

                await objectMessageSender.PushMessageToPeerAsync(
                    new Peer(PeerType.User, giftFromUserId.Value),
                    senderUpdates,
                    excludeUserId: userId,
                    pts: senderPts
                );
            }
        }

        var countersCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_counters");
        var counterDoc = await countersCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).FirstOrDefaultAsync();
        var availabilityIssued = counterDoc != null ? GetInt(counterDoc, "UpgradedCount") : giftNum;

        var uniqueGift = new TStarGiftUnique
        {
            Id = GetLong(savedGiftDoc, "SavedId"),
            GiftId = giftId,
            Title = GetNullableString(giftDoc, "Title") ?? "Collectible Gift",
            Slug = slug,
            Num = giftNum,
            OwnerId = new TPeerUser { UserId = ownerUserId },
            Attributes = attributes,
            AvailabilityIssued = availabilityIssued,
            AvailabilityTotal = availabilityTotal,
            ResellAmount = StarGiftResaleHelper.BuildResellAmount(savedGiftDoc),
            OfferMinStars = ResolveOfferMinStars(savedGiftDoc, giftDoc)
        };

        var messageAction = new TMessageActionStarGiftUnique
        {
            Upgrade = true,
            Transferred = false,
            Saved = savedGiftDoc.GetValue("Saved", false).AsBoolean,
            Refunded = false,
            Gift = uniqueGift,
            FromId = new TPeerUser { UserId = userId },
            Peer = new TPeerUser { UserId = ownerUserId },
            SavedId = GetLong(savedGiftDoc, "SavedId")
        };

        if (msgId > 0 && giftFromUserId.HasValue && giftFromUserId.Value != ownerUserId)
        {
            var senderUpgradeMessage = new TMessageService
            {
                Id = await idGenerator.NextIdAsync(IdType.MessageId, giftFromUserId.Value),
                FromId = new TPeerUser { UserId = ownerUserId },
                PeerId = new TPeerUser { UserId = ownerUserId },
                Date = now,
                Out = false,
                Action = new TMessageActionStarGiftUnique
                {
                    Upgrade = true,
                    Transferred = false,
                    Saved = savedGiftDoc.GetValue("Saved", false).AsBoolean,
                    Refunded = false,
                    Gift = uniqueGift,
                    FromId = new TPeerUser { UserId = ownerUserId },
                    Peer = new TPeerUser { UserId = ownerUserId },
                    SavedId = GetLong(savedGiftDoc, "SavedId")
                }
            };

            var senderUpgradePts = await idGenerator.NextIdAsync(IdType.Pts, giftFromUserId.Value);
            var senderUpgradeUserList = await userConverterService.GetUserListAsync(input, [giftFromUserId.Value, ownerUserId], true, true, input.Layer);
            var senderUpgradeUsers = new TVector<IUser>();
            foreach (var user in senderUpgradeUserList) senderUpgradeUsers.Add(user);

            var senderUpgradeUpdates = new TUpdates
            {
                Updates =
                [
                    new TUpdateNewMessage
                    {
                        Message = senderUpgradeMessage,
                        Pts = senderUpgradePts,
                        PtsCount = 1
                    }
                ],
                Users = senderUpgradeUsers,
                Chats = [],
                Date = now,
                Seq = 0
            };

            await objectMessageSender.PushMessageToPeerAsync(
                new Peer(PeerType.User, giftFromUserId.Value),
                senderUpgradeUpdates,
                excludeUserId: userId,
                pts: senderUpgradePts
            );
        }

        var userList = await userConverterService.GetUserListAsync(input, [userId], true, true, input.Layer);
        var users = new TVector<IUser>();
        foreach (var user in userList) users.Add(user);

        var updateNewMessage = new TUpdateNewMessage
        {
            Message = new TMessageService
            {
                Id = GetInt(savedGiftDoc, "MsgId"),
                FromId = new TPeerUser { UserId = userId },
                PeerId = new TPeerUser { UserId = ownerUserId },
                Date = now,
                Out = true,
                Action = messageAction
            },
            Pts = await idGenerator.NextIdAsync(IdType.Pts, userId),
            PtsCount = 1
        };

        var updates = new TVector<IUpdate> { updateNewMessage };
        if (newBalance.HasValue)
        {
            updates.Add(new TUpdateStarsBalance
            {
                Balance = new TStarsAmount { Amount = newBalance.Value }
            });
        }

        return new TUpdates { Updates = updates, Users = users, Chats = [], Date = now, Seq = 0 };
    }

    private static string GenerateUniqueSlug(string giftTitle, int giftNum)
    {
        // Convert title to slug format: "Astral Shard" -> "astral-shard-11"
        var slugBase = giftTitle
            .ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");
        
        // Remove any non-alphanumeric characters except hyphens
        slugBase = System.Text.RegularExpressions.Regex.Replace(slugBase, @"[^a-z0-9\-]", "");
        
        // Remove multiple consecutive hyphens
        slugBase = System.Text.RegularExpressions.Regex.Replace(slugBase, @"-+", "-");
        
        // Trim hyphens from start and end
        slugBase = slugBase.Trim('-');
        
        return $"{slugBase}-{giftNum}";
    }

    private static async Task<int> GetNextGiftNumAsync(IMongoDatabase mongoDatabase, long giftId)
    {
        var countersCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_counters");
        var filter = Builders<BsonDocument>.Filter.Eq("GiftId", giftId);
        var update = Builders<BsonDocument>.Update.Inc("UpgradedCount", 1).SetOnInsert("GiftId", giftId);
        var options = new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After };
        var result = await countersCollection.FindOneAndUpdateAsync(filter, update, options);
        return GetInt(result, "UpgradedCount");
    }

    private static async Task<(TStarGiftAttributeModel?, TStarGiftAttributePattern?, TStarGiftAttributeBackdrop?)> SelectRandomAttributesAsync(
        IMongoDatabase mongoDatabase,
        long giftId,
        BsonDocument giftDoc,
        IMongoCollection<BsonDocument> documentsCollection)
    {
        TStarGiftAttributeModel? modelAttr = null;
        TStarGiftAttributePattern? patternAttr = null;
        TStarGiftAttributeBackdrop? backdropAttr = null;

        var modelsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_models");
        var models = await modelsCollection.Find(Builders<BsonDocument>.Filter.Eq("GiftId", giftId)).ToListAsync();
        if (models.Count > 0)
        {
            var randomModel = SelectModel(models, giftDoc);
            var modelDocId = GetLong(randomModel, "DocumentId");
            var modelStickerDoc = await documentsCollection.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", modelDocId)).FirstOrDefaultAsync();
            modelAttr = new TStarGiftAttributeModel
            {
                Name = GetString(randomModel, "Name") ?? "Model",
                Document = modelStickerDoc != null ? ConvertDocument(modelStickerDoc) : new TDocumentEmpty { Id = modelDocId },
                RarityPermille = GetInt(randomModel, "RarityPermille")
            };
        }

        var patternsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_patterns");
        var patterns = await patternsCollection.Find(Builders<BsonDocument>.Filter.Eq("GiftId", giftId)).ToListAsync();
        if (patterns.Count > 0)
        {
            var randomPattern = SelectByRarity(patterns);
            var patternDocId = GetLong(randomPattern, "DocumentId");
            var patternStickerDoc = await documentsCollection.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", patternDocId)).FirstOrDefaultAsync();
            patternAttr = new TStarGiftAttributePattern
            {
                Name = GetString(randomPattern, "Name") ?? "Pattern",
                Document = patternStickerDoc != null ? ConvertDocument(patternStickerDoc) : new TDocumentEmpty { Id = patternDocId },
                RarityPermille = GetInt(randomPattern, "RarityPermille")
            };
        }

        var backdropsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_backdrops");
        var backdrops = await backdropsCollection.Find(Builders<BsonDocument>.Filter.Eq("GiftId", giftId)).ToListAsync();
        if (backdrops.Count > 0)
        {
            var randomBackdrop = SelectByRarity(backdrops);
            backdropAttr = new TStarGiftAttributeBackdrop
            {
                Name = GetString(randomBackdrop, "Name") ?? "Backdrop",
                BackdropId = GetInt(randomBackdrop, "BackdropId"),
                CenterColor = GetInt(randomBackdrop, "CenterColor"),
                EdgeColor = GetInt(randomBackdrop, "EdgeColor"),
                PatternColor = GetInt(randomBackdrop, "PatternColor"),
                TextColor = GetInt(randomBackdrop, "TextColor"),
                RarityPermille = GetInt(randomBackdrop, "RarityPermille")
            };
        }

        return (modelAttr, patternAttr, backdropAttr);
    }

    private static BsonDocument SelectModel(List<BsonDocument> models, BsonDocument giftDoc)
    {
        var craftModelName = GetNullableString(giftDoc, "CraftModelName");
        if (!string.IsNullOrWhiteSpace(craftModelName))
        {
            var byName = models.FirstOrDefault(x =>
                string.Equals(GetNullableString(x, "Name"), craftModelName, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
            {
                return byName;
            }
        }

        var craftModel = GetNullableInt(giftDoc, "CraftModel");
        if (craftModel.HasValue && craftModel.Value > 0)
        {
            var byModelId = models.FirstOrDefault(x =>
                GetNullableInt(x, "ModelId") == craftModel.Value ||
                GetNullableInt(x, "Id") == craftModel.Value ||
                string.Equals(GetNullableString(x, "Name"), craftModel.Value.ToString(), StringComparison.OrdinalIgnoreCase));
            if (byModelId != null)
            {
                return byModelId;
            }

            var orderedModels = models
                .OrderBy(x => GetLong(x, "DocumentId"))
                .ToList();
            var index = craftModel.Value - 1;
            if (index >= 0 && index < orderedModels.Count)
            {
                return orderedModels[index];
            }
        }

        return SelectByRarity(models);
    }

    private static int GetCraftRequiredCount(BsonDocument giftDoc)
    {
        var count = GetNullableInt(giftDoc, "CraftRequiredCount") ?? 1;
        return Math.Max(1, count);
    }

    private static async Task<List<long>> ResolveCraftBurnSavedIdsAsync(
        IMongoCollection<BsonDocument> savedGiftsCollection,
        BsonDocument baseSavedGiftDoc,
        long ownerUserId,
        long giftId,
        int craftRequiredCount,
        bool baseIsUpgraded)
    {
        if (craftRequiredCount <= 1)
        {
            return [];
        }

        var baseSavedId = GetLong(baseSavedGiftDoc, "SavedId");
        if (baseSavedId == 0)
        {
            RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();
        }

        var candidatesFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
            baseIsUpgraded
                ? Builders<BsonDocument>.Filter.Eq("Upgraded", true)
                : Builders<BsonDocument>.Filter.Ne("Upgraded", true),
            Builders<BsonDocument>.Filter.Ne("Converted", true),
            Builders<BsonDocument>.Filter.Ne("Refunded", true)
        );

        var candidateDocs = await savedGiftsCollection.Find(candidatesFilter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("Date").Ascending("SavedId"))
            .ToListAsync();

        var candidateIds = candidateDocs
            .Select(x => GetLong(x, "SavedId"))
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        if (!candidateIds.Contains(baseSavedId))
        {
            RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();
        }

        var selectedIds = new List<long> { baseSavedId };
        foreach (var candidateId in candidateIds)
        {
            if (candidateId == baseSavedId)
            {
                continue;
            }

            selectedIds.Add(candidateId);
            if (selectedIds.Count == craftRequiredCount)
            {
                break;
            }
        }

        if (selectedIds.Count < craftRequiredCount)
        {
            RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();
        }

        return selectedIds.Skip(1).ToList();
    }

    private static BsonDocument SelectByRarity(List<BsonDocument> items)
    {
        var totalWeight = items.Sum(i => Math.Max(1, GetInt(i, "RarityPermille")));
        var randomValue = Random.Shared.Next(totalWeight);
        var currentWeight = 0;
        foreach (var item in items)
        {
            currentWeight += Math.Max(1, GetInt(item, "RarityPermille"));
            if (randomValue < currentWeight) return item;
        }
        return items[^1];
    }

    private static IDocument ConvertDocument(BsonDocument doc) => new TDocument
    {
        Id = GetLong(doc, "DocumentId"), AccessHash = GetLong(doc, "AccessHash"),
        Date = doc["Date"].AsInt32, MimeType = doc["MimeType"].AsString,
        Size = GetLong(doc, "Size"), DcId = doc["DcId"].AsInt32,
        FileReference = doc.Contains("FileReference") && !doc["FileReference"].IsBsonNull ? doc["FileReference"].AsByteArray : [],
        Attributes = []
    };

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    private static int GetInt(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }

    private static long? GetNullableLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    private static int? GetNullableInt(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }

    private static string? GetNullableString(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].AsString;
    }

    private static string? GetString(BsonDocument doc, string field) => GetNullableString(doc, field);

    private static int? ResolveOfferMinStars(BsonDocument savedGiftDoc, BsonDocument giftDoc)
    {
        var minOffer = GetNullableLong(savedGiftDoc, "OfferMinStars")
            ?? GetNullableLong(giftDoc, "ResellMinStars")
            ?? GetNullableLong(giftDoc, "Stars")
            ?? 1;
        if (minOffer <= 0)
        {
            minOffer = 1;
        }

        return minOffer > int.MaxValue ? int.MaxValue : (int)minOffer;
    }
}
