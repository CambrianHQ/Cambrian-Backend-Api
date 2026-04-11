#!/usr/bin/env node
// Send the "upgraded to Pro for life" announcement to every active Cambrian user.
//
// SAFETY: dry-run by default. You must pass --send to actually hit Resend.
//
// Usage:
//   RESEND_API_KEY=... DATABASE_URL=... node send-pro-upgrade-email.mjs              # dry run
//   RESEND_API_KEY=... DATABASE_URL=... node send-pro-upgrade-email.mjs --send       # live
//   RESEND_API_KEY=... DATABASE_URL=... node send-pro-upgrade-email.mjs --send --limit=5
//
// Optional flags:
//   --limit=N        only send to the first N recipients (useful for staged rollouts)
//   --rate=MS        ms to wait between sends (default 500ms = 2/sec; raise if Resend 429s)
//   --from="Name <addr@domain>"
//   --reply-to="addr@domain"
//   --subject="..."
//
// Files written in this directory:
//   sent.log      — one JSON line per successful send (prevents duplicates on rerun)
//   failed.log    — one JSON line per failure (rerun the script to retry failures)

import { Resend } from 'resend';
import pg from 'pg';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// ---------- Tiny .env loader (no extra dep) ----------
// If a .env file exists next to the script, load KEY=VALUE pairs into
// process.env (without overwriting existing env vars). This sidesteps the
// PowerShell double-quote/$-interpolation footgun entirely — put the secrets
// in .env (which is gitignored) and just run `node send-pro-upgrade-email.mjs`.
(function loadDotEnv() {
  const envPath = path.join(__dirname, '.env');
  if (!fs.existsSync(envPath)) return;
  const content = fs.readFileSync(envPath, 'utf8');
  for (const rawLine of content.split('\n')) {
    const line = rawLine.trim();
    if (!line || line.startsWith('#')) continue;
    const eq = line.indexOf('=');
    if (eq < 0) continue;
    const key = line.slice(0, eq).trim();
    let value = line.slice(eq + 1).trim();
    // Strip surrounding quotes if present
    if (
      (value.startsWith('"') && value.endsWith('"')) ||
      (value.startsWith("'") && value.endsWith("'"))
    ) {
      value = value.slice(1, -1);
    }
    if (!(key in process.env)) process.env[key] = value;
  }
})();

// ---------- CLI args ----------
const args = Object.fromEntries(
  process.argv.slice(2).map((a) => {
    const m = a.match(/^--([^=]+)(?:=(.*))?$/);
    if (!m) return [a, true];
    return [m[1], m[2] ?? true];
  }),
);

const SEND = args.send === true;
const LIMIT = args.limit ? parseInt(args.limit, 10) : Infinity;
const RATE_MS = args.rate ? parseInt(args.rate, 10) : 500; // 2 emails/sec default

// Allowlist file — one email per line, # for comments. The script intersects
// this list with the active users in the DB and will ONLY send to addresses
// that appear in BOTH. This is a hard filter: anything not in this file is
// never contacted. Override path with --allowlist=/path/to/file.
const ALLOWLIST_PATH = args.allowlist
  ? path.resolve(args.allowlist)
  : path.join(__dirname, 'recipients.txt');

const FROM = args.from ?? 'Logan from Cambrian <logan@cambrianmusic.com>';
const REPLY_TO = args['reply-to'] ?? 'logan@cambrianmusic.com';
// Default subject is deliberately spam-safe: carries the "upgraded to Pro for
// life" meaning but avoids the literal phrase "for life" (a Gmail/SpamAssassin
// trigger alongside "free", "act now", "limited time"). "Permanently" is
// transactional and carries no such signal. Override with --subject="..." if
// you want to tune it.
const SUBJECT =
  args.subject ?? 'Your Cambrian account has been upgraded to Pro — permanently';

// ---------- Env ----------
const DATABASE_URL = process.env.DATABASE_URL;
const RESEND_API_KEY = process.env.RESEND_API_KEY;

