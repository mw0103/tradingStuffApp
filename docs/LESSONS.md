# Lessons

Practices that earned their place by catching something real in this repository. Every entry names
the incident that produced it, because a rule without its scar tissue gets optimised away by the next
person who finds it inconvenient.

Read this before reviewing, before fixing, and before believing a green test run.

---

## 1. Reproduce it. Reading the code is not evidence.

Two reviewers examined the same subscription-lease lifetime on the same day. One traced every
interleaving by hand and concluded it was sound — it published a list of what it had verified. The
other built a harness against the real `IbkrPacingGovernor` and found that 57 leases consumed 80
market-data lines and left 57 held after all of them were released.

The one that ran the code was right.

Careful reading produces confident wrong answers, and it produces them in exactly the cases that
matter — the ones subtle enough to survive the first two reviews. **When a claim can be executed,
execute it.** A scratch harness outside the repo takes minutes. Every critical defect found in this
codebase came with numbers attached.

## 2. A test that has never failed is decoration

**Reintroduce the defect. Confirm the test fails. Restore.** No exceptions.

Six false-green tests were caught this way in one session, and *three of them were caught by the
agent that had just written them*:

- A gap-detection suite with no positive `Reconciled == true` control — a fix that simply never
  counted anything as audited would have passed every other test in the file.
- A partition-isolation test that stopped isolating the moment a new migration pre-created the date
  it poisoned. It passed for the wrong reason.
- A `WaitUntilAsync` whose return value was discarded, so it passed with zero rows persisted.
- An `openOrder` test that called the tracker method directly and passed with the callback wiring
  deleted.
- A migration test asserting `Contains("assumed", ex.Message)` — where the fixture was named
  `995_assumed_then_edited_test.sql` and the message quotes the filename back. It matched itself.
- A test whose first draft passed at the wrong place, because `HttpClient` buffers response content
  and the failure it thought it was injecting was already caught upstream.

None would have been caught by reading the test. Note also the failure mode *inside* the control: one
agent's restore script used `mv`, which left the build binary stale, so its "restored" runs silently
executed the mutated build — a false-green generator inside the false-green detector. Touch the files
and re-run.

## 3. Absence renders as health

The dominant defect class in this repository, by a wide margin. **A query cannot emit a row for a
case that has no row**, so the worst state — nothing there at all — is the one that produces the
cleanest report.

It has appeared at every level:

- A `GROUP BY` over landed bars emits nothing for a window where zero bars landed.
- Coverage built its expected set from `node_assignments` rather than the registry, so an unassigned
  node did not score 0 % — it *vanished*, and the unweighted mean went **up**. 53 of 54 roles
  recording nothing reported 100 %.
- The gap detector's own audit window had a seam between two jobs that widened by a day per day, and
  nothing looked at it. Three deliberately-emptied sessions reported `checked`, zero gaps.
- A missing session row shrinks the coverage denominator, so a wrong calendar makes coverage read
  *higher*.

**Build the expected set first, then left-join reality onto it.** And when a query claims "nothing is
missing", say out loud which table the claim is measured on and what makes an absent row visible. If
you cannot name the mechanism, it does not exist.

## 4. Confident comments are where defects hide

A comment asserting an invariant the code does not hold is worse than no comment: it tells the next
reader not to look.

- *"a republish only happens on a replay, and by then the ledger has already been zeroed"* — the
  grant path violated it, and that comment is why nobody checked. The same false premise was written
  a second time in `ForgetLineLease`, producing a second defect.
- *"the expected total would only ever be too high… never a truncated record"* — true of the
  `orderStatus` path it reasoned about, false of the `ApplyError` path that never updates the total.
- *"Safe to run with other gateways live only because gap scope is per-LEASE"* — four scopes were
  already process-wide constants when it was written.
- *"the clock never reads the table, so it is a genuinely independent witness"* — both sides resolve
  the same singleton, cache included.

When you fix a defect, fix its comment. When you review, **read the comments as claims to be tested,
not as documentation to be trusted.**

## 5. A green unit suite says nothing about the broker

Unit tests stub the socket. They cannot tell you what TWS *accepts* — contract shapes, tick types,
entitlements, error semantics, callback ordering. Those are not knowledge until a live connection has
demonstrated them.

The recorder subscribed to SPX and VIX with a hardcoded `Exchange = "SMART"`. Every test passed. TWS
accepts that for options and stocks and rejects it for index conIds with error 200, so two of the
three core underlyings recorded **nothing at all**.

And when live evidence contradicts a plausible design, the evidence wins: the fix for a leaked
account-summary subscription was "cancel before re-issuing". Live TWS proved that wrong — it caps
*distinct request ids* and does not release the slot on cancel, so cancel-then-reissue failed on the
third cycle exactly like never cancelling. Re-issuing on the **same** id is what works. No amount of
reasoning would have produced that.

**If it touches TWS, run it against the paper account before claiming it works.** Then pin what you
learned with a `Category=RequiresTws` test, so re-verification stops being a ritual that gets skipped.

