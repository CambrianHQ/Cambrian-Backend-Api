#!/usr/bin/env node
// Patches openapi.v1.json to add POST /settings/profile/avatar endpoint
// and update the SettingsProfileResponse schema with profileImageUrl.
const fs = require('node:fs');
const path = require('node:path');

const file = path.join(__dirname, '../contracts/openapi.v1.json');
const spec = JSON.parse(fs.readFileSync(file, 'utf8'));

// 1. Add profileImageUrl to SettingsProfileResponse schema (if it exists)
if (spec.components?.schemas?.SettingsProfileResponse) {
  spec.components.schemas.SettingsProfileResponse.properties = {
    ...spec.components.schemas.SettingsProfileResponse.properties,
    profileImageUrl: { type: 'string', nullable: true }
  };
}

// 2. Add POST /settings/profile/avatar path
spec.paths['/settings/profile/avatar'] = {
  post: {
    tags: ['Settings'],
    summary: 'Upload or replace creator profile photo (Creator role required)',
    security: [{ Bearer: [] }],
    requestBody: {
      content: {
        'multipart/form-data': {
          schema: {
            type: 'object',
            properties: {
              file: { type: 'string', format: 'binary' }
            },
            required: ['file']
          }
        }
      }
    },
    responses: {
      '200': {
        description: 'OK',
        content: {
          'application/json': {
            schema: {
              properties: {
                success: { type: 'boolean' },
                data: {
                  properties: {
                    profileImageUrl: { type: 'string', nullable: true }
                  },
                  type: 'object'
                },
                message: { type: 'string', nullable: true },
                error: { type: 'string', nullable: true }
              },
              type: 'object'
            }
          }
        }
      },
      '400': { description: 'Invalid file' },
      '403': { description: 'Creator role required' }
    }
  }
};

fs.writeFileSync(file, JSON.stringify(spec), 'utf8');
console.log('openapi.v1.json patched with /settings/profile/avatar.');