if (!DATABASE_URL) {
  console.error('ERROR: DATABASE_URL env var is required.');
  console.error('  Set it in scripts/email-pro-upgrade/.env, or pass it via your shell.');
  console.error("  PowerShell: use SINGLE quotes to avoid $ interpolation —");
  console.error("    $env:DATABASE_URL = 'postgresql://user:pass@host/db?sslmode=require'");
  process.exit(1);
}
// Fail fast with a clear message if the URL is obviously malformed — otherwise
// pg throws a cryptic "TypeError: Invalid URL" deep in its parser. The most
// common cause is PowerShell double-quote interpolation eating a $-prefixed
// segment of the Render password.
if (!/^postgres(ql)?:\/\//i.test(DATABASE_URL)) {
  console.error('ERROR: DATABASE_URL does not start with postgres:// or postgresql://');
  console.error(`  got: ${DATABASE_URL.slice(0, 20)}${DATABASE_URL.length > 20 ? '...' : ''}`);
  console.error('  If you set this in PowerShell with double quotes, a $-prefixed');
  console.error('  segment of the password may have been interpolated away. Use');
  console.error("  SINGLE quotes instead: $env:DATABASE_URL = 'postgresql://...'");
  process.exit(1);
}
try {
  // eslint-disable-next-line no-new
  new URL(DATABASE_URL);
} catch {
  console.error('ERROR: DATABASE_URL is not a valid URL.');
  console.error('  Likely cause: PowerShell double-quote interpolation mangled the value.');
  console.error("  Fix: use single quotes — $env:DATABASE_URL = 'postgresql://...'");
  console.error('  Or put it in scripts/email-pro-upgrade/.env and rerun.');
  process.exit(1);
}
if (SEND && !RESEND_API_KEY) {
  console.error('ERROR: RESEND_API_KEY env var is required when running with --send.');
  process.exit(1);
}

// ---------- Log paths ----------
const SENT_LOG_PATH = path.join(__dirname, 'sent.log');
const FAILED_LOG_PATH = path.join(__dirname, 'failed.log');

// ---------- Email body (exact wording preserved per request) ----------
const TEXT_BODY = `Hey there,

Logan here, founder of Cambrian. I wanted to personally thank you for being one of our earliest users. That means a lot.

As a thank you, your account has been upgraded to Cambrian Pro — for life. That means unlimited uploads, full analytics, and priority payouts. No charge, ever.

A few things we've shipped recently:

- Rebuilt audio streaming — faster, more reliable playback across all devices
- Fixed upload flow — files now process correctly every time
- Improved creator profiles — avatars, bios, and storefronts are all working smoothly

Coming very soon:
- Full Stripe checkout — buyers will be able to purchase and license tracks directly
- Instant payouts through Stripe for creators

We're still early. There are rough edges and we're fixing things every day. But the core is here — upload your music, set your price, get paid. Any generator, any genre.

If you run into anything broken or have ideas, just reply to this email. I read every one.

Thank you for believing in this early.

— Logan
Founder, Cambrian
cambrianmusic.com
`;

// Deliberately minimal HTML — system font stack, no tables, no images, no
// tracking pixels, no inline CSS beyond line-height. Anything fancier looks
// like bulk marketing to Gmail's classifier.
const HTML_BODY = `<!doctype html>
<html>
  <body style="font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:16px;line-height:1.6;color:#111;max-width:600px;margin:0 auto;padding:24px;">
    <p>Hey there,</p>
    <p>Logan here, founder of Cambrian. I wanted to personally thank you for being one of our earliest users. That means a lot.</p>
    <p>As a thank you, your account has been upgraded to <strong>Cambrian Pro &mdash; for life</strong>. That means unlimited uploads, full analytics, and priority payouts. No charge, ever.</p>
    <p>A few things we&rsquo;ve shipped recently:</p>
    <ul>
      <li>Rebuilt audio streaming &mdash; faster, more reliable playback across all devices</li>
      <li>Fixed upload flow &mdash; files now process correctly every time</li>
      <li>Improved creator profiles &mdash; avatars, bios, and storefronts are all working smoothly</li>
    </ul>
    <p>Coming very soon:</p>
    <ul>
      <li>Full Stripe checkout &mdash; buyers will be able to purchase and license tracks directly</li>
      <li>Instant payouts through Stripe for creators</li>
    </ul>
    <p>We&rsquo;re still early. There are rough edges and we&rsquo;re fixing things every day. But the core is here &mdash; upload your music, set your price, get paid. Any generator, any genre.</p>
    <p>If you run into anything broken or have ideas, just reply to this email. I read every one.</p>
    <p>Thank you for believing in this early.</p>
    <p>&mdash; Logan<br/>Founder, Cambrian<br/><a href="https://cambrianmusic.com">cambrianmusic.com</a></p>
  </body>
</html>
`;

