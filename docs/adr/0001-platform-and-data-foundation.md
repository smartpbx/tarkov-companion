# ADR 0001: Platform and data foundation

Status: Accepted — 2026-09-09

## Decision

Use .NET 10, Avalonia 12.1.2, MVVM, Microsoft.Data.Sqlite with hand-written migrations, and `json.tarkov.dev` as the primary structured data source. Keep Core dependency-free and implement all Windows integration through narrow observation/capture interfaces.

Map artwork is not a compiled dependency. Render configuration and optional asset providers retain source/license metadata. Unvalidated transforms or route graphs degrade to honest waypoint guidance.

## Rationale

This permits Linux-first deterministic development and self-contained Windows publishing without weakening the external read-only anti-cheat boundary. Explicit SQLite migrations and source DTOs make schema drift testable. Avalonia provides a native second-screen desktop UI without shipping Chromium.

## Consequences

Windows capture/hotkey services require VM validation. OCR remains provider-neutral and uses deterministic fixture engines in tests. Optional map artwork may be unavailable offline until cached, but no incompatible artwork is silently redistributed.
