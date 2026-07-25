# Star Gifts MongoDB Schema

## Collection: `eventflow-stargiftreadmodel`

This collection stores Star Gift data that is returned by the `payments.getStarGifts` API method.

### Document Structure

```json
{
  "_id": ObjectId("..."),
  "GiftId": 1234567890,
  "Limited": false,
  "SoldOut": false,
  "Birthday": false,
  "RequirePremium": false,
  "LimitedPerUser": false,
  "PeerColorAvailable": false,
  "Auction": false,
  "StickerId": 5678901234,
  "Stars": 100,
  "AvailabilityRemains": null,
  "AvailabilityTotal": null,
  "AvailabilityResale": null,
  "ConvertStars": 50,
  "FirstSaleDate": null,
  "LastSaleDate": null,
  "UpgradeStars": 200,
  "ResellMinStars": null,
  "Title": "Birthday Gift",
  "ReleasedByPeerId": null,
  "ReleasedByPeerType": null,
  "PerUserTotal": null,
  "PerUserRemains": null,
  "LockedUntilDate": null,
  "UpgradeVariants": 5,
  "AuctionSlug": null,
  "GiftsPerRound": null,
  "AuctionStartDate": null,
  "AuctionEndDate": null,
  "TotalRounds": null,
  "CurrentRound": null,
  "RoundDuration": null,
  "BackgroundCenterColor": null,
  "BackgroundEdgeColor": null,
  "BackgroundTextColor": null,
  "Version": 1
}
```

---

## Upgrade Attributes Collections

These collections store the upgrade attributes (models, patterns, backdrops) for collectible gifts.

### Collection: `stargift_upgrade_models`

Stores model variants for upgraded gifts.

```json
{
  "_id": ObjectId("..."),
  "Name": "Golden Star",
  "RarityPermille": 100,
  "GiftId": 1234567890,
  "DocumentId": 5000000001
}
```

| Field | Type | Description |
|-------|------|-------------|
| `Name` | string | Model name |
| `RarityPermille` | int | Rarity per 1000 upgrades (e.g., 100 = 10%) |
| `GiftId` | long | Reference to gift in stargiftreadmodel |
| `DocumentId` | long | Reference to sticker document |

### Collection: `stargift_upgrade_patterns`

Stores pattern variants for upgraded gifts.

```json
{
  "_id": ObjectId("..."),
  "Name": "Stars Pattern",
  "RarityPermille": 150,
  "GiftId": 1234567890,
  "DocumentId": 5000000002
}
```

| Field | Type | Description |
|-------|------|-------------|
| `Name` | string | Pattern name |
| `RarityPermille` | int | Rarity per 1000 upgrades |
| `GiftId` | long | Reference to gift in stargiftreadmodel |
| `DocumentId` | long | Reference to pattern sticker document |

### Collection: `stargift_upgrade_backdrops`

Stores backdrop (background) variants for upgraded gifts.

```json
{
  "_id": ObjectId("..."),
  "Name": "Sunset",
  "BackdropId": 1,
  "RarityPermille": 200,
  "GiftId": 1234567890,
  "CenterColor": 16777215,
  "EdgeColor": 0,
  "PatternColor": 16711680,
  "TextColor": 0
}
```

| Field | Type | Description |
|-------|------|-------------|
| `Name` | string | Backdrop name |
| `BackdropId` | int | Unique backdrop identifier |
| `RarityPermille` | int | Rarity per 1000 upgrades |
| `GiftId` | long | Reference to gift in stargiftreadmodel |
| `CenterColor` | int | Center color in RGB24 format |
| `EdgeColor` | int | Edge color in RGB24 format |
| `PatternColor` | int | Pattern color in RGB24 format |
| `TextColor` | int | Text color in RGB24 format |

### Collection: `stargift_upgrade_counters`

Tracks the number of upgraded gifts per gift type.

```json
{
  "_id": ObjectId("..."),
  "GiftId": 1234567890,
  "UpgradedCount": 42
}
```

| Field | Type | Description |
|-------|------|-------------|
| `GiftId` | long | Reference to gift in stargiftreadmodel |
| `UpgradedCount` | int | Total number of upgraded gifts of this type |

---

## Collection: `eventflow-userstarsbalancereadmodel`