// ---------- Helpers ----------
function loadSentSet() {
  if (!fs.existsSync(SENT_LOG_PATH)) return new Set();
  const lines = fs.readFileSync(SENT_LOG_PATH, 'utf8').split('\n').filter(Boolean);
  const set = new Set();
  for (const line of lines) {
    try {
      const entry = JSON.parse(line);
      if (entry?.email) set.add(entry.email.toLowerCase());
    } catch {
      /* skip malformed */
    }
  }
  return set;
}

function appendSent(email, messageId) {
  fs.appendFileSync(
    SENT_LOG_PATH,
    JSON.stringify({ email, messageId, sentAt: new Date().toISOString() }) + '\n',
  );
}

function appendFailed(email, error) {
  fs.appendFileSync(
    FAILED_LOG_PATH,
    JSON.stringify({
      email,
      error: String(error?.message ?? error),
      attemptedAt: new Date().toISOString(),
    }) + '\n',
  );
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function isValidEmail(s) {
  return typeof s === 'string' && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(s);
}

// Load the explicit allowlist file. One email per line, # for comments,
// blank lines ignored, case-insensitive. Returns a Set of lowercased emails.
// Hard-fails if the file is missing — we never send without an explicit list.
function loadAllowlist() {
  if (!fs.existsSync(ALLOWLIST_PATH)) {
    console.error(`ERROR: allowlist file not found: ${ALLOWLIST_PATH}`);
    console.error('  Create recipients.txt (one email per line) or pass --allowlist=/path/to/file');
    process.exit(1);
  }
  const set = new Set();
  const lines = fs.readFileSync(ALLOWLIST_PATH, 'utf8').split('\n');
  for (const raw of lines) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    set.add(line.toLowerCase());
  }
  if (set.size === 0) {
    console.error(`ERROR: allowlist file ${ALLOWLIST_PATH} is empty.`);
    process.exit(1);
  }
  return set;
}

