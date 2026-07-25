namespace MyTelegram.Converters.Mappers.LatestLayer;

internal sealed class UserMapper
    : IObjectMapper<IUserReadModel, TUser>,
        ILayeredMapper,
        ITransientDependency
{
    private static readonly HashSet<long> ScamUserIds = [];

    public int Layer => Layers.LayerLatest;
    

    public TUser Map(IUserReadModel source)
    {
        return Map(source, new TUser());
    }

    public TUser Map(
        IUserReadModel source,
        TUser destination
    )
    {
        var isScamUser = ScamUserIds.Contains(source.UserId);
        var hasFrzLabel = source.Frozen || source.TestLabel;

        destination.Id = source.UserId;
        destination.Photo = new TUserProfilePhotoEmpty();
        destination.AccessHash = source.AccessHash;
        destination.Bot = source.Bot;
        destination.BotInfoVersion = source.BotInfoVersion;
        destination.Username = source.UserName;
        destination.Phone = source.PhoneNumber;
        destination.FirstName = source.FirstName;
        destination.LastName = source.LastName;
        destination.Verified = source.Verified;
        // FRZ label is a custom marker:
        // - Frozen users and TestLabel users both get FRZ marker
        // - account freeze behavior is still controlled only by source.Frozen in handlers
        destination.Restricted = hasFrzLabel;
        if (hasFrzLabel)
        {
            destination.RestrictionReason =
            [
                new TRestrictionReason
                {
                    Platform = "all",
                    Reason = "frozen",
                    Text = "FRZ"
                }
            ];
        }
        destination.Fake = false;
        destination.Scam = isScamUser;
        destination.Support = source.Support;
        destination.Premium = source.Premium;

        destination.Color = source.Color.ToPeerColor();
        destination.ProfileColor = source.ProfileColor.ToPeerColor();
        destination.ContactRequirePremium = source.GlobalPrivacySettings?.NewNoncontactPeersRequirePremium ?? false;
        destination.SendPaidMessagesStars = source.GlobalPrivacySettings?.NoncontactPeersPaidStars;
        destination.BotHasMainApp = source.BotHasMainApp;
        destination.BotActiveUsers = source.BotActiveUsers;
        destination.BotVerificationIcon = source.BotVerificationIcon;

        return destination;
    }
}
