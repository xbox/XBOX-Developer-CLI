---
title: Editing the docs
description: Where a change's documentation goes, how to record a known issue, and how to write and format a page.
---

# Editing the docs

Every command, verb, or option change updates these docs in the same pull request.

## Where a command is documented

The pages follow the command tree. In the docs folder, `xbox <command>` is documented in
`commands/<command>.md`, and `xbox playfab <service>` in `commands/playfab/<service>.md`. A
new group needs its own page in the matching place, a row in its parent page's command
table, and an entry in the tree in the [docs README](../README.md). Each command page says
whether its commands accept `--output table`.

## Known issues

[Known issues](../known-issues.md) lists the known problems in the latest release, including
unfinished features, so readers can see that a problem is already known. Update it in the
change that causes or fixes a problem:

- **A change that ships a known problem or an unfinished feature adds an entry,** under the
  group for what the reader is doing. Its heading names the symptom, using the exact error
  text where that helps a reader find it. One or two sentences say what happens, followed
  by a **Workaround:** only if one is verified.
- **A change that fixes one marks its entry instead of deleting it,** by adding `**Fixed**`
  as the first line under its heading. The entry is removed after the first release that
  shows it as **Fixed** is published, so readers can confirm the fix in that release.

## Writing a page

Say only what the reader needs to understand, decide, act, avoid a risk, or verify the
result.

- Describe what the reader sees and does, not how the CLI does it.
- Document a case only when a realistic reader will meet it.
- Say what a command doesn't do only when a reader would expect it to.
- Keep tutorials on the common path, and put exceptions on the reference page.
- Make every code block work when pasted whole. Put alternatives, and examples that change
  something, in separate blocks; a list of read-only examples can share one.
- Say each shared fact once, on the page that owns it, and link to it from the others. For
  example, [Context](../commands/context.md) owns where a command gets its seller or title,
  and [Safety and destructive operations](../safety.md) owns which commands ask first.
- Warn only when a command does something its name doesn't tell the reader, such as removing
  what you didn't name, making a change you can't reverse, or reaching players or
  certification.

## Formatting

The docs are plain Markdown, published elsewhere as well as read here, so:

- **Link between pages with relative file paths,** such as `[Commands](../commands.md)`. Don't
  use absolute URLs or site-generator template syntax.
- **Front matter carries `title` and `description`, and nothing else.** `description` is one
  sentence saying what the page covers; the published page shows it under the title and uses
  it for search. The [docs README](../README.md) sets the order and grouping of pages.
- **The page's `#` heading matches its `title`,** so the page reads the same here as it does
  once published.
