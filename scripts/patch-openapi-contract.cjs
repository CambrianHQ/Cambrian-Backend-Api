#!/usr/bin/env node
/**
 * OpenAPI Contract Correction Pass
 * 
 * Rules:
 * - Spec must match actual backend responses, including conditional shapes.
 * - Reuse shared schemas/components where possible.
 * - If two routes are aliases, mark one as canonical in descriptions.
 * - Document current truth, do not idealize.
 * - Legacy-but-live routes stay documented.
 *
 * Implementation order:
 * 1. Add missing paths
 * 2. Add/fix shared auth/user schemas
 * 3. Fix /auth/me
 * 4. Fix /api/creator/me partial-response behavior
 * 5. Add V1 wrapper schemas and apply them consistently
 * 6. Resolve license verify path mismatch
 */

const fs = require('fs');
const path = require('path');

const specPath = path.join(__dirname, '..', 'contracts', 'openapi.v1.json');
const spec = JSON.parse(fs.readFileSync(specPath, 'utf-8'));

// Helper: standard JSON response wrapper
function jsonResponse(schemaRef, description = 'OK') {
  return {
    description,
    content: {
      'application/json': {
        schema: { $ref: `#/components/schemas/${schemaRef}` }
      }
    }
  };
}

function jsonResponseInline(schema, description = 'OK') {
  return {
    description,
    content: {
      'application/json': { schema }
    }
  };
}

function envelopeOf(dataSchema) {
  return {
    type: 'object',
    properties: {
      success: { type: 'boolean' },
      data: dataSchema,
      message: { type: 'string', nullable: true },
      error: { type: 'string', nullable: true }
    }
  };
}

function envelopeRef(schemaName) {
  return envelopeOf({ $ref: `#/components/schemas/${schemaName}` });
}

function envelopeArrayRef(schemaName) {
  return envelopeOf({ type: 'array', items: { $ref: `#/components/schemas/${schemaName}` } });
}

function messageOnlyEnvelope() {
  return {
    type: 'object',
    properties: {
      success: { type: 'boolean' },
      data: { nullable: true },
      message: { type: 'string' }
    }
  };
}

// ──────────────────────────────────────────────────────────────────────
// STEP 2: Add/fix shared schemas in components
// ──────────────────────────────────────────────────────────────────────

const schemas = spec.components.schemas;

// ApiEnvelope — generic wrapper
schemas.ApiEnvelope = {
  type: 'object',
  description: 'Standard API response envelope. All endpoints wrap responses in this shape.',
  properties: {
    success: { type: 'boolean' },
    data: { description: 'Response payload. Null on error responses.' },
    message: { type: 'string', nullable: true, description: 'Human-readable message (used for message-only responses).' },
    error: { type: 'string', nullable: true, description: 'Error description when success=false.' }
  }
};

// Auth session user sub-object
schemas.SessionUser = {
  type: 'object',
  description: 'User identity returned in login/register/google session responses.',
  properties: {
    id: { type: 'string', description: 'ApplicationUser GUID string.' },
    email: { type: 'string' },
    username: { type: 'string', nullable: true, description: 'Null if username not yet set (needsUsername=true).' },
    displayName: { type: 'string', nullable: true },
    phoneNumber: { type: 'string', nullable: true }
  }
};

// Auth session response (login/register/google)
schemas.AuthSessionResponse = {
  type: 'object',
  description: 'Response from POST /auth/register, /auth/login, /auth/google. Wrapped in ApiEnvelope.data.',
  properties: {
    token: { type: 'string', description: 'JWT bearer token.' },
    tier: { type: 'string', enum: ['free', 'paid', 'pro'], description: 'Current subscription tier.' },
    role: { type: 'string', enum: ['User', 'Creator', 'Admin'], description: 'Currently always "Creator" — every account is a creator.' },
    isNewUser: { type: 'boolean', description: 'True when account was just registered and username not yet set.' },
    needsUsername: { type: 'boolean', description: 'True when user.username is null (username setup not complete).' },
    requiresUsernameSetup: { type: 'boolean', description: 'Alias for needsUsername. Both fields are always identical.' },
    user: { $ref: '#/components/schemas/SessionUser' }
  }
};

// Auth /me response (richer than session)
schemas.AuthMeResponse = {
  type: 'object',
  description: 'Response from GET /auth/me. Wrapped in ApiEnvelope.data.',
  properties: {
    token: { type: 'string', description: 'Refreshed JWT bearer token.' },
    id: { type: 'string', description: 'ApplicationUser GUID string.' },
    email: { type: 'string' },
    tier: { type: 'string', enum: ['free', 'paid', 'pro'] },
    role: { type: 'string', enum: ['User', 'Creator', 'Admin'] },
    username: { type: 'string', nullable: true },
    displayName: { type: 'string', nullable: true },
    phoneNumber: { type: 'string', nullable: true },
    isNewUser: { type: 'boolean' },
    needsUsername: { type: 'boolean' },
    requiresUsernameSetup: { type: 'boolean' },
    canChangeUsername: { type: 'boolean', description: 'False once username has been set (usernames are immutable).' },
    canChangeUsername: { type: 'boolean', description: 'False once username has been set (usernames are immutable).' },
    creatorTier: { type: 'string', enum: ['Free', 'Pro'] },
    uploadCount: { type: 'integer' },
    uploadLimit: { type: 'integer', nullable: true, description: 'Null means unlimited (Pro tier).' },
    subscriptionStatus: { type: 'string', enum: ['Inactive', 'Active', 'Cancelled'] },
    subscriptionEndDate: { type: 'string', format: 'date-time', nullable: true },
    platformFeePercent: { type: 'number', format: 'double', description: 'Platform fee rate as decimal (e.g. 0.35 = 35%).' },
    contractVersion: { type: 'string', description: 'TierManifest contract version.' }
  }
};

// Set-username response
schemas.SetUsernameResponse = {
  type: 'object',
  description: 'Response from POST /auth/set-username.',
  properties: {
    username: { type: 'string', description: 'Normalized lowercase username.' },
    displayName: { type: 'string' },
    role: { type: 'string' },
    token: { type: 'string', description: 'Fresh JWT with updated claims.' }
  }
};

