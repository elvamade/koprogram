using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Users;
/// <summary>
/// Check whether we can write to the specified users, used to implement bulk checks for <a href="https://corefork.telegram.org/api/privacy#require-premium-for-new-non-contact-users">Premium-only messages »</a> and <a href="https://corefork.telegram.org/api/paid-messages">paid messages »</a>.For each input user, returns a <a href="https://corefork.telegram.org/type/RequirementToContact">RequirementToContact</a> constructor (at the same offset in the vector) containing requirements to contact them.
/// <para><c>See <a href="https://corefork.telegram.org/method/users.getRequirementsToContact"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetRequirementsToContactHandler(
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IPrivacyAppService privacyAppService,
    IContactAppService contactAppService,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Users.RequestGetRequirementsToContact, TVector<MyTelegram.Schema.IRequirementToContact>>
{
    private const string CollectionName = "paid_message_exceptions";

    protected override async Task<TVector<MyTelegram.Schema.IRequirementToContact>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Users.RequestGetRequirementsToContact obj)
    {
        var result = new TVector<MyTelegram.Schema.IRequirementToContact>();
        var exceptionsCollection = mongoDatabase.GetCollection<PaidMessageException>(CollectionName);

        foreach (var inputUser in obj.Id)
        {
            await accessHashHelper.CheckAccessHashAsync(input, inputUser);
            var targetPeer = peerHelper.GetPeer(inputUser, input.UserId);
            if (targetPeer.PeerType != PeerType.User || targetPeer.PeerId == input.UserId)
            {
                result.Add(new TRequirementToContactEmpty());
                continue;
            }

            var privacy = await privacyAppService.GetGlobalPrivacySettingsAsync(targetPeer.PeerId);
            var paidStars = privacy?.NoncontactPeersPaidStars;
            if (!paidStars.HasValue || paidStars.Value <= 0)
            {
                result.Add(new TRequirementToContactEmpty());
                continue;
            }

            var contactType = await contactAppService.GetContactTypeAsync(input.UserId, targetPeer.PeerId);
            var isExemptByContact = contactType is ContactType.Mutual or ContactType.ContactOfTargetUser;
            if (isExemptByContact)
            {
                result.Add(new TRequirementToContactEmpty());
                continue;
            }

            var exceptionFilter = Builders<PaidMessageException>.Filter.And(
                Builders<PaidMessageException>.Filter.Eq(x => x.ScopeType, null),
                Builders<PaidMessageException>.Filter.Eq(x => x.ScopePeerId, null),
                Builders<PaidMessageException>.Filter.Eq(x => x.OwnerUserId, targetPeer.PeerId),
                Builders<PaidMessageException>.Filter.Eq(x => x.TargetUserId, input.UserId)
            );
            var isExempt = await exceptionsCollection.Find(exceptionFilter).AnyAsync();
            result.Add(isExempt
                ? new TRequirementToContactEmpty()
                : new TRequirementToContactPaidMessages { StarsAmount = paidStars.Value });
        }

        return result;
    }

    private sealed class PaidMessageException
    {
        public PeerType? ScopeType { get; set; }
        public long? ScopePeerId { get; set; }
        public long OwnerUserId { get; set; }
        public long TargetUserId { get; set; }
    }
}
