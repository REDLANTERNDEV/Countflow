<!-- Thanks for the contribution. Keep this short; the questions are the ones a
     reviewer will ask anyway. -->

## What and why

<!-- What changed, and the problem it solves. Link the issue: Fixes #123 -->

## How you verified it

<!-- The important one. "Tests pass" on its own is not an answer here — every
     phase of this project shipped a defect that a green suite could not see.
     Say what you ran, against what. For example:
       - drove the counting loop through the handheld on the compose stack
       - stopped the API mid-confirmation and watched the queue drain on restart
       - reverted the fix and watched the new test fail                        -->

## Checklist

- [ ] `dotnet build` and `dotnet test` pass (backend changes)
- [ ] `pnpm --recursive typecheck`, `pnpm test` and `pnpm build` pass (frontend changes)
- [ ] `pnpm export:openapi && pnpm generate:api` run and committed, if any endpoint changed
- [ ] New coded errors appear in `en.ts`, `tr.ts` and `docs/api/error-codes.md` — and in `refusals.ts` if a handheld can meet them
- [ ] Docs updated: `docs/decisions.md` for a changed decision, `docs/invariants.md` for a new rule, `docs/gotchas.md` for anything that cost you an afternoon
- [ ] `CHANGELOG.md` updated under Unreleased, for anything user-visible
- [ ] No new way to write to the stock ledger

## Anything you are unsure about

<!-- Reviewers would rather hear it here than find it. -->