// Creator stats shared schema
schemas.CreatorStatsDto = {
  type: 'object',
  properties: {
    trackCount: { type: 'integer' },
    totalSales: { type: 'integer' },
    totalDownloads: { type: 'integer' },
    averageRating: { type: 'number', format: 'double' },
    followerCount: { type: 'integer' }
  }
};

// Creator /me response (normal)
schemas.CreatorMeResponse = {
  type: 'object',
  description: 'Response from GET /api/creator/me when Creator row exists.',
  properties: {
    id: { type: 'string', description: 'Creator UUID.' },
    username: { type: 'string' },
    canChangeUsername: { type: 'boolean' },
    displayName: { type: 'string', nullable: true },
    bio: { type: 'string' },
    profileImageUrl: { type: 'string', nullable: true },
    coverImageUrl: { type: 'string', nullable: true },
    socialLinks: { type: 'array', nullable: true, items: { $ref: '#/components/schemas/SocialLinkDto' } },
    stats: { $ref: '#/components/schemas/CreatorStatsDto' },
    tracks: { type: 'array', items: { $ref: '#/components/schemas/TrackResponse' } }
  }
};

// Creator /me partial response (needsUsername)
schemas.CreatorMePartialResponse = {
  type: 'object',
  description: 'Response from GET /api/creator/me when Creator row does not exist yet (username not set). Frontend should check needsUsername and prompt for username setup.',
  properties: {
    id: { type: 'string', nullable: true, description: 'Always null in this response.' },
    username: { type: 'string', nullable: true, description: 'Always null in this response.' },
    canChangeUsername: { type: 'boolean', description: 'Always true.' },
    displayName: { type: 'string', description: 'From ApplicationUser.DisplayName or email prefix.' },
    bio: { type: 'string' },
    profileImageUrl: { type: 'string', nullable: true },
    coverImageUrl: { type: 'string', nullable: true },
    socialLinks: { nullable: true, description: 'Always null.' },
    stats: { nullable: true, description: 'Always null.' },
    tracks: { type: 'array', items: {}, description: 'Always empty array.' },
    needsUsername: { type: 'boolean', description: 'Always true.' }
  }
};

// Public creator DTO
schemas.PublicCreatorDto = {
  type: 'object',
  description: 'Public creator profile. Returned by GET /api/creators/{id}, /resolve/{identifier}, /by-username/{username}.',
  properties: {
    id: { type: 'string', description: 'Creator UUID.' },
    userId: { type: 'string' },
    username: { type: 'string' },
    displayName: { type: 'string', nullable: true },
    bio: { type: 'string' },
    profileImageUrl: { type: 'string', nullable: true },
    coverImageUrl: { type: 'string', nullable: true },
    socialLinks: { type: 'array', nullable: true, items: { $ref: '#/components/schemas/SocialLinkDto' } },
    stats: { $ref: '#/components/schemas/CreatorStatsDto' },
    tracks: { type: 'array', items: { $ref: '#/components/schemas/TrackResponse' } },
    createdAt: { type: 'string', format: 'date-time' },
    updatedAt: { type: 'string', format: 'date-time' }
  }
};

// TrackResponse — full track DTO
schemas.TrackResponse = {
  type: 'object',
  description: 'Track data returned by catalog, creator, and search endpoints.',
  properties: {
    id: { type: 'string', format: 'uuid' },
    cambrianTrackId: { type: 'string', description: 'Public track ID, e.g. CAMB-TRK-A1B2C3D4.' },
    title: { type: 'string' },
    name: { type: 'string', description: 'Alias for title.' },
    description: { type: 'string', nullable: true },
    genre: { type: 'string', nullable: true },
    mood: { type: 'string', nullable: true },
    tempo: { type: 'string', nullable: true },
    tags: { type: 'array', items: { type: 'string' } },
    instrumental: { type: 'boolean' },
    visibility: { type: 'string', enum: ['public', 'limited', 'hidden'] },
    price: { type: 'number', format: 'double', description: 'Legacy price field.' },
    nonExclusivePrice: { type: 'number', format: 'double' },
    exclusivePrice: { type: 'number', format: 'double' },
    copyrightBuyoutPrice: { type: 'number', format: 'double' },
    platformFeePercent: { type: 'number', format: 'double' },
    nonExclusivePlatformFee: { type: 'number', format: 'double' },
    nonExclusiveCreatorEarnings: { type: 'number', format: 'double' },
    exclusivePlatformFee: { type: 'number', format: 'double' },
    exclusiveCreatorEarnings: { type: 'number', format: 'double' },
    copyrightBuyoutPlatformFee: { type: 'number', format: 'double' },
    copyrightBuyoutCreatorEarnings: { type: 'number', format: 'double' },
    exclusiveSold: { type: 'boolean' },
    status: { type: 'string', enum: ['available', 'exclusive_sold', 'copyright_transferred'] },
    isCopyrightTransferred: { type: 'boolean' },
    licenseType: { type: 'string', nullable: true },
    duration: { type: 'string', nullable: true },
    audioUrl: { type: 'string', nullable: true },
    coverArtUrl: { type: 'string', nullable: true },
    creatorId: { type: 'string' },
    creatorSlug: { type: 'string', nullable: true },
    creatorProfileImageUrl: { type: 'string', nullable: true },
    artist: { type: 'string', nullable: true },
    createdAt: { type: 'string', format: 'date-time' }
  }
};

// Creator profile DTO
schemas.CreatorProfileDto = {
  type: 'object',
  description: 'Creator profile (from CreatorProfile table). Returned by /creator-profile endpoints.',
  properties: {
    id: { type: 'string', format: 'uuid' },
    userId: { type: 'string' },
    slug: { type: 'string' },
    displayName: { type: 'string', nullable: true },
    username: { type: 'string', nullable: true },
    bio: { type: 'string' },
    niche: { type: 'string', nullable: true },
    profileImageUrl: { type: 'string', nullable: true },
    bannerImageUrl: { type: 'string', nullable: true },
    socialLinks: { type: 'array', nullable: true, items: { $ref: '#/components/schemas/SocialLinkDto' } },
    stats: { $ref: '#/components/schemas/CreatorStatsDto' },
    showEarnings: { type: 'boolean' },
    showDownloadStats: { type: 'boolean' },
    pinnedTrackIds: { type: 'string', nullable: true, description: 'Comma-separated GUIDs.' },
    createdAt: { type: 'string', format: 'date-time' },
    updatedAt: { type: 'string', format: 'date-time' }
  }
};

