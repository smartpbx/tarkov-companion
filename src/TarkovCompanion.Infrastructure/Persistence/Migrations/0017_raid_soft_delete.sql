-- #291 (Debrief): a raid can be deleted, with a preview and an undo while the workspace is open.
-- A row is marked deleted rather than removed so the delete can be undone; ListAsync excludes a
-- marked row, so it and the per-map stats it fed already read as gone. It is hard-deleted only
-- once another delete starts (the one-press undo the workspace offers covers the most recent
-- action only) or the app is restarted, both of which mean the undo that covered it can no longer
-- be pressed.
ALTER TABLE raids ADD COLUMN deleted_utc TEXT;
