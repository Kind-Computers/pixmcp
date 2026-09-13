## Summary

<!-- What changes and why. Name the tools, parameters or result fields whose wire contract moves. -->

## Checklist

- [ ] `CHANGELOG.md` has an entry under `[Unreleased]` (Added / Changed / Removed) for every user-visible change.
- [ ] `dotnet test pixmcp.sln -c Release` passes locally against PIX Preview `PixVerifiedVersion` (see `Directory.Build.props`).
- [ ] Native suites ran when the change touches replay, timing, counters or dumps (`PIX_TEST_CAPTURE`, `PIX_TEST_ANALYSIS=1`, `PIX_TEST_TIMING_CAPTURE`).
- [ ] `python scripts/check_versions.py` passes (PIX version strings stay consistent); README and CLAUDE.md updated when behaviour or setup changed.
- [ ] New tools are listed in the README catalog and covered by a scenario, benchmark task or test.
