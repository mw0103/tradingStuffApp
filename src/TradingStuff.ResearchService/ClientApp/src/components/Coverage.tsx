import React, { useState, useEffect } from 'react';
import type { CoverageReport, CoverageStatus } from '../types/coverage';
import './Coverage.css';

interface LoadingState {
  isLoading: boolean;
  error: string | null;
}

interface ExpandedState {
  [nodeId: number]: boolean;
}

const Coverage: React.FC = () => {
  const [data, setData] = useState<CoverageReport | null>(null);
  const [state, setState] = useState<LoadingState>({ isLoading: true, error: null });
  const [autoRefresh, setAutoRefresh] = useState(true);
  const [expandedNodes, setExpandedNodes] = useState<ExpandedState>({});
  const REFRESH_INTERVAL = 30000; // 30 seconds

  const fetchCoverage = async () => {
    setState({ isLoading: true, error: null });
    try {
      const response = await fetch('/research/coverage');
      if (!response.ok) {
        if (response.status === 503) {
          throw new Error('Research service is unavailable. Is the database running?');
        }
        throw new Error(`Failed to fetch coverage: ${response.statusText}`);
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

  useEffect(() => {
    fetchCoverage();
  }, []);

  useEffect(() => {
    if (!autoRefresh) return;

    const interval = setInterval(() => {
      fetchCoverage();
    }, REFRESH_INTERVAL);

    return () => clearInterval(interval);
  }, [autoRefresh]);

  // The same rule the backfill table follows: green has to mean "we hold this data", not "the
  // number is high". Two things leave a high ratio unbacked, and neither is visible in the ratio
  // itself. A basis other than 'measured' means the denominator cannot be believed — and since a
  // missing session row SHRINKS the denominator, an out-of-sync session table reads as near-perfect
  // rather than as broken. An open gap means minutes are being lost right now, and a trailing
  // window absorbs a fresh outage almost without moving, so recording can be down at 99.9%.
  // Gap scopes are lease- and writer-keyed, not node-keyed, so an open gap cannot be attributed to
  // individual rows; it is withheld from every row, because none of them can be called healthy
  // while the recorder that feeds them all is down.
  const openGaps = data?.gaps.filter((gap) => !gap.endedAt) ?? [];
  const coverageIsBacked =
    data !== null && data.basis.status === 'measured' && openGaps.length === 0;

  const getCoverageColor = (ratio: number | null): string => {
    if (ratio === null) return 'warning';
    if (ratio >= 0.95) return coverageIsBacked ? 'good' : 'warning';
    if (ratio >= 0.8) return 'warning';
    return 'bad';
  };

  // A window with no believable denominator gets a sentence, not a percentage. Showing 0% for a
  // weekend or 100% for an unsynced session table is how a gate stops being read.
  const statusExplanation: Record<CoverageStatus, string> = {
    'measured': '',
    'not-configured': 'No database is configured, so nothing was measured.',
    'no-session-in-window': 'No exchange session overlaps this window — nothing was expected to record.',
    'sessions-out-of-sync': 'research.sessions disagrees with the session generator; the denominator cannot be trusted.',
    'window-rejected': 'The requested window was empty, inverted, or too long to measure.',
    'calendar-unknown': 'A configured calendar key is not in the shipped calendar dataset.',
  };

  /** Why a ratio at or above the threshold is not being shown as healthy. */
  const describeWithheldGood = (ratio: number | null): string | undefined => {
    if (data === null || ratio === null || ratio < 0.95 || coverageIsBacked) return undefined;
    if (data.basis.status !== 'measured') {
      return `Above the threshold, but not measurable: ${statusExplanation[data.basis.status]}`;
    }

    return (
      `Above the threshold, but ${openGaps.length} recording gap(s) are still open — this window ` +
      'is still losing minutes, and the ratio lags the outage.'
    );
  };

  const formatRatio = (ratio: number | null): string => {
    if (ratio === null) return 'Not measured';
    return (ratio * 100).toFixed(2);
  };

  const formatDuration = (minutes: number): string => {
    const hours = Math.floor(minutes / 60);
    const mins = minutes % 60;
    if (hours === 0) return `${mins}m`;
    if (mins === 0) return `${hours}h`;
    return `${hours}h ${mins}m`;
  };

  const formatDateTime = (isoString: string): string => {
    try {
      return new Date(isoString).toLocaleString();
    } catch {
      return isoString;
    }
  };

  const toggleNodeExpanded = (nodeId: number) => {
    setExpandedNodes((prev) => ({
      ...prev,
      [nodeId]: !prev[nodeId],
    }));
  };

  const withheldOverall = describeWithheldGood(data?.overallCoverageRatio ?? null);

  const sortedNodes = data?.perNode
    ? [...data.perNode].sort((a, b) => {
        // Sort by coverage ratio, putting null values (unmeasured) at the end
        const aRatio = a.coverageRatio ?? -1;
        const bRatio = b.coverageRatio ?? -1;
        if (aRatio === -1 && bRatio === -1) return 0;
        if (aRatio === -1) return 1;
        if (bRatio === -1) return -1;
        return aRatio - bRatio;
      })
    : [];

  const sortedUnassigned = data?.unassignedConIds
    ? [...data.unassignedConIds].sort((a, b) => a.coverageRatio - b.coverageRatio)
    : [];

  const sortedGaps = data?.gaps
    ? [...data.gaps].sort((a, b) => new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime())
    : [];

  return (
    <div className="coverage-container">
      <header className="coverage-header">
        <h1>Data Coverage Report</h1>
        <div className="header-controls">
          <button className="refresh-button" onClick={fetchCoverage} disabled={state.isLoading}>
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
          <p>Loading coverage report...</p>
        </div>
      )}

      {data && (
        <>
          {/* Overall Coverage */}
          <section className="coverage-section">
            <h2>Overall Coverage</h2>
            <div className={`coverage-summary ${getCoverageColor(data.overallCoverageRatio)}`}>
              <div className="coverage-value">
                {data.overallCoverageRatio === null ? 'Not measured' : `${formatRatio(data.overallCoverageRatio)}%`}
              </div>
              <div className="coverage-meta">
                {data.totalMinutes} expected session minutes
                {data.basis.sessions.length > 0 && ` across ${data.basis.sessions.length} session(s)`}
              </div>
              {data.overallCoverageRatio === null && (
                <div className="coverage-flag">
                  {statusExplanation[data.basis.status]}
                  {data.basis.detail && ` (${data.basis.detail})`}
                </div>
              )}
              {data.overallCoverageRatio !== null && data.overallCoverageRatio < 0.95 && (
                <div className="coverage-flag">Below 95% acceptance threshold</div>
              )}
              {withheldOverall && <div className="coverage-flag">{withheldOverall}</div>}
            </div>
          </section>

          {/* Time Range */}
          <section className="coverage-section">
            <h2>Report Period</h2>
            <div className="time-range">
              <div><strong>From:</strong> {formatDateTime(data.from)}</div>
              <div><strong>To:</strong> {formatDateTime(data.to)}</div>
              <div><strong>Sessions:</strong> {data.basis.calendars.join(', ')}</div>
            </div>
          </section>

          {/* Nodes Coverage Table */}
          {sortedNodes.length > 0 && (
            <section className="coverage-section">
              <h2>Option Nodes ({sortedNodes.length})</h2>
              <div className="table-wrapper">
                <table className="coverage-table">
                  <thead>
                    <tr>
                      <th style={{ width: '40px' }}></th>
                      <th>Node</th>
                      <th>Role</th>
                      <th>Minutes with Data</th>
                      <th>Total Minutes</th>
                      <th>Coverage</th>
                    </tr>
                  </thead>
                  <tbody>
                    {sortedNodes.map((node) => (
                      <React.Fragment key={node.nodeId}>
                        <tr
                          className={getCoverageColor(node.coverageRatio)}
                          title={describeWithheldGood(node.coverageRatio)}
                        >
                          <td className="expand-cell">
                            {node.conIdSegments.length > 0 && (
                              <button
                                className="expand-button"
                                onClick={() => toggleNodeExpanded(node.nodeId)}
                                title={expandedNodes[node.nodeId] ? 'Collapse' : 'Expand'}
                              >
                                {expandedNodes[node.nodeId] ? '▼' : '▶'}
                              </button>
                            )}
                          </td>
                          <td className="conid-cell">{node.nodeId}</td>
                          <td className="role-cell">{node.role}</td>
                          <td className="number-cell">{node.minutesWithData}</td>
                          <td className="number-cell">{node.totalMinutes}</td>
                          <td className="coverage-cell">
                            {node.coverageRatio !== null ? (
                              <>
                                <div className="coverage-bar">
                                  <div
                                    className="coverage-fill"
                                    style={{ width: `${node.coverageRatio * 100}%` }}
                                  ></div>
                                </div>
                                <span className="coverage-percent">{formatRatio(node.coverageRatio)}%</span>
                              </>
                            ) : (
                              <span className="coverage-not-measured">Not measured</span>
                            )}
                          </td>
                        </tr>
                        {expandedNodes[node.nodeId] && node.conIdSegments.length > 0 && (
                          <>
                            {node.conIdSegments.map((segment) => (
                              // Keyed on assignedFrom, not conId: a node that rotates off a conId
                              // and back onto it inside the window has two segments for that conId,
                              // and the partial unique index makes assignedFrom the unique one.
                              <tr key={`segment-${node.nodeId}-${segment.assignedFrom}`} className="segment-row">
                                <td></td>
                                <td></td>
                                <td className="segment-conid-cell">
                                  <span className="segment-label">ConId {segment.conId}</span>
                                </td>
                                <td className="number-cell">{segment.minutesWithData}</td>
                                <td className="number-cell">{segment.totalMinutes}</td>
                                <td className="coverage-cell">
                                  {segment.coverageRatio !== null ? (
                                    <>
                                      <div className="coverage-bar segment-bar">
                                        <div
                                          className="coverage-fill"
                                          style={{ width: `${segment.coverageRatio * 100}%` }}
                                        ></div>
                                      </div>
                                      <span className="coverage-percent segment-percent">
                                        {formatRatio(segment.coverageRatio)}%
                                      </span>
                                    </>
                                  ) : (
                                    <span className="coverage-not-measured">Not measured</span>
                                  )}
                                </td>
                              </tr>
                            ))}
                          </>
                        )}
                      </React.Fragment>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}

          {/* Unassigned ConIds Table */}
          {sortedUnassigned.length > 0 && (
            <section className="coverage-section">
              <h2>Core Underlyings ({sortedUnassigned.length})</h2>
              <p className="section-note">
                These are core underlyings (SPX, VIX, SPY) measured across the entire reporting window,
                not assigned to a specific node.
              </p>
              <div className="table-wrapper">
                <table className="coverage-table">
                  <thead>
                    <tr>
                      <th>ConId</th>
                      <th>Minutes with Data</th>
                      <th>Total Minutes</th>
                      <th>Coverage</th>
                    </tr>
                  </thead>
                  <tbody>
                    {sortedUnassigned.map((conId) => (
                      <tr
                        key={conId.conId}
                        className={getCoverageColor(conId.coverageRatio)}
                        title={describeWithheldGood(conId.coverageRatio)}
                      >
                        <td className="conid-cell">{conId.conId}</td>
                        <td className="number-cell">{conId.minutesWithData}</td>
                        <td className="number-cell">{conId.totalMinutes}</td>
                        <td className="coverage-cell">
                          <div className="coverage-bar">
                            <div
                              className="coverage-fill"
                              style={{ width: `${conId.coverageRatio * 100}%` }}
                            ></div>
                          </div>
                          <span className="coverage-percent">{formatRatio(conId.coverageRatio)}%</span>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}

          {/* Gaps Table */}
          {sortedGaps.length > 0 && (
            <section className="coverage-section">
              <h2>Data Gaps ({sortedGaps.length})</h2>
              <div className="table-wrapper">
                <table className="gaps-table">
                  <thead>
                    <tr>
                      <th>Scope</th>
                      <th>Reason</th>
                      <th>Started</th>
                      <th>Ended / Duration</th>
                    </tr>
                  </thead>
                  <tbody>
                    {sortedGaps.map((gap) => {
                      const duration =
                        gap.endedAt
                          ? new Date(gap.endedAt).getTime() - new Date(gap.startedAt).getTime()
                          : new Date().getTime() - new Date(gap.startedAt).getTime();
                      const durationMinutes = Math.floor(duration / 60000);
                      const isOpen = !gap.endedAt;

                      return (
                        <tr key={gap.gapId} className={isOpen ? 'open-gap' : ''}>
                          <td className="scope-cell">
                            <code>{gap.scope}</code>
                          </td>
                          <td className="reason-cell">{gap.reason}</td>
                          <td className="datetime-cell">{formatDateTime(gap.startedAt)}</td>
                          <td className="datetime-cell">
                            {isOpen ? (
                              <strong>Still open ({formatDuration(durationMinutes)})</strong>
                            ) : (
                              <>
                                {formatDateTime(gap.endedAt ?? '')}
                                <br />
                                <small>({formatDuration(durationMinutes)})</small>
                              </>
                            )}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </section>
          )}

          {sortedGaps.length === 0 && (
            <section className="coverage-section">
              <h2>Data Gaps</h2>
              <p className="no-data">No gaps detected in this period</p>
            </section>
          )}
        </>
      )}
    </div>
  );
};

export default Coverage;