## 6. Fixes are unreviewed code, and they are written fast

Reviewing the *fix diffs* from a previous round found five new defects — including
`PlanForward` added to only one of its two planners, so the ES job still stopped at its frozen anchor:
**the exact defect that commit's message claimed to have fixed.**

Two fixes shipped broken in a single session, both having passed their author's verification. The tell
was identical each time: the author confirmed the *symptom* disappeared instead of testing the
*mechanism* on a clean state.

So: after fixing, ask **"did I fix every instance?"** (grep for sibling call paths — the gap-bounding
fix reached one of two termination paths, and missed the one that runs every two minutes), and
**"does my new comment match my new code?"**

## 7. Verify the mechanism, not the symptom

A `csproj` change appeared to fix missing SPA assets. It had not — a target-time `<Content Include>`
lands too late to influence `CopyToOutputDirectory` and did nothing. It *looked* fixed because by the
next build `wwwroot` existed and the SDK's implicit glob was doing the copying. Deleting `wwwroot` and
building proved it: zero assets copied.

Test from the clean state that reproduces the original condition, not from the state you happen to be
in after having already worked around it.

## 8. Refuse rather than project

IBKR's schedule feed reaches 1998 for SPY, but before ~2010 it is a weekday fill: sessions on
Christmas Day 1998, every half day 1999-2005 reported as a full close, zero-length rows. Modelling
04:00 back to 2005 would have over-expected up to 240 minutes a day and **manufactured gaps**.

The calendar's `effectiveFrom` is 2010-01-04 and nothing is asserted before it. Not one row is marked
`unverified`, because nothing was projected.

The same discipline appears wherever a fact cannot be recovered:

- `recorder_gaps.closed_by` — `observed` (a tick resumed) vs `inferred` (a later process bounded it).
- `perm_id_state` — `assigned` vs `never_reported` vs `pending`, where pre-existing NULLs are
  `pending` because they were written by code that *could* drop a permId, so calling them
  never-reported would assert something unknown.
- `checksum_source` — `verified` vs `assumed` vs `unknown`.

**An honest gap in the record beats a plausible fabrication, always.** A number nobody can justify is
worse than no number: `null` for "unmeasured" is a first-class answer.

## 9. Fail-safe parts compose into an unsafe whole

Every individual config switch degrades to its safe value on an unrecognised string. That is correct
in isolation and wrong in combination.

`MarketData:Source` was set to `"ibkr"` — plausible, and not one of the recognised values. The market
data service degraded to the deterministic generator *exactly as designed*, while
`Execution:Router=ibkr` kept transmitting. A 10-lot SPY vertical was approved against synthetic quotes
of bid 27.34 / ask 28.46 on a Saturday, when the real market was 0/0, and rested at TWS.

`UNPRICEABLE_LEG` did not catch it: that guard refuses quotes it *cannot price*, and the generator
emits confident, well-formed, entirely fictional ones.

**When two settings must agree, check them together at startup and refuse to boot.** Neither component
can see that the other changed meaning.

## 10. A permanently-red gate is a gate nobody reads

Hit three separate times, and each time the fix was to make the alarm *rarer and truer* rather than
louder:

- Coverage reporting a fabricated 0 % for a weekend, when the honest answer is "not measured".
- The VIX calendar mismatch flagging `succeeded_but_absent` on every correct, complete session.
- A stranded-partition Critical firing every sweep forever, making a genuinely new stranded date
  indistinguishable from the standing one.

A false alarm costs more than the defect it was meant to catch, because it destroys the operator's
willingness to read the signal at all. If a report is red for a non-problem, that is a defect in the
report.

## 11. The test infrastructure lies too

Diagnosis time lost this session to things that were not code defects:

- Postgres `53300: sorry, too many clients already` at ~96 tests, because `PrepareAsync` never
  disposes its data source. Looked exactly like a real failure.
- The same container segfaulting under ~1,000 accumulated test databases that are never dropped,
  producing 83 bogus failures at once.
- `--no-build` silently running a stale DLL after a failed compile, twice.
- A live TWS suite that failed 1-of-12 after heavy probing and passed 12/12 on re-run — some of those
  tests drive a raw `EClientSocket`, bypassing the pacing governor.

Before diagnosing a failure as a defect, rule out the harness. And **duration is not evidence**: a
live test running in 110 ms looks like a silent early return. Prove a live test connects by breaking
the expectation and confirming the venue's real data comes back in the failure message.

## 12. Say what you did not verify

Every report should distinguish what was measured from what was reasoned. Real examples worth
imitating:

> "Markets are closed, so no partial fill could be obtained. Consequence 2's live sequence is covered
> by unit tests and the vendored API's documented fields only, **not by the wire**."

> "Both live cancels received `orderStatus` before `error 202`, but the opposite order is recorded in
> this repo's own history — **so the code is written to be ordering-independent rather than relying on
> what I measured.**"

Two live samples are not a protocol guarantee. Writing the code to survive either ordering costs
little; assuming the ordering you happened to see costs a defect that appears months later.
</content>
