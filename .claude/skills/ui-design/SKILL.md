---
name: ui-design
description: The AI Chat Assistant's visual design system (tokens, typefaces, layout rules, copy voice). Use before adding or changing any Angular page, component, or style in frontend/, so new UI matches the existing design instead of drifting back to generic defaults.
---

# AI Chat Assistant UI design

The Angular frontend (`frontend/src/`) has a deliberate design system. It was set with the `frontend-design` plugin skill. Follow it for any UI change, and update this file if you change the system itself.

## The one idea

**Each speaker has their own typeface.** The assistant writes in a reading serif, and the user writes in a sans. Conversations are transcripts, not chat bubbles: the speaker's name sits in a left margin column (`6rem`) and the text sits beside it, left-aligned. Everything else stays quiet so this carries the personality. Don't add a second "bold" element that competes with it.

## Tokens (`frontend/src/styles.scss`)

Always use the CSS variables. Never hardcode a hex value in a component stylesheet.

| Token | Light | Dark | Use |
|---|---|---|---|
| `--paper` | `#ffffff` | `#142024` | page background |
| `--surface` | `#f4f6f5` | `#1b2a2f` | sidebar, auth sample panel (inputs and the composer use `--paper`) |
| `--ink` | `#17262b` | `#e4ecea` | body text |
| `--ink-muted` | `#5a6b70` | `#93a5a8` | secondary text, assistant speaker label, hints |
| `--rule` | `#cbd3d1` | `#2c3e43` | borders and dividers |
| `--accent` | `#0e6b5c` | `#4fc1a9` | primary buttons, links, the user's speaker label, focus ring, chart bars |
| `--accent-soft` | `#d5e6e1` | `#1d3b37` | the active sidebar item |
| `--danger` / `--danger-soft` | `#b42318` / `#fbe4e1` | `#f2877b` / `#3a2220` | errors only |

The page background is **white by product decision**, even when the OS is in dark mode. Don't reintroduce a `prefers-color-scheme` switch. The dark values only apply when `data-theme="dark"` is set on `<html>` (opt-in, nothing sets it today).

**Type**: `--font-voice` = Newsreader (the assistant's replies, page `h1`/`h2`, the wordmark). `--font-ui` = Instrument Sans (everything else, including the user's messages). Both load from Google Fonts in `index.html`. The scale is `--step--1` (13px) through `--step-3` (36px). Don't introduce a third family or a monospace face.

**Radius**: `--radius-control` (6px) for buttons and inputs, `--radius-field` (10px) for the chat composer only. Most surfaces have no radius and are separated by `--rule` lines, not shadows.

## Shared primitives (global classes)

- Buttons: `.btn` plus `.btn-primary` (one per view, the main action) or `.btn-quiet` (outlined, secondary).
- Forms: `.field` (a label wrapping an input), `.field-row` (label + inline link), `.field-hint` (add `.is-error` to turn it red instead of adding a duplicate error line), `.field-error`, and `.form-error` (a server error, with a left danger rule and `role="alert"`).
- `.muted`, `.visually-hidden`.
- Auth pages wrap their content in `<app-auth-shell>` (`auth/auth-shell/`). It provides the wordmark, the form column, and the sample transcript panel on wide screens. Keep `.auth-card` on the form: it styles the form, and the live-test scripts select on it.

## Layout rules

- Chat (`chat/`): a sidebar (`16.5rem`, `--surface`) plus a transcript column capped at `48rem`. Below `48rem` the sidebar becomes a drawer (`sidebarOpen` signal, scrim, "Chats" button in `.mobile-bar`), and the speaker label stacks above the text.
- Admin (`admin/usage/`): max width `64rem`. The table has no card around it, just a strong ink rule on top and `--rule` row lines. Number columns use `.num` (right-aligned, `tabular-nums`).
- Content is left-aligned everywhere except the auth form, which is centered in its column on wide screens.
- Everything must work at 390px wide with no horizontal scroll.

## Motion

Only two animations exist: the "Thinking…" dots and the top loading bar. Both are functional, and both are disabled by the global `prefers-reduced-motion` rule. Motion is allowed when it answers a user action (the drawer slide). Don't add entrance animations or hover transitions on lists.

## Copy voice

- Sentence case. Active verbs that say what happens: "Create account", "Send reset link", "Save new password", "Apply dates", "Send again". A button's label and its result use the same word.
- Errors say what happened and what to do next, and never apologize ("This message didn't send." plus a "Send again" button).
- Empty states invite an action ("What are you working on?").
- Avoid these defaults: arrows in link or button text ("← Back", "Next →"), ALL-CAPS labels or eyebrows, middle-dot meta strings, and one accented word inside a headline.

## Before you finish a UI change

1. `npm run build` stays within the component style budget (4 kB warning).
2. `npm test -- --watch=false --browsers=ChromeHeadless` passes.
3. Screenshot it for real (puppeteer-core against the local Chrome, per CLAUDE.md) at desktop width and at 390px wide, with the OS in dark mode too (the page must stay white), and look at the screenshots before calling it done.
