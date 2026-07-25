<h1 align="center">
  <img src="Assets/Square44x44Logo.scale-200.png" width="72" height="72" alt="MdPad"><br>
  MdPad
</h1>

<p align="center">
  A fast, native Markdown editor for Windows — and a workbench for the files you write <strong>for agents to read</strong>.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4" alt="Windows 10/11">
  <img src="https://img.shields.io/badge/UI-WinUI%203-6236E2" alt="WinUI 3">
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10">
  <img src="https://img.shields.io/badge/WebView2-not%20required-2E9E5B" alt="No WebView2">
  <img src="https://img.shields.io/badge/license-MIT-1F883D" alt="MIT license">
</p>

<p align="center">
  <img src="docs/screenshot.png" width="900" alt="MdPad showing a SKILL.md: skill header card, heading outline, reference list with token counts, and the context budget in the status bar">
</p>

---

## The problem

A `SKILL.md` looks like a document. Every editor you might write one in agrees: words, headings, a preview that tells you how it reads.

It is not a document. It is a prompt fragment, and it has four properties that prose rendering cannot show you:

- Its `name` and `description` are in the model's context **in every single prompt**, for as long as the skill is installed — whether it is ever used or not.
- Its body loads **whole**, the moment the skill is invoked. A 12k-token body costs 12k tokens on the request that needed two paragraphs of it.
- Whether it gets invoked at all comes down to **one line of metadata**, competing for attention with every other skill installed.
- It is bound by a **contract that fails silently**. A `name` that does not match its folder raises no error; the skill simply loads under a name nothing routes to, and you are left wondering why the agent ignored it.

You do not find out about any of this while writing. You find out later, indirectly — an agent that never picks the skill, or picks it and spends the context window on a reference table it did not need. The feedback loop runs through someone else's model, and it is slow and ambiguous.

## What MdPad does about it

MdPad renders Markdown into native WinUI controls, like any decent previewer. The difference is a second set of instruments pointed at the same file, showing the properties above **while you type**, so the loop closes in the editor instead of in a transcript.

| Instrument | The question it answers |
|---|---|
| **Context budget meter** | What does this file cost an agent — and *when* is it charged: every prompt, on invoke, or only if something reads it? |
| **Front matter validation** | Will this load at all, and once loaded, is there anything here a model could route on? |
| **Agent view** | Forget the preview: what are the exact bytes the model receives, tier by tier? |
| **Reference panel** | What does this skill pull in, what does it link to that is not there, and what is sitting in the folder as dead weight? |
| **Extract to a reference** | The body is too expensive. Move a section out to `references/`, leave a link, and see the new number before committing. |

Nothing here is a guess or a language model second-guessing your writing. Every number is computed from the bytes of your file — MdPad never calls a model, and there is nothing to sign in to. The estimates are honest about being estimates: token counts come from a character heuristic (~4 chars/token, configurable), which is wrong in the third digit and right about whether a `description` is bloated.

The bet behind the whole app is simple: **the thing that makes a skill work is invisible in a preview, so put it on screen.** A budget meter that turns amber, a `name` that does not match its folder called out on the line it is on, a link that resolves to nothing rendered as broken rather than rendered as blue — these are cheap to compute and they change what you write.

MdPad is free and open source under the [MIT license](LICENSE).

## It is also just a Markdown editor

The skill features only wake up for a file that is one. Everything else is an ordinary, fast, native editor, and it is a genuinely pleasant place to write a README:

