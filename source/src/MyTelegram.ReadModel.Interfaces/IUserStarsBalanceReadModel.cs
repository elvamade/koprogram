namespace MyTelegram.ReadModel.Interfaces;

/// <summary>
/// Read model for user's Telegram Stars balance
/// </summary>
public interface IUserStarsBalanceReadModel : IReadModel
{
    string Id { get; }
    long UserId { get; }
    long Balance { get; }
    DateTime LastUpdated { get; }
}
