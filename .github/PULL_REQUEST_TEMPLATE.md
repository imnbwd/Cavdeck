## Summary

<!-- What changed, and why? Keep the scope focused. -->

## Related issue

<!-- Link an Issue where applicable, e.g. "Closes #123". Write "None" otherwise. -->

## Validation

<!-- Check only what you actually verified. Explain relevant cases that were not tested. -->

- [ ] `dotnet build Cavdeck.sln -c Release`
- [ ] `dotnet test Cavdeck.sln -c Release --no-build` (after a successful Release build)
- [ ] Manual verification of the affected workflow (describe below)
- [ ] Not applicable / docs-only change (explain below)

**Test details / remaining gaps:**

## Safety and UX review

<!-- Check applicable items only; do not mark tests complete unless performed. -->

- [ ] No secrets, private object names, presigned URLs, unredacted logs, or sensitive screenshots included
- [ ] Storage operations preserve confirmation, cancellation, conflict, and error behavior (if applicable)
- [ ] Changed UI strings are localized in English, Chinese, Japanese, and German (if applicable)
- [ ] Light/dark themes, keyboard behavior, and relevant DPI scenarios checked (if applicable)
- [ ] Documentation, licensing/attribution, and dependencies reviewed (if applicable)

## Screenshots (optional)

<!-- For UI changes only. Redact sensitive paths, bucket names, and credentials. -->
