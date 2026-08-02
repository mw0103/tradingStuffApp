import React, { useState, useEffect } from 'react';
import type { OptionChainStatusReport, OptionChainJobStatus, CapabilityProbeRunResponse } from '../types/optionChains';
import './OptionChains.css';

interface LoadingState {
  isLoading: boolean;
  error: string | null;
}

const OptionChains: React.FC = () => {
  const [data, setData] = useState<OptionChainStatusReport | null>(null);
  const [state, setState] = useState<LoadingState>({ isLoading: true, error: null });
  const [autoRefresh, setAutoRefresh] = useState(true);
  const [probeResult, setProbeResult] = useState<CapabilityProbeRunResponse | null>(null);
  const [probeRunning, setProbeRunning] = useState(false);
  const [probeError, setProbeError] = useState<string | null>(null);

  const [form, setForm] = useState({
    name: '',
    underlying: 'SPX',
    tradingClass: 'SPXW',
    from: '',
    to: '',
    interval: '1m',
    confirmTick: false,
  });
  const [createError, setCreateError] = useState<string | null>(null);
  const REFRESH_INTERVAL = 30000;

  const fetchStatus = async () => {
    setState({ isLoading: true, error: null });
    try {
      const response = await fetch('/research/options/status');
      if (!response.ok) {
        if (response.status === 503) {
          throw new Error('Research service is unavailable. Is the database running?');
        }
        throw new Error(`Failed to fetch option-chain status: ${response.statusText}`);
      }
      setData(await response.json());
      setState({ isLoading: false, error: null });
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown error occurred';
      setState({ isLoading: false, error: message });
      setData(null);
    }
  };

  useEffect(() => {
    fetchStatus();
  }, []);

  useEffect(() => {
    if (!autoRefresh) return;
    const interval = setInterval(fetchStatus, REFRESH_INTERVAL);
    return () => clearInterval(interval);
  }, [autoRefresh]);

  const runProbes = async () => {
    setProbeRunning(true);
    setProbeError(null);
    try {
      const response = await fetch('/research/options/probes/run', { method: 'POST' });
      if (!response.ok) {
        throw new Error(`Probe run failed: ${response.statusText}`);
      }
      setProbeResult(await response.json());
    } catch (err) {
      setProbeError(err instanceof Error ? err.message : 'Unknown error occurred');
    } finally {
      setProbeRunning(false);
    }
  };

  const createJob = async (e: React.FormEvent) => {
    e.preventDefault();
    setCreateError(null);

    const params = new URLSearchParams({
      name: form.name,
      underlying: form.underlying,
      tradingClass: form.tradingClass,
      from: form.from,
      to: form.to,
      interval: form.interval,
      confirmTick: String(form.confirmTick),
    });

    try {
      const response = await fetch(`/research/options/jobs?${params.toString()}`, { method: 'POST' });
      if (!response.ok) {
        const problem = await response.json().catch(() => null);
        throw new Error(problem?.detail ?? `Failed to create job: ${response.statusText}`);
      }
      await fetchStatus();
    } catch (err) {
      setCreateError(err instanceof Error ? err.message : 'Unknown error occurred');
    }
  };

  const getProgressColor = (job: OptionChainJobStatus): string => {
    if (job.status === 'paused') return 'waiting';
    if (job.totalRequests === 0) return 'waiting';
    if (job.percentComplete >= 0.95) return job.exhaustedCount > 0 ? 'warning' : 'good';
    if (job.percentComplete >= 0.5) return 'warning';
    return 'bad';
  };

  const formatPercent = (percent: number): string => (percent * 100).toFixed(1);

  const sortedJobs = data?.jobs ? [...data.jobs].sort((a, b) => b.priority - a.priority || a.jobId - b.jobId) : [];

  return (
    <div className="option-chains-container">
      <header className="option-chains-header">
        <h1>Option Chain Ingestion</h1>
        <div className="header-controls">
          <button className="refresh-button" onClick={fetchStatus} disabled={state.isLoading}>
            {state.isLoading ? 'Refreshing...' : 'Refresh'}
          </button>
          <label className="auto-refresh">
            <input type="checkbox" checked={autoRefresh} onChange={(e) => setAutoRefresh(e.target.checked)} />
            Auto-refresh ({REFRESH_INTERVAL / 1000}s)
          </label>
        </div>
      </header>

      {state.error && (
        <div className="error-box">
          <strong>Error:</strong> {state.error}
        </div>
      )}

      {data && (
        <section className="option-chains-section">
          <h2>Coordinator</h2>
          <div className={`coordinator-status ${data.enabled ? 'enabled' : 'disabled'}`}>
            <div className="status-label">{data.enabled ? '✓ Enabled' : '✗ Disabled'}</div>
            <div className="status-detail">
              Owner: <code>{data.ownerId}</code> | Max Attempts: {data.maxAttempts}
            </div>
          </div>
        </section>
      )}

      <section className="option-chains-section">
        <h2>New ingestion job</h2>
        <form className="job-form" onSubmit={createJob}>
          <input
            placeholder="name (e.g. spxw-2016-h1)"
            value={form.name}
            onChange={(e) => setForm({ ...form, name: e.target.value })}
            required
          />
          <input
            placeholder="underlying (SPX, VIX)"
            value={form.underlying}
            onChange={(e) => setForm({ ...form, underlying: e.target.value })}
            required
          />
          <input
            placeholder="trading class (SPXW, SPX, VIX)"
            value={form.tradingClass}
            onChange={(e) => setForm({ ...form, tradingClass: e.target.value })}
            required
          />
          <input type="date" value={form.from} onChange={(e) => setForm({ ...form, from: e.target.value })} required />
          <input type="date" value={form.to} onChange={(e) => setForm({ ...form, to: e.target.value })} required />
          <select value={form.interval} onChange={(e) => setForm({ ...form, interval: e.target.value })}>
            <option value="1m">1m (default)</option>
            <option value="tick">tick — study-scoped, never auto-drained</option>
          </select>
          {form.interval === 'tick' && (
            <label className="confirm-tick">
              <input
                type="checkbox"
                checked={form.confirmTick}
                onChange={(e) => setForm({ ...form, confirmTick: e.target.checked })}
              />
              I understand bulk tick ingestion is out of scope for the automatic coordinator
            </label>
          )}
          <button type="submit">Create job</button>
        </form>
        {createError && (
          <div className="error-box">
            <strong>Could not create job:</strong> {createError}
          </div>
        )}
      </section>

      <section className="option-chains-section">
        <h2>Jobs ({sortedJobs.length})</h2>
        {sortedJobs.length > 0 ? (
          <div className="table-wrapper">
            <table className="option-chains-table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Underlying</th>
                  <th>Class</th>
                  <th>Interval</th>
                  <th>Range</th>
                  <th>Status</th>
                  <th>Expirations</th>
                  <th>Complete</th>
                  <th title="Rows actually persisted to research.option_chain_quotes.">Quotes landed</th>
                </tr>
              </thead>
              <tbody>
                {sortedJobs.map((job) => (
                  <tr key={job.jobId} className={getProgressColor(job)}>
                    <td>
                      <code>{job.name}</code>
                    </td>
                    <td>{job.underlying}</td>
                    <td>{job.tradingClass}</td>
                    <td>{job.interval}</td>
                    <td>
                      {job.targetFrom} .. {job.targetTo}
                    </td>
                    <td className={`status-cell status-${job.status.replace(/_/g, '-')}`}>
                      {job.status.replace(/_/g, ' ')}
                      {job.status === 'paused' && job.interval === 'tick' && (
                        <span title="Tick jobs are created paused and never planned or claimed automatically."> (tick, never auto-drained)</span>
                      )}
                    </td>
                    <td>{job.totalRequests}</td>
                    <td>
                      {job.status === 'paused' ? '—' : `${formatPercent(job.percentComplete)}%`}
                    </td>
                    <td title={`${job.quotesReturned.toLocaleString()} returned pre-dedup`}>
                      {job.quotesLanded.toLocaleString()}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <p className="no-data">No option-chain ingestion jobs yet.</p>
        )}
      </section>

      <section className="option-chains-section">
        <h2>ThetaData capability probes</h2>
        <p className="probe-note">
          Re-runs the capability probes against the live Theta Terminal (expirations coverage, quote
          schema, index/stock subscription gating, and the as-of survivorship check) and persists
          every finding to <code>research.capability_probes</code>.
        </p>
        <button onClick={runProbes} disabled={probeRunning}>
          {probeRunning ? 'Running probes...' : 'Run probes now'}
        </button>
        {probeError && (
          <div className="error-box">
            <strong>Probe run failed:</strong> {probeError}
          </div>
        )}
        {probeResult && (
          <div className="table-wrapper">
            <table className="option-chains-table">
              <thead>
                <tr>
                  <th>Probe</th>
                  <th>Result</th>
                  <th>Detail</th>
                </tr>
              </thead>
              <tbody>
                {Object.entries(probeResult).map(([key, value]) => (
                  <tr key={key} className={value.succeeded ? 'good' : 'bad'}>
                    <td>
                      <code>{key}</code>
                    </td>
                    <td>{value.succeeded ? '✓' : '✗'}</td>
                    <td className="probe-detail">{value.detail}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
};

export default OptionChains;