// Creator profile /me response (extends dto with canChangeUsername)
schemas.CreatorProfileMeResponse = {
  type: 'object',
  description: 'Response from GET /creator-profile/me when profile exists.',
  properties: {
    id: { type: 'string', format: 'uuid' },
    userId: { type: 'string' },
    slug: { type: 'string' },
    username: { type: 'string', nullable: true },
    displayName: { type: 'string', nullable: true },
    canChangeUsername: { type: 'boolean' },
    bio: { type: 'string' },
    niche: { type: 'string', nullable: true },
    profileImageUrl: { type: 'string', nullable: true },
    bannerImageUrl: { type: 'string', nullable: true },
    socialLinks: { type: 'array', nullable: true, items: { $ref: '#/components/schemas/SocialLinkDto' } },
    showEarnings: { type: 'boolean' },
    showDownloadStats: { type: 'boolean' },
    pinnedTrackIds: { type: 'string', nullable: true },
    stats: { $ref: '#/components/schemas/CreatorStatsDto' },
    createdAt: { type: 'string', format: 'date-time' },
    updatedAt: { type: 'string', format: 'date-time' }
  }
};

// Storefront response
schemas.StorefrontResponse = {
  type: 'object',
  description: 'Full creator storefront with profile, tracks, and collections.',
  properties: {
    profile: { $ref: '#/components/schemas/CreatorProfileDto' },
    pinnedTracks: { type: 'array', items: { $ref: '#/components/schemas/TrackResponse' } },
    collections: { type: 'array', items: { $ref: '#/components/schemas/TrackCollectionDto' } },
    allTracks: { type: 'array', items: { $ref: '#/components/schemas/TrackResponse' } },
    stats: { $ref: '#/components/schemas/CreatorStatsDto' }
  }
};

// TrackCollectionDto
schemas.TrackCollectionDto = {
  type: 'object',
  properties: {
    id: { type: 'string', format: 'uuid' },
    title: { type: 'string' },
    description: { type: 'string', nullable: true },
    coverImageUrl: { type: 'string', nullable: true },
    trackIds: { type: 'string', description: 'Comma-separated GUIDs.' },
    createdAt: { type: 'string', format: 'date-time' },
    updatedAt: { type: 'string', format: 'date-time' }
  }
};

// Image upload response
schemas.ImageUploadResponse = {
  type: 'object',
  properties: {
    profileImageUrl: { type: 'string', nullable: true },
    coverImageUrl: { type: 'string', nullable: true },
    bannerImageUrl: { type: 'string', nullable: true }
  }
};

// Presigned URL response
schemas.PresignedUploadResponse = {
  type: 'object',
  properties: {
    uploadUrl: { type: 'string', description: 'Presigned PUT URL (S3/R2) or proxy upload endpoint.' },
    publicUrl: { type: 'string', description: 'Public CDN URL for the uploaded file.' }
  }
};

// Username availability
schemas.UsernameAvailabilityResponse = {
  type: 'object',
  properties: {
    username: { type: 'string' },
    available: { type: 'boolean' },
    reason: { type: 'string', nullable: true, description: 'Present when available=false, explaining why.' }
  }
};

// User public profile
schemas.UserPublicProfile = {
  type: 'object',
  description: 'Public user profile from GET /users/{username}.',
  properties: {
    username: { type: 'string' },
    displayName: { type: 'string', nullable: true },
    profileImageUrl: { type: 'string', nullable: true },
    coverImageUrl: { type: 'string', nullable: true },
    bio: { type: 'string', nullable: true },
    role: { type: 'string' },
    verifiedCreator: { type: 'boolean' },
    tracks: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          id: { type: 'string', format: 'uuid' },
          title: { type: 'string' },
          genre: { type: 'string', nullable: true },
          coverArtUrl: { type: 'string', nullable: true },
          nonExclusivePriceCents: { type: 'integer' },
          exclusivePriceCents: { type: 'integer' },
          copyrightBuyoutPriceCents: { type: 'integer' },
          createdAt: { type: 'string', format: 'date-time' }
        }
      }
    }
  }
};

// UserProfileUpdate response (PATCH /users/me)
schemas.UserProfileUpdateResponse = {
  type: 'object',
  properties: {
    username: { type: 'string' },
    displayName: { type: 'string', nullable: true },
    profileImageUrl: { type: 'string', nullable: true },
    coverImageUrl: { type: 'string', nullable: true },
    bio: { type: 'string', nullable: true }
  }
};

// V1 paginated wrapper
schemas.V1PaginatedResponse = {
  type: 'object',
  description: 'V1 API paginated response envelope.',
  properties: {
    success: { type: 'boolean' },
    data: { type: 'array', items: {} },
    meta: { $ref: '#/components/schemas/V1PaginationMeta' }
  }
};

schemas.V1PaginationMeta = {
  type: 'object',
  properties: {
    page: { type: 'integer' },
    limit: { type: 'integer' },
    total: { type: 'integer' },
    totalPages: { type: 'integer' },
    hasNext: { type: 'boolean' },
    hasPrev: { type: 'boolean' }
  }
};

// Genre item (V1)
schemas.GenreItem = {
  type: 'object',
  properties: {
    genre: { type: 'string' },
    count: { type: 'integer' }
  }
};

// API key response
schemas.ApiKeyCreateResponse = {
  type: 'object',
  description: 'Returned once on key creation. The raw key is never shown again.',
  properties: {
    key: { type: 'string', description: 'Raw API key (cbr_ prefix + 32 hex bytes). Store securely.' },
    prefix: { type: 'string', description: 'Display prefix, e.g. cbr_0544.' },
    name: { type: 'string' },
    id: { type: 'string', format: 'uuid' },
    createdAt: { type: 'string', format: 'date-time' }
  }
};

schemas.ApiKeyListItem = {
  type: 'object',
  properties: {
    id: { type: 'string', format: 'uuid' },
    prefix: { type: 'string', description: 'Display prefix, e.g. cbr_0544.' },
    name: { type: 'string' },
    createdAt: { type: 'string', format: 'date-time' }
  }
};

