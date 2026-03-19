# Cambrian API Contracts

## TrackResponse

Returned by: `GET /discover`, `GET /catalog`, `GET /trending`, `GET /tracks`, `GET /tracks/{trackId}`, storefront endpoints.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | string (GUID) | yes | Track ID |
| cambrianTrackId | string | yes | Cambrian track identifier (CAMB-TRK-XXXX) |
| title | string | yes | Track title |
| description | string? | no | Track description |
| genre | string | yes | Genre |
| price | decimal | yes | Legacy price field |
| nonExclusivePrice | decimal | yes | Non-exclusive license price |
| exclusivePrice | decimal | yes | Exclusive license price |
| copyrightBuyoutPrice | decimal | yes | Copyright buyout price |
| platformFeePercent | decimal | yes | Creator's platform fee rate |
| nonExclusivePlatformFee | decimal | yes | Calculated platform fee |
| nonExclusiveCreatorEarnings | decimal | yes | Calculated creator earnings |
| exclusivePlatformFee | decimal | yes | Calculated platform fee |
| exclusiveCreatorEarnings | decimal | yes | Calculated creator earnings |
| copyrightBuyoutPlatformFee | decimal | yes | Calculated platform fee |
| copyrightBuyoutCreatorEarnings | decimal | yes | Calculated creator earnings |
| exclusiveSold | bool | yes | Whether exclusive license is sold |
| status | string | yes | available, exclusive_sold, copyright_transferred |
| copyrightOwnerId | string? | no | Current copyright owner |
| licenseType | string? | no | License type |
| duration | string? | no | Track duration |
| audioUrl | string? | no | Streaming URL |
| coverArtUrl | string? | no | Cover art URL |
| creatorId | string | yes | Creator user ID |
| artist | string? | no | Creator display name (never email) |
| creatorUsername | string? | no | Creator display name for UI linking |
| creatorSlug | string? | no | Creator profile slug for navigation |
| creatorProfileImageUrl | string? | no | Creator avatar URL |
| createdAt | datetime | yes | Upload timestamp |

## CreatorProfileResponse

Returned by: `GET /creator-profile/{slug}`, `GET /creator-profile/me`, `PUT /creator-profile/me`.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | string | yes | Profile ID |
| userId | string | yes | User ID |
| slug | string | yes | URL-safe identifier |
| displayName | string? | no | Creator's public display name |
| bio | string | yes | Bio text |
| niche | string? | no | Creator niche |
| bannerImageUrl | string? | no | Banner image URL |
| profileImageUrl | string? | no | Profile image URL |
| socialLinks | object? | no | Social media links |
| showEarnings | bool | yes | Whether to show earnings publicly |
| showDownloadStats | bool | yes | Whether to show download stats |
| pinnedTrackIds | string? | no | Comma-separated pinned track IDs |
| stats | CreatorStatsDto | yes | Stats object |
| createdAt | datetime | yes | Profile creation date |
| updatedAt | datetime | yes | Last update date |

## AuthSession (Login/Register/Me)

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| token | string | yes | JWT token |
| tier | string? | no | User tier |
| user.id | string | yes | User ID |
| user.email | string | yes | Email (auth-scoped, not public) |
| user.displayName | string? | no | Public display name |
| user.tier | string | yes | Tier (free, creator, pro) |
| user.role | string | yes | Role (User, Creator, Admin) |

## Upload Duplicate Error (409)

When a creator attempts to upload a file they have already uploaded:

```json
{
  "success": false,
  "error": "You have already uploaded this audio file. Existing track: \"<title>\" (ID: <cambrianTrackId>)."
}
```

HTTP Status: 409 Conflict