// ---------- Main ----------
async function main() {
  console.log(`Mode:      ${SEND ? 'LIVE SEND' : 'DRY RUN (pass --send to actually send)'}`);
  console.log(`From:      ${FROM}`);
  console.log(`Reply-To:  ${REPLY_TO}`);
  console.log(`Subject:   ${SUBJECT}`);
  console.log(`Rate:      1 email per ${RATE_MS}ms`);
  console.log(`Allowlist: ${ALLOWLIST_PATH}`);
  if (LIMIT !== Infinity) console.log(`Limit:     ${LIMIT}`);
  console.log('');

  // ---- Load the explicit allowlist ----
  const allowlist = loadAllowlist();
  console.log(`Allowlist entries: ${allowlist.size}`);

  // ---- Pull users from the DB ----
  const needsSsl =
    /render\.com|supabase|amazonaws|neon\.tech/.test(DATABASE_URL) ||
    /sslmode=require/.test(DATABASE_URL);

  const client = new pg.Client({
    connectionString: DATABASE_URL,
    ssl: needsSsl ? { rejectUnauthorized: false } : undefined,
  });
  await client.connect();

  let users;
  try {
    const res = await client.query(`
      SELECT LOWER(TRIM("Email")) AS email,
             "DisplayName"         AS display_name
      FROM "AspNetUsers"
      WHERE "Email" IS NOT NULL
        AND TRIM("Email") <> ''
        AND ("Status" IS NULL OR "Status" = 'active')
      ORDER BY "CreatedAt" ASC
    `);
    users = res.rows;
  } finally {
    await client.end();
  }

  console.log(`Fetched ${users.length} active users from database.`);

  // ---- Dedupe + validate ----
  const seen = new Set();
  const deduped = [];
  let invalidCount = 0;
  for (const u of users) {
    if (!isValidEmail(u.email)) {
      invalidCount++;
      continue;
    }
    if (seen.has(u.email)) continue;
    seen.add(u.email);
    deduped.push(u);
  }
  if (invalidCount > 0) console.log(`Skipped ${invalidCount} invalid / blank emails.`);
  console.log(`After dedupe + validation: ${deduped.length} unique recipients in DB.`);

  // ---- Intersect DB results with the allowlist ----
  // Hard filter: only send to addresses that are in BOTH the DB and the
  // allowlist file. Anything else is silently skipped.
  const dbEmailSet = new Set(deduped.map((u) => u.email));
  const allowlisted = deduped.filter((u) => allowlist.has(u.email));
  const missingFromDb = [...allowlist].filter((e) => !dbEmailSet.has(e));

  console.log(`In allowlist ∩ DB:         ${allowlisted.length}`);
  if (missingFromDb.length > 0) {
    console.log(
      `In allowlist but NOT in DB: ${missingFromDb.length} (check for typos — these will NOT be sent):`,
    );
    for (const e of missingFromDb) console.log(`    ${e}`);
  }

  // ---- Exclude already sent ----
  const alreadySent = loadSentSet();
  if (alreadySent.size > 0) {
    console.log(`Already sent previously (sent.log): ${alreadySent.size} — will be skipped.`);
  }
  const toSend = allowlisted.filter((u) => !alreadySent.has(u.email)).slice(0, LIMIT);
  console.log(`Will send to: ${toSend.length} recipient(s).`);
  console.log('');

  if (!SEND) {
    console.log('---- DRY RUN — recipients that WOULD receive the email ----');
    for (const u of toSend) {
      console.log(`  ${u.email}${u.display_name ? ` (${u.display_name})` : ''}`);
    }
    console.log('');
    console.log('Re-run with --send to actually send. Before going live, verify:');
    console.log('  • cambrianmusic.com is verified in Resend (SPF + DKIM green)');
    console.log('  • DMARC TXT exists (p=none is fine to start)');
    console.log('  • Open/click tracking is OFF for this domain in the Resend dashboard');
    console.log('  • Run --limit=5 first and eyeball placement in Gmail/Outlook');
    return;
  }

  // ---- Live send ----
  const resend = new Resend(RESEND_API_KEY);
  let sentCount = 0;
  let failedCount = 0;

  for (let i = 0; i < toSend.length; i++) {
    const u = toSend[i];
    const progress = `[${i + 1}/${toSend.length}]`;

    try {
      const result = await resend.emails.send({
        from: FROM,
        to: u.email,
        subject: SUBJECT,
        text: TEXT_BODY,
        html: HTML_BODY,
        replyTo: REPLY_TO,
        // Gmail 2024 bulk-sender rules: List-Unsubscribe header + One-Click
        // post. mailto form requires no backend endpoint. Also helps Yahoo.
        headers: {
          'List-Unsubscribe':
            '<mailto:unsubscribe@cambrianmusic.com?subject=unsubscribe>',
          'List-Unsubscribe-Post': 'List-Unsubscribe=One-Click',
          // Custom X- header makes the message look transactional to some
          // classifiers (harmless if ignored).
          'X-Entity-Ref-ID': `cambrian-pro-upgrade-${Date.now()}-${i}`,
        },
      });

      if (result.error) {
        throw new Error(
          typeof result.error === 'string' ? result.error : JSON.stringify(result.error),
        );
      }

      const id = result.data?.id ?? null;
      appendSent(u.email, id);
      sentCount++;
      console.log(`${progress} OK  ${u.email}  id=${id ?? 'n/a'}`);
    } catch (err) {
      appendFailed(u.email, err);
      failedCount++;
      console.error(`${progress} ERR ${u.email}  ${err?.message ?? err}`);
    }

    if (i < toSend.length - 1) await sleep(RATE_MS);
  }

  console.log('');
  console.log('==== DONE ====');
  console.log(`Sent:   ${sentCount}`);
  console.log(`Failed: ${failedCount}`);
  console.log(`Sent log:   ${SENT_LOG_PATH}`);
  console.log(`Failed log: ${FAILED_LOG_PATH}`);
  if (failedCount > 0) {
    console.log('');
    console.log('To retry failures: fix the underlying issue, then rerun the script.');
    console.log('Addresses already in sent.log will be skipped automatically.');
  }
}

main().catch((err) => {
  console.error('FATAL:', err);
  process.exit(1);
});