// License certificate
schemas.LicenseCertificateDto = {
  type: 'object',
  properties: {
    licenseId: { type: 'string' },
    trackId: { type: 'string' },
    licenseType: { type: 'string' },
    usageType: { type: 'string', nullable: true },
    buyerId: { type: 'string' },
    creatorId: { type: 'string' },
    issuedAt: { type: 'string', format: 'date-time' },
    allowedUses: { type: 'array', nullable: true, items: { type: 'string' } },
    restrictions: { type: 'array', nullable: true, items: { type: 'string' } },
    copyrightOwner: { type: 'string', nullable: true }
  }
};

schemas.LicenseVerifyResponse = {
  type: 'object',
  properties: {
    licenseId: { type: 'string' },
    trackId: { type: 'string' },
    licenseType: { type: 'string' },
    usageType: { type: 'string', nullable: true },
    buyerId: { type: 'string' },
    issuedAt: { type: 'string', format: 'date-time' },
    valid: { type: 'boolean' }
  }
};

// API key create request
schemas.CreateApiKeyRequest = {
  type: 'object',
  properties: {
    name: { type: 'string', description: 'User-assigned label for the key.' }
  },
  required: ['name']
};

// Edit track request
schemas.EditTrackRequest = {
  type: 'object',
  description: 'Request body for PUT /creator/tracks/{trackId}.',
  properties: {
    title: { type: 'string', nullable: true },
    description: { type: 'string', nullable: true },
    genre: { type: 'string', nullable: true },
    mood: { type: 'string', nullable: true },
    tempo: { type: 'string', nullable: true },
    tags: { type: 'string', nullable: true, description: 'Comma-separated tags.' },
    nonExclusivePriceCents: { type: 'integer', nullable: true },
    exclusivePriceCents: { type: 'integer', nullable: true },
    copyrightBuyoutPriceCents: { type: 'integer', nullable: true }
  }
};

// Upgrade tier request (admin)
schemas.UpgradeTierRequest = {
  type: 'object',
  properties: {
    tier: { type: 'string', description: 'Target tier, e.g. "pro".' }
  },
  required: ['tier']
};

// ──────────────────────────────────────────────────────────────────────
// STEP 1 + 3–6: Patch existing paths and add missing ones
// ──────────────────────────────────────────────────────────────────────

const paths = spec.paths;

// Helper: Standard request body for JSON
function jsonRequestBody(schemaRef) {
  return {
    content: {
      'application/json': { schema: { $ref: `#/components/schemas/${schemaRef}` } },
      'text/json': { schema: { $ref: `#/components/schemas/${schemaRef}` } },
      'application/*+json': { schema: { $ref: `#/components/schemas/${schemaRef}` } }
    }
  };
}

// ── AUTH: Fix responses ──

// POST /auth/register
if (paths['/auth/register']?.post) {
  paths['/auth/register'].post.summary = 'Register a new account. Every account is created as a Creator (Role=Creator).';
  paths['/auth/register'].post.responses = {
    '200': jsonResponseInline(envelopeRef('AuthSessionResponse'))
  };
}

// POST /auth/login
if (paths['/auth/login']?.post) {
  paths['/auth/login'].post.summary = 'Log in with email and password.';
  paths['/auth/login'].post.responses = {
    '200': jsonResponseInline(envelopeRef('AuthSessionResponse'))
  };
}

// POST /auth/google
if (paths['/auth/google']?.post) {
  paths['/auth/google'].post.summary = 'Log in or register with a Google ID token.';
  paths['/auth/google'].post.responses = {
    '200': jsonResponseInline(envelopeRef('AuthSessionResponse'))
  };
}

// GET /auth/me
if (paths['/auth/me']?.get) {
  paths['/auth/me'].get.summary = 'Get current user profile with full account details. Requires authentication.';
  paths['/auth/me'].get.security = [{ Bearer: [] }];
  paths['/auth/me'].get.responses = {
    '200': jsonResponseInline(envelopeRef('AuthMeResponse')),
    '401': { description: 'Unauthorized' }
  };
}

// POST /auth/set-username
if (paths['/auth/set-username']?.post) {
  paths['/auth/set-username'].post.summary = 'Set username during onboarding (one-time, immutable). Returns fresh JWT.';
  paths['/auth/set-username'].post.security = [{ Bearer: [] }];
  paths['/auth/set-username'].post.responses = {
    '200': jsonResponseInline(envelopeRef('SetUsernameResponse')),
    '409': { description: 'Username already taken.' }
  };
}

// PUT /auth/display-name + /settings/display-name
for (const p of ['/auth/display-name', '/settings/display-name']) {
  if (paths[p]?.put) {
    paths[p].put.summary = p === '/auth/display-name'
      ? 'Update display name. Canonical endpoint.'
      : 'Update display name. Alias for PUT /auth/display-name.';
    paths[p].put.security = [{ Bearer: [] }];
    paths[p].put.responses = {
      '200': jsonResponseInline(envelopeOf({
        type: 'object',
        properties: { displayName: { type: 'string' } }
      }))
    };
  }
}

// POST /auth/refresh
if (paths['/auth/refresh']?.post) {
  paths['/auth/refresh'].post.summary = 'Refresh JWT token. Requires valid (or within clock-skew) token.';
  paths['/auth/refresh'].post.security = [{ Bearer: [] }];
  paths['/auth/refresh'].post.responses = {
    '200': jsonResponseInline(envelopeOf({
      type: 'object', properties: { token: { type: 'string' } }
    }))
  };
}

// POST /auth/logout
if (paths['/auth/logout']?.post) {
  paths['/auth/logout'].post.summary = 'Log out. Clears auth_token cookie.';
  paths['/auth/logout'].post.security = [{ Bearer: [] }];
  paths['/auth/logout'].post.responses = {
    '200': jsonResponseInline(messageOnlyEnvelope())
  };
}

