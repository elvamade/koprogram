namespace MyTelegram.ReadModel.Interfaces;

/// <summary>
/// ReadModel for Star Gifts - represents a gift that can be sent to users
/// </summary>
public interface IStarGiftReadModel : IReadModel
{
    /// <summary>
    /// Unique identifier of the gift
    /// </summary>
    long GiftId { get; }

    /// <summary>
    /// Whether this is a limited-supply gift
    /// </summary>
    bool Limited { get; }

    /// <summary>
    /// Whether this gift sold out and cannot be bought anymore
    /// </summary>
    bool SoldOut { get; }

    /// <summary>
    /// Whether this is a birthday-themed gift
    /// </summary>
    bool Birthday { get; }

    /// <summary>
    /// This gift can only be bought by users with a Premium subscription
    /// </summary>
    bool RequirePremium { get; }

    /// <summary>
    /// If set, the maximum number of gifts of this type that can be owned by a single user is limited
    /// </summary>
    bool LimitedPerUser { get; }

    /// <summary>
    /// Document ID of the sticker that represents the gift
    /// </summary>
    long StickerId { get; }

    /// <summary>
    /// Price of the gift in Telegram Stars
    /// </summary>
    long Stars { get; }

    /// <summary>
    /// For limited-supply gifts: the remaining number of gifts that may be bought
    /// </summary>
    int? AvailabilityRemains { get; }

    /// <summary>
    /// For limited-supply gifts: the total number of gifts that was available in the initial supply
    /// </summary>
    int? AvailabilityTotal { get; }

    /// <summary>
    /// The total number of upgraded collectible gifts of this type currently on resale
    /// </summary>
    long? AvailabilityResale { get; }

    /// <summary>
    /// The receiver of this gift may convert it to this many Telegram Stars
    /// </summary>
    long ConvertStars { get; }

    /// <summary>
    /// For sold out gifts only: when was the gift first bought (Unix timestamp)
    /// </summary>
    int? FirstSaleDate { get; }

    /// <summary>
    /// For sold out gifts only: when was the gift last bought (Unix timestamp)
    /// </summary>
    int? LastSaleDate { get; }

    /// <summary>
    /// The number of Telegram Stars the user can pay to convert the gift into a collectible gift
    /// </summary>
    long? UpgradeStars { get; }

    /// <summary>
    /// The minimum price in Stars for gifts of this type currently on resale
    /// </summary>
    long? ResellMinStars { get; }

    /// <summary>
    /// Title of the gift
    /// </summary>
    string? Title { get; }

    /// <summary>
    /// This gift was released by the specified peer (user/channel ID)
    /// </summary>
    long? ReleasedByPeerId { get; }

    /// <summary>
    /// Type of peer that released the gift (1 = User, 2 = Chat, 3 = Channel)
    /// </summary>
    int? ReleasedByPeerType { get; }

    /// <summary>
    /// Maximum number of gifts of this type that can be owned by any user
    /// </summary>
    int? PerUserTotal { get; }

    /// <summary>
    /// Remaining number of gifts of this type that can be owned by the current user
    /// </summary>
    int? PerUserRemains { get; }

    /// <summary>
    /// If set, the specified gift possibly cannot be sent until the specified date (Unix timestamp)
    /// </summary>
    int? LockedUntilDate { get; }

    /// <summary>
    /// Number of upgrade variants available
    /// </summary>
    int? UpgradeVariants { get; }

    /// <summary>
    /// Whether peer color is available for this gift
    /// </summary>
    bool PeerColorAvailable { get; }

    /// <summary>
    /// Whether this gift is available via auction
    /// </summary>
    bool Auction { get; }

    /// <summary>
    /// Auction slug identifier for auction gifts
    /// </summary>
    string? AuctionSlug { get; }

    /// <summary>
    /// Number of gifts distributed per auction round
    /// </summary>
    int? GiftsPerRound { get; }

    /// <summary>
    /// Unix timestamp when the auction starts
    /// </summary>
    int? AuctionStartDate { get; }

    /// <summary>
    /// Background center color (RGB)
    /// </summary>
    int? BackgroundCenterColor { get; }

    /// <summary>
    /// Background edge color (RGB)
    /// </summary>
    int? BackgroundEdgeColor { get; }

    /// <summary>
    /// Background text color (RGB)
    /// </summary>
    int? BackgroundTextColor { get; }
}
