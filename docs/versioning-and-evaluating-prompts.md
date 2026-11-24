# Versioning and testing prompts like code

A prompt in production is code. It decides what an application does, it changes
over time, and a small change can break things far from where it was made.
Yet prompts often live in a string constant, a spreadsheet or a chat history,
and the only test is someone reading a few answers and saying "looks fine".

This note explains the choices behind Prompt Manager: what a version is, what
"production" means, how evals decide whether a change may ship, and where the
approach stops working.

## A version is immutable; production is a pointer

Every change of a prompt's text creates a new numbered version. Versions are
never edited afterwards. That sounds like ceremony, but it is what makes
everything else possible:

- a run records *which* version produced an answer, so the answer can be
  explained a month later;
- an eval result belongs to one version, so "v4 passed 9 of 10 cases" stays
  true;
- a diff between two versions is meaningful, because neither will change.

What applications use is a separate question. Each prompt has a production
pointer that names one version. A new version starts as a **draft**; it becomes
**tested** when it passes every case of an eval, and **production** when someone
promotes it. Rolling back is the same operation pointed at an older version.

Separating "newest" from "in production" lets people keep working on a prompt
without changing what applications see. The API serves the production version
by default and the newest one only when asked (`?label=latest`).

## What a test case looks like

A case is an input and a set of expectations about the answer. The checks are
deliberately plain:

| Check | Good for |
|---|---|
| must contain / must not contain | Facts, names, required phrases, forbidden promises |
| regular expression | Formats: "starts with a greeting", "ends with a number" |
| JSON Schema | Structured output that code will parse |
| rubric for a judge model | Tone, helpfulness, anything a rule cannot express |

The first three are deterministic: the same answer always gets the same
verdict, and they cost nothing. A judge is a second model call that reads the
answer against a rubric and must reply with `{"pass": ..., "reason": ...}`.
Anything else (no JSON, no boolean) counts as a failure rather than a guess.
Judges are useful and noisy, so a case should lean on deterministic checks
where it can and keep rubrics for what is left.

## The gate: a worse version cannot ship quietly

Testing is only worth something if it changes what happens. Here, it decides
promotion: a version whose latest eval passes fewer cases than production's on
the same model is refused. An admin can override that, and the override is
written on the version ("over a regression"), because sometimes a known
trade-off is the right call, but it should never be invisible.

"On the same model" matters. A prompt that passes 10 of 10 on a large model and
7 of 10 on a small one has not regressed between versions; the comparison is
only fair when everything except the prompt is the same.

## Attacks are test cases too

Prompt injection is easy to forget until it happens. The injection pack adds
six standard attacks as ordinary cases, sent through the variable that carries
user input: an instruction override, a fake "developer mode", a request for
the prompt itself, a fake end of input, an attack in another language, and an
order hidden in base64.

Each attack asks the model to repeat a canary, a random word generated when
the pack is added. A case passes when the answer contains neither the canary
nor a recognisable piece of the prompt. Because the canary is fresh, a model
cannot pass by having seen the test before, and because these are normal
cases, they run in every eval and take part in the promotion gate.

## When a model suggests the fix

A failed eval contains most of what is needed to fix the prompt: the version,
the inputs, the answers and exactly which checks failed. The assistant hands
that to a model and asks for a revised prompt with the same variables.

The important part is what happens next. The suggestion is shown as a diff,
can be edited, and is saved only as a **draft**. Saving it immediately runs the
same eval, on the same model with the same judge. A model's revision gets no
shortcut: it passes the cases or it does not ship.

Two details keep this honest. Answers from injection cases are attacker text,
so they are wrapped in tags and described as data, never as instructions. And
the call is stored as a run with its cost, so asking for a suggestion is as
visible as any other model call.

## Where this approach stops working

- **Cases are a sample.** Ten passing cases do not prove a prompt works; they
  prove it works on those ten. The cases need to grow with every bug found in
  production.
- **Judges drift.** A judge model can change its mind between runs or after an
  upgrade. Pinning the judge model and keeping rubrics specific helps, but
  rubric checks remain the least reliable signal.
- **Pass counts hide detail.** "8 of 10" before and after can be two different
  sets of failures. The eval page lists every case, but the gate itself only
  compares counts.
- **Cost and latency are not gated.** A version that passes everything but
  costs twice as much will still be promoted; the model comparison shows the
  numbers, but nothing enforces them.

None of these are reasons to skip testing prompts. They are reasons to treat
an eval as a floor, not a guarantee, and to keep adding cases.