// Message-only auth endpoints
const messageAuthEndpoints = {
  '/auth/forgot-password': { method: 'post', summary: 'Request password reset code via email or SMS.' },
  '/auth/verify-code': { method: 'post', summary: 'Verify password reset code.' },
  '/auth/reset-password': { method: 'post', summary: 'Reset password using verified code.' },
  '/auth/recover-username': { method: 'post', summary: 'Send username recovery email.' },
  '/auth/link-google': { method: 'post', summary: 'Link Google account to existing local account. Requires auth.' },
  '/auth/set-password': { method: 'post', summary: 'Set password for Google-only accounts.' },
  '/auth/verify-email-change': { method: 'get', summary: 'Complete email change via verification token.' },
  '/settings/email': { method: 'post', summary: 'Request email change. Sends verification link.' }
};
for (const [p, cfg] of Object.entries(messageAuthEndpoints)) {
  if (paths[p]?.[cfg.method]) {
    paths[p][cfg.method].summary = cfg.summary;
    if (['link-google', 'set-password'].some(s => p.includes(s)) || p === '/settings/email') {
      paths[p][cfg.method].security = [{ Bearer: [] }];
    }
    paths[p][cfg.method].responses = {
      '200': jsonResponseInline(messageOnlyEnvelope())
    };
  }
}

// PUT /settings/email (alias)
if (paths['/settings/email']?.put) {
  paths['/settings/email'].put.summary = 'Alias for POST /settings/email.';
  paths['/settings/email'].put.security = [{ Bearer: [] }];
  paths['/settings/email'].put.responses = {
    '200': jsonResponseInline(messageOnlyEnvelope())
  };
}

// GET /auth/google/status
if (paths['/auth/google/status']?.get) {
  paths['/auth/google/status'].get.summary = 'Check if Google OAuth is configured on this server.';
  paths['/auth/google/status'].get.responses = {
    '200': jsonResponseInline(envelopeOf({
      type: 'object',
      properties: {
        configured: { type: 'boolean' },
        clientIdPrefix: { type: 'string', nullable: true }
      }
    }))
  };
}

// GET /auth/health
if (paths['/auth/health']?.get) {
  paths['/auth/health'].get.summary = 'Auth health check.';
  paths['/auth/health'].get.responses = {
    '200': jsonResponseInline(envelopeOf({
      type: 'object', properties: { status: { type: 'string' }, timestamp: { type: 'string', format: 'date-time' } }
    }))
  };
}

// GET /auth/username-availability
if (paths['/auth/username-availability']?.get) {
  paths['/auth/username-availability'].get.summary = 'Check username availability (public, no auth required).';
  paths['/auth/username-availability'].get.responses = {
    '200': jsonResponseInline(envelopeRef('UsernameAvailabilityResponse'))
  };
}

// GET /settings/profile
if (paths['/settings/profile']?.get) {
  paths['/settings/profile'].get.summary = 'Get current user settings profile (includes subscription details).';
  paths['/settings/profile'].get.security = [{ Bearer: [] }];
}

// PUT /settings/password
if (paths['/settings/password']?.put) {
  paths['/settings/password'].put.summary = 'Change password (requires current password).';
  paths['/settings/password'].put.security = [{ Bearer: [] }];
  paths['/settings/password'].put.responses = {
    '200': jsonResponseInline(messageOnlyEnvelope())
  };
}
if (paths['/settings/password']?.post) {
  paths['/settings/password'].post.summary = 'Alias for PUT /settings/password.';
  paths['/settings/password'].post.security = [{ Bearer: [] }];
  paths['/settings/password'].post.responses = {
    '200': jsonResponseInline(messageOnlyEnvelope())
  };
}

// ── CREATORS: Fix /api/creator/me ──

if (paths['/api/creator/me']?.get) {
  paths['/api/creator/me'].get.summary = 'Get own creator profile. Returns partial response with needsUsername=true if username not yet set.';
  paths['/api/creator/me'].get.security = [{ Bearer: [] }];
  paths['/api/creator/me'].get.responses = {
    '200': jsonResponseInline({
      type: 'object',
      description: 'Returns CreatorMeResponse when creator row exists, or CreatorMePartialResponse when username not set.',
      properties: {
        success: { type: 'boolean' },
        data: {
          oneOf: [
            { $ref: '#/components/schemas/CreatorMeResponse' },
            { $ref: '#/components/schemas/CreatorMePartialResponse' }
          ],
          description: 'Check data.needsUsername to distinguish.'
        }
      }
    }),
    '401': { description: 'Unauthorized' }
  };
}

// PUT /api/creator/me — ADD MISSING
paths['/api/creator/me'] = paths['/api/creator/me'] || {};
paths['/api/creator/me'].put = {
  tags: ['Creators'],
  summary: 'Update own creator profile (username, displayName, bio, images, social links). Username is immutable once set.',
  security: [{ Bearer: [] }],
  requestBody: jsonRequestBody('UpdateCreatorProfileRequest'),
  responses: {
    '200': jsonResponseInline(envelopeRef('PublicCreatorDto')),
    '409': { description: 'Username already taken.' }
  }
};

// GET /api/creators/{creatorId}
if (paths['/api/creators/{creatorId}']?.get) {
  paths['/api/creators/{creatorId}'].get.summary = 'Get public creator profile by UUID.';
  paths['/api/creators/{creatorId}'].get.responses = {
    '200': jsonResponseInline(envelopeRef('PublicCreatorDto')),
    '404': { description: 'Creator not found.' }
  };
}

// GET /api/creators/resolve/{identifier}
if (paths['/api/creators/resolve/{identifier}']?.get) {
  paths['/api/creators/resolve/{identifier}'].get.summary = 'Resolve creator by legacy identifier (ApplicationUser.Id, UUID, or username).';
  paths['/api/creators/resolve/{identifier}'].get.responses = {
    '200': jsonResponseInline(envelopeRef('PublicCreatorDto')),
    '404': { description: 'Creator not found.' }
  };
}

// GET /api/creators/by-username/{username}
if (paths['/api/creators/by-username/{username}']?.get) {
  paths['/api/creators/by-username/{username}'].get.summary = 'Get public creator profile by username.';
  paths['/api/creators/by-username/{username}'].get.responses = {
    '200': jsonResponseInline(envelopeRef('PublicCreatorDto')),
    '404': { description: 'Creator not found.' }
  };
}

// GET /api/creators/{creatorId}/tracks
if (paths['/api/creators/{creatorId}/tracks']?.get) {
  paths['/api/creators/{creatorId}/tracks'].get.summary = 'Get tracks by creator UUID. Canonical endpoint.';
  paths['/api/creators/{creatorId}/tracks'].get.responses = {
    '200': jsonResponseInline(envelopeArrayRef('TrackResponse')),
    '404': { description: 'Creator not found.' }
  };
}

