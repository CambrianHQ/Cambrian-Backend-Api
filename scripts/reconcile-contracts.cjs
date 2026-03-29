#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const openApiPath = path.join(root, "contracts", "openapi.v1.json");

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function writeJson(filePath, value) {
  fs.writeFileSync(filePath, JSON.stringify(value, null, 2) + "\n", "utf8");
}

function ensureMessageResponse(operation) {
  if (!operation.responses) operation.responses = {};
  operation.responses["200"] = {
    description: operation.responses["200"]?.description || "OK",
    content: {
      "application/json": {
        schema: { $ref: "#/components/schemas/ApiResponseOfMessage" }
      }
    }
  };
}

const spec = readJson(openApiPath);

// Auth: message-only endpoints
ensureMessageResponse(spec.paths["/auth/forgot-password"].post);
ensureMessageResponse(spec.paths["/auth/verify-code"].post);
ensureMessageResponse(spec.paths["/auth/reset-password"].post);
ensureMessageResponse(spec.paths["/auth/recover-username"].post);

// Auth: add refresh
spec.paths["/auth/refresh"] = {
  post: {
    tags: ["Auth"],
    summary: "Refresh the current JWT",
    security: [{ Bearer: [] }],
    responses: {
      "200": {
        description: "Refreshed token",
        content: {
          "application/json": {
            schema: {
              type: "object",
              properties: {
                success: { type: "boolean" },
                data: {
                  type: "object",
                  properties: {
                    token: { type: "string" }
                  }
                },
                message: { type: "string", nullable: true },
                error: { type: "string", nullable: true }
              }
            }
          }
        }
      },
      "401": { description: "Unauthorized" },
      "404": { description: "User not found" }
    }
  }
};

// Upload: main track upload returns 201
if (spec.paths["/upload"]?.post?.responses?.["200"]) {
  spec.paths["/upload"].post.responses["201"] = spec.paths["/upload"].post.responses["200"];
  spec.paths["/upload"].post.responses["201"].description = "Created";
  delete spec.paths["/upload"].post.responses["200"];
}

// Upload: signed URL semantics
if (spec.paths["/uploads/image"]?.post) {
  spec.paths["/uploads/image"].post.summary = "Upload an image and receive its current signed access URL";
}

// Creator profile: ensure legacy alias paths exist alongside canonical upload routes
if (spec.paths["/creator-profile/me/cover-image-upload"] && !spec.paths["/creator-profile/me/banner"]) {
  spec.paths["/creator-profile/me/banner"] = JSON.parse(JSON.stringify(spec.paths["/creator-profile/me/cover-image-upload"]));
}

if (spec.paths["/creator-profile/me/profile-image-upload"] && !spec.paths["/creator-profile/me/avatar"]) {
  spec.paths["/creator-profile/me/avatar"] = JSON.parse(JSON.stringify(spec.paths["/creator-profile/me/profile-image-upload"]));
}

// Creator profile: delete collection is 204
if (spec.paths["/creator-profile/me/collections/{collectionId}"]?.delete) {
  spec.paths["/creator-profile/me/collections/{collectionId}"].delete.responses = {
    "204": {
      description: "No Content"
    },
    "404": {
      description: "Collection not found"
    },
    "403": {
      description: "Forbidden"
    }
  };
}

// Admin settings: POST is intentionally not implemented yet
if (spec.paths["/admin/settings"]?.post) {
  spec.paths["/admin/settings"].post.responses = {
    "501": {
      description: "Not Implemented",
      content: {
        "application/json": {
          schema: {
            type: "object",
            properties: {
              error: { type: "string" }
            }
          }
        }
      }
    }
  };
}

writeJson(openApiPath, spec);
console.log("Reconciled contracts/openapi.v1.json");
