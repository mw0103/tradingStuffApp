-- The A4 term-structure series: the 9-day and 30-day constant-maturity model-free implied
-- variance points and their slope, one row per SPX session date, built EXACTLY per the frozen
-- construction (docs/research/a4-slope-construction.md) from the ingested chain snapshot.
--
-- Three states a session date can be in, and all three are rows — nothing is silently absent:
--   usable            — both points bracketed and computed; slope present.
--   unusable          — the chain data is fully ingested for the date but the construction
--                       could not produce both points (reason in note). Genuine absence.
--   unresolved        — chain ingestion has not yet terminally resolved every expiration the
--                       date's brackets could need; the row is a placeholder that a later
--                       rebuild replaces. Fetch-failure is NOT absence (readiness audit lesson;
--                       frozen doc § 8), and no unresolved date may enter a backtest sample.
--
-- Rebuilds upsert by session_date, so re-running after more ingestion lands is the intended
-- operating mode, not an anomaly.

CREATE TABLE IF NOT EXISTS research.implied_term_structure (
    session_date        date PRIMARY KEY,
    status              text NOT NULL CHECK (status IN ('usable', 'unusable', 'unresolved')),
    snapshot_utc        timestamptz NOT NULL,

    -- Annualized model-free implied variances at the two frozen tenors, and the frozen
    -- primary slope ln(sigma9/sigma30). NULL unless status = 'usable'.
    variance_9d         double precision,
    variance_30d        double precision,
    slope               double precision,

    -- Bracket diagnostics per the frozen doc § 5 (recorded, never filters).
    near_9d_days        double precision,
    far_9d_days         double precision,
    strikes_9d          integer,
    near_30d_days       double precision,
    far_30d_days        double precision,
    strikes_30d         integer,

    -- Underlying close diagnostic from our own bars (frozen doc § 7); NULL when bars absent.
    underlying_15_30    double precision,

    note                text,
    built_at            timestamptz NOT NULL DEFAULT now(),
    schema_version      integer NOT NULL DEFAULT 1
);

COMMENT ON TABLE research.implied_term_structure IS
    'A4 9d/30d constant-maturity implied variance and slope per docs/research/a4-slope-construction.md; unresolved rows await further chain ingestion and must not enter analyses.';

-- The 4-week T-bill discount rates (FRED DTB4WK) the construction discounts with. Stored as
-- published — percent, discount basis — and converted to continuous compounding in code at
-- load, so the table remains a faithful copy of the source (provenance over convenience).
CREATE TABLE IF NOT EXISTS research.risk_free_rates (
    rate_date           date PRIMARY KEY,
    discount_rate_pct   double precision NOT NULL,
    source              text NOT NULL,
    loaded_at           timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE research.risk_free_rates IS
    'FRED DTB4WK 4-week T-bill discount rates, percent, as published; carried forward across non-publication days by readers.';
