import React, { useState, useEffect } from 'react';
import type { BackfillStatusReport } from '../types/backfill';
import './Backfill.css';

interface LoadingState {
  isLoading: boolean;
  error: string | null;
}

const Backfill: React.FC = () => {
  const [data, setData] = useState<BackfillStatusReport | null>(null);
  const [state, setState] = useState<LoadingState>({ isLoading: true, error: null });
  const [autoRefresh, setAutoRefresh] = useState(true);
  const REFRESH_INTERVAL = 30000; // 30 seconds

  const fetchBackfill = async () => {
    setState({ isLoading: true, error: null });
    try {
      const response = await fetch('/research/backfill');
      if (!response.ok) {
        if (response.status === 503) {
          throw new Error('Research service is unavailable. Is the database running?');
        }
        throw new Error(`Failed to fetch backfill progress: ${response.statusText}`);
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
    fetchBackfill();
  }, []);

  useEffect(() => {
    if (!autoRefresh) return;

    const interval = setInterval(() => {
      fetchBackfill();
    }, REFRESH_INTERVAL);

    return () => clearInterval(interval);
  }, [autoRefresh]);

  const getProgressColor = (percentComplete: number, totalSlices: number): string => {
    // Zero slices: waiting/disabled state, not an error, not success
    if (totalSlices === 0) return 'waiting';
    if (percentComplete >= 0.95) return 'good';
    if (percentComplete >= 0.5) return 'warning';
    return 'bad';
  };

  const formatPercent = (percent: number): string => {
    return (percent * 100).toFixed(1);
  };

  const formatDuration = (isoString: string | null): string => {
    if (!isoString) return '—';
    try {
      const date = new Date(isoString);
      const now = new Date();
      const diffMs = now.getTime() - date.getTime();
      const diffSecs = Math.floor(diffMs / 1000);

      if (diffSecs < 60) return `${diffSecs}s ago`;
      const diffMins = Math.floor(diffSecs / 60);
      if (diffMins < 60) return `${diffMins}m ago`;
      const diffHours = Math.floor(diffMins / 60);
      if (diffHours < 24) return `${diffHours}h ago`;
      const diffDays = Math.floor(diffHours / 24);
      return `${diffDays}d ago`;
    } catch {
      return isoString;
    }
  };

  const sortedJobs = data?.jobs
    ? [...data.jobs].sort((a, b) => b.priority - a.priority || a.jobId - b.jobId)
    : [];

  return (
    <div className="backfill-container">
      <header className="backfill-header">
        <h1>Backfill Progress</h1>
        <div className="header-controls">
          <button className="refresh-button" onClick={fetchBackfill} disabled={state.isLoading}>
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
          <p>Loading backfill progress...</p>
        </div>
      )}

      {data && (
        <>
          {/* Coordinator Status */}
          <section className="backfill-section">
            <h2>Coordinator</h2>
            <div className={`coordinator-status ${data.enabled ? 'enabled' : 'disabled'}`}>
              <div className="status-label">
                {data.enabled ? '✓ Enabled' : '✗ Disabled'}
              </div>
              <div className="status-detail">
                Owner: <code>{data.ownerId}</code> | Max Attempts: {data.maxAttempts}
              </div>
            </div>
          </section>

          {/* Jobs Table */}
          {sortedJobs.length > 0 ? (
            <section className="backfill-section">
              <h2>Jobs ({sortedJobs.length})</h2>
              <div className="table-wrapper">
                <table className="backfill-table">
                  <thead>
                    <tr>
                      <th>Name</th>
                      <th>Kind</th>
                      <th>Status</th>
                      <th>Total Slices</th>
                      <th>Progress by State</th>
                      <th>Complete</th>
                      <th>Bars</th>
                      <th>Last Update</th>
                    </tr>
                  </thead>
                  <tbody>
                    {sortedJobs.map((job) => {
                      const progressColor = getProgressColor(job.percentComplete, job.totalSlices);
                      const isDisabled = !data.enabled;
                      const isZeroSlices = job.totalSlices === 0;

                      return (
                        <tr
                          key={job.jobId}
                          className={`${progressColor} ${isDisabled ? 'disabled-coordinator' : ''}`}
                        >
                          <td className="name-cell">
                            <code>{job.name}</code>
                          </td>
                          <td className="kind-cell">{job.kind}</td>
                          <td className="status-cell">{job.status}</td>
                          <td className="slices-cell">
                            <span className="slice-count">{job.totalSlices}</span>
                            {isZeroSlices && <span className="zero-slices-note">waiting for plan</span>}
                          </td>
                          <td className="breakdown-cell">
                            <div className="state-breakdown">
                              {job.succeededCount > 0 && (
                                <span className="state succeeded" title="Succeeded">
                                  {job.succeededCount}✓
                                </span>
                              )}
                              {job.pendingCount > 0 && (
                                <span className="state pending" title="Pending">
                                  {job.pendingCount}◯
                                </span>
                              )}
                              {job.inflightCount > 0 && (
                                <span className="state inflight" title="Inflight">
                                  {job.inflightCount}→
                                </span>
                              )}
                              {job.emptyCount > 0 && (
                                <span className="state empty" title="Empty">
                                  {job.emptyCount}∅
                                </span>
                              )}
                              {job.retryableCount > 0 && (
                                <span className="state retryable" title="Retryable">
                                  {job.retryableCount}↻
                                </span>
                              )}
                              {job.exhaustedCount > 0 && (
                                <span className="state exhausted" title="Exhausted">
                                  {job.exhaustedCount}✗
                                </span>
                              )}
                              {job.permanentCount > 0 && (
                                <span className="state permanent" title="Permanent error">
                                  {job.permanentCount}✘
                                </span>
                              )}
                              {job.nowAnchoredCount > 0 && (
                                <span className="state now-anchored" title="Now-anchored (end_time_utc IS NULL)">
                                  {job.nowAnchoredCount}⧖
                                </span>
                              )}
                            </div>
                          </td>
                          <td className="progress-cell">
                            {isZeroSlices ? (
                              <div className="progress-bar zero-slices">
                                <div className="progress-label">0%</div>
                              </div>
                            ) : (
                              <>
                                <div className="progress-bar">
                                  <div
                                    className="progress-fill"
                                    style={{ width: `${Math.max(1, job.percentComplete * 100)}%` }}
                                  ></div>
                                </div>
                                <span className="progress-percent">
                                  {formatPercent(job.percentComplete)}%
                                </span>
                              </>
                            )}
                          </td>
                          <td className="bars-cell">
                            {job.barsLanded.toLocaleString()}
                          </td>
                          <td className="timestamp-cell">
                            {job.earliestLeaseExpiry ? (
                              <span className="inflight-indicator" title="Lease expires">
                                {formatDuration(job.earliestLeaseExpiry)}
                              </span>
                            ) : job.highWaterMarkUtc ? (
                              formatDuration(job.highWaterMarkUtc)
                            ) : (
                              '—'
                            )}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </section>
          ) : (
            <section className="backfill-section">
              <h2>Jobs</h2>
              <p className="no-data">No backfill jobs defined</p>
            </section>
          )}
        </>
      )}
    </div>
  );
};

export default Backfill;
