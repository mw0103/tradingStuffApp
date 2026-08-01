import React, { useState, useEffect } from 'react';
import type { AutomationStatusReport } from '../types/automation';
import './Automation.css';

interface LoadingState {
  isLoading: boolean;
  error: string | null;
}

const Automation: React.FC = () => {
  const [data, setData] = useState<AutomationStatusReport | null>(null);
  const [state, setState] = useState<LoadingState>({ isLoading: true, error: null });
  const [autoRefresh, setAutoRefresh] = useState(true);
  const [killSwitchReason, setKillSwitchReason] = useState('');
  const [isKillSwitchPending, setIsKillSwitchPending] = useState(false);
  const REFRESH_INTERVAL = 30000; // 30 seconds

  const fetchAutomation = async () => {
    setState({ isLoading: true, error: null });
    try {
      const response = await fetch('/research/automation');
      if (!response.ok) {
        if (response.status === 503) {
          throw new Error('Research service is unavailable. Is the database running?');
        }
        throw new Error(`Failed to fetch automation status: ${response.statusText}`);
      }
      const jsonData = await response.json();
      setData(jsonData);
      setState({ isLoading: false, error: null });
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown error occurred';
      setState({ isLoading: false, error: message });
      setData(null);
    }
  };

  const engageKillSwitch = async () => {
    if (!data) return;
    setIsKillSwitchPending(true);
    try {
      const response = await fetch('/research/automation/kill', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ reason: killSwitchReason || undefined }),
      });

      if (!response.ok) {
        if (response.status === 503) {
          throw new Error('Research service is unavailable.');
        }
        throw new Error(`Failed to engage kill switch: ${response.statusText}`);
      }

      setKillSwitchReason('');
      // Refetch the status after engaging
      setTimeout(() => {
        fetchAutomation();
      }, 500);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown error occurred';
      setState({ isLoading: false, error: message });
    } finally {
      setIsKillSwitchPending(false);
    }
  };

  useEffect(() => {
    fetchAutomation();
  }, []);

  useEffect(() => {
    if (!autoRefresh) return;

    const interval = setInterval(() => {
      fetchAutomation();
    }, REFRESH_INTERVAL);

    return () => clearInterval(interval);
  }, [autoRefresh]);

  const formatDateTime = (isoString: string | null): string => {
    if (!isoString) return '—';
    try {
      return new Date(isoString).toLocaleString();
    } catch {
      return isoString || '—';
    }
  };

  const formatPrice = (price: number | null): string => {
    if (price === null) return '—';
    return price.toFixed(2);
  };

  const getArmStateBgColor = (armState: string): string => {
    if (armState === 'armed') return 'good';
    return 'bad';
  };

  const sortedRecentDecisions = data?.recentDecisions
    ? [...data.recentDecisions].sort((a, b) => new Date(b.decidedAt).getTime() - new Date(a.decidedAt).getTime())
    : [];

  const sortedSubmittedDecisions = data?.submittedThisSession
    ? [...data.submittedThisSession].sort((a, b) => new Date(b.decidedAt).getTime() - new Date(a.decidedAt).getTime())
    : [];

  return (
    <div className="automation-container">
      <header className="automation-header">
        <h1>Automation Status</h1>
        <div className="header-controls">
          <button className="refresh-button" onClick={fetchAutomation} disabled={state.isLoading}>
            {state.isLoading ? 'Refreshing...' : 'Refresh'}
          </button>
          <label className="auto-refresh">
            <input
              type="checkbox"
              checked={autoRefresh}
              onChange={(e) => setAutoRefresh(e.target.checked)}
            />
            Auto-refresh ({REFRESH_INTERVAL / 1000}s)
          </label>
        </div>
      </header>

      {state.error && (
        <div className="error-box">
          <strong>Error:</strong> {state.error}
        </div>
      )}

      {state.isLoading && !data && (
        <div className="loading-box">
          <p>Loading automation status...</p>
        </div>
      )}

      {data && (
        <>
          {/* Armed/Disarmed Banner */}
          <section className="automation-section">
            <div className={`arm-status-banner ${getArmStateBgColor(data.armState)}`}>
              <div className="arm-status-header">
                <div className="arm-indicator">{data.armed ? '⊕ ARMED' : '⊘ DISARMED'}</div>
                <div className="arm-state-badge">{data.armState}</div>
              </div>
              <div className="arm-reason">
                <strong>Reason:</strong> {data.armReason}
              </div>
              <div className="arm-details">
                <div><strong>Enabled:</strong> {data.enabled ? 'Yes' : 'No'}</div>
                <div><strong>Last Checked:</strong> {formatDateTime(data.armCheckedAt)}</div>
                {data.notes && <div><strong>Notes:</strong> {data.notes}</div>}
              </div>
            </div>
          </section>

          {/* Execution Plane */}
          <section className="automation-section">
            <h2>Execution Plane</h2>
            {data.executionPlaneError ? (
              <div className="error-box">
                <strong>Could not be read:</strong> {data.executionPlaneError}
              </div>
            ) : data.executionPlane ? (
              <div className="execution-plane-box">
                <div><strong>Router:</strong> {data.executionPlane.router}</div>
                <div><strong>Portfolio Source:</strong> {data.executionPlane.portfolioSource}</div>
                <div><strong>Market Data Source:</strong> {data.executionPlane.marketDataSource}</div>
                {data.executionPlane.marketDataSourceConfigured && (
                  <div>
                    <strong>MarketData:Source (configured):</strong>{' '}
                    {data.executionPlane.marketDataSourceConfigured}
                  </div>
                )}
              </div>
            ) : (
              /*
               * Neither a value NOR an error means nobody has measured it yet — automation only reads
               * the execution plane when it evaluates, and a disabled loop never evaluates. Rendering
               * that as "Error" was a red state for a non-problem, which is the false alarm
               * docs/LESSONS.md §10 is about: an operator who sees a red box on a correctly-idle
               * service learns to stop reading the box. "Not measured" is the honest third state.
               */
              <div className="not-measured-box">
                <strong>Not measured yet.</strong> Automation reads the execution plane only when it
                evaluates, and it has not evaluated since this service started. This is not an error.
              </div>
            )}
          </section>

          {/* Session */}
          <section className="automation-section">
            <h2>Session</h2>
            <div className="session-box">
              <div><strong>Calendar:</strong> {data.session.calendar}</div>
              <div><strong>In Session:</strong> {data.session.inSession ? 'Yes' : 'No'}</div>
              {data.session.label && <div><strong>Label:</strong> {data.session.label}</div>}
              {data.session.tradingDate && <div><strong>Trading Date:</strong> {data.session.tradingDate}</div>}
              <div><strong>Session Key:</strong> <code>{data.session.sessionKey}</code></div>
            </div>
          </section>

          {/* Cap */}
          <section className="automation-section">
            <h2>Order Cap</h2>
            <div className="cap-box">
              <div className="cap-status">
                <span className="cap-label">This Session:</span>
                <span className="cap-value">{data.ordersThisSession} / {data.orderCap}</span>
              </div>
              <div className="cap-remaining">
                <span className="cap-label">Remaining:</span>
                <span className={`cap-value ${data.capRemaining <= 0 ? 'exhausted' : ''}`}>
                  {data.capRemaining}
                </span>
              </div>
            </div>
          </section>

          {/* Kill Switch */}
          <section className="automation-section">
            <h2>Kill Switch</h2>
            <div className={`kill-switch-status ${data.killSwitch.engaged ? 'engaged' : 'disengaged'}`}>
              <div className="kill-switch-state">
                {data.killSwitch.engaged ? '🛑 ENGAGED' : '✓ Disengaged'}
              </div>
              {data.killSwitch.engagedAt && (
                <div className="kill-switch-time">
                  <strong>Engaged at:</strong> {formatDateTime(data.killSwitch.engagedAt)}
                </div>
              )}
              {data.killSwitch.reason && (
                <div className="kill-switch-reason">
                  <strong>Reason:</strong> {data.killSwitch.reason}
                </div>
              )}
              <div className="kill-switch-durability">
                <strong>Durability:</strong> {data.killSwitch.durability}
              </div>
              {!data.killSwitch.engaged && (
                <div className="kill-switch-control">
                  <input
                    type="text"
                    placeholder="Optional reason"
                    value={killSwitchReason}
                    onChange={(e) => setKillSwitchReason(e.target.value)}
                    disabled={isKillSwitchPending}
                  />
                  <button
                    className="kill-switch-button"
                    onClick={engageKillSwitch}
                    disabled={isKillSwitchPending}
                  >
                    {isKillSwitchPending ? 'Engaging...' : 'Engage Kill Switch'}
                  </button>
                </div>
              )}
            </div>
          </section>

          {/* Submitted This Session */}
          <section className="automation-section">
            <h2>Submitted This Session ({sortedSubmittedDecisions.length})</h2>
            {data.persistenceError ? (
              <div className="error-box">
                <strong>Persistence error:</strong> {data.persistenceError}
              </div>
            ) : sortedSubmittedDecisions.length > 0 ? (
              <div className="table-wrapper">
                <table className="automation-table">
                  <thead>
                    <tr>
                      <th>Decided At</th>
                      <th>Trigger</th>
                      <th>Action</th>
                      <th>Order ID</th>
                      <th>Lifecycle Status</th>
                      <th>Limit Price</th>
                      <th>Price Source</th>
                    </tr>
                  </thead>
                  <tbody>
                    {sortedSubmittedDecisions.map((decision) => (
                      <tr key={decision.decisionId}>
                        <td className="datetime-cell">{formatDateTime(decision.decidedAt)}</td>
                        <td className="trigger-cell">{decision.trigger}</td>
                        <td className="action-cell">{decision.action}</td>
                        <td className="orderid-cell">
                          {decision.orderId ? <code>{decision.orderId}</code> : '—'}
                        </td>
                        <td className="status-cell">{decision.lifecycleStatus || '—'}</td>
                        <td className="price-cell">{formatPrice(decision.limitPrice)}</td>
                        <td className="source-cell">{decision.limitPriceSource || '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <p className="no-data">No orders submitted this session</p>
            )}
          </section>

          {/* Recent Decisions */}
          <section className="automation-section">
            <h2>Recent Decisions ({sortedRecentDecisions.length})</h2>
            {data.persistenceError ? (
              <div className="error-box">
                <strong>Persistence error:</strong> {data.persistenceError}
              </div>
            ) : sortedRecentDecisions.length > 0 ? (
              <div className="table-wrapper">
                <table className="decisions-table">
                  <thead>
                    <tr>
                      <th>Decided At</th>
                      <th>Trigger</th>
                      <th>Arm State</th>
                      <th>In Session</th>
                      <th>Signal State</th>
                      <th>Action</th>
                      <th>Reason</th>
                    </tr>
                  </thead>
                  <tbody>
                    {sortedRecentDecisions.map((decision) => (
                      <tr key={decision.decisionId}>
                        <td className="datetime-cell">{formatDateTime(decision.decidedAt)}</td>
                        <td className="trigger-cell">{decision.trigger}</td>
                        <td className="armstate-cell">
                          <span className={`arm-badge ${decision.armed ? 'armed' : 'disarmed'}`}>
                            {decision.armState}
                          </span>
                        </td>
                        <td className="session-cell">{decision.inSession ? 'Yes' : 'No'}</td>
                        <td className="signal-cell">{decision.signalState}</td>
                        <td className="action-cell">{decision.action}</td>
                        <td className="reason-cell">{decision.actionReason}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <p className="no-data">No decisions recorded</p>
            )}
          </section>
        </>
      )}
    </div>
  );
};

export default Automation;
