/**
 * Cross-bucket copy: Supabase cambrian-audio-prod → Supabase cambrian-audio-staging
 *
 * Streams every object from the prod bucket into the staging bucket so that
 * staging has the same seed images and audio files as production. Idempotent
 * when SKIP_EXISTING=true.
 *
 * Usage:
 *   node migrate-storage.mjs
 */

import {
  S3Client,
  ListObjectsV2Command,
  GetObjectCommand,
  PutObjectCommand,
  HeadObjectCommand,
} from "@aws-sdk/client-s3";

// ─── CONFIG ──────────────────────────────────────────────────────────────────

const SUPABASE_COMMON = {
  endpoint:   "https://vjfiakxdnmcqcktyrhik.supabase.co/storage/v1/s3",
  accessKey:  "7ec62745d42596a3896703bc17eb39a5",
  secretKey:  "6bd5f1c47295922258cf8204d30428ce6a020a360e280422c0983ea20625fc06",
  region:     "us-east-1",
};

const SOURCE = {
  ...SUPABASE_COMMON,
  bucket: "cambrian-audio-prod",
};

const DEST = {
  ...SUPABASE_COMMON,
  bucket: "cambrian-audio-staging",
};

// Set to true to skip objects that already exist in the destination (safe to re-run).
const SKIP_EXISTING = true;

// ─────────────────────────────────────────────────────────────────────────────

const sourceClient = new S3Client({
  endpoint:       SOURCE.endpoint,
  region:         SOURCE.region,
  credentials:    { accessKeyId: SOURCE.accessKey, secretAccessKey: SOURCE.secretKey },
  forcePathStyle: true,
});

const destClient = new S3Client({
  endpoint:       DEST.endpoint,
  region:         DEST.region,
  credentials:    { accessKeyId: DEST.accessKey, secretAccessKey: DEST.secretKey },
  forcePathStyle: true,
});

async function exists(key) {
  try {
    await destClient.send(new HeadObjectCommand({ Bucket: DEST.bucket, Key: key }));
    return true;
  } catch {
    return false;
  }
}

async function migrate() {
  let totalCopied = 0;
  let totalSkipped = 0;
  let totalFailed = 0;
  let continuationToken;

  console.log(`Copying: ${SOURCE.bucket} → ${DEST.bucket} (Supabase)\n`);

  do {
    const list = await sourceClient.send(new ListObjectsV2Command({
      Bucket:            SOURCE.bucket,
      ContinuationToken: continuationToken,
    }));

    const objects = list.Contents ?? [];
    console.log(`Page: ${objects.length} objects`);

    for (const obj of objects) {
      const key = obj.Key;

      if (SKIP_EXISTING && await exists(key)) {
        console.log(`  SKIP  ${key}`);
        totalSkipped++;
        continue;
      }

      try {
        const { Body, ContentType, ContentLength } = await sourceClient.send(
          new GetObjectCommand({ Bucket: SOURCE.bucket, Key: key })
        );

        await destClient.send(new PutObjectCommand({
          Bucket:        DEST.bucket,
          Key:           key,
          Body:          Body,
          ContentType:   ContentType ?? "application/octet-stream",
          ContentLength: ContentLength,
        }));

        console.log(`  OK    ${key}  (${formatBytes(ContentLength)})`);
        totalCopied++;
      } catch (err) {
        console.error(`  FAIL  ${key}: ${err.message}`);
        totalFailed++;
      }
    }

    continuationToken = list.NextContinuationToken;
  } while (continuationToken);

  console.log(`\nDone. Copied: ${totalCopied}  Skipped: ${totalSkipped}  Failed: ${totalFailed}`);
  if (totalFailed > 0) process.exit(1);
}

function formatBytes(bytes) {
  if (!bytes) return "?";
  if (bytes < 1024) return `${bytes}B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)}KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)}MB`;
}

migrate().catch(err => { console.error(err); process.exit(1); });
