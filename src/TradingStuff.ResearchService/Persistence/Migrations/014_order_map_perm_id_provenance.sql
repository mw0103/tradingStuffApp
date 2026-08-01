-- 014: say WHY gateway.ibkr_order_map.perm_id is null.
--
-- THE FAILURE THIS EXISTS FOR. perm_id is the only order identifier that survives a reconnect or a
-- gateway restart: ibkr_order_id belongs to this process, and TWS hands the same numbers out again
-- after its "Reset API order ID sequence". So perm_id is what a reconciliation against the broker
-- has to key on, and a null one means the row cannot be matched to anything at IBKR.
--
-- Until now a null could mean either of two opposite things. Either TWS never reported a permId --
-- which is itself an answer, and a strong one: verified live on the paper account, an order refused
-- at TWS's own validation layer (errors 110 and 201) produces an `error` callback and NOTHING else,
-- no openOrder, no orderStatus, so there is no permId in existence to record and the order almost
-- certainly never reached IB's servers. Or the gateway had one and dropped it, which is what it did:
-- IbkrOrderTracker.ApplyStatus early-returned on an already-terminal order, so a trailing
-- orderStatus arriving after an error had killed the order took its permId with it. Precisely the
-- orders whose fate is most ambiguous -- the rejected and cancelled ones -- were the ones stored
-- without the identifier needed to resolve them, and nothing in the row said which case it was.
--
-- The tracker fix makes the gateway stop dropping them (permId is now taken from openOrder,
-- orderStatus and execDetails alike, first non-zero wins). This column is the other half: it records
-- the CONCLUSION rather than leaving a default to be interpreted, so an operator or a reconciliation
-- tool can tell a row it should go and match at the broker from one it should not.
--
--   pending        -- no terminal outcome recorded yet. Says nothing about perm_id either way.
--   assigned       -- TWS reported a permId and it is in perm_id.
--   never_reported -- the order reached a terminal state and TWS never reported a permId on any
--                     callback. Deliberately not "never_assigned": what is being recorded is what
--                     the broker told us, not a claim about TWS's internal state.
--
-- EXISTING ROWS. Backfilled to 'assigned' where a perm_id is present. Rows already null stay
-- 'pending' rather than being called 'never_reported', because they were written by the code that
-- could drop a permId and there is no way after the fact to tell which case any one of them was.
-- Recording the ambiguity is the honest move; guessing would make the column mean the same nothing
-- the null already meant.

ALTER TABLE gateway.ibkr_order_map
    ADD COLUMN perm_id_state text NOT NULL DEFAULT 'pending';

UPDATE gateway.ibkr_order_map
SET perm_id_state = 'assigned'
WHERE perm_id IS NOT NULL;

-- The second conjunct is the invariant that keeps the column honest: a stored perm_id can only ever
-- be described as 'assigned', so no future writer can leave a real identifier behind a state that
-- tells a reconciliation tool to ignore the row.
ALTER TABLE gateway.ibkr_order_map
    ADD CONSTRAINT ibkr_order_map_perm_id_state_chk
    CHECK (perm_id_state IN ('pending', 'assigned', 'never_reported')
           AND (perm_id IS NULL OR perm_id_state = 'assigned'));
