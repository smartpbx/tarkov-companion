-- #379 step 1 needs a wiki deep link for tasks, the same way items already carry wiki_url.
-- Old caches stay readable: existing rows get a null link until the next sync fills it in.
ALTER TABLE quest_catalog_tasks ADD COLUMN wiki_url TEXT;
