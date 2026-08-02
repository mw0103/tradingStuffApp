import { useState, useEffect, type ReactNode } from 'react';
import type { VolatilityResidualStudyRun } from '../types/study';
import './Study.css';

interface LoadingState {
  isLoading: boolean;
  error: string | null;
  isRunning: boolean;
}

const Study = () => {
  const [data, setData] = useState<VolatilityResidualStudyRun | null>(null);
  const [state, setState] = useState<LoadingState>({
    isLoading: true,
    error: null,
    isRunning: false,
  });

  const fetchLatestRun = async () => {
    setState((prev) => ({ ...prev, isLoading: true, error: null }));
    try {
      const response = await fetch('/research/studies/vol-residual/latest');
      if (!response.ok) {
        if (response.status === 503) {
          throw new Error('Research service is unavailable. Is the database running?');
        }
        if (response.status === 404) {
          // No run exists yet, which is fine
          setState({ isLoading: false, error: null, isRunning: false });
          setData(null);
          return;
        }
        throw new Error(`Failed to fetch latest study run: ${response.statusText}`);
      }
      const jsonData = await response.json();
      setData(jsonData);
      setState({ isLoading: false, error: null, isRunning: false });
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown error occurred';
      setState({ isLoading: false, error: message, isRunning: false });
      setData(null);
    }
  };

  const triggerRun = async () => {
    setState((prev) => ({ ...prev, isRunning: true, error: null }));
    try {
      const response = await fetch('/research/studies/vol-residual/run', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
      });

      if (!response.ok) {
        if (response.status === 400) {
          const body = await response.text();
          throw new Error(`Invalid request: ${body}`);
        }
        if (response.status === 503) {
          throw new Error('Research service is unavailable.');
        }
        throw new Error(`Failed to trigger study run: ${response.statusText}`);
      }

      // Poll for the result after a short delay, then fetch latest
      setTimeout(() => {
        fetchLatestRun();
      }, 2000);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown error occurred';
      setState({ isLoading: false, error: message, isRunning: false });
    }
  };

  useEffect(() => {
    fetchLatestRun();
  }, []);

  const formatDateTime = (isoString: string): string => {
    try {
      return new Date(isoString).toLocaleString();
    } catch {
      return isoString;
    }
  };

  const formatNumber = (num: number, decimals: number = 4): string => {
    return num.toFixed(decimals);
  };

  // improvementVsGatePct is ALREADY a percentage from the API (3.02 means 3.02%). This used to
  // multiply by 100 and render "302.14%" for a 3.02% improvement — a display bug that overstated
  // every result by two orders of magnitude while the underlying JSON was correct.
  const formatPercent = (percent: number): string => {
    return percent.toFixed(2);
  };

  // Session realized VARIANCE is ~1e-5, so four decimal places renders every level as "0.0000".
  // Shown as annualised volatility in % instead: sqrt(variance * 252) * 100. That is the unit the
  // numbers are actually discussed in ("VIX was 18"), and it makes the columns readable without
  // changing anything the models were scored on — QLIKE is still computed on variance server-side.
  const formatVolPct = (variance: number): string => {
    if (!Number.isFinite(variance) || variance < 0) return '—';
    return (Math.sqrt(variance * 252) * 100).toFixed(2);
  };

  // Chart: Actual vs Forecast
  const renderActualVsForecastChart = (): ReactNode => {
    if (!data || data.daily.length === 0) return null;

    const dailyData = data.daily;
    const width = 800;
    const height = 300;
    const margin = { top: 20, right: 20, bottom: 40, left: 60 };
    const plotWidth = width - margin.left - margin.right;
    const plotHeight = height - margin.top - margin.bottom;

    // Find min/max for scaling
    let minVal = Infinity;
    let maxVal = -Infinity;

    dailyData.forEach((day) => {
      minVal = Math.min(minVal, day.actualRv);
      maxVal = Math.max(
        maxVal,
        day.actualRv,
        day.forecasts.HAR,
        day.forecasts.VIX,
        day.forecasts.HARX,
        day.forecasts.CORRECTED
      );
    });

    const padding = (maxVal - minVal) * 0.1;
    minVal -= padding;
    maxVal += padding;

    const scaleX = plotWidth / (dailyData.length - 1 || 1);
    const scaleY = plotHeight / (maxVal - minVal || 1);

    const points = (key: keyof typeof dailyData[0]['forecasts']): string => {
      return dailyData
        .map((day, i) => {
          const x = margin.left + i * scaleX;
          const y = margin.top + plotHeight - (day.forecasts[key] - minVal) * scaleY;
          return `${x},${y}`;
        })
        .join(' ');
    };

    const actualPoints = dailyData
      .map((day, i) => {
        const x = margin.left + i * scaleX;
        const y = margin.top + plotHeight - (day.actualRv - minVal) * scaleY;
        return `${x},${y}`;
      })
      .join(' ');

    const yTicks = 5;
    const yTickValues = Array.from({ length: yTicks }, (_, i) => {
      const ratio = i / (yTicks - 1);
      return minVal + ratio * (maxVal - minVal);
    });

    return (
      <svg width="100%" height={height} viewBox={`0 0 ${width} ${height}`} className="chart">
        {/* Grid lines */}
        {yTickValues.map((_val, i) => (
          <line
            key={`grid-${i}`}
            x1={margin.left}
            y1={margin.top + ((i * plotHeight) / (yTicks - 1))}
            x2={width - margin.right}
            y2={margin.top + ((i * plotHeight) / (yTicks - 1))}
            stroke="currentColor"
            strokeOpacity="0.1"
            strokeDasharray="2,2"
          />
        ))}

        {/* Y-axis */}
        <line x1={margin.left} y1={margin.top} x2={margin.left} y2={height - margin.bottom} stroke="currentColor" />

        {/* X-axis */}
        <line
          x1={margin.left}
          y1={height - margin.bottom}
          x2={width - margin.right}
          y2={height - margin.bottom}
          stroke="currentColor"
        />

        {/* Y-axis labels */}
        {yTickValues.map((val, i) => (
          <g key={`y-label-${i}`}>
            <line
              x1={margin.left - 5}
              y1={margin.top + ((i * plotHeight) / (yTicks - 1))}
              x2={margin.left}
              y2={margin.top + ((i * plotHeight) / (yTicks - 1))}
              stroke="currentColor"
            />
            <text
              x={margin.left - 10}
              y={margin.top + ((i * plotHeight) / (yTicks - 1)) + 4}
              textAnchor="end"
              fontSize="11"
              fill="currentColor"
            >
              {formatVolPct(val)}
            </text>
          </g>
        ))}

        {/* Series */}
        <polyline points={actualPoints} fill="none" stroke="#1976d2" strokeWidth="2" />
        <polyline points={points('HAR')} fill="none" stroke="#d32f2f" strokeWidth="1.5" strokeOpacity="0.7" />
        <polyline points={points('VIX')} fill="none" stroke="#f57c00" strokeWidth="1.5" strokeOpacity="0.7" />
        <polyline points={points('HARX')} fill="none" stroke="#2e7d32" strokeWidth="1.5" strokeOpacity="0.7" />
        <polyline points={points('CORRECTED')} fill="none" stroke="#7b1fa2" strokeWidth="1.5" strokeOpacity="0.7" />

        {/* Legend */}
        <g className="legend">
          <rect x={width - 180} y={10} width="170" height="100" fill="white" stroke="currentColor" opacity="0.9" />
          <line x1={width - 170} y1={25} x2={width - 150} y2={25} stroke="#1976d2" strokeWidth="2" />
          <text x={width - 145} y={30} fontSize="12" fill="currentColor" fontWeight="500">
            Actual RV
          </text>
          <line x1={width - 170} y1={45} x2={width - 150} y2={45} stroke="#d32f2f" strokeWidth="1.5" />
          <text x={width - 145} y={50} fontSize="12" fill="currentColor">
            HAR
          </text>
          <line x1={width - 170} y1={65} x2={width - 150} y2={65} stroke="#f57c00" strokeWidth="1.5" />
          <text x={width - 145} y={70} fontSize="12" fill="currentColor">
            VIX
          </text>
          <line x1={width - 170} y1={85} x2={width - 150} y2={85} stroke="#2e7d32" strokeWidth="1.5" />
          <text x={width - 145} y={90} fontSize="12" fill="currentColor">
            HARX
          </text>
          <line x1={width - 170} y1={105} x2={width - 150} y2={105} stroke="#7b1fa2" strokeWidth="1.5" />
          <text x={width - 145} y={110} fontSize="12" fill="currentColor">
            CORRECTED
          </text>
        </g>
      </svg>
    );
  };

  // Chart: Cumulative QLIKE Differential vs Gate
  const renderCumulativeQlikeChart = (): ReactNode => {
    if (!data || data.daily.length === 0) return null;

    const dailyData = data.daily;
    const width = 800;
    const height = 300;
    const margin = { top: 20, right: 20, bottom: 40, left: 60 };
    const plotWidth = width - margin.left - margin.right;
    const plotHeight = height - margin.top - margin.bottom;

    // Find min/max
    let minVal = 0;
    let maxVal = 0;

    dailyData.forEach((day) => {
      minVal = Math.min(minVal, day.cumulativeQlikeDiffVsGate);
      maxVal = Math.max(maxVal, day.cumulativeQlikeDiffVsGate);
    });

    const padding = Math.max(Math.abs(minVal), Math.abs(maxVal)) * 0.1 || 1;
    minVal -= padding;
    maxVal += padding;

    const scaleX = plotWidth / (dailyData.length - 1 || 1);
    const scaleY = plotHeight / (maxVal - minVal || 1);

    const points = dailyData
      .map((day, i) => {
        const x = margin.left + i * scaleX;
        const y = margin.top + plotHeight - (day.cumulativeQlikeDiffVsGate - minVal) * scaleY;
        return `${x},${y}`;
      })
      .join(' ');

    // Zero line
    const zeroY = margin.top + plotHeight - (0 - minVal) * scaleY;

    const yTicks = 5;
    const yTickValues = Array.from({ length: yTicks }, (_, i) => {
      const ratio = i / (yTicks - 1);
      return minVal + ratio * (maxVal - minVal);
    });

    return (
      <svg width="100%" height={height} viewBox={`0 0 ${width} ${height}`} className="chart">
        {/* Grid lines */}
        {yTickValues.map((_val, i) => (
          <line
            key={`grid-${i}`}
            x1={margin.left}
            y1={margin.top + ((i * plotHeight) / (yTicks - 1))}
            x2={width - margin.right}
            y2={margin.top + ((i * plotHeight) / (yTicks - 1))}
            stroke="currentColor"
            strokeOpacity="0.1"
            strokeDasharray="2,2"
          />
        ))}

        {/* Zero line (highlighted) */}
        <line x1={margin.left} y1={zeroY} x2={width - margin.right} y2={zeroY} stroke="currentColor" strokeWidth="1.5" opacity="0.5" />

        {/* Y-axis */}
        <line x1={margin.left} y1={margin.top} x2={margin.left} y2={height - margin.bottom} stroke="currentColor" />

        {/* X-axis */}
        <line
          x1={margin.left}
          y1={height - margin.bottom}
          x2={width - margin.right}
          y2={height - margin.bottom}
          stroke="currentColor"
        />

        {/* Y-axis labels */}
        {yTickValues.map((val) => (
          <g key={`y-label-${val}`}>
            <line
              x1={margin.left - 5}
              y1={margin.top + ((yTickValues.indexOf(val) * plotHeight) / (yTicks - 1))}
              x2={margin.left}
              y2={margin.top + ((yTickValues.indexOf(val) * plotHeight) / (yTicks - 1))}
              stroke="currentColor"
            />
            <text
              x={margin.left - 10}
              y={margin.top + ((yTickValues.indexOf(val) * plotHeight) / (yTicks - 1)) + 4}
              textAnchor="end"
              fontSize="11"
              fill="currentColor"
            >
              {formatNumber(val, 2)}
            </text>
          </g>
        ))}

        {/* Series */}
        <polyline points={points} fill="none" stroke="#2e7d32" strokeWidth="2" />

        {/* Legend */}
        <g className="legend">
          <rect x={width - 180} y={10} width="170" height="40" fill="white" stroke="currentColor" opacity="0.9" />
          <line x1={width - 170} y1={25} x2={width - 150} y2={25} stroke="#2e7d32" strokeWidth="2" />
          <text x={width - 145} y={30} fontSize="12" fill="currentColor" fontWeight="500">
            Cumulative Diff vs Gate
          </text>
        </g>
      </svg>
    );
  };

  return (
    <div className="study-container">
      <header className="study-header">
        <h1>Volatility Residual Study</h1>
        <div className="header-controls">
          <button
            className="run-button"
            onClick={triggerRun}
            disabled={state.isRunning || state.isLoading}
          >
            {state.isRunning ? 'Running study...' : 'Run development study'}
          </button>
          <button className="refresh-button" onClick={fetchLatestRun} disabled={state.isLoading || state.isRunning}>
            {state.isLoading ? 'Loading...' : 'Refresh'}
          </button>
        </div>
      </header>

      {state.error && (
        <div className="error-box">
          <strong>Error:</strong> {state.error}
        </div>
      )}

      {state.isLoading && !data && (
        <div className="loading-box">
          <p>Loading study results...</p>
        </div>
      )}

      {data && (
        <>
          {/* Development Banner */}
          <div className="dev-banner">
            <strong>DEVELOPMENT RUN</strong>
            <p>This is a development run for research purposes only. Results are not for trading decisions.</p>
            <p>
              Reserved holdout period: <code>{data.reservedHoldout.from}</code> to{' '}
              <code>{data.reservedHoldout.to}</code> (excluded from training and testing)
            </p>
          </div>

          {/* Insufficient Data Case */}
          {data.status === 'insufficient-data' && (
            <section className="study-section">
              <div className="insufficient-data-box">
                <h2>Insufficient Data</h2>
                <p className="reason">{data.insufficientReason}</p>
                <div className="data-window-info">
                  <div>
                    <strong>Data Window:</strong> {data.dataWindow.from} to {data.dataWindow.to}
                  </div>
                  <div>
                    <strong>Sessions Available:</strong> {data.dataWindow.sessionsAvailable}
                  </div>
                  <div>
                    <strong>Sessions Used:</strong> {data.dataWindow.sessionsUsed}
                  </div>
                </div>
              </div>
            </section>
          )}

          {/* Success Case */}
          {data.status === 'ok' && (
            <>
              {/* Run Info */}
              <section className="study-section">
                <h2>Run Information</h2>
                <div className="run-info">
                  <div>
                    <strong>Run ID:</strong> <code>{data.runId}</code>
                  </div>
                  <div>
                    <strong>Generated:</strong> {formatDateTime(data.generatedAt)}
                  </div>
                  <div>
                    <strong>Data Window:</strong> {data.dataWindow.from} to {data.dataWindow.to}
                  </div>
                  <div>
                    <strong>Sessions Used:</strong> {data.dataWindow.sessionsUsed} of {data.dataWindow.sessionsAvailable} available
                  </div>
                  <div>
                    <strong>Gate Model:</strong> {data.gateModelKey}
                  </div>
                </div>
              </section>

              {/* Model Comparison Table */}
              <section className="study-section">
                <h2>Model Comparison</h2>
                <div className="table-wrapper">
                  <table className="study-table">
                    <thead>
                      <tr>
                        <th>Model</th>
                        <th>Label</th>
                        <th>Role</th>
                        <th>Pooled QLIKE</th>
                        <th>Improvement vs Gate</th>
                      </tr>
                    </thead>
                    <tbody>
                      {data.models.map((model) => {
                        const isGate = model.role === 'gate';
                        const improvementText = isGate ? '—' : `${formatPercent(model.improvementVsGatePct)}%`;

                        return (
                          <tr key={model.key} className={isGate ? 'gate-row' : ''}>
                            <td className="model-key">
                              <code>{model.key}</code>
                            </td>
                            <td className="model-label">{model.label}</td>
                            <td className="model-role">
                              <span className={`role-badge role-${model.role}`}>{model.role}</span>
                            </td>
                            <td className="number-cell">{formatNumber(model.pooledQlike)}</td>
                            <td className="number-cell">{improvementText}</td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              </section>

              {/* Charts */}
              {data.daily.length > 0 && (
                <>
                  <section className="study-section">
                    <h2>Actual vs Forecast Realized Volatility</h2>
                    <div className="chart-container">{renderActualVsForecastChart()}</div>
                  </section>

                  <section className="study-section">
                    <h2>Cumulative QLIKE Differential vs Gate</h2>
                    <div className="chart-container">{renderCumulativeQlikeChart()}</div>
                  </section>
                </>
              )}

              {/* Daily Data Table */}
              {data.daily.length > 0 ? (
                <section className="study-section">
                  <h2>Daily Results ({data.daily.length} days)</h2>
                  <div className="table-wrapper">
                    <table className="daily-table">
                      <thead>
                        <tr>
                          <th>Date</th>
                          <th>Fold</th>
                          <th>Actual vol %</th>
                          <th>HAR vol %</th>
                          <th>VIX vol %</th>
                          <th>HARX vol %</th>
                          <th>CORRECTED vol %</th>
                          <th>QLIKE (HAR)</th>
                          <th>QLIKE (VIX)</th>
                          <th>QLIKE (HARX)</th>
                          <th>QLIKE (CORRECTED)</th>
                          <th>Cumul. Diff vs Gate</th>
                        </tr>
                      </thead>
                      <tbody>
                        {data.daily.map((day, idx) => (
                          <tr key={idx}>
                            <td className="date-cell">{day.date}</td>
                            <td className="number-cell">{day.fold}</td>
                            <td className="number-cell">{formatVolPct(day.actualRv)}</td>
                            <td className="number-cell">{formatVolPct(day.forecasts.HAR)}</td>
                            <td className="number-cell">{formatVolPct(day.forecasts.VIX)}</td>
                            <td className="number-cell">{formatVolPct(day.forecasts.HARX)}</td>
                            <td className="number-cell">{formatVolPct(day.forecasts.CORRECTED)}</td>
                            <td className="number-cell">{formatNumber(day.qlike.HAR, 6)}</td>
                            <td className="number-cell">{formatNumber(day.qlike.VIX, 6)}</td>
                            <td className="number-cell">{formatNumber(day.qlike.HARX, 6)}</td>
                            <td className="number-cell">{formatNumber(day.qlike.CORRECTED, 6)}</td>
                            <td className="number-cell">{formatNumber(day.cumulativeQlikeDiffVsGate, 2)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </section>
              ) : (
                <section className="study-section">
                  <h2>Daily Results</h2>
                  <p className="no-data">No daily results available</p>
                </section>
              )}
            </>
          )}
        </>
      )}
    </div>
  );
};

export default Study;