// GET /creator/tracks/{slug}
if (paths['/creator/tracks/{slug}']?.get) {
  paths['/creator/tracks/{slug}'].get.summary = 'Get tracks by creator slug (username) or GUID. Alias — canonical: GET /api/creators/{creatorId}/tracks.';
  paths['/creator/tracks/{slug}'].get.responses = {
    '200': jsonResponseInline(envelopeArrayRef('TrackResponse')),
    '404': { description: 'Creator not found.' }
  };
}

// GET /api/creators/username-availability
if (paths['/api/creators/username-availability']?.get) {
  paths['/api/creators/username-availability'].get.summary = 'Check username availability. Canonical endpoint.';
  paths['/api/creators/username-availability'].get.responses = {
    '200': jsonResponseInline(envelopeRef('UsernameAvailabilityResponse'))
  };
}

// GET /creator/username-availability
if (paths['/creator/username-availability']?.get) {
  paths['/creator/username-availability'].get.summary = 'Check username availability. Alias for GET /api/creators/username-availability.';
  paths['/creator/username-availability'].get.responses = {
    '200': jsonResponseInline(envelopeRef('UsernameAvailabilityResponse'))
  };
}

// ── CREATOR PROFILE: Fix responses ──

// GET /creator-profile/{slug}
if (paths['/creator-profile/{slug}']?.get) {
  paths['/creator-profile/{slug}'].get.summary = 'Get public creator profile by slug.';
  paths['/creator-profile/{slug}'].get.responses = {
    '200': jsonResponseInline(envelopeRef('CreatorProfileDto')),
    '404': { description: 'Creator not found.' }
  };
}

// GET /creator-profile/{slug}/storefront
if (paths['/creator-profile/{slug}/storefront']?.get) {
  paths['/creator-profile/{slug}/storefront'].get.summary = 'Full creator storefront. Requires creator_storefront feature flag.';
  paths['/creator-profile/{slug}/storefront'].get.responses = {
    '200': jsonResponseInline(envelopeRef('StorefrontResponse')),
    '404': { description: 'Creator not found or storefront feature disabled.' }
  };
}

// GET /creator-profile/me
if (paths['/creator-profile/me']?.get) {
  paths['/creator-profile/me'].get.summary = 'Get own creator profile. Returns { exists: false } if profile not yet created, or full profile if exists.';
  paths['/creator-profile/me'].get.security = [{ Bearer: [] }];
  paths['/creator-profile/me'].get.responses = {
    '200': jsonResponseInline({
      type: 'object',
      description: 'Envelope with data=CreatorProfileMeResponse when profile exists, or data={ exists: false } when not yet created.',
      properties: {
        success: { type: 'boolean' },
        data: {
          oneOf: [
            { $ref: '#/components/schemas/CreatorProfileMeResponse' },
            { type: 'object', properties: { exists: { type: 'boolean', description: 'Always false when profile not yet created.' } } }
          ]
        }
      }
    })
  };
}

// PUT /creator-profile/me
if (paths['/creator-profile/me']?.put) {
  paths['/creator-profile/me'].put.summary = 'Create or update own creator profile (slug, bio, niche, social links, stats toggles).';
  paths['/creator-profile/me'].put.security = [{ Bearer: [] }];
  paths['/creator-profile/me'].put.responses = {
    '200': jsonResponseInline(envelopeRef('CreatorProfileDto')),
    '409': { description: 'Slug already taken.' }
  };
}

// PATCH /creator-profile/me/settings
if (paths['/creator-profile/me/settings']?.patch) {
  paths['/creator-profile/me/settings'].patch.summary = 'Toggle showEarnings / showDownloadStats.';
  paths['/creator-profile/me/settings'].patch.security = [{ Bearer: [] }];
  paths['/creator-profile/me/settings'].patch.responses = {
    '200': jsonResponseInline(envelopeRef('CreatorProfileDto')),
    '404': { description: 'Create a profile first.' }
  };
}

// Image upload endpoints
const imageUploadPaths = {
  '/creator-profile/me/banner': 'Upload banner/cover image. Canonical endpoint.',
  '/creator-profile/me/cover-image-upload': 'Upload cover image. Alias for POST /creator-profile/me/banner.',
  '/creator-profile/me/avatar': 'Upload avatar/profile image. Canonical endpoint.',
  '/creator-profile/me/profile-image-upload': 'Upload profile image. Alias for POST /creator-profile/me/avatar.',
  '/settings/profile/avatar': 'Upload profile avatar. Alias for POST /creator-profile/me/avatar.'
};
for (const [p, desc] of Object.entries(imageUploadPaths)) {
  if (paths[p]?.post) {
    paths[p].post.summary = desc;
    paths[p].post.security = [{ Bearer: [] }];
    paths[p].post.responses = {
      '200': jsonResponseInline(envelopeRef('ImageUploadResponse'))
    };
  }
}

// Collections
if (paths['/creator-profile/me/collections']?.post) {
  paths['/creator-profile/me/collections'].post.summary = 'Create a track collection.';
  paths['/creator-profile/me/collections'].post.security = [{ Bearer: [] }];
  paths['/creator-profile/me/collections'].post.responses = {
    '201': jsonResponseInline(envelopeRef('TrackCollectionDto'))
  };
}

if (paths['/creator-profile/me/collections/{collectionId}']) {
  if (paths['/creator-profile/me/collections/{collectionId}'].put) {
    paths['/creator-profile/me/collections/{collectionId}'].put.summary = 'Update a collection. Only the owner can update.';
    paths['/creator-profile/me/collections/{collectionId}'].put.security = [{ Bearer: [] }];
    paths['/creator-profile/me/collections/{collectionId}'].put.responses = {
      '200': jsonResponseInline(envelopeRef('TrackCollectionDto'))
    };
  }
  if (paths['/creator-profile/me/collections/{collectionId}'].delete) {
    paths['/creator-profile/me/collections/{collectionId}'].delete.summary = 'Delete a collection. Only the owner can delete.';
    paths['/creator-profile/me/collections/{collectionId}'].delete.security = [{ Bearer: [] }];
    paths['/creator-profile/me/collections/{collectionId}'].delete.responses = {
      '204': { description: 'Collection deleted.' }
    };
  }
}

