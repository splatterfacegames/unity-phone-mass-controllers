# site/

The project website for unity-phone-mass-controllers — a static, dependency-free explainer
(no build step, no framework). Everything is hand-written HTML/CSS/vanilla JS and works over
`file://` as well as https.

Deployed automatically by [.github/workflows/pages.yml](../.github/workflows/pages.yml) to
GitHub Pages on every push to `main` that touches `site/`. Served at the custom domain
**pmc-unity.jethachan.net** (see `CNAME`; DNS via Cloudflare → GitHub Pages).

Sibling site: **pmc.jethachan.net** (the Godot port) — same wire protocol, independent codebase.

- `index.html` — the page
- `style.css` — the only stylesheet
- `app.js` — the simulated lobby widget (TV roster + a phone that buzzes in sync)
- `assets/` — `repo-qr.png` (a QR of this repo's URL, generated offline) and `og-card.png`
  (social card)