This collection stores user's Telegram Stars balance. Balance is updated in real-time when users purchase gifts or receive stars.

### Document Structure

```json
{
  "_id": ObjectId("..."),
  "UserId": 123456789,
  "Balance": 10000,
  "LastUpdated": ISODate("2025-01-04T12:00:00Z")
}
```

### Field Descriptions

| Field | Type | Description |
|-------|------|-------------|
| `_id` | ObjectId | MongoDB auto-generated document ID |
| `UserId` | long | User ID |
| `Balance` | long | Current star balance |
| `LastUpdated` | DateTime | Last balance update timestamp |

### Example: Set User Balance

```javascript
// Set balance for user ID 123456789 to 5000 stars
db.getCollection('eventflow-userstarsbalancereadmodel').updateOne(
  { "UserId": NumberLong(123456789) },
  { 
    $set: { 
      "Balance": NumberLong(5000),
      "LastUpdated": new Date()
    }
  },
  { upsert: true }
);

// Add 1000 stars to user's balance
db.getCollection('eventflow-userstarsbalancereadmodel').updateOne(
  { "UserId": NumberLong(123456789) },
  { 
    $inc: { "Balance": NumberLong(1000) },
    $set: { "LastUpdated": new Date() }
  },
  { upsert: true }
);
```

---

## Collection: `eventflow-savedstargiftreadmodel`

This collection stores gifts that users have received. Used by `payments.getSavedStarGifts` API.

### Document Structure

```json
{
  "_id": ObjectId("..."),
  "SavedId": 1,
  "OwnerUserId": 123456789,
  "FromUserId": 987654321,
  "GiftId": 1001,
  "Date": 1704067200,
  "MsgId": 12345,
  "NameHidden": false,
  "Saved": true,
  "PinnedToTop": false,
  "Converted": false,
  "Upgraded": false,
  "Refunded": false,
  "UpgradeSeparate": false,
  "CanUpgrade": true,
  "ConvertStars": 50,
  "UpgradeStars": 200,
  "Message": "Happy Birthday!",
  "MessageEntities": null,
  
  // Fields set after upgrade (Upgraded=true):
  "Slug": "gift_1001_a1b2c3d4",
  "GiftNum": 42,
  "UpgradedDate": 1704153600,
  "KeepOriginalDetails": true,
  "ModelName": "Golden Star",
  "PatternName": "Stars Pattern",
  "BackdropId": 1,
  "BackdropName": "Sunset",
  "BackdropCenterColor": 16777215,
  "BackdropEdgeColor": 0,
  "BackdropPatternColor": 16711680,
  "BackdropTextColor": 0,
  "CanTransferAt": 1704758400,
  "CanResellAt": 1704758400,
  "CanExportAt": 1706745600
}
```

### Field Descriptions

