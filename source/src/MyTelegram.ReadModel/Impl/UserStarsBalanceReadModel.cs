namespace MyTelegram.ReadModel.Impl;

/// <summary>
/// Read model for user's Telegram Stars balance
/// MongoDB collection: eventflow-userstarsbalancereadmodel
/// </summary>
public class UserStarsBalanceReadModel : IUserStarsBalanceReadModel
{
    public virtual string Id { get; set; } = null!;
    public virtual long UserId { get; set; }
    public virtual long Balance { get; set; }
    public virtual DateTime LastUpdated { get; set; }
    public virtual long? Version { get; set; }
}
