# Changelog

All notable changes to MenYou are documented in this file.

The format is maintained by [release-please](https://github.com/googleapis/release-please) — every section below corresponds to a tag `v<version>` on GitHub. Entries are grouped by [Conventional Commits](https://www.conventionalcommits.org/) type (`feat:` → Features, `fix:` → Bug Fixes, etc.) following [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Do not hand-edit released sections — release-please regenerates them from the merged commit log when it cuts the next release PR.

<!-- release-please starts maintaining content below this comment. -->

## [0.5.7](https://github.com/Alpaq92/MenYou/compare/v0.5.6...v0.5.7) (2026-06-07)


### Features

* near-instant startup, first-run splash, and power-button fix ([#19](https://github.com/Alpaq92/MenYou/issues/19)) ([f086644](https://github.com/Alpaq92/MenYou/commit/f086644fcc4c9ef05245b2940af9a278c048ba76))

## [0.5.6](https://github.com/Alpaq92/MenYou/compare/v0.5.5...v0.5.6) (2026-06-05)


### Features

* theme showcase gallery, layout polish, and settings cleanup ([#17](https://github.com/Alpaq92/MenYou/issues/17)) ([e655698](https://github.com/Alpaq92/MenYou/commit/e655698a49efa72aa54c2d145adff7db585830d6))

## [0.5.5](https://github.com/Alpaq92/MenYou/compare/v0.5.0...v0.5.5) (2026-06-05)


### Features

* app version in Settings, refined translations, release-notes checksum ([#13](https://github.com/Alpaq92/MenYou/issues/13)) ([728bea5](https://github.com/Alpaq92/MenYou/commit/728bea55bb10b3f81e359464dca7b3a7418d55bc))

## [0.5.0](https://github.com/Alpaq92/MenYou/compare/v0.2.0...v0.5.0) (2026-06-05)

> **The first startup pass — a starting point, not the finish line.** 0.5.0 began painting cold-start from a persisted discovery cache and shipped ReadyToRun binaries. Real gains, but on the work *after* launch — the autostart launch itself stayed throttled (~15 s) until the **0.7.0** logon-task fix actually moved the needle. See [`docs/OPTIMIZATION.md`](docs/OPTIMIZATION.md).


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