| Field | Type | Description |
|-------|------|-------------|
| `SavedId` | long | Unique ID for this saved gift (used by client in TInputSavedStarGiftUser.MsgId) |
| `OwnerUserId` | long | User ID who owns/received the gift |
| `FromUserId` | long? | User ID who sent the gift (null if anonymous) |
| `GiftId` | long | Reference to gift in stargiftreadmodel |
| `Date` | int | Unix timestamp when gift was received |
| `MsgId` | int | Message ID of the gift service message (sender's outbox ID) |
| `NameHidden` | bool | Whether sender's name is hidden |
| `Saved` | bool | Whether gift is pinned to profile |
| `PinnedToTop` | bool | Whether gift is pinned to top of profile |
| `Converted` | bool | Whether gift was converted to stars |
| `Upgraded` | bool | Whether gift was upgraded to collectible |
| `Refunded` | bool | Whether gift was refunded |
| `UpgradeSeparate` | bool | Whether upgrade was paid separately |
| `CanUpgrade` | bool | Whether gift can be upgraded |
| `ConvertStars` | long | Stars received if converted |
| `UpgradeStars` | long? | Stars needed to upgrade |
| `PrepaidUpgradeHash` | string? | Hash set when upgrade is prepaid |
| `PrepaidKeepOriginalDetails` | bool? | Keep original details preference (set during prepaid) |
| `PrepaidDate` | int? | Unix timestamp when upgrade was prepaid |
| `Message` | string? | Message attached to gift |
| `MessageEntities` | array? | Message formatting entities |
| `BurnedByCraft` | bool? | Whether this gift was consumed (burned) as a crafting ingredient |
| `BurnedByCraftSavedId` | long? | `SavedId` of the upgraded gift created from this burn |

**Note on MsgId lookup:** When client sends `TInputSavedStarGiftUser`, the `MsgId` field may contain either the actual message ID or the `SavedId`. Handlers implement fallback logic: first try to find by `MsgId`, then by `SavedId`.

#### Upgraded Gift Fields (set when Upgraded=true)

| Field | Type | Description |
|-------|------|-------------|
| `Slug` | string | Unique slug for collectible gift link |
| `GiftNum` | int | Unique number among all upgraded gifts of this type |
| `UpgradedDate` | int | Unix timestamp when gift was upgraded |
| `KeepOriginalDetails` | bool | Whether original sender/message is preserved |
| `ModelName` | string? | Name of selected model attribute |
| `PatternName` | string? | Name of selected pattern attribute |
| `BackdropId` | int? | ID of selected backdrop |
| `BackdropName` | string? | Name of selected backdrop |
| `BackdropCenterColor` | int? | Backdrop center color (RGB24) |
| `BackdropEdgeColor` | int? | Backdrop edge color (RGB24) |
| `BackdropPatternColor` | int? | Backdrop pattern color (RGB24) |
| `BackdropTextColor` | int? | Backdrop text color (RGB24) |
| `CanTransferAt` | int? | Unix timestamp when transfer becomes available |
| `CanResellAt` | int? | Unix timestamp when resale becomes available |
| `CanExportAt` | int? | Unix timestamp when blockchain export becomes available |
| `Crafted` | bool? | Whether this collectible was obtained through crafting |
| `CraftBurnedCount` | int? | Number of additional gifts consumed during crafting |

---

## Example Insert (MongoDB Shell)

```javascript
// Insert a star gift - _id will be auto-generated as ObjectId
db.getCollection('eventflow-stargiftreadmodel').insertOne({
  "GiftId": NumberLong(1001),
  "Limited": false,
  "SoldOut": false,
  "Birthday": true,
  "RequirePremium": false,
  "LimitedPerUser": false,
  "PeerColorAvailable": false,
  "Auction": false,
  "StickerId": NumberLong(5000000001),
  "Stars": NumberLong(100),
  "AvailabilityRemains": null,
  "AvailabilityTotal": null,
  "AvailabilityResale": null,
  "ConvertStars": NumberLong(50),
  "FirstSaleDate": null,
  "LastSaleDate": null,
  "UpgradeStars": NumberLong(200),
  "ResellMinStars": null,
  "Title": "Birthday Cake",
  "ReleasedByPeerId": null,
  "ReleasedByPeerType": null,
  "PerUserTotal": null,
  "PerUserRemains": null,
  "LockedUntilDate": null,
  "UpgradeVariants": 5,
  "AuctionSlug": null,
  "GiftsPerRound": null,
  "AuctionStartDate": null,
  "BackgroundCenterColor": null,
  "BackgroundEdgeColor": null,
  "BackgroundTextColor": null,
  "Version": NumberLong(1)
});

// Insert an auction gift
db.getCollection('eventflow-stargiftreadmodel').insertOne({
  "GiftId": NumberLong(2001),
  "Limited": true,
  "SoldOut": false,
  "Birthday": false,
  "RequirePremium": false,
  "LimitedPerUser": false,
  "PeerColorAvailable": true,
  "Auction": true,
  "StickerId": NumberLong(5000000002),
  "Stars": NumberLong(500),
  "AvailabilityRemains": 100,
  "AvailabilityTotal": 1000,
  "AvailabilityResale": null,
  "ConvertStars": NumberLong(250),
  "FirstSaleDate": null,
  "LastSaleDate": null,
  "UpgradeStars": NumberLong(1000),
  "ResellMinStars": null,
  "Title": "Exclusive Auction Gift",
  "ReleasedByPeerId": null,
  "ReleasedByPeerType": null,
  "PerUserTotal": 5,
  "PerUserRemains": 5,
  "LockedUntilDate": null,
  "UpgradeVariants": 10,
  "AuctionSlug": "exclusive-gift-2025",
  "GiftsPerRound": 10,
  "AuctionStartDate": 1735689600,
  "AuctionEndDate": 1735776000,
  "TotalRounds": 5,
  "CurrentRound": 1,
  "RoundDuration": 3600,
  "BackgroundCenterColor": 16777215,
  "BackgroundEdgeColor": 0,
  "BackgroundTextColor": 16711680,
  "Version": NumberLong(1)
});
```

### Sticker Document Reference

The `StickerId` field references documents in the `eventflow-documentreadmodel` collection. Make sure the sticker document exists before adding a gift.

### Import Script

Use `scripts/import_gifts.py` to import gifts from official Telegram:

```bash
# Install dependencies
pip install -r scripts/requirements-import-gifts.txt

# Add Telegram API credentials to .env
# TELEGRAM_API_ID=your_api_id
# TELEGRAM_API_HASH=your_api_hash

# Run importer
python scripts/import_gifts.py
```

The script supports:
- Import single gift, multiple gifts, or all gifts
- Custom pricing (ConvertStars auto-calculated as 85%)
- Limited/unlimited gifts with availability settings
- Additional fields: RequirePremium, LimitedPerUser, ReleasedByPeer, etc.
- Automatic sticker download and MinIO upload

Example sticker document:
```javascript
db.getCollection('eventflow-documentreadmodel').insertOne({
  "_id": "document-5000000001",
  "DocumentId": NumberLong(5000000001),
  "AccessHash": NumberLong(123456789),
  "Date": 1704067200,
  "MimeType": "application/x-tgsticker",
  "Size": NumberLong(10240),
  "DcId": 2,
  "FileReference": BinData(0, ""),
  "Attributes2": [
    {
      "_t": "TDocumentAttributeSticker",
      "Alt": "🎂",
      "Stickerset": {
        "_t": "TInputStickerSetID",
        "Id": NumberLong(1234567890),
        "AccessHash": NumberLong(987654321)
      }
    }
  ]
});
```

### Index

An index is automatically created on `GiftId` field for efficient queries.

---

## Gift Purchase Flow

When a user purchases a Star Gift via `payments.sendStarsForm`:

1. **Balance Check**: User's star balance is checked in `eventflow-userstarsbalancereadmodel`
2. **Balance Deduction**: Stars are deducted from sender's balance
3. **Gift Availability**: For limited gifts, `AvailabilityRemains` is decremented
4. **Saved Gift**: A new document is created in `eventflow-savedstargiftreadmodel` for the recipient
5. **Service Message**: A `messageActionStarGift` service message is created and sent to both sender and recipient

### Message Flow

- **Sender**: Receives `TPaymentResult` with `TUpdates` containing the service message
- **Recipient**: Receives the service message via push updates (inbox message)
- **Sender's Other Devices**: Receive push updates with the service message

---

## Quick Setup Commands

```javascript
// Create indexes for better performance
db.getCollection('eventflow-userstarsbalancereadmodel').createIndex({ "UserId": 1 }, { unique: true });
db.getCollection('eventflow-savedstargiftreadmodel').createIndex({ "OwnerUserId": 1 });
db.getCollection('eventflow-savedstargiftreadmodel').createIndex({ "SavedId": 1 });
db.getCollection('eventflow-stargiftauctionbidreadmodel').createIndex({ "UserId": 1, "GiftId": 1 });
db.getCollection('eventflow-stargiftauctionbidreadmodel').createIndex({ "GiftId": 1, "BidAmount": -1 });

// Set initial balance for a test user (e.g., user ID 777000)
db.getCollection('eventflow-userstarsbalancereadmodel').insertOne({
  "UserId": NumberLong(777000),
  "Balance": NumberLong(10000),
  "LastUpdated": new Date()
});
```

---

## Collection: `eventflow-stargiftauctionbidreadmodel`

This collection stores auction bids for star gifts. Used by auction-related API methods.

### Document Structure

```json
{
  "_id": ObjectId("..."),
  "BidId": 1,
  "UserId": 123456789,
  "GiftId": 2001,
  "BidAmount": 500,
  "BidDate": 1735689600,
  "HideName": false,
  "Message": "I want this gift!",
  "RecipientUserId": null,
  "Returned": false,
  "Won": false,
  "AcquiredCount": 0,
  "BidPeerId": 123456789,
  "BidPeerType": 1
}
```

### Field Descriptions

| Field | Type | Description |
|-------|------|-------------|
| `_id` | ObjectId | MongoDB auto-generated document ID |
| `BidId` | long | Unique identifier for this bid |
| `UserId` | long | User ID who placed the bid |
| `GiftId` | long | Reference to gift in stargiftreadmodel |
| `BidAmount` | long | Bid amount in Telegram Stars |
| `BidDate` | int | Unix timestamp when bid was placed |
| `HideName` | bool | Whether bidder's name is hidden |
| `Message` | string? | Optional message attached to bid |
| `RecipientUserId` | long? | User ID to receive gift if won (null = self) |
| `Returned` | bool | Whether bid was returned (outbid or auction ended) |
| `Won` | bool | Whether this bid won the auction |
| `AcquiredCount` | int | Number of gifts acquired from this auction |
| `BidPeerId` | long | Peer ID of the bidder |
| `BidPeerType` | int | Peer type (1=User, 2=Chat, 3=Channel) |

### Example Insert

```javascript
// Insert an auction bid
db.getCollection('eventflow-stargiftauctionbidreadmodel').insertOne({
  "BidId": NumberLong(1),
  "UserId": NumberLong(123456789),
  "GiftId": NumberLong(2001),
  "BidAmount": NumberLong(500),
  "BidDate": 1735689600,
  "HideName": false,
  "Message": "I want this gift!",
  "RecipientUserId": null,
  "Returned": false,
  "Won": false,
  "AcquiredCount": 0,
  "BidPeerId": NumberLong(123456789),
  "BidPeerType": 1
});
```

---

## Auction Flow

When a user places a bid on an auction gift via `payments.sendStarsForm` with `inputInvoiceStarGiftAuctionBid`:

1. **Auction Validation**: Check if gift is an auction gift and auction is active
2. **Bid Validation**: Validate bid amount is >= minimum bid
3. **Balance Check**: User's star balance is checked
4. **Balance Deduction**: Stars are deducted (full amount for new bid, difference for update)
5. **Bid Record**: Create or update bid in `eventflow-stargiftauctionbidreadmodel`
6. **Auction State Update**: Update top bidders, bid levels, and minimum bid in gift document
7. **Response**: Return `TUpdateStarGiftAuctionUserState` with user's bid state

### Auction State Fields in Gift Document

The following fields are used for auction state in `eventflow-stargiftreadmodel`:

| Field | Type | Description |
|-------|------|-------------|
| `Auction` | bool | Whether this gift is available via auction |
| `AuctionSlug` | string? | Auction slug identifier |
| `AuctionStartDate` | int? | Unix timestamp when auction starts |
| `AuctionEndDate` | int? | Unix timestamp when auction ends |
| `MinBidAmount` | long? | Current minimum bid amount |
| `GiftsPerRound` | int? | Number of gifts distributed per round |
| `TotalRounds` | int? | Total number of auction rounds |
| `CurrentRound` | int? | Current auction round |
| `RoundDuration` | int? | Duration of each round in seconds |
| `TopBidders` | array | Array of top bidder user IDs |
| `BidLevels` | array | Array of current bid levels with positions |
| `AuctionVersion` | int? | Version counter for auction state changes |

---

## Gift Upgrade Flow

When a user upgrades a gift to a collectible via `payments.upgradeStarGift`:

1. **Validation**: Check gift exists, is owned by user, not converted, not already upgraded, can_upgrade=true
2. **Payment Check**: If prepaid_upgrade_hash is empty and upgrade_stars > 0, payment is required
3. **Craft Check (optional)**: If `CraftRequiredCount > 1`, enough same-type gifts must exist and extra gifts are burned
4. **Attribute Selection**: Model/pattern/backdrop are selected by rarity, unless a craft model is forced
5. **Counter Increment**: `stargift_upgrade_counters.UpgradedCount` is incremented atomically
6. **Gift Update**: Saved gift is updated with Upgraded=true, Slug, GiftNum, selected attributes
7. **Response**: Returns `TUpdates` with `messageActionStarGiftUnique` containing the upgraded gift

### Craft Upgrade Configuration (optional)

Optional fields in `eventflow-stargiftreadmodel`:

| Field | Type | Description |
|-------|------|-------------|
| `CraftRequiredCount` | int? | Total same gifts required for upgrade (1 = normal, >1 = crafting with burn) |
| `CraftModel` | int? | Preferred model number/id for crafted result (example: `4`) |
| `CraftModelName` | string? | Preferred model name for crafted result (checked before `CraftModel`) |

When `CraftRequiredCount > 1`, the upgraded gift is created from one item and the remaining required items are marked burned (`Converted=true`, `BurnedByCraft=true`) without adding stars to balance.
For collectible gifts, craft ingredients are taken from the owner's collectible gifts with the same `GiftId` (models may be different).

### Prepaid Upgrade Flow

Users can prepay for an upgrade via `payments.sendStarsForm` with `inputInvoiceStarGiftUpgrade`:

1. **Payment**: Stars are deducted from user's balance
2. **Prepaid Hash**: A `PrepaidUpgradeHash` is set on the saved gift
3. **Keep Original Details**: The `KeepOriginalDetails` preference is stored as `PrepaidKeepOriginalDetails`
4. **Upgrade**: User calls `payments.upgradeStarGift` which checks for the prepaid hash and proceeds

### Rarity-Based Selection

Attributes are selected based on `RarityPermille` (rarity per 1000 upgrades):
- Higher RarityPermille = more common
- Lower RarityPermille = more rare
- Selection is weighted random based on rarity values

---

## Import Upgrade Attributes

Use `scripts/import_gifts.py` option 4 to import upgrade attributes from official Telegram:

```bash
python scripts/import_gifts.py
# Select option 4: Import upgrade attributes
# Choose a gift from Telegram
# Enter your local GiftId
# Script will collect models, patterns, backdrops via GetStarGiftUpgradePreview
```

### Manual Insert Examples

```javascript
// Insert upgrade model
db.getCollection('stargift_upgrade_models').insertOne({
  "Name": "Golden Star",
  "RarityPermille": 100,  // 10% chance
  "GiftId": NumberLong(1001),
  "DocumentId": NumberLong(5000000010)
});

// Insert upgrade pattern
db.getCollection('stargift_upgrade_patterns').insertOne({
  "Name": "Stars Pattern",
  "RarityPermille": 150,  // 15% chance
  "GiftId": NumberLong(1001),
  "DocumentId": NumberLong(5000000011)
});

// Insert upgrade backdrop
db.getCollection('stargift_upgrade_backdrops').insertOne({
  "Name": "Sunset",
  "BackdropId": 1,
  "RarityPermille": 200,  // 20% chance
  "GiftId": NumberLong(1001),
  "CenterColor": 16777215,  // White
  "EdgeColor": 16744448,    // Orange
  "PatternColor": 16711680, // Red
  "TextColor": 0            // Black
});

// Initialize upgrade counter
db.getCollection('stargift_upgrade_counters').insertOne({
  "GiftId": NumberLong(1001),
  "UpgradedCount": 0
});
```

### Create Indexes for Upgrade Collections

```javascript
db.getCollection('stargift_upgrade_models').createIndex({ "GiftId": 1 });
db.getCollection('stargift_upgrade_patterns').createIndex({ "GiftId": 1 });
db.getCollection('stargift_upgrade_backdrops').createIndex({ "GiftId": 1 });
db.getCollection('stargift_upgrade_counters').createIndex({ "GiftId": 1 }, { unique: true });
db.getCollection('eventflow-savedstargiftreadmodel').createIndex({ "Slug": 1 });
db.getCollection('eventflow-savedstargiftreadmodel').createIndex({ "GiftId": 1, "Upgraded": 1 });
```

---

## API Methods Summary

| Method | Description |
|--------|-------------|
| `payments.getStarGifts` | Get list of available gifts |
| `payments.sendStarsForm` | Purchase a gift (with `inputInvoiceStarGift`) |
| `payments.getSavedStarGifts` | Get user's received gifts |
| `payments.getStarGiftUpgradePreview` | Get random sample of upgrade attributes |
| `payments.upgradeStarGift` | Upgrade gift to collectible |
| `payments.getUniqueStarGift` | Get collectible gift info by slug |
| `payments.convertStarGift` | Convert gift to stars |
| `payments.saveStarGift` | Pin/unpin gift to profile |
| `payments.toggleStarGiftsPinnedToTop` | Pin gifts to top of profile |
| `payments.transferStarGift` | Transfer collectible to another user |
