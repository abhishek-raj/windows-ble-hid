# Translating BLE HID

BLE HID is available in English, German (Deutsch) and Simplified Chinese (简体中文). Every
translation is contributed through a GitHub issue. You do not need Git, a code editor, or
a build environment.

- [Translators](#translators)
- [Maintainers](#maintainers)
- [Developers](#developers)

## Translators

### Submit a translation

1. Open [`localization/en.json`](localization/en.json), the English text, and copy all of
   it with **Copy raw file**. To improve an existing language, copy that language's file
   from [`localization`](localization) instead, for example `de.json`.
2. Open a [translation issue](https://github.com/abhishek-raj/windows-ble-hid/issues/new?template=translation.yml),
   choose your language, and paste your translated file.
3. A bot checks the submission within a few minutes and replies on the issue. If it asks for
   changes, edit the issue; the check runs again.
4. A maintainer reviews the translation and approves it. The bot then opens a pull request
   that lists you as a co-author, so you appear among the project's contributors.

Partial translations are welcome. Anything you leave in English, or delete, simply shows
in English until someone translates it.

### What the file looks like

```json
{
  "app.capture.sendingTo": "Sending to: {target}",
  "@app.capture.sendingTo": { "description": "{target} is a device name, or one of the target texts." }
}
```

- Translate only the text **after** the colon. Keep the key before it unchanged.
- Words in `{braces}` are filled in by BLE HID, for example with a device name. Keep them
  exactly as written, but move them wherever your grammar needs them.
- Entries starting with `@` are notes for you. They can stay in the file; they are ignored.
- `\n` starts a new line. Keep it where the English has it.
- Keep text inside straight quotes `"..."`. If your text needs a quotation mark, write `\"`
  or use your language's own quotation marks, such as „…“ or 「…」.
- Shortcuts appear as `{stopShortcut}`, `{switchShortcut}` and `{alternativeShortcut}`.
  Keep these placeholders as they are. BLE HID builds every shortcut from `key.ctrl` and
  `key.capsLock`, so translate those two to the names on keyboards in your language, such as
  `Strg` and `Feststelltaste` in German. If your keyboards show the English names, leave them.
- Command names, options such as `--lang`, and words the notes ask you to keep (such as
  `local`, `on` and `off`) are typed by users and must stay in English.

### What is not translated

Logs, the CLI's `--diagnose` report, the Android companion's event timeline and the raw
output of diagnostic CLI commands stay in English. They are pasted into bug reports, and
maintainers need to be able to read them. Product names (BLE HID, BleHid) also stay as
they are.

### Style

- Write as the operating system does in your language: use its terms for Bluetooth,
  pairing, the notification area and Settings, and its level of formality.
- Prefer short text. Labels sit on cards and tiles that do not grow much.
- Keep terms consistent. A **host** is a paired phone, tablet or computer that receives
  input; **capture** means taking over this PC's keyboard and mouse; the **peripheral** is
  the Bluetooth keyboard and mouse this PC pretends to be.

## Maintainers

### One-time setup

1. Create two labels: `translation` (the issue form applies it) and `translation-approved`
   (you apply it to approve).
2. In **Settings → Actions → General → Workflow permissions**, enable **Allow GitHub
   Actions to create and approve pull requests**. Without it, approving cannot open a pull
   request.

### Reviewing

1. Read the bot's comment. Only **Ready for review** submissions can be approved.
2. Check the translation itself. Ask another speaker of the language when you can; the bot
   only checks structure, never meaning.
3. Add `translation-approved`. The
   [translation-apply](.github/workflows/translation-apply.yml) workflow validates the
   issue again, merges it into the language file, regenerates the Android resources, and
   opens `translations/issue-<number>` as a pull request with the translator credited.
   Approving again after a later edit updates the same pull request.
4. Merge the pull request. Pull requests opened by `GITHUB_TOKEN` do not trigger other
   workflows, so build it locally if in doubt; the release workflow's tests check every
   translation again.

### Safety

The issue body is untrusted. The workflows never pass it to a shell. The tool reads it from
GitHub's event file, accepts only known keys, and rejects text that changes placeholders,
contains control or text-direction characters, or is far longer than the English. Bot
comments never repeat submitted text. Only people with triage access can add labels, so
nothing reaches a branch without a maintainer's approval.

## Developers

### Adding or changing text

1. Add the English string to [`localization/en.json`](localization/en.json), with an `@`
   note whenever a translator might misread it. Keys are lowercase dotted paths:
   - `app.*` for the desktop app, `cli.*` for the command line;
   - `common.*`, `target.*`, `lock.*`, `advertisement.*` and `key.*` for text both share;
   - `android.snake_case` for the Android companion; the part after `android.` becomes the
     resource name.
2. Use it:
   - C#: `Loc.T("app.key")`, or `Loc.T("app.key", ("name", value))` for placeholders.
   - XAML: `{l:Tr app.key}`, or `{Binding Value, Converter={l:Format app.key}}` for a
     string with one placeholder.
   - Kotlin: `stringResource(R.string.key)` or `getString(R.string.key)`.
   - Shortcuts: write `{stopShortcut}`, `{switchShortcut}` or `{alternativeShortcut}` in the
     text, never key names. `Shortcuts` in Core builds them from the translated `key.*`
     names, and `Shortcuts.ShareWithTranslations()` makes them available to every string.
3. Regenerate the Android resources:

   ```powershell
   dotnet run --project tools\BleHid.Translations -- check --fix
   ```

4. Run the Core tests. They fail if code uses a key `en.json` lacks, if `en.json` has a key
   nothing uses, or if any translation or generated file is stale.

**Changing the meaning** of an English string? Rename its key. Existing translations of the
old key are then dropped by `check --fix`, and English shows until someone translates the
new text. Fixing a typo or punctuation can keep the key.

Never edit `android/.../res/values*/strings.xml`; they are generated from the JSON files.

### Trying a language

The desktop app follows the Windows display language; **Settings → Language** overrides it
on the next start. The CLI accepts `--lang de` or the `BLEHID_LANG` environment variable.
The Android companion follows the phone's language, or its per-app language setting.

### How it works

- [`src/BleHid.Localization`](src/BleHid.Localization) embeds the JSON files and looks
  strings up through the culture's parent chain (`zh-CN` → `zh-Hans`, `de-AT` → `de`),
  falling back to English for each missing string. Embedding keeps single-file publishing
  working without satellite assemblies.
- [`tools/BleHid.Translations`](tools/BleHid.Translations) validates submissions, merges
  approved ones, and generates Android resources. It uses the same validator as the
  tests, and runs on Linux in the workflows.
- Core logs and `BleHidPeripheral.SelectedHostDisplay` stay English for bug reports;
  `DisplayText` provides the translated equivalents for the app and CLI.
