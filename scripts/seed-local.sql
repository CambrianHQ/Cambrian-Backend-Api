-- ========================================
-- RESET (safe for local)
-- ========================================

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

DROP TABLE IF EXISTS plays;
DROP TABLE IF EXISTS likes;
DROP TABLE IF EXISTS playlist_songs;
DROP TABLE IF EXISTS playlists;
DROP TABLE IF EXISTS follows;
DROP TABLE IF EXISTS user_songs;
DROP TABLE IF EXISTS users;
DROP TABLE IF EXISTS songs;
DROP TABLE IF EXISTS avatar_pool;
DROP TABLE IF EXISTS cover_pool;

-- ========================================
-- TABLES
-- ========================================

CREATE TABLE users (
  id UUID PRIMARY KEY,
  name TEXT,
  email TEXT,
  avatar_url TEXT,
  is_artist BOOLEAN DEFAULT false
);

CREATE TABLE songs (
  id UUID PRIMARY KEY,
  title TEXT,
  artist TEXT,
  duration INT,
  cover_url TEXT,
  play_count INT DEFAULT 0
);

CREATE TABLE avatar_pool (
  id SERIAL PRIMARY KEY,
  url TEXT
);

CREATE TABLE cover_pool (
  id SERIAL PRIMARY KEY,
  url TEXT
);

CREATE TABLE user_songs (
  user_id UUID,
  song_id UUID
);

CREATE TABLE likes (
  user_id UUID,
  song_id UUID
);

CREATE TABLE plays (
  user_id UUID,
  song_id UUID,
  play_count INT
);

CREATE TABLE playlists (
  id UUID PRIMARY KEY,
  user_id UUID,
  name TEXT
);

CREATE TABLE playlist_songs (
  playlist_id UUID,
  song_id UUID
);

CREATE TABLE follows (
  follower_id UUID,
  following_id UUID
);

-- ========================================
-- AVATAR POOL (30)
-- ========================================

INSERT INTO avatar_pool (url)
SELECT 'https://cdn.local/avatars/a' || i || '.png'
FROM generate_series(1, 30) i;

-- ========================================
-- COVER POOL (40)
-- ========================================

INSERT INTO cover_pool (url)
SELECT 'https://cdn.local/covers/c' || i || '.png'
FROM generate_series(1, 40) i;

-- ========================================
-- USERS (500)
-- ========================================

INSERT INTO users (id, name, email)
SELECT 
  gen_random_uuid(),
  'User ' || i,
  'user' || i || '@app.com'
FROM generate_series(1, 500) i;

-- ========================================
-- MARK SOME USERS AS ARTISTS (~10%)
-- ========================================

UPDATE users
SET is_artist = true
WHERE random() < 0.1;

-- ========================================
-- ASSIGN USER AVATARS
-- ========================================

UPDATE users
SET avatar_url = (
  SELECT url FROM avatar_pool ORDER BY RANDOM() LIMIT 1
);

-- ========================================
-- SONGS (300)
-- ========================================

INSERT INTO songs (id, title, artist, duration)
SELECT
  gen_random_uuid(),
  'Track ' || i,
  'Artist ' || (i % 50),
  (random() * 240 + 60)::int
FROM generate_series(1, 300) i;

-- ========================================
-- ASSIGN SONG COVERS (slightly biased)
-- ========================================

UPDATE songs
SET cover_url = (
  SELECT url FROM cover_pool
  ORDER BY RANDOM() * RANDOM()
  LIMIT 1
);

-- ========================================
-- GLOBAL SONG POPULARITY
-- ========================================

UPDATE songs
SET play_count = (random() * 5000)::int;

-- ========================================
-- USER → SONG RELATIONSHIPS
-- ========================================

INSERT INTO user_songs (user_id, song_id)
SELECT
  u.id,
  s.id
FROM users u
JOIN songs s ON random() < 0.04;

-- ========================================
-- LIKES
-- ========================================

INSERT INTO likes (user_id, song_id)
SELECT
  u.id,
  s.id
FROM users u
JOIN songs s ON random() < 0.06;

-- ========================================
-- PLAYS
-- ========================================

INSERT INTO plays (user_id, song_id, play_count)
SELECT
  u.id,
  s.id,
  (random() * 25)::int
FROM users u
JOIN songs s ON random() < 0.08;

-- ========================================
-- PLAYLISTS (~60% of users)
-- ========================================

INSERT INTO playlists (id, user_id, name)
SELECT
  gen_random_uuid(),
  u.id,
  CASE 
    WHEN random() < 0.3 THEN 'My Favorites'
    WHEN random() < 0.6 THEN 'Chill Vibes'
    ELSE 'Daily Mix'
  END
FROM users u
WHERE random() < 0.6;

-- ========================================
-- PLAYLIST SONGS
-- ========================================

INSERT INTO playlist_songs (playlist_id, song_id)
SELECT
  p.id,
  s.id
FROM playlists p
JOIN songs s ON random() < 0.07;

-- ========================================
-- USER FOLLOWS (normal)
-- ========================================

INSERT INTO follows (follower_id, following_id)
SELECT
  u1.id,
  u2.id
FROM users u1
JOIN users u2 ON u1.id <> u2.id
WHERE random() < 0.015;

-- ========================================
-- EXTRA FOLLOWS FOR ARTISTS (boost realism)
-- ========================================

INSERT INTO follows (follower_id, following_id)
SELECT
  u.id,
  a.id
FROM users u
JOIN users a ON a.is_artist = true
WHERE u.id <> a.id
AND random() < 0.2;

-- ========================================
-- DONE
-- ========================================

-- Quick sanity output
SELECT 
  (SELECT COUNT(*) FROM users) AS users,
  (SELECT COUNT(*) FROM songs) AS songs,
  (SELECT COUNT(*) FROM playlists) AS playlists,
  (SELECT COUNT(*) FROM follows) AS follows;
