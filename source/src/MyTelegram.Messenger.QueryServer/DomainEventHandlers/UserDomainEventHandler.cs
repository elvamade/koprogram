using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

public class UserDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IEventBus eventBus,
    ILogger<UserDomainEventHandler> logger,
    IPhotoAppService photoAppService,
    ILayeredService<IPhotoConverter> photoLayeredConverter,
    ILayeredService<IAuthorizationConverter> layeredAuthorizationService,
    ILayeredService<IEmojiStatusConverter> emojiStatusLayeredService,
    IUserConverterService userConverterService)
    : DomainEventHandlerBase(objectMessageSender,
            commandBus,
            idGenerator,
            ackCacheService),
        ISubscribeSynchronousTo<UserAggregate, UserId, UserCreatedEvent>,
        ISubscribeSynchronousTo<UserAggregate, UserId, UserProfileUpdatedEvent>,
        ISubscribeSynchronousTo<UserAggregate, UserId, UserNameUpdatedEvent>,
        ISubscribeSynchronousTo<UserAggregate, UserId, UserProfilePhotoChangedEvent>,
        ISubscribeSynchronousTo<UserAggregate, UserId, UserProfilePhotoUploadedEvent>,
        ISubscribeSynchronousTo<UserAggregate, UserId, UserEmojiStatusUpdatedEvent>
{
    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "User created successfully, userId: {UserId}  phoneNumber: {PhoneNumber} firstName: {FirstName} lastName: {LastName}",
            domainEvent.AggregateEvent.UserId,
            domainEvent.AggregateEvent.PhoneNumber,
            domainEvent.AggregateEvent.FirstName,
            domainEvent.AggregateEvent.LastName
        );

        var userId = domainEvent.AggregateEvent.UserId;

        await eventBus.PublishAsync(new UserSignUpSuccessIntegrationEvent(
            domainEvent.AggregateEvent.RequestInfo.AuthKeyId,
            domainEvent.AggregateEvent.RequestInfo.PermAuthKeyId,
            userId));
        var user = await userConverterService.GetUserAsync(domainEvent.AggregateEvent.RequestInfo, userId, layer: domainEvent.AggregateEvent.RequestInfo.Layer);
        user.Self = true;
        var r = layeredAuthorizationService.GetConverter(domainEvent.AggregateEvent.RequestInfo.Layer)
            .CreateAuthorization(user);
        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo,
            r,
            domainEvent.AggregateEvent.UserId);
    }

    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserNameUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var userId = domainEvent.AggregateEvent.RequestInfo.UserId;
        if (userId == 0)
        {
            return;
        }
        var user = await userConverterService.GetUserAsync(domainEvent.AggregateEvent.RequestInfo, userId, layer: domainEvent.AggregateEvent.RequestInfo.Layer);

        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo, user);
    }

    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserProfilePhotoChangedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var userId = domainEvent.AggregateEvent.RequestInfo.UserId;
        var user = await userConverterService.GetUserAsync(domainEvent.AggregateEvent.RequestInfo, userId, layer: domainEvent.AggregateEvent.RequestInfo.Layer);
        var photoReadModel = await photoAppService.GetAsync(domainEvent.AggregateEvent.PhotoId);

        var photo = new MyTelegram.Schema.Photos.TPhoto
        {
            Photo = photoLayeredConverter.GetConverter(domainEvent.AggregateEvent.RequestInfo.Layer).ToPhoto(photoReadModel),
            Users = [user]
        };

        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo, photo);
    }

    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserProfilePhotoUploadedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var userId = domainEvent.AggregateEvent.RequestInfo.UserId;
        var user = await userConverterService.GetUserAsync(domainEvent.AggregateEvent.RequestInfo, userId, layer: domainEvent.AggregateEvent.RequestInfo.Layer);
        var photoReadModel = await photoAppService.GetAsync(domainEvent.AggregateEvent.PhotoId);

        var photo = new MyTelegram.Schema.Photos.TPhoto
        {
            Photo = photoLayeredConverter.GetConverter(domainEvent.AggregateEvent.RequestInfo.Layer).ToPhoto(photoReadModel),
            Users = [user]
        };

        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo, photo);
    }

    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserProfileUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var userId = domainEvent.AggregateEvent.RequestInfo.UserId;
        var user = await userConverterService.GetUserAsync(domainEvent.AggregateEvent.RequestInfo, userId, layer: domainEvent.AggregateEvent.RequestInfo.Layer);
        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo, user, domainEvent.AggregateEvent.UserId);
    }

    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserEmojiStatusUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo, new TBoolTrue());

        var requestLayer = domainEvent.AggregateEvent.RequestInfo.Layer;
        var emojiStatus = domainEvent.AggregateEvent.EmojiStatus == null
            ? new TEmojiStatusEmpty()
            : emojiStatusLayeredService.GetConverter(requestLayer).ToEmojiStatus(domainEvent.AggregateEvent.EmojiStatus);

        var updates = new TUpdates
        {
            Updates =
            [
                new TUpdateUserEmojiStatus
                {
                    UserId = domainEvent.AggregateEvent.UserId,
                    EmojiStatus = emojiStatus ?? new TEmojiStatusEmpty()
                },
                new TUpdateRecentEmojiStatuses()
            ],
            Users = [],
            Chats = [],
            Date = DateTime.UtcNow.ToTimestamp()
        };

        await PushUpdatesToPeerAsync(domainEvent.AggregateEvent.UserId.ToUserPeer(), updates);
    }
}
