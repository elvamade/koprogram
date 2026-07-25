namespace MyTelegram.ReadModel.Impl;

/// <summary>
/// ReadModel implementation for Star Gifts stored in MongoDB
/// </summary>
public class StarGiftReadModel : IStarGiftReadModel
{
    public string Id { get; set; } = null!;
    public long GiftId { get; set; }
    public bool Limited { get; set; }
    public bool SoldOut { get; set; }
    public bool Birthday { get; set; }
    public bool RequirePremium { get; set; }
    public bool LimitedPerUser { get; set; }
    public long StickerId { get; set; }
    public long Stars { get; set; }
    public int? AvailabilityRemains { get; set; }
    public int? AvailabilityTotal { get; set; }
    public long? AvailabilityResale { get; set; }
    public long ConvertStars { get; set; }
    public int? FirstSaleDate { get; set; }
    public int? LastSaleDate { get; set; }
    public long? UpgradeStars { get; set; }
    public long? ResellMinStars { get; set; }
    public string? Title { get; set; }
    public long? ReleasedByPeerId { get; set; }
    public int? ReleasedByPeerType { get; set; }
    public int? PerUserTotal { get; set; }
    public int? PerUserRemains { get; set; }
    public int? LockedUntilDate { get; set; }
    public int? UpgradeVariants { get; set; }
    public bool PeerColorAvailable { get; set; }
    public bool Auction { get; set; }
    public string? AuctionSlug { get; set; }
    public int? GiftsPerRound { get; set; }
    public int? AuctionStartDate { get; set; }
    public int? BackgroundCenterColor { get; set; }
    public int? BackgroundEdgeColor { get; set; }
    public int? BackgroundTextColor { get; set; }
    public long? Version { get; set; }
}
