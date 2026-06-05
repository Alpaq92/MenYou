# Changelog

All notable changes to MenYou are documented in this file.

The format is maintained by [release-please](https://github.com/googleapis/release-please) — every section below corresponds to a tag `v<version>` on GitHub. Entries are grouped by [Conventional Commits](https://www.conventionalcommits.org/) type (`feat:` → Features, `fix:` → Bug Fixes, etc.) following [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Do not hand-edit released sections — release-please regenerates them from the merged commit log when it cuts the next release PR.

<!-- release-please starts maintaining content below this comment. -->

## [0.5.5](https://github.com/Alpaq92/MenYou/compare/v0.5.0...v0.5.5) (2026-06-05)


### Features

* app version in Settings, refined translations, release-notes checksum ([#13](https://github.com/Alpaq92/MenYou/issues/13)) ([728bea5](https://github.com/Alpaq92/MenYou/commit/728bea55bb10b3f81e359464dca7b3a7418d55bc))

## [0.5.0](https://github.com/Alpaq92/MenYou/compare/v0.2.0...v0.5.0) (2026-06-05)

> **Deeply optimized release** — a from-scratch startup-performance pass: cold start now paints from a persisted snapshot instead of waiting on a live shell scan, and the binaries are AOT-compiled ahead of first run.


### Features

* Windows 11 default look, discovery cache, and Developer settings ([8d39ce3](https://github.com/Alpaq92/MenYou/commit/8d39ce385bde7aa64b8ca4c5da8acb0d8fa3317f))


### Performance

* **startup:** persisted app-discovery cache for instant cold paint, immediate menu reveal, ReadyToRun-compiled binaries (~halved framework startup), and a COM-free UWP fingerprint to kill the removed-app ghost ([8d39ce3](https://github.com/Alpaq92/MenYou/commit/8d39ce385bde7aa64b8ca4c5da8acb0d8fa3317f))

## [0.2.0](https://github.com/Alpaq92/MenYou/compare/v0.1.1...v0.2.0) (2026-06-04)


### Performance

* **startup:** faster, flicker-free Start menu open ([5a758a6](https://github.com/Alpaq92/MenYou/commit/5a758a6e79339f340e8cc5ff1b5e9318d48c3ffb))

## [0.1.1](https://github.com/Alpaq92/MenYou/compare/v0.1.0...v0.1.1) (2026-06-04)


### Bug Fixes

* **installer:** avoid premature comment close in Inno [Code] block ([f21162e](https://github.com/Alpaq92/MenYou/commit/f21162eeec508077ea6bb8af386d9c2045be7fc3))
* **installer:** use correct Inno page type for the Setup Type page ([0c317a6](https://github.com/Alpaq92/MenYou/commit/0c317a67fe56358eadd12c72c60803d9e6ae0644))