// Pinned tracks
if (paths['/creator-profile/me/pinned-tracks']?.put) {
  paths['/creator-profile/me/pinned-tracks'].put.summary = 'Update pinned track list.';
  paths['/creator-profile/me/pinned-tracks'].put.security = [{ Bearer: [] }];
  paths['/creator-profile/me/pinned-tracks'].put.responses = {
    '200': jsonResponseInline(envelopeOf({
      type: 'object',
      properties: {
        pinnedTrackIds: { type: 'string', nullable: true, description: 'Comma-separated GUIDs of pinned tracks.' }
      }
    }))
  };
}

// Follow endpoint
if (paths['/api/creators/{creatorId}/follow']?.post) {
  paths['/api/creators/{creatorId}/follow'].post.summary = 'Follow a creator. Idempotent.';
  paths['/api/creators/{creatorId}/follow'].post.security = [{ Bearer: [] }];
}
if (paths['/api/creators/{creatorId}/follow']?.delete) {
  paths['/api/creators/{creatorId}/follow'].delete.summary = 'Unfollow a creator. Idempotent.';
  paths['/api/creators/{creatorId}/follow'].delete.security = [{ Bearer: [] }];
}

// ── USERS: Fix responses ──

if (paths['/users/{username}']?.get) {
  paths['/users/{username}'].get.summary = 'Get public user profile by username.';
  paths['/users/{username}'].get.responses = {
    '200': jsonResponseInline(envelopeRef('UserPublicProfile')),
    '404': { description: 'User not found.' }
  };
}

if (paths['/users/me']?.patch) {
  paths['/users/me'].patch.summary = 'Update own profile (bio, profileImageUrl, coverImageUrl). Syncs to CreatorProfile.';
  paths['/users/me'].patch.security = [{ Bearer: [] }];
  paths['/users/me'].patch.responses = {
    '200': jsonResponseInline(envelopeRef('UserProfileUpdateResponse'))
  };
}

// ── CREATOR (singular): Fix/add ──

// PUT /creator/tracks/{trackId} — ADD MISSING
paths['/creator/tracks/{trackId}'] = {
  put: {
    tags: ['Creator'],
    summary: 'Edit track metadata. Only the track owner can edit.',
    security: [{ Bearer: [] }],
    parameters: [{
      name: 'trackId',
      in: 'path',
      required: true,
      schema: { type: 'string', format: 'uuid' }
    }],
    requestBody: jsonRequestBody('EditTrackRequest'),
    responses: {
      '200': jsonResponseInline(envelopeRef('TrackResponse')),
      '403': { description: 'Not the track owner.' },
      '404': { description: 'Track not found.' }
    }
  }
};

// GET /api/uploads/creator-image-url
if (paths['/api/uploads/creator-image-url']?.post) {
  paths['/api/uploads/creator-image-url'].post.summary = 'Generate a presigned PUT URL for direct S3/R2 image upload.';
  paths['/api/uploads/creator-image-url'].post.security = [{ Bearer: [] }];
  paths['/api/uploads/creator-image-url'].post.responses = {
    '200': jsonResponseInline(envelopeRef('PresignedUploadResponse'))
  };
}

// ── V1 API: Add missing endpoints ──

// GET /api/v1/genres — ADD MISSING
paths['/api/v1/genres'] = {
  get: {
    tags: ['V1 Catalog'],
    summary: 'List distinct genres with track counts.',
    responses: {
      '200': jsonResponseInline(envelopeArrayRef('GenreItem'))
    }
  }
};

// POST /api/v1/keys — ADD MISSING
paths['/api/v1/keys'] = {
  post: {
    tags: ['V1 API Keys'],
    summary: 'Create a new API key. Raw key is returned once and never stored. Requires JWT (cannot use API key to create keys).',
    security: [{ Bearer: [] }],
    requestBody: jsonRequestBody('CreateApiKeyRequest'),
    responses: {
      '200': jsonResponseInline(envelopeRef('ApiKeyCreateResponse'))
    }
  },
  get: {
    tags: ['V1 API Keys'],
    summary: 'List active API keys (prefix + metadata, no hashes). Requires JWT.',
    security: [{ Bearer: [] }],
    responses: {
      '200': jsonResponseInline(envelopeArrayRef('ApiKeyListItem'))
    }
  }
};

// DELETE /api/v1/keys/{id} — ADD MISSING
paths['/api/v1/keys/{id}'] = {
  delete: {
    tags: ['V1 API Keys'],
    summary: 'Revoke (soft-delete) an API key. Requires JWT.',
    security: [{ Bearer: [] }],
    parameters: [{
      name: 'id',
      in: 'path',
      required: true,
      schema: { type: 'string', format: 'uuid' }
    }],
    responses: {
      '204': { description: 'Key revoked.' },
      '404': { description: 'Key not found.' }
    }
  }
};

// ── V1 Track endpoints: Fix responses ──

if (paths['/api/v1/tracks']?.get) {
  // Verify it exists — just fix description
  // The path might not exist yet if it wasn't in the auto-gen
}

// ── ADMIN: Add missing upgrade endpoints ──

paths['/admin/upgrade-tier'] = {
  post: {
    tags: ['Admin'],
    summary: 'Bulk upgrade user tiers. Requires Admin role.',
    security: [{ Bearer: [] }],
    requestBody: jsonRequestBody('UpgradeTierRequest'),
    responses: {
      '200': jsonResponseInline(envelopeOf({
        type: 'object',
        properties: {
          upgraded: { type: 'integer' },
          message: { type: 'string' }
        }
      }))
    }
  }
};

paths['/admin/users/{id}/upgrade-tier'] = {
  post: {
    tags: ['Admin'],
    summary: 'Upgrade a specific user\'s tier. Requires Admin role.',
    security: [{ Bearer: [] }],
    parameters: [{
      name: 'id',
      in: 'path',
      required: true,
      schema: { type: 'string' }
    }],
    requestBody: jsonRequestBody('UpgradeTierRequest'),
    responses: {
      '200': jsonResponseInline(messageOnlyEnvelope()),
      '404': { description: 'User not found.' }
    }
  }
};

