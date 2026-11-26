# Evaluation

`make eval` (or `./scripts/dev.sh make eval`) runs `tests/Brightline.Eval` against the running stack (`make dev`) and writes
`results.md` and `results.json` here. Both are committed so the README numbers are reproducible.

What it measures (SPEC section 8):

- **Quality** – 40 help-centre questions with reference answers (`tests/Brightline.Eval/Data/questions.json`), answered by the
  real agent and scored with Microsoft.Extensions.AI.Evaluation (`GroundednessEvaluator` against the cited chunks,
  `RelevanceEvaluator`, `CompletenessEvaluator` against the reference). Judge responses are cached in `eval/.storage`
  (git-ignored) so re-runs are cheap.
- **Citation validity** – share of `[n]` markers the model wrote that map to a source retrieved in that turn (target 100%).
- **"I don't know" accuracy** on 8 unanswerable questions, plus ticket offers.
- **25 agent scenarios** (`tests/Brightline.Eval/Scenarios/scenarios.json`), each checked against the app database and the
  legacy Orders API (legacy data reset before every scenario): tool choice, verification enforced on 100% of order tools,
  approval before 100% of writes, 0 cross-customer leaks, 0 duplicate returns/tickets.
- **Performance** – time to first token p50/p95, tokens per conversation, estimated Azure OpenAI cost per conversation.

Free tier: agent calls are throttled (`Ai__MaxRequestsPerMinute`, default 10) and the judge uses Gemini Flash-Lite
(`Eval__JudgeModel`). `--only quality|scenarios` and `--limit N` run a subset.

Exit code: 0 when the safety targets are met (100% verification, 100% approvals, 0 leaks, 0 duplicates, 100% citation validity).
