---
name: ui-design
description: The AI Chat Assistant's visual design system (modern SaaS look - tokens, type, components, layout rules, copy voice). Use before adding or changing any Angular page, component, or style in frontend/, so new UI matches the existing design instead of drifting back to generic defaults.
---

# AI Chat Assistant UI design

The Angular frontend (`frontend/src/`) has a deliberate design system: a **modern SaaS look**. Follow it for any UI change, and update this file if you change the system itself.

## The one idea

**Quiet slate surfaces, one indigo accent, soft depth.** Cards and bubbles sit on a pale page with a 1px border and a faint shadow; the accent is reserved for the primary action, the user's message bubble, the active sidebar item, links, focus and chart bars. Don't add a second accent colour or heavy decoration.

## Tokens (`frontend/src/styles.scss`)

Always use the CSS variables. Never hardcode a hex value in a component stylesheet.

| Token | Light | Dark | Use |
|---|---|---|---|
| `--bg` | `#f8fafc` | `#0b1220` | page background |
| `--surface` | `#ffffff` | `#111a2e` | cards, sidebar, inputs, composer |
| `--surface-muted` | `#f1f5f9` | `#16213a` | hover fills, table headers, code blocks |
| `--border` / `--border-strong` | `#e2e8f0` / `#cbd5e1` | `#223052` / `#31426b` | dividers and card borders / input and button borders |
| `--text` / `--text-muted` | `#0f172a` / `#64748b` | `#e5e9f2` / `#94a3b8` | body / secondary text |
| `--accent`, `--accent-hover`, `--accent-soft`, `--accent-contrast` | `#4f46e5`, `#4338ca`, `#eef2ff`, `#fff` | `#818cf8`, `#a5b4fc`, `#1e2750`, `#0b1020` | primary buttons, user bubble, links, focus, active sidebar item |
| `--danger`, `--danger-soft`, `--danger-border` | `#b91c1c`, `#fef2f2`, `#fecaca` | `#fca5a5`, `#3a1a1d`, `#7f1d1d` | errors only |
| `--shadow-sm` / `--shadow-md` | | | cards and buttons / composer, auth card, drawer |

**Light is the default regardless of the OS setting** - don't reintroduce a `prefers-color-scheme` switch. Dark is opt-in: `ThemeService` (`core/services/theme.service.ts`) sets `data-theme="dark"` on `<html>` and remembers the choice in `localStorage` (every access try/catch'd, so the app works when storage is blocked). `AppComponent` injects it so the stored theme applies on every route. The toggle lives in the chat sidebar and the usage page's top bar.

**Type:** one family, Inter (`--font-sans`, loaded from Google Fonts in `index.html`). The scale is `--step--1` (13px) to `--step-3` (28px), body is `--step-0` (15px). `--font-code` (system monospace) is for code blocks only. Don't add a third family.

**Shape:** `--radius-sm` 8px (buttons, inputs), `--radius-md` 12px (cards), `--radius-lg` 16px (message bubbles, composer). Spacing follows 4/8px steps.

## Shared primitives (global classes)

- `.brand` - the product mark (gradient square plus the name). Used on the auth pages, the chat sidebar and the usage page.
- `.card` - bordered, rounded surface with `--shadow-sm`.
- Buttons: `.btn` plus `.btn-primary` (one per view, the main action) or `.btn-quiet` (outlined, secondary).
- Forms: `.field` (a label wrapping an input), `.field-row`, `.field-hint` (add `.is-error` to turn it red), `.field-error`, `.form-error` (a server error: bordered red box, `role="alert"`), `.input-reveal` + `.reveal` (show/hide password via `PasswordVisibilityDirective`).
- `.muted`, `.visually-hidden`.
- Auth pages wrap their content in `<app-auth-shell>` (`auth/auth-shell/`): the brand, a centred `.card` panel, and a one-line tagline. Keep `.auth-card` on the form; the live-test scripts select on it.

## Layout rules

