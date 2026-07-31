-- 002: the 2026-07-31 manual wire-probe session, persisted as data.
-- Source: read-only TWS probes recorded in docs/research/ibkr-data-capability-matrix.md.
-- Paper TWS on 7497, API server version 187, live entitlements shared to the paper user
-- (Cboe indexes + OPRA + CME), all data farms connected. Raw head timestamps are TWS strings
-- (formatDate=1, exchange-local); research ingestion uses formatDate=2 precisely to avoid them.

INSERT INTO research.capability_probes
    (probe_key, con_id, ran_at, tws_server_version, market_data_type, succeeded, result, error_code, notes)
VALUES
    ('contract:SPX:IND', 416904, '2026-07-31T15:40:00Z', 187, NULL, true,
     '{"conId": 416904, "exchange": "CBOE"}', NULL,
     'SPX index resolves on CBOE.'),

    ('chain:SPX', 416904, '2026-07-31T15:40:00Z', 187, NULL, true,
     '{"classes": {"SPX": {"expirations": 20, "lastExpiration": "20311218"}, "SPXW": {"expirations": 39, "lastExpiration": "20270630", "includesZeroDte": true}}}', NULL,
     'reqSecDefOptParams returns both classes. SPX (AM) dates are Thursdays; SPXW (PM) Fridays; tradingClass is required or resolution hits error 200.'),

    ('head_timestamp:SPX:TRADES', 416904, '2026-07-31T15:40:00Z', 187, NULL, true,
     '{"raw": "20040304-14:30:00"}', NULL, 'SPX index intraday history reaches 2004.'),

    ('head_timestamp:SPY:TRADES', NULL, '2026-07-31T15:40:00Z', 187, NULL, true,
     '{"raw": "19930129-09:00:00"}', NULL, NULL),

    ('head_timestamp:VIX:TRADES', NULL, '2026-07-31T15:40:00Z', 187, NULL, true,
     '{"raw": "20051003-13:30:00"}', NULL, NULL),

    ('head_timestamp:ES-CONTFUT:TRADES', NULL, '2026-07-31T15:40:00Z', 187, NULL, true,
     '{"raw": "20220619-22:00:00"}', NULL, 'Continuous futures view only reaches ~4 years back.'),

    ('head_timestamp:ES-FUT-202609:TRADES', NULL, '2026-07-31T15:42:00Z', 187, NULL, true,
     '{"raw": "20230820-22:00:00"}', NULL,
     'A single ES contract carries ~3 years — deeper than the documented 2-year post-expiry floor.'),

    ('head_timestamp:SPX-OPT-20260820-7500C:BID_ASK', 800237324, '2026-07-31T15:46:00Z', 187, NULL, true,
     '{"raw": "20260610-16:08:51"}', NULL,
     'DECISIVE: a long-listed SPX monthly has quote history only weeks deep. Option data must be recorded live-forward.'),

    ('head_timestamp:SPX-OPT-20260820-7500C:TRADES', 800237324, '2026-07-31T15:46:00Z', 187, NULL, true,
     '{"raw": "20260511-13:47:28"}', NULL, NULL),

    ('hist:SPX:1min:TRADES:endDate=2010-07-30', 416904, '2026-07-31T15:42:00Z', 187, NULL, true,
     '{"bars": 90}', NULL, 'Deep 1-minute SPX backfill is real, not just a head-timestamp claim.'),

    ('hist:SPX:1min:TRADES:endDate=2021-07-30', 416904, '2026-07-31T15:42:00Z', 187, NULL, true,
     '{"bars": 90}', NULL, NULL),

    ('hist:SPY:1min:TRADES:endDate=2005-07-29', NULL, '2026-07-31T15:42:00Z', 187, NULL, true,
     '{"bars": 90}', NULL, NULL),

    ('hist:SPY:5secs:TRADES:recent', NULL, '2026-07-31T15:42:00Z', 187, NULL, true,
     '{"bars": 360, "window": "30 minutes"}', NULL, NULL),

    ('hist:ES-CONTFUT:1min:TRADES:recent', NULL, '2026-07-31T15:42:00Z', 187, NULL, true,
     '{"bars": 1062, "includesOvernight": true}', NULL, NULL),

    ('hist:ES-CONTFUT:1day:TRADES:3Y', NULL, '2026-07-31T15:42:00Z', 187, NULL, true,
     '{"bars": 766}', NULL, NULL),

    ('hist:ES-CONTFUT:endDateTime', NULL, '2026-07-31T15:44:00Z', 187, NULL, false,
     '{}', 10339,
     'CONTFUT rejects a past endDateTime: deep ES intraday backfill must walk individual expired contracts.'),

    ('hist:SPX:1min:MIDPOINT', 416904, '2026-07-31T15:44:00Z', 187, NULL, false,
     '{}', 162, 'Indices are TRADES-only for historical bars.'),

    ('hist:VIX:1min:TRADES:today', NULL, '2026-07-31T15:44:00Z', 187, NULL, true,
     '{"bars": 495, "firstBarLocal": "02:15 America/Chicago"}', NULL,
     'VIX intraday values exist through the global session; deep 1-min floor still unprobed.'),

    ('hist:VIX:1day:TRADES:10Y', NULL, '2026-07-31T15:44:00Z', 187, NULL, true,
     '{"bars": 2512, "firstBar": "20160803"}', NULL, NULL),

    ('hist:SPX-OPT-20260820-7500C:1min:BID_ASK:endDate=2026-04-30', 800237324, '2026-07-31T15:46:00Z', 187, NULL, false,
     '{}', 162,
     'HMDS returns no data before the head timestamp even for a long-listed contract: option history really is weeks deep.'),

    ('hist:SPXW-OPT-20260826-7450C:1min:BID_ASK:useRTH=0', NULL, '2026-07-31T15:46:00Z', 187, NULL, true,
     '{"bars": 927, "firstBarLocal": "20260730 19:15 America/Chicago"}', NULL,
     'Overnight (Cboe GTH) option bars exist — the GTH session is recordable and backtestable once recorded.'),

    ('mkt_data_type:SPX', 416904, '2026-07-31T15:41:00Z', 187, 1, true,
     '{"last": 7445.70}', NULL, 'Live, not delayed: entitlements are shared to the paper user.'),

    ('mkt_data_type:SPY', NULL, '2026-07-31T15:41:00Z', 187, 1, true,
     '{"bid": 741.36, "ask": 741.39, "sizes": true}', NULL, NULL),

    ('mkt_data_type:ES-FUT-202609', NULL, '2026-07-31T15:41:00Z', 187, 1, true,
     '{"last": 7464.75, "fullL1": true}', NULL, NULL),

    ('mkt_data_type:VIX', NULL, '2026-07-31T15:44:00Z', 187, 1, true,
     '{"last": 17.40, "bidAsk": false}', NULL, 'Indices publish no bid/ask.'),

    ('stream:SPXW-OPT-20260826-7450C', NULL, '2026-07-31T15:41:00Z', 187, 1, true,
     '{"bidAsk": true, "sizes": true, "volumeGenericTick": 100, "openInterestGenericTick": 101, "greeksVariants": [10, 11, 12, 13], "modelGreeks": {"iv": true, "delta": true, "gamma": true, "vega": true, "theta": true, "undPrice": true}}', NULL,
     'Full live option surface inputs verified: L1 with sizes, volume, OI, and all four tickOptionComputation variants.');
