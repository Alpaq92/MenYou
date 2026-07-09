# Changelog

All notable changes to MenYou are documented in this file.

The format is maintained by [release-please](https://github.com/googleapis/release-please) — every section below corresponds to a tag `v<version>` on GitHub. Entries are grouped by [Conventional Commits](https://www.conventionalcommits.org/) type (`feat:` → Features, `fix:` → Bug Fixes, etc.) following [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Do not hand-edit released sections — release-please regenerates them from the merged commit log when it cuts the next release PR.

<!-- release-please starts maintaining content below this comment. -->

## [0.8.13](https://github.com/Alpaq92/MenYou/compare/v0.8.12...v0.8.13) (2026-07-09)


### Bug Fixes

* **start:** DPI-scale the Start-button rect and default to centered alignment ([#67](https://github.com/Alpaq92/MenYou/issues/67)) ([879fcb7](https://github.com/Alpaq92/MenYou/commit/879fcb749d2d4a5080ca60ca2e2c90b168b86460))

## [0.8.12](https://github.com/Alpaq92/MenYou/compare/v0.8.11...v0.8.12) (2026-07-09)


### Bug Fixes

* Bump Fluid.Avalonia from 1.9.0 to 2.0.1 ([#63](https://github.com/Alpaq92/MenYou/issues/63)) ([346935c](https://github.com/Alpaq92/MenYou/commit/346935cd7eb9983006accd8a6cbfb48720a9a936))
* **start:** stop the native Start menu leaking on some taskbar clicks ([#64](https://github.com/Alpaq92/MenYou/issues/64)) ([297ca67](https://github.com/Alpaq92/MenYou/commit/297ca679c25242b70e307c58c2d677dd4f1b7e02))

## [0.8.11](https://github.com/Alpaq92/MenYou/compare/v0.8.10...v0.8.11) (2026-07-01)


### Bug Fixes

* Bump the nuget-minor-and-patch group with 6 updates ([#61](https://github.com/Alpaq92/MenYou/issues/61)) ([a2d1970](https://github.com/Alpaq92/MenYou/commit/a2d19704a114be07f9b69de162ffce8fa028a3b0))

## [0.8.10](https://github.com/Alpaq92/MenYou/compare/v0.8.9...v0.8.10) (2026-06-22)


### Bug Fixes

* Bump the nuget-minor-and-patch group with 1 update ([#59](https://github.com/Alpaq92/MenYou/issues/59)) ([372cbc8](https://github.com/Alpaq92/MenYou/commit/372cbc83165fc6131e97e3eb878d87abbcea84bb))

## [0.8.9](https://github.com/Alpaq92/MenYou/compare/v0.8.8...v0.8.9) (2026-06-15)


### Bug Fixes

* Bump the nuget-minor-and-patch group with 3 updates ([#55](https://github.com/Alpaq92/MenYou/issues/55)) ([acfda65](https://github.com/Alpaq92/MenYou/commit/acfda65ed8b7f20884a90772e6a0323c43dcdeb9))

## [0.8.8](https://github.com/Alpaq92/MenYou/compare/v0.8.7...v0.8.8) (2026-06-11)


### Features

* **startup:** subtle "Updating apps…" caption during background refresh ([#52](https://github.com/Alpaq92/MenYou/issues/52)) ([0c63af1](https://github.com/Alpaq92/MenYou/commit/0c63af188c916e31561f0bd03bdeca80a1abcf47))

## [0.8.7](https://github.com/Alpaq92/MenYou/compare/v0.8.6...v0.8.7) (2026-06-11)


### Bug Fixes

* **startup:** stale-while-revalidate discovery cache + persist on change ([#49](https://github.com/Alpaq92/MenYou/issues/49)) ([2c0a4fd](https://github.com/Alpaq92/MenYou/commit/2c0a4fded60d456b674c80435981097b8f2051d6))

## [0.8.6](https://github.com/Alpaq92/MenYou/compare/v0.8.5...v0.8.6) (2026-06-11)


### Bug Fixes

* **uninstaller:** close a running MenYou before removing files ([#48](https://github.com/Alpaq92/MenYou/issues/48)) ([1318c56](https://github.com/Alpaq92/MenYou/commit/1318c5620c1a9e6ee99186c3ab2a5bc6181d2f3a))

## [0.8.5](https://github.com/Alpaq92/MenYou/compare/v0.8.4...v0.8.5) (2026-06-09)


### Bug Fixes

* **uninstaller:** remove the autostart task and legacy Run value on uninstall ([#41](https://github.com/Alpaq92/MenYou/issues/41)) ([de9b868](https://github.com/Alpaq92/MenYou/commit/de9b8681a4f9560036d48943f70a17ed3c5e6c3f))

## [0.8.4](https://github.com/Alpaq92/MenYou/compare/v0.8.3...v0.8.4) (2026-06-09)


### Bug Fixes

* stop the "ready" tray balloon from showing twice ([#40](https://github.com/Alpaq92/MenYou/issues/40)) ([37223a0](https://github.com/Alpaq92/MenYou/commit/37223a089133bec0ba5d3c2241040acf9091d22b))

## [0.8.3](https://github.com/Alpaq92/MenYou/compare/v0.8.2...v0.8.3) (2026-06-09)


### Bug Fixes

* **i18n:** complete missing translations across all locales ([#38](https://github.com/Alpaq92/MenYou/issues/38)) ([5640e7e](https://github.com/Alpaq92/MenYou/commit/5640e7e9de65c76fc7c3b17888a91f7e703a4e8e))

## [0.8.2](https://github.com/Alpaq92/MenYou/compare/v0.8.1...v0.8.2) (2026-06-09)


### Features

* show the "MenYou is ready" balloon on every install/update, not just on a brand-new profile ([#34](https://github.com/Alpaq92/MenYou/issues/34)) ([0745741](https://github.com/Alpaq92/MenYou/commit/07457416894089503477f78d3c229ba329e330dd))
* re-cap the recent and pinned lists immediately when "Max recent items" changes, instead of only at the next launch ([#34](https://github.com/Alpaq92/MenYou/issues/34)) ([0745741](https://github.com/Alpaq92/MenYou/commit/07457416894089503477f78d3c229ba329e330dd))

## [0.8.1](https://github.com/Alpaq92/MenYou/compare/v0.8.0...v0.8.1) (2026-06-08)


### Features

* cap recent files in context menus (configurable) ([#32](https://github.com/Alpaq92/MenYou/issues/32)) ([8a66185](https://github.com/Alpaq92/MenYou/commit/8a66185bdfadee6f15ed5cff2247ccdf4599176a))

## [0.8.0](https://github.com/Alpaq92/MenYou/compare/v0.7.2...v0.8.0) (2026-06-08)


### Features

* bundle native input bridge; single-instance, startup + UI polish ([#30](https://github.com/Alpaq92/MenYou/issues/30)) ([517ed10](https://github.com/Alpaq92/MenYou/commit/517ed10c1ce3bc63ae2215bdaaf0e34f08c07d0e))

## [0.7.2](https://github.com/Alpaq92/MenYou/compare/v0.7.1...v0.7.2) (2026-06-08)


### Bug Fixes

* **search:** launch results on a single click; keep them stable while typing ([#25](https://github.com/Alpaq92/MenYou/issues/25)) ([3f3f575](https://github.com/Alpaq92/MenYou/commit/3f3f57542a50c77b1334a32c2730fedab400083b))

## [0.7.1](https://github.com/Alpaq92/MenYou/compare/v0.7.0...v0.7.1) (2026-06-07)


### Bug Fixes

* **recents:** record app launches opened from search; default count to 8 ([#23](https://github.com/Alpaq92/MenYou/issues/23)) ([6dbf914](https://github.com/Alpaq92/MenYou/commit/6dbf91485dfce339af0db6117693c822a11b233a))

## [0.7.0](https://github.com/Alpaq92/MenYou/compare/v0.5.6...v0.7.0) (2026-06-07)

> 🚀 **Deeply optimized release** — the cold-start payoff: autostart moved off Windows' throttled `HKCU\Run` value onto a per-user logon scheduled task, cutting time-to-usable from **~15 s to ~1 s** after sign-in. Plus a first-run splash, a one-time "ready" tray balloon, and a power-button (`SeShutdownPrivilege`) fix. Full write-up in [`docs/OPTIMIZATION.md`](docs/OPTIMIZATION.md).


### Features

* near-instant startup, first-run splash, and power-button fix ([#19](https://github.com/Alpaq92/MenYou/issues/19)) ([f086644](https://github.com/Alpaq92/MenYou/commit/f086644fcc4c9ef05245b2940af9a278c048ba76))


### Documentation

* **changelog:** reframe 0.5.0 as a starting point; release as 0.7.0 ([#21](https://github.com/Alpaq92/MenYou/issues/21)) ([e6591c0](https://github.com/Alpaq92/MenYou/commit/e6591c091aa4c1ed3acc9455d21789faea81ae6a))

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