- **Top bar** (`shared/top-nav/`, `<app-top-nav>`): on the signed-in pages (chat, appointments). The brand (`.brand` link home), then the section tabs (today only **Chatbot**; the active tab uses `--accent-soft` + `--accent` and `aria-current="page"`; **Messages** is added in Phase 12), and a content-projection slot at the right-hand end for page controls. Pages that have their own account controls (chat: in the sidebar footer) project nothing; the appointments page projects the theme toggle and Log out. Below `40rem` the brand text is visually hidden and projected controls wrap onto their own row. Don't put the brand anywhere else on those pages. (The usage page keeps its own top bar.)
- Chat (`chat/`): a 17rem `--surface` sidebar below the top bar. Top to bottom: a **pinned** `Book appointment` row (`.pinned-link`: calendar icon, a "Soon" tag, links to `/appointments`; it never scrolls away), a "Chats" header with an **icon-only round "+"** at its right (`.new-chat`, `aria-label="New chat"`; there is no text "New chat" button), the chat list (the only part that scrolls) grouped Today / Yesterday / Earlier, then the footer (avatar and name, theme toggle, Log out, and "Usage logs" for admins). Beside the sidebar is a transcript column capped at `50rem`. User messages are right-aligned accent bubbles; assistant replies are left-aligned bordered cards with an "AI" avatar and a Copy button beneath. The composer is a rounded, shadowed pill pinned to the bottom. The page is a column: top bar, then the sidebar and transcript filling the rest of the height. Below `48rem` the sidebar becomes a drawer (`sidebarOpen` signal, scrim, "Chats" button in `.mobile-bar`).
- Admin (`admin/usage/`): a top bar (brand, theme toggle, Back to chat), a heading, a filter card, stat cards, the per-day bar chart, then the table in a card with a muted header row and a hover row. Max width `68rem`. Number columns use `.num` (right-aligned, `tabular-nums`).
- Everything must work at 390px wide with no horizontal scroll.

## Rendering model replies (markdown)

Assistant text goes through `<app-markdown [source]="...">` (`shared/markdown/`), never `{{ }}` or `innerHTML` directly. It is untrusted model output: raw HTML and images are shown as plain text (a remote image URL can leak the reader's IP), and Angular's sanitizer stays on. Its styles hang off `.md` and use the same tokens; only code uses `--font-code`.

## Motion

Only these: the "Thinking…" dots, the top loading bar, the 0.12s button/colour transition, and the mobile drawer slide. All are disabled by the global `prefers-reduced-motion` rule. No entrance animations or hover transforms.

## Copy voice

- Page titles come from the route (`title:` in `app.routes.ts`) via `AppTitleStrategy`: "Log in - AI Chat Assistant".
- Don't show a number that isn't real: the usage page says "Not tracked" for cost instead of `$0.00`.
- Sentence case. Active verbs that say what happens: "Create account", "Send reset link", "Save new password", "Apply dates", "Send again". A button's label and its result use the same word.
- Errors say what happened and what to do next, and never apologize ("This message didn't send." plus a "Send again" button).
- Empty states invite an action ("What are you working on?" plus example prompts).
- Avoid: arrows or "←" in link/button text (the Send button's icon is an SVG, not text), ALL-CAPS labels, and middle-dot meta strings.

## Before you finish a UI change

1. `npm run build` stays within the component style budget (7 kB warning, 10 kB error in `angular.json`).
2. `npm test -- --watch=false --browsers=ChromeHeadless` passes.
3. **Never write test data into the development database to get screenshots** (no seeded accounts, no direct SQL). Fake the API instead: run `ng serve` alone and answer `/api/*` with Puppeteer request interception (`page.setRequestInterception`), with a fake JWT in `localStorage` (`aichat.auth.token` / `aichat.auth.user`; the token needs a real `exp`).
4. Screenshot it for real (puppeteer-core against the local Chrome) at desktop width and at 390px wide, in light and dark themes, and with the OS in dark mode too (the page must stay light unless the theme toggle was used). Use a fresh browser context per shot or clear `aichat.theme`, otherwise a stored dark theme leaks into later shots. Look at the screenshots before calling it done.
