#!/usr/bin/env node
// Debug helper: list all recipients grouped by domain so we can see what's
// real vs seeded before sending. Does NOT send anything.
import pg from 'pg';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// .env loader (same as main script)
const envPath = path.join(__dirname, '.env');
if (fs.existsSync(envPath)) {
  for (const line of fs.readFileSync(envPath, 'utf8').split('\n')) {
    const t = line.trim();
    if (!t || t.startsWith('#')) continue;
    const eq = t.indexOf('=');
    if (eq < 0) continue;
    const k = t.slice(0, eq).trim();
    let v = t.slice(eq + 1).trim();
    if ((v.startsWith('"') && v.endsWith('"')) || (v.startsWith("'") && v.endsWith("'")))
      v = v.slice(1, -1);
    if (!(k in process.env)) process.env[k] = v;
  }
}

const client = new pg.Client({
  connectionString: process.env.DATABASE_URL,
  ssl: { rejectUnauthorized: false },
});
await client.connect();
const { rows } = await client.query(`
  SELECT LOWER(TRIM("Email")) AS email, "DisplayName" AS name, "Role", "CreatedAt"
  FROM "AspNetUsers"
  WHERE "Email" IS NOT NULL AND TRIM("Email") <> ''
    AND ("Status" IS NULL OR "Status" = 'active')
  ORDER BY "Email"
`);
await client.end();

const byDomain = new Map();
for (const r of rows) {
  const domain = r.email.split('@')[1] ?? '(no domain)';
  if (!byDomain.has(domain)) byDomain.set(domain, []);
  byDomain.get(domain).push(r);
}

const sorted = [...byDomain.entries()].sort((a, b) => b[1].length - a[1].length);
console.log(`TOTAL: ${rows.length} active users\n`);
for (const [domain, users] of sorted) {
  console.log(`@${domain} — ${users.length}`);
  for (const u of users) {
    console.log(`  ${u.email.padEnd(45)} ${u.name ?? ''}`);
  }
  console.log('');
}