- Live preview with a draggable split, or editor-only / preview-only views
- Tabs in the title bar, Windows 11 Notepad style, with per-tab caret and scroll position
- **Session restore** — the tabs you left open come back, unsaved work included
- **Notices changes on disk** — a file rewritten by an agent while it sits open reloads, rather than being overwritten by a stale buffer
- Heading outline sidebar; **anchor links** that move both panes, across files too
- **Find and replace**, over the file or the whole folder, with every match highlighted in the preview as you type
- Tables, task lists, blockquotes, fenced code, strikethrough, autolinks (Markdig's advanced extensions)
- **Inline HTML** — the centred header, badge row, `<kbd>` and `<table>` a README is built from render as native controls too. The header of this file is the test case
- **Preview theme independent of the app theme** — check how a README reads in light while you work in dark
- Six pages of live-applied settings, stored as readable JSON you are welcome to edit by hand

No WebView2, no Electron, no HTML. A heading becomes a `TextBlock`, a table becomes a `Grid`, a code fence becomes a bordered `ScrollViewer`. Startup is instant, memory stays low, and the preview inherits your Windows theme, accent colour and Mica backdrop for free.

## What it is not

Being clear about this saves everyone a download:

- **Not an agent runner.** MdPad does not invoke skills, call an API, or talk to a model. It is where you *write* the file; running it is somebody else's job. There is no account, no key, no telemetry. The only network traffic it will ever generate is fetching a remote image referenced by a document you are previewing.
- **Not cross-platform.** WinUI 3 on Windows 10 1809+. There is no Mac or Linux build and there will not be one.
- **Not a real tokeniser.** The budget is a character-count heuristic, deliberately: no model-specific dependency, no network call, no version skew. Use it to make decisions, not invoices.
- **Not a validator for every skill format.** It encodes the conventions of Claude's Agent Skills — front matter contract, `references/` for progressive disclosure, folder-keyed names. Much of it generalises to any "prompt file with metadata" setup, but the specific rules are those.
- **Not small on disk.** Self-contained builds carry the .NET runtime and the Windows App SDK: ~270 MB installed, ~100 MB zipped. That buys an install with no prerequisites at all.
- **Not battle-tested.** One author, one machine, no automated test suite. It has been used daily to write real skills, which is not the same thing as being proven. Bug reports are genuinely welcome.

## The skill workbench, in detail

### Context budget

A skill enters an agent's context in three stages, and the status bar reports each one:

```
~105 always  ·  ~4.8k on invoke  ·  ~16k on demand
```

| Tier | What it is | When it costs you |
|---|---|---|
| **Always loaded** | `name` + `description` | Every single prompt, for every skill installed |
| **On invoke** | The body of `SKILL.md` | Only when the agent decides to use the skill |
| **On demand** | Files linked from the body | Only if the agent actually reads them |

That distinction is the whole game. The skill in the screenshot is ~20k tokens in total, but only 105 of them are in every prompt — that is a well-shaped skill. A 5k-token `description` would be a disaster, and a normal Markdown preview renders it beautifully. Click the meter for a per-file breakdown; the thresholds where it turns amber and red are yours to set.

> Token counts are estimates (~4 characters per token). They are for making budget decisions, not for billing.

### References

Relative links are the backbone of a multi-file skill, so MdPad treats them as first-class:

- `[styling.md](./rules/styling.md)` resolves against the document's folder and **opens in a tab** when clicked
- A link whose target is missing renders struck through and tagged `(missing)`
- The sidebar lists linked files with token counts, and flags folder files that nothing points at
- `#section` links scroll the preview and move the caret; `./rules/styling.md#naming` opens the file *and* lands on the heading. Matching follows GitHub's slug, so links written for a repo work unchanged

### Extract to a reference

The budget meter says when a body has grown too expensive to load on invoke. This is the edit that answers it.

Put the caret in a section and press `Ctrl+Shift+E` (**Edit → Extract to a reference…**, or the button that appears in the budget breakdown once the body is over the warning threshold). MdPad takes the heading you are under and everything beneath it — nested subsections included, stopping at the next heading of the same level — and shows you the trade before committing to it:

```
On invoke: ~6.2k → ~2.1k  ·  ~4.1k moves to on demand
```

Confirm, and the section is written to `references/error-codes.md` with the heading lifted to `#` so the new file reads as a document rather than a fragment, and the body keeps:

```markdown
## Error codes

See [Error codes](references/error-codes.md) — you hit an unfamiliar code.
```

The heading stays put deliberately: the outline, and any `#error-codes` anchor pointing at it, survive the move. The optional note is what makes progressive disclosure work — it is the sentence an agent reads to decide whether following the link is worth the tokens.

Details worth knowing:

- **Selection wins over inference.** Select lines and exactly those are taken; a bare caret means "the section I am in". A selection with no heading of its own gets a title from its opening words and an `#` heading of that name in the new file
- **Front matter is never moved** — it is the skill's contract, not its content
- A `#` inside a fenced code block is not a heading, and never ends a section early
- The file name is proposed as a kebab-case slug of the title and stepped (`error-codes-2.md`) if that name is taken; a name that already exists blocks *Extract* rather than overwriting
- **Both halves land together.** The reference is written and the document is saved in one gesture, so the extraction can never leave a file on disk that nothing links to — the exact orphan the sidebar would then warn about
- A document that already lives in `references/` extracts to a sibling, not to `references/references/`

### New skill

**File → New skill…** (`Ctrl+Shift+N`) asks for the two things that cannot be guessed — a kebab-case name and a description — and writes:

```
review-pull-request/
  SKILL.md
  references/reference.md
```

The name is checked against the same kebab-case rule the validator applies, so *Create* is only offered for a name that will load. What lands is a skill with no errors, no warnings, no broken links and no orphans: the first red mark you see in the sidebar is one you introduced. The caret opens on the `description` value, because that is the line that decides whether the skill is ever picked.

### Front matter validation

A `SKILL.md` has a metadata contract, and getting it wrong fails quietly: the skill loads under the wrong name, or never gets picked for the request it was written for. The **FRONT MATTER** panel in the sidebar checks that contract on every keystroke and lists what it finds — click an issue to put the caret on the line it is about.

| Field | Checked for |
|---|---|
| `name` | Present, kebab-case, within 64 characters, and **identical to the folder it lives in** — the loader keys the skill by its folder |
| `description` | Present, within 1024 characters, not so short it cannot be routed on, not so long it taxes every prompt, and carrying a **trigger phrase** (`Use when …`) so the model knows what request should reach it |
| `allowed-tools` | Valid entry syntax (`Read, Bash(git diff:*)`), balanced parentheses, known and correctly-cased tool names, no permission spec on a tool that ignores one |
| Every key | Recognised, spelled with hyphens (`allowed-tools`, not `allowed_tools`), and not declared twice |

Problems are ranked: **errors** stop the skill loading or load it under the wrong name, **warnings** mean an agent is likely to mis-route or waste context on it, and **notes** are style points. Ordinary Markdown never sees the panel — the rules only apply to a file that is a skill. Any house-style rule you disagree with can be demoted or switched off, because a panel full of noise stops being read at all.

### Agent view

The preview answers *how does this read*. The fourth view mode — the robot icon in the top right, or **View → Agent view** — answers the question that actually decides whether a skill works: **what lands in the context window, and when.**

It rebuilds the document as three monospace blocks, each with its own token cost and a copy button:

| Block | What it shows |
|---|---|
| **Always loaded** | The single line the skill listing carries, `- name: description`, exactly as assembled. This is all a model has to decide from — reading it on its own, out of context, is the fastest way to tell whether the skill would ever get picked |
| **On invoke** | The file as delivered once the model picks the skill, front matter included |
| **On demand** | The linked files, as paths and costs rather than content — none of it is in context until the agent opens it. Broken links say so, because what the agent gets there is an error, not a document |

A file with no front matter gets an honest version of the same thing: no listing entry, nothing preloaded, and one block for the text a model only sees once something reads the file.

### Find and replace

`Ctrl+F` to find, `Ctrl+H` to replace, `Esc` to close. The scope box is the part that matters for skills:

- **This file** — a running match count, `F3` / `Shift+F3` to step, wrapping at both ends
- **This folder** — every text file beside the document, listed as `path:line` with the matching line; click one to open the file with the occurrence selected

Matches are **shown, not just counted**. As you type, the editor jumps to and selects the first match from where you were — and keeps that selection painted while the caret is in the find box, which is where it always is during a search. In the preview, *every* match is highlighted at once, so you see the shape of the result: where the hits cluster, and which section to read first.

A skill is a folder, not a file, so renaming a section or a tool means touching `SKILL.md` and every reference next to it. **Replace all** in folder scope confirms the count first, then writes files that aren't open straight to disk. Anything open in a tab is changed in its buffer and **left unsaved** — so no tab is rewritten under you, and `Ctrl+Z` still works on the file you were editing. Results reflect unsaved buffers, not just what was last written to disk.

## Settings

**File → Settings…** (`Ctrl+,`) opens six pages, all of which live-apply — there is no OK button, because the only way to judge a font or a theme is against the document behind the dialog.

| Page | What it holds |
|---|---|
| **Appearance** | App theme, **preview theme**, window backdrop, accent colour, preview measure |
| **Fonts** | Separate family and size for the editor, the preview body, and code; preview line height; zoom |
| **Editor** | Word wrap, line numbers, spell check, tab behaviour, list continuation, bracket closing, on-save tidy-ups, line endings |
| **Skills** | Skills folder, characters per token, budget thresholds, what counts as a skill, orphan scope, ignore patterns, per-rule validation severity |
| **Files** | Startup behaviour, autosave, and what to do when a file changes on disk |
| **Find** | Default scope, remembered options, folder-search excludes and size cap, the Replace-all confirmation threshold |

Three of these are worth calling out.

**Preview theme, independent of the app.** A README is going to be read on GitHub in whichever theme the reader uses. Because the preview is native controls rather than a WebView, MdPad can render it light while the rest of the window stays dark — the question "how does this look for everyone else" gets answered without leaving the app.

**Characters per token, and the budget thresholds.** The estimate's divisor and the points at which the meter turns amber and then red are judgement calls, and different teams draw those lines differently. They are settings rather than constants, and the meter recolours the moment you change them.

**Validation severity, per rule.** The rules the loader itself enforces — a missing `name`, an unparsable tool list — are fixed. The house-style ones are not: the trigger-phrase warning, the description length bounds, unknown front matter keys and the rest can each be promoted, demoted, or switched off.

Settings live in `%LOCALAPPDATA%\MdPad\settings.json` as plain, nested JSON. **Open settings.json** at the foot of the dialog opens it for editing directly; a hand-edited value out of range is clamped rather than rejected, and a file that will not parse falls back to the defaults instead of stopping the app.

## Install

**Download** — grab `MdPad-1.2.0-win-x64.zip` from [Releases](../../releases), unzip, and run `Install.cmd`.

The installer is per-user and needs **no administrator rights**. It installs to `%LOCALAPPDATA%\Programs\MdPad`, adds Start-menu and desktop shortcuts, registers MdPad in the Windows *Open with* menu, and creates an Add/Remove Programs entry. The build is self-contained: no .NET runtime or Windows App SDK needed on the target machine.

```powershell
# options
.\Install-MdPad.ps1 -SetAsDefault        # also make MdPad the default .md handler
.\Install-MdPad.ps1 -NoDesktopShortcut   # skip the desktop shortcut
```

**Uninstall** — Settings → Apps → MdPad, or run `%LOCALAPPDATA%\Programs\MdPad\Uninstall-MdPad.ps1`. It removes the files, shortcuts, and registry entries, and only clears the `.md` association if it still points at MdPad.

### MSIX package

Prefer a packaged install? Grab `MdPad-1.2.0-win-x64-msix.zip` from Releases, unzip, and run `Install-Msix.ps1`. The package is self-contained and per-user; uninstall from Settings → Apps like any Store app.

Because the package is signed with a self-signed developer certificate, that certificate has to be trusted before Windows will install it. `Install-Msix.ps1` does this for you: it imports the bundled `MdPad.cer` into the machine's *Trusted People* store (self-elevating for that one step), then installs the `.msix`. Once trusted, you can also just double-click the `.msix`.

```powershell
.\Install-Msix.ps1              # trust the cert, then install
.\Install-Msix.ps1 -Uninstall   # remove MdPad
```

> Shipping under your own code-signing certificate removes the trust step entirely — a package signed by a publicly-trusted cert installs by double-click. See *Build from source* below.

### Open with

MdPad registers for `.md`, `.markdown`, `.mdown`, and `.mkd`, and deliberately **does not steal the default handler** — it appears as a choice:

> Right-click a Markdown file → **Open with** → **MdPad**

Tick *Always use this app* (or install with `-SetAsDefault`) if you want it to take over. `MdPad.exe <path>` also works from any shell.

## Shortcuts

| Keys | Action | | Keys | Action |
|---|---|---|---|---|
| `Ctrl+T` / `Ctrl+N` | New tab | | `Ctrl+B` | Bold |
| `Ctrl+Shift+N` | New skill | | `Ctrl+I` | Italic |
| `Ctrl+O` | Open | | `Ctrl+K` | Link |
| `Ctrl+S` | Save | | `Ctrl+1` `Ctrl+2` `Ctrl+3` | Heading 1 / 2 / 3 |
| `Ctrl+Shift+S` | Save as | | `Ctrl+\` | Toggle outline sidebar |
| `Ctrl+W` | Close tab | | `Ctrl+Z` / `Ctrl+Y` | Undo / redo |
| `Ctrl+Tab` | Next tab | | `Ctrl+F` / `Ctrl+H` | Find / replace |
| `Ctrl+,` | Settings | | `F3` / `Shift+F3` | Next / previous match |
| | | | `Ctrl+Shift+E` | Extract section to a reference |
| | | | `Ctrl+=` / `Ctrl+-` / `Ctrl+0` | Zoom in / out / reset |

## Build from source

Requirements: **.NET 10 SDK**, Windows 10 1809 or later. The Windows App SDK restores from NuGet.

```powershell
git clone https://github.com/radueduard97/MdPad.git
cd MdPad

dotnet build                                   # debug build
dotnet run                                     # build and launch

.\tools\New-AppIcon.ps1                        # regenerate the icon and logo assets
.\installer\Build-Installer.ps1                # publish + package the installer zip
.\installer\Build-Installer.ps1 -Runtime win-arm64

.\installer\Build-Msix.ps1                     # build + sign an MSIX package
.\installer\Build-Msix.ps1 -Runtime win-arm64
.\installer\Build-Msix.ps1 -CertificatePath cert.pfx -CertificatePassword (Read-Host -AsSecureString)
```

`Build-Msix.ps1` drives the Windows App SDK single-project MSIX tooling through `dotnet build` (no Visual Studio needed — `signtool.exe` comes from the `Microsoft.Windows.SDK.BuildTools` NuGet package). With no `-CertificatePath`, it creates and reuses a self-signed certificate whose subject matches the manifest's `Publisher`, signs the package, and exports `MdPad.cer` for `Install-Msix.ps1` to trust. Pass `-CertificatePath` to sign with a real code-signing certificate instead.

## Project layout

| File | Responsibility |
|---|---|
| `MainWindow.xaml` | Window shell: the `TabView` doubles as the title bar |
| `MainPage.xaml` | Outline sidebar, menu line, formatting toolbar, find bar, editor/preview card, status bar |
| `MainPage.xaml.cs` | Tabs, file commands, Markdown text transforms, anchor navigation, budget and reference wiring |
| `MainPage.Find.cs` | The find bar: search, stepping, replace, and folder-wide replace |
| `MainPage.Highlight.cs` | Painting search matches: every one in the preview, the current one in the editor |
| `MainPage.Session.cs` | Saving and restoring the open tabs, view mode, and unsaved text |
| `MainPage.Settings.cs` | Applying settings, the typing assists, the line-number gutter, autosave, and the on-disk watch |
| `MainPage.Skill.cs` | The New Skill dialog and its live name validation |
| `MainPage.Extract.cs` | The Extract-to-a-reference dialog: names the file, previews the link, reports the saving |
| `Settings.cs` | The settings model, the JSON file behind it, and the ignore-pattern matcher |
| `SettingsDialog.cs` | The settings pane: six pages of live-applied rows |
| `MdDocument.cs` | One open document — text, saved baseline, dirty state, caret, scroll |
| `MarkdownRenderer.cs` | Markdig AST → WinUI control tree; front matter card; local-link resolution |
| `MarkdownRenderer.Html.cs` | The HTML half of the renderer: block and inline HTML, images, SVG |
| `HtmlParser.cs` | A small forgiving HTML tokenizer — tags, attributes, entities, implied closes |
| `SkillAnalysis.cs` | Token estimates, context tiers, reference/orphan discovery, YAML front matter |
| `SkillValidation.cs` | The skill metadata contract: `name`, `description`, `allowed-tools`, unknown keys |
| `SkillScaffold.cs` | What a new skill is made of: folder layout and the starter `SKILL.md` |
| `ReferenceExtraction.cs` | Working out an extraction: section boundaries, the new file, and the link left behind |
| `FindReplace.cs` | Plain-text search and replace over a document or a folder |
| `SessionStore.cs` | Reading and writing `%LOCALAPPDATA%\MdPad\session.json` |
| `AgentView.cs` | The transcript behind the agent view: what a model receives from the document, by tier |
| `installer/` | Build, install, and uninstall scripts (zip + MSIX) |
| `tools/New-AppIcon.ps1` | Draws the app mark and packs the multi-resolution `.ico` |

## Design decisions

- **Native controls over HTML.** A WebView2 preview means shipping a browser, waiting for it to start, and fighting CSS to match the system theme. Rendering to `TextBlock`/`Grid`/`Border` gives instant startup, real text selection, and automatic theme, accent, and Mica integration.
- **Measure, don't advise.** MdPad never tells you your description is bad. It tells you it is 40 characters long and in every prompt, and lets you draw the conclusion. Every panel is a measurement of the file, which is why none of it needs a model or a network.
- **Estimates over exactness.** Token counts use a character heuristic instead of a model-specific tokeniser — no dependency, no network, no version skew, and accurate enough to catch a bloated `description`.
- **Mica for chrome, opaque for content.** The outline sidebar, menu line, and status bar sit on the window's Mica backdrop; the editor and preview live on an opaque rounded card that casts a shadow over it.
- **HTML rendered, not embedded.** Inline HTML goes through a small hand-written parser and lands on the same `TextBlock`/`Grid`/`Border` vocabulary as Markdown, so a centred `<h1>` still joins the outline and a `<table>` still scrolls like a Markdown one. Bringing in a browser to render six tags would undo the point of the app.
- **One editor, many documents.** Tabs swap the document behind a single editor and preview rather than duplicating the UI tree per tab.
- **Settings live-apply, and the file is the other interface.** No OK button and no staged copy of the configuration. The file it saves is nested JSON meant to be read, because the people writing agent skills are the people who keep their dotfiles in a repository.

## Roadmap

**As a skill workbench**

- **Listing preview** — your `name` and `description` shown among the other skills installed in `~/.claude/skills`. The question is never whether a description reads well, it is whether a model would pick this skill over its neighbours
- **Front matter as a form** — `name`, `description`, and an `allowed-tools` picker built from the tool list the validator already knows, so the syntax errors it reports never get typed in the first place
- **A skill folder tree** — every file in the skill, not just the `.md` ones, with create, rename and delete in place; renaming rewrites the links that point at the old name

**As an editor**

- Regular-expression and whole-word search, and undo for a folder-wide replace
- More than one window, so two skills can be open side by side

## Known limitations

- Search is plain text: no regular expressions, and no whole-word option
- The editor marks only the match you are on. A WinUI `TextBox` has a selection and no other way to mark text, so highlighting every match at once is something only the preview — a control tree MdPad builds itself — can do. A match that exists in the source but not in the rendered text (`**`, a link URL) is therefore shown in the editor alone
- Line numbers count source lines, so the gutter hides itself while word wrap is on rather than showing numbers that drift down the page
- There is no editor line-height setting, and no show-whitespace option: WinUI's `TextBox` exposes neither line height nor per-glyph rendering, and swapping it for a custom text surface would cost more than either is worth. The preview has a line-height setting
- The font pickers are editable lists of families a Windows machine reliably has, not an enumeration of what is installed — enumerating fonts needs a DirectWrite or Win2D dependency this app does not carry. Any installed family can be typed in
- Open and Save as start wherever Windows last left the picker; the WinRT pickers take no start path
- A folder-wide **Replace all** writes files that aren't open directly to disk; that write is not undoable from inside MdPad
- Anchors resolve against headings only — a `#name` that matches nothing reports so in the status bar rather than jumping
- Session restore reopens files by path, so a file moved between runs comes back missing rather than relocated
- Autosave only writes files that already have a path; an untitled tab is never saved without a picker
- Orphan detection only considers `.md` files by default, so scripts and assets aren't flagged until the scope is widened in settings
- Validation reads the front matter with MdPad's own small YAML reader, so it checks top-level keys rather than nested structures, and the tool list it knows is the built-in set (`mcp__…` names are taken on trust)
- The agent view shows the document's own contribution to the context window, not the wrapper text an agent adds around it, and it does not inline the content of linked files
- Inline HTML covers the tags READMEs use, not the whole language: `<script>` and `<style>` are dropped, `<details>` renders expanded, and CSS in a `style` attribute is ignored apart from `text-align`
- SVG images draw through WinUI's SVG support, which does not render `<text>` elements — shields.io badges show their colours but not their labels
- Self-contained builds are large on disk (~270 MB installed, ~100 MB zipped)
- Single window; opening a second file from Explorer starts a second process

## Contributing

Issues and pull requests are welcome, and bug reports especially — this is a one-person project with no automated test suite, so the failure modes that get found are the ones people report.

The codebase is small and deliberately plain: no MVVM framework, no DI container, no code generation beyond XAML, about 9,000 lines of C#. If you are adding a feature, `MarkdownRenderer.cs` (how Markdown becomes controls) and `SkillAnalysis.cs` (what a skill costs) are the two files worth reading first.

By contributing you agree that your contributions are licensed under the MIT license.

## License

[MIT](LICENSE) © 2026 Radu Eduard — do what you like with it, commercially or otherwise; just keep the copyright notice with copies.

Third-party components:

| Component | License |
|---|---|
| [Markdig](https://github.com/xoofx/markdig) | BSD-2-Clause |
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) / WinUI 3 | MIT (redistributable binaries carry Microsoft's own distribution terms) |
