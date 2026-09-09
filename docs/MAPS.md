# Maps and last-known position

Map metadata enters the application through `IMapDefinitionCache` and `MapDataService`. The normalized cache JSON reader accepts unknown fields so a source can add data without breaking an installed client, but it rejects missing map, floor, or extract identities. Each definition keeps its `DataProvenance`; map artwork is referenced rather than embedded and must retain its own attribution and distribution terms.

## Coordinate transforms

A transform is usable only when all values are finite, world and visual extents are positive, and floor ranges are finite, non-empty, and non-overlapping. World X/Z is mapped to the visual plane, with configured flips and rotation. World Y selects a floor using an inclusive lower and exclusive upper bound.

If a map has no transform, or validation fails, the application returns a clear status and no marker. It never estimates, clamps, or borrows coordinates from another map. `fixtures/maps/training-ground.json` is a code-authored test map and is not distributable third-party artwork.

## Screenshot observations

Normal EFT screenshot filenames are parsed as timestamp, X/Y/Z position, quaternion, optional in-game time, and duplicate index. The quaternion is normalized and converted to a compass heading; a zero quaternion and malformed or unsafe extension are rejected. Timestamp conversion uses the supplied local UTC offset because the filename itself has no zone.

Positions are always labeled last known. By default an observation becomes stale after two minutes, and one more than 30 seconds in the future is not plotted. Both thresholds are application-side safeguards rather than claims about the game. Captured image content is neither required nor persisted for position parsing.

## Extracts

Recognized active extracts are joined to cached static extract metadata by canonical ID. An active extract remains visible when its static position is missing, with explicit guidance that no marker can be shown. OCR confidence and source remain attached to the active observation.
