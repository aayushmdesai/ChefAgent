# src/frontend/ — React app

React 19.2.6 + Vite 8.0.12 + Tailwind CSS 3.4.19, ESLint 10.3.0. Entry: `index.html` → `src/main.jsx` → `src/App.jsx`.

Components (`src/components/`):
- `ChatInput.jsx`
- `ChatMessages.jsx`
- `MealPlanView.jsx`
- `ProfileSidebar.jsx`
- `RecipeCard.jsx`

## Commands

```bash
npm install
npm run dev      # vite, dev server
npm run build    # vite build
npm run lint      # eslint .
npm run preview
```
Or from the repo root: `make frontend`.

## No test suite exists

There is no test framework, test file, or `test` script in `package.json` — no Vitest, no Jest, no `@testing-library/*` in `dependencies`/`devDependencies`, no `*.test.jsx`/`*.spec.jsx` files anywhere under `src/frontend/`. Do not claim frontend test coverage exists, do not assume a test runner is configured, and do not try to run "the frontend tests" — there aren't any to run. If verification is needed, use the `dev` server and manual/browser-driven checks instead.

## Deploy

Referenced in docs as deployed to Vercel — confirmed only via a doc-referenced live URL, not any in-repo config (no `vercel.json`, no frontend-specific Dockerfile). Treat as unverified from this repo alone.
