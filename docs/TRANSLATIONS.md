# Translating MenYou

MenYou's user-visible strings come from two sources:

1. **Windows itself** — `Strings.cs` calls `SHLoadIndirectString` on
   shell DLLs (shell32, twinui, themecpl, comres, …) for labels Windows
   already owns ("Settings", "Pinned", "Apply", "Theme", "Sign out", …).
   These translate themselves — there's nothing for contributors to do.
2. **MenYou's own JSON bundles** under
   [`src/MenYou/Languages/`](../src/MenYou/Languages/) for labels that
   are MenYou-specific ("Mirror Windows Start pins", "Replace Start: …",
   the four `Update*` update-check status strings, etc.). These need human
   translators — that's what this doc is about.

## Contributing a translation (no GitHub account needed)

1. Open the **Crowdin project** for MenYou:
   <https://crowdin.com/project/menyou>
2. Pick your language (or request a new one if it isn't listed).
3. Crowdin shows every missing / outdated string with the English
   source on the left and an empty field on the right. Translate the
   strings that read awkwardly in your language; skip the rest.
4. Click **Save** on each entry. That's it — no PR to write, no Git
   knowledge needed.

A scheduled GitHub Action ([`.github/workflows/crowdin.yml`](../.github/workflows/crowdin.yml))
pulls completed translations back into the repo every **Monday 06:00
UTC** and opens an auto-merging pull request. Your work lands in the
next MenYou release on its own.

### What gets translated

Open [`src/MenYou/Languages/en.json`](../src/MenYou/Languages/en.json)
for the canonical English wording. Every string in there is shown to
the user somewhere in MenYou — Settings dialog, tray menu, Start menu
section header, or update-status indicator.

Strings that aren't in `en.json` come from Windows itself and translate
automatically (see top of this doc). If a label in MenYou reads weird
in your language and *isn't* in `en.json`, that means **Windows owns
that label** — file the bug with Microsoft, not here. (We can't override
Windows shell strings without breaking the auto-locale property.)

## Contributing a translation by editing the JSON directly

If you'd rather skip Crowdin (some translators prefer offline work):

1. Fork the repo, copy `Languages/en.json` to `Languages/<code>.json`
   where `<code>` is your locale's two-letter ISO 639-1 code
   (`de`, `pl`, `es`, `ko`, `pt`, …).
2. Translate the values; keep the keys exactly as they are.
3. Open a PR. The title should be `i18n: <language name>` — the
   `i18n:` prefix routes it through the same auto-merge path Crowdin
   uses, so it lands without human review once CI is green.
4. Conventional commit style: `i18n: add Italian translation` /
   `i18n: refresh German for new MirrorWinStartDescription wording`.

If your locale already has a file but is missing keys, fall through
automatically loads English for the missing ones — partial coverage
is fine. Don't add a key whose value is identical to the English
source; the runtime fallback handles that more cleanly than a
duplicate entry.

## Maintainer setup (one-time)

To wire the Crowdin sync end-to-end, the repo owner needs to:

1. **Apply for the free Crowdin OSS plan** at
   <https://crowdin.com/page/open-source-project-setup-request>.
   Provide the repo URL, MIT license, and contact details. Approval
   usually takes 1–3 days.
2. **Create a Crowdin project** pointing at this repo. Use the
   numeric project ID — that's what `CROWDIN_PROJECT_ID` expects.
3. **Generate a personal API token** at
   <https://crowdin.com/settings#api-key> with `Projects (Source files
   / Translations)` scope. Store it as the `CROWDIN_PERSONAL_TOKEN`
   secret in **Settings → Secrets and variables → Actions**.
4. **`RELEASE_PLEASE_PAT` is reused** — same secret already configured
   for the release pipeline. The translation PRs need a PAT so the
   resulting merge fires downstream workflows (CI on bumped main,
   release-please, etc.).
5. **Push `crowdin.yml` and `.github/workflows/crowdin.yml`** (done in
   this commit). The Action takes over from there.

After step 5, opening `crowdin.yml` in a browser shows the source
files Crowdin is tracking. Every push to `main` that changes
`en.json` automatically uploads new strings to Crowdin; every Monday
the workflow pulls completed translations into a PR.

## Trying alternatives

If Crowdin doesn't fit (e.g. you want a self-hosted FLOSS option),
the JSON layout works with these too:

| Service | Hosted by | License | OSS tier | Notes |
|---|---|---|---|---|
| **Crowdin** | Crowdin Inc. | proprietary | free for approved OSS | recommended above |
| **Weblate** | Hosted Weblate, or self-host | GPL-3.0 | free for libre projects | most polished FLOSS option |
| **Tolgee** | Tolgee Cloud or self-host | Apache 2.0 | free tier | in-context editing in dev builds |

Swapping is mostly a workflow file change (each service has its own
GitHub Action) plus a config file in a different format. The
`Languages/*.json` layout doesn't need to change.
