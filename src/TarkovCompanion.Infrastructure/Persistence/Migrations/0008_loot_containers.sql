-- What each kind of container on a map is called.
--
-- A map's loot list carries a container id and a position and no name, so without this a
-- container on the map is a twenty-four character identifier. The catalog arrives in the same
-- payload as the maps themselves and has been discarded on every sync since the first one.
--
-- Only the normalized name is stored. Upstream's "name" field is the literal string
-- "578f87a3245977356274f2cb Name" for every container in the feed, so it is not a name and
-- storing it would only tempt somebody into rendering it.
CREATE TABLE loot_containers (
    id TEXT PRIMARY KEY,
    normalized_name TEXT NOT NULL
);