// ── STEP 6: License verify paths ──
// Add /api/v1/licenses/{id}/verify if missing
if (!paths['/api/v1/licenses/{id}/verify']) {
  paths['/api/v1/licenses/{id}/verify'] = {
    get: {
      tags: ['V1 Licenses'],
      summary: 'Verify a license certificate. Public endpoint. Canonical V1 path.',
      parameters: [{
        name: 'id',
        in: 'path',
        required: true,
        schema: { type: 'string', format: 'uuid' }
      }],
      responses: {
        '200': jsonResponseInline(envelopeRef('LicenseVerifyResponse')),
        '404': { description: 'License not found.' }
      }
    }
  };
}

// Fix existing /licenses/{licenseId}/verify if present — mark as alias
const legacyVerify = paths['/licenses/{licenseId}/verify'] || paths['/licenses/{licenseId}'];
if (paths['/licenses/{licenseId}/verify']?.get) {
  paths['/licenses/{licenseId}/verify'].get.summary = 'Verify license certificate. Alias — canonical: GET /api/v1/licenses/{id}/verify.';
  paths['/licenses/{licenseId}/verify'].get.responses = {
    '200': jsonResponseInline(envelopeRef('LicenseVerifyResponse')),
    '404': { description: 'License not found.' }
  };
}

// Fix /licenses/{licenseId} GET response
if (paths['/licenses/{licenseId}']?.get) {
  paths['/licenses/{licenseId}'].get.summary = 'Get license certificate details.';
  paths['/licenses/{licenseId}'].get.security = [{ Bearer: [] }];
  paths['/licenses/{licenseId}'].get.responses = {
    '200': jsonResponseInline(envelopeRef('LicenseCertificateDto')),
    '404': { description: 'License not found.' }
  };
}

// Fix /licenses GET response
if (paths['/licenses']?.get) {
  paths['/licenses'].get.summary = 'List license certificates for the current user.';
  paths['/licenses'].get.security = [{ Bearer: [] }];
  paths['/licenses'].get.responses = {
    '200': jsonResponseInline(envelopeArrayRef('LicenseCertificateDto'))
  };
}

// ── Add V1 licenses POST if missing ──
if (!paths['/api/v1/licenses']) {
  paths['/api/v1/licenses'] = {
    post: {
      tags: ['V1 Licenses'],
      summary: 'Initiate license purchase checkout. Returns a Stripe checkout URL.',
      security: [{ Bearer: [] }],
      requestBody: {
        content: {
          'application/json': {
            schema: {
              type: 'object',
              properties: {
                trackId: { type: 'string', format: 'uuid' },
                licenseType: { type: 'string', enum: ['nonexclusive', 'exclusive', 'copyright_buyout'] }
              },
              required: ['trackId', 'licenseType']
            }
          }
        }
      },
      responses: {
        '200': jsonResponseInline({
          type: 'object',
          description: 'V1 raw response (not wrapped in ApiResponse envelope).',
          properties: {
            success: { type: 'boolean' },
            checkoutUrl: { type: 'string', description: 'Stripe checkout URL to redirect the user to.' },
            status: { type: 'string' }
          }
        })
      }
    }
  };
}

// ── Add V1 tracks if missing ──
if (!paths['/api/v1/tracks']) {
  paths['/api/v1/tracks'] = {
    get: {
      tags: ['V1 Catalog'],
      summary: 'Search and browse tracks with pagination.',
      parameters: [
        { name: 'genre', in: 'query', schema: { type: 'string' } },
        { name: 'mood', in: 'query', schema: { type: 'string' } },
        { name: 'search', in: 'query', schema: { type: 'string' } },
        { name: 'tempo', in: 'query', schema: { type: 'string' } },
        { name: 'instrumental', in: 'query', schema: { type: 'boolean' } },
        { name: 'sort', in: 'query', schema: { type: 'string' } },
        { name: 'page', in: 'query', schema: { type: 'integer', default: 1 } },
        { name: 'limit', in: 'query', schema: { type: 'integer', default: 20, maximum: 100 } }
      ],
      responses: {
        '200': jsonResponseInline({
          type: 'object',
          properties: {
            success: { type: 'boolean' },
            data: { type: 'array', items: { $ref: '#/components/schemas/TrackResponse' } },
            meta: { $ref: '#/components/schemas/V1PaginationMeta' }
          }
        })
      }
    }
  };
}

if (!paths['/api/v1/tracks/{id}']) {
  paths['/api/v1/tracks/{id}'] = {
    get: {
      tags: ['V1 Catalog'],
      summary: 'Get track details by ID (GUID or CambrianTrackId).',
      parameters: [{
        name: 'id',
        in: 'path',
        required: true,
        schema: { type: 'string' }
      }],
      responses: {
        '200': jsonResponseInline(envelopeRef('TrackResponse')),
        '404': { description: 'Track not found.' }
      }
    }
  };
}

// V1 Creators
if (!paths['/api/v1/creators/{identifier}']) {
  paths['/api/v1/creators/{identifier}'] = {
    get: {
      tags: ['V1 Creators'],
      summary: 'Get public creator profile by UUID or username.',
      parameters: [{
        name: 'identifier',
        in: 'path',
        required: true,
        schema: { type: 'string' }
      }],
      responses: {
        '200': jsonResponseInline(envelopeRef('PublicCreatorDto')),
        '404': { description: 'Creator not found.' }
      }
    }
  };
}

// ── Security schemes ──
if (!spec.components.securitySchemes) {
  spec.components.securitySchemes = {};
}
spec.components.securitySchemes.Bearer = {
  type: 'http',
  scheme: 'bearer',
  bearerFormat: 'JWT',
  description: 'JWT bearer token. Also accepts auth_token cookie.'
};
spec.components.securitySchemes.ApiKey = {
  type: 'apiKey',
  in: 'header',
  name: 'X-API-Key',
  description: 'API key (cbr_ prefix). Cannot be used for key management endpoints.'
};

// ──────────────────────────────────────────────────────────────────────
// Write output
// ──────────────────────────────────────────────────────────────────────

const output = JSON.stringify(spec, null, 2);
fs.writeFileSync(specPath, output + '\n', 'utf-8');

// Count changes
const pathCount = Object.keys(spec.paths).length;
const schemaCount = Object.keys(spec.components.schemas).length;
console.log(`✅ OpenAPI spec updated:`);
console.log(`   Paths: ${pathCount}`);
console.log(`   Schemas: ${schemaCount}`);
console.log(`   Written to: ${specPath}`);
