import { useState, useEffect, type ReactNode } from 'react';
import type { VolatilityResidualStudyRun, H1Verdict, ExploratoryRung } from '../types/study';
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

  // Render H1 Verdict Panel
  const renderH1VerdictPanel = (h1: H1Verdict): ReactNode => {
    return (
      <section className="study-section h1-verdict-section">
        <h2>H1 verdict</h2>
        <div className="h1-verdict-panel">
          {/* Verdict Banner */}
          <div className={`verdict-banner verdict-${h1.verdict}`}>
            <div className="verdict-word">{h1.verdict.toUpperCase()}</div>
          </div>

          {/* Conditions Grid */}
          <div className="h1-conditions">
            <div className={`condition-row ${h1.marginPasses ? 'pass' : 'fail'}`}>
              <span className="condition-label">Pooled margin vs gate</span>
              <span className="condition-value">{h1.marginPct.toFixed(2)}%</span>
              <span className="condition-threshold">threshold {'>='} 2%</span>
              <span className={`condition-marker ${h1.marginPasses ? 'pass' : 'fail'}`}>
                {h1.marginPasses ? '✓' : '✗'}
              </span>
            </div>

            <div className={`condition-row ${h1.dmPasses ? 'pass' : 'fail'}`}>
              <span className="condition-label">Diebold-Mariano (margin-adjusted, τ=0.02)</span>
              <span className="condition-value">
                DM = {h1.dmStatistic.toFixed(3)}, p = {h1.dmPValue.toFixed(4)}
              </span>
              <span className="condition-threshold">threshold p {'<'} 0.05</span>
              <span className={`condition-marker ${h1.dmPasses ? 'pass' : 'fail'}`}>
                {h1.dmPasses ? '✓' : '✗'}
              </span>
            </div>

            <div className={`condition-row ${h1.foldsPass ? 'pass' : 'fail'}`}>
              <span className="condition-label">Folds positive</span>
              <span className="condition-value">
                {h1.foldsPositive} of {h1.foldsTotal}
              </span>
              <span className="condition-threshold">threshold {'>='} 2 of 3</span>
              <span className={`condition-marker ${h1.foldsPass ? 'pass' : 'fail'}`}>
                {h1.foldsPass ? '✓' : '✗'}
              </span>
            </div>

            <div className={`condition-row ${h1.bootstrapExcludesZero ? 'pass' : 'fail'}`}>
              <span className="condition-label">Bootstrap lower bound</span>
              <span className="condition-value">{h1.bootstrapLower.toExponential(3)}</span>
              <span className="condition-threshold">threshold {'>'} 0</span>
              <span className={`condition-marker ${h1.bootstrapExcludesZero ? 'pass' : 'fail'}`}>
                {h1.bootstrapExcludesZero ? '✓' : '✗'}
              </span>
            </div>

            <div className={`condition-row ${h1.vixHalvesPositive ? 'pass' : 'fail'}`}>
              <span className="condition-label">VIX halves both positive</span>
              <span className="condition-value">{h1.vixHalvesPositive ? 'yes' : 'no'}</span>
              <span className="condition-threshold">threshold both</span>
              <span className={`condition-marker ${h1.vixHalvesPositive ? 'pass' : 'fail'}`}>
                {h1.vixHalvesPositive ? '✓' : '✗'}
              </span>
            </div>
          </div>

          {/* Failed Conditions */}
          {h1.failedConditions.length > 0 && (
            <div className="h1-section failed-conditions">
              <strong>Failed conditions:</strong>
              <div className="condition-list">{h1.failedConditions.join(', ')}</div>
            </div>
          )}

          {/* Permitted Claim */}
          <div className="h1-section permitted-claim">
            <div className="section-title">Permitted claim</div>
            <p className="claim-text">{h1.permittedClaim}</p>
            <div className="claim-basis">Basis: {h1.claimBasis}</div>
          </div>

          {/* DM Table */}
          <div className="h1-section dm-table-section">
            <div className="section-title">Diebold-Mariano Results</div>
            <div className="table-wrapper">
              <table className="h1-dm-table">
                <thead>
                  <tr>
                    <th>Model</th>
                    <th>τ</th>
                    <th>Statistic</th>
                    <th>One-sided p</th>
                    <th>Mean Loss Advantage</th>
                    <th>Observations</th>
                    <th>HAC Lag</th>
                    <th>Interpretation</th>
                  </tr>
                </thead>
                <tbody>
                  <tr className="dm-row-adjusted">
                    <td>Margin-adjusted</td>
                    <td>{h1.marginAdjusted.tau}</td>
                    <td>{h1.marginAdjusted.statistic.toFixed(3)}</td>
                    <td>{h1.marginAdjusted.pValueOneSided.toFixed(4)}</td>
                    <td>{h1.marginAdjusted.meanLossAdvantage.toFixed(4)}</td>
                    <td>{h1.marginAdjusted.observations}</td>
                    <td>{h1.marginAdjusted.hacLag}</td>
                    <td className="interpretation">{h1.marginAdjusted.interpretation}</td>
                  </tr>
                  <tr className="dm-row-unadjusted">
                    <td>Unadjusted</td>
                    <td>{h1.unadjusted.tau}</td>
                    <td>{h1.unadjusted.statistic.toFixed(3)}</td>
                    <td>{h1.unadjusted.pValueOneSided.toFixed(4)}</td>
                    <td>{h1.unadjusted.meanLossAdvantage.toFixed(4)}</td>
                    <td>{h1.unadjusted.observations}</td>
                    <td>{h1.unadjusted.hacLag}</td>
                    <td className="interpretation">{h1.unadjusted.interpretation}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>

          {/* Folds Table */}
          {h1.folds.length > 0 && (
            <div className="h1-section folds-table-section">
              <div className="section-title">Per-fold Results</div>
              <div className="table-wrapper">
                <table className="h1-folds-table">
                  <thead>
                    <tr>
                      <th>Fold</th>
                      <th>Days</th>
                      <th>Gate QLIKE</th>
                      <th>Candidate QLIKE</th>
                      <th>Improvement %</th>
                      <th>Sign</th>
                    </tr>
                  </thead>
                  <tbody>
                    {h1.folds.map((fold) => (
                      <tr key={fold.fold} className={fold.positive ? 'positive' : 'negative'}>
                        <td>{fold.fold}</td>
                        <td>{fold.days}</td>
                        <td>{fold.gateQlike.toFixed(4)}</td>
                        <td>{fold.candidateQlike.toFixed(4)}</td>
                        <td>{fold.improvementPct.toFixed(2)}%</td>
                        <td className={`sign-marker ${fold.positive ? 'positive' : 'negative'}`}>
                          {fold.positive ? '+' : '−'}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}

          {/* VIX Halves Table */}
          {h1.vixHalves.length > 0 && (
            <div className="h1-section vix-halves-table-section">
              <div className="section-title">VIX Regime Results</div>
              <div className="table-wrapper">
                <table className="h1-vix-halves-table">
                  <thead>
                    <tr>
                      <th>Regime</th>
                      <th>Days</th>
                      <th>Gate QLIKE</th>
                      <th>Candidate QLIKE</th>
                      <th>Improvement %</th>
                      <th>Sign</th>
                    </tr>
                  </thead>
                  <tbody>
                    {h1.vixHalves.map((half, idx) => (
                      <tr key={idx} className={half.positive ? 'positive' : 'negative'}>
                        <td>{half.regime}</td>
                        <td>{half.days}</td>
                        <td>{half.gateQlike.toFixed(4)}</td>
                        <td>{half.candidateQlike.toFixed(4)}</td>
                        <td>{half.improvementPct.toFixed(2)}%</td>
                        <td className={`sign-marker ${half.positive ? 'positive' : 'negative'}`}>
                          {half.positive ? '+' : '−'}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      </section>
    );
  };

  // Render Exploratory Result Section
  const renderExploratorySection = (exploratory: ExploratoryRung): ReactNode => {
    return (
      <section className="study-section exploratory-result-section">
        <h2>{exploratory.label}</h2>

        {/* Exploratory Warning Banner */}
        <div className="exploratory-banner">
          <strong>EXPLORATORY — not eligible for any claim</strong>
          <p>{exploratory.reason}</p>
        </div>

        <div className="exploratory-content">
          {/* Metrics */}
          <div className="exploratory-metrics">
            <div className="metric">
              <span className="metric-label">Pooled QLIKE:</span>
              <span className="metric-value">{exploratory.pooledQlike.toFixed(4)}</span>
            </div>
            <div className="metric">
              <span className="metric-label">Improvement vs Gate:</span>
              <span className="metric-value">{exploratory.improvementVsGatePct.toFixed(2)}%</span>
            </div>
            <div className="metric">
              <span className="metric-label">Positivity Floor Hits:</span>
              <span className="metric-value">{exploratory.positivityFloorHits}</span>
            </div>
          </div>

          {/* Retransformation Note */}
          <div className="exploratory-section">
            <div className="section-title">Retransformation Note</div>
            <p className="retransformation-note">{exploratory.retransformationNote}</p>
          </div>

          {/* DM Table */}
          <div className="exploratory-section">
            <div className="section-title">Diebold-Mariano Results</div>
            <div className="table-wrapper">
              <table className="exploratory-dm-table">
                <thead>
                  <tr>
                    <th>Model</th>
                    <th>τ</th>
                    <th>Statistic</th>
                    <th>One-sided p</th>
                    <th>Mean Loss Advantage</th>
                    <th>Observations</th>
                    <th>HAC Lag</th>
                    <th>Interpretation</th>
                  </tr>
                </thead>
                <tbody>
                  <tr>
                    <td>Margin-adjusted</td>
                    <td>{exploratory.marginAdjusted.tau}</td>
                    <td>{exploratory.marginAdjusted.statistic.toFixed(3)}</td>
                    <td>{exploratory.marginAdjusted.pValueOneSided.toFixed(4)}</td>
                    <td>{exploratory.marginAdjusted.meanLossAdvantage.toFixed(4)}</td>
                    <td>{exploratory.marginAdjusted.observations}</td>
                    <td>{exploratory.marginAdjusted.hacLag}</td>
                    <td className="interpretation">{exploratory.marginAdjusted.interpretation}</td>
                  </tr>
                  <tr>
                    <td>Unadjusted</td>
                    <td>{exploratory.unadjusted.tau}</td>
                    <td>{exploratory.unadjusted.statistic.toFixed(3)}</td>
                    <td>{exploratory.unadjusted.pValueOneSided.toFixed(4)}</td>
                    <td>{exploratory.unadjusted.meanLossAdvantage.toFixed(4)}</td>
                    <td>{exploratory.unadjusted.observations}</td>
                    <td>{exploratory.unadjusted.hacLag}</td>
                    <td className="interpretation">{exploratory.unadjusted.interpretation}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>

          {/* Frozen Hyperparameters */}
          {Object.keys(exploratory.frozenHyperparameters).length > 0 && (
            <div className="exploratory-section">
              <div className="section-title">Frozen Hyperparameters</div>
              <div className="hyperparameters-list">
                {Object.entries(exploratory.frozenHyperparameters).map(([key, value]) => (
                  <div key={key} className="hyperparameter-item">
                    <span className="param-key">{key}:</span>
                    <span className="param-value">{value}</span>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Permitted Claim */}
          <div className="exploratory-section">
            <div className="section-title">Permitted Claim</div>
            <p className="claim-text">{exploratory.permittedClaim}</p>
          </div>
        </div>
      </section>
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
          {/* Exploratory Banner */}
          {data.isExploratory && (
            <div className="exploratory-banner">
              <strong>EXPLORATORY — not eligible for any claim</strong>
              <p>{data.exploratoryReason}</p>
            </div>
          )}

          {/* Development Banner */}
          <div className="dev-banner">
            <strong>DEVELOPMENT RUN</strong>
            <p>This is a development run for research purposes only. Results are not for trading decisions.</p>
            <p>
              Reserved holdout period: <code>{data.reservedHoldout.from}</code> to{' '}
              <code>{data.reservedHoldout.to}</code> (excluded from training and testing)
            </p>
          </div>

          {/* H1 Verdict Panel */}
          {data.status === 'ok' && data.h1 && renderH1VerdictPanel(data.h1)}

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
                          <tr key={model.key} className={isGate ? 'gate-row' : model.role === 'exploratory' ? 'exploratory-row' : ''}>
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

              {/* Exploratory Result Section */}
              {data.exploratory && renderExploratorySection(data.exploratory)}

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
                          <th>Prior VIX</th>
                          <th>VIX half</th>
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
                            <td className="number-cell">{day.priorVix !== undefined ? day.priorVix.toFixed(2) : '—'}</td>
                            <td className="number-cell">{day.vixRegime ?? '—'}</td>
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
