import { useState, useEffect, type ReactNode } from 'react';
import type { VrpConditioningRun } from '../types/vrp';
import './Vrp.css';

interface LoadingState {
  isLoading: boolean;
  error: string | null;
  isRunning: boolean;
}

const Vrp = () => {
  const [data, setData] = useState<VrpConditioningRun | null>(null);
  const [state, setState] = useState<LoadingState>({
    isLoading: true,
    error: null,
    isRunning: false,
  });

  const fetchLatestRun = async () => {
    setState((prev) => ({ ...prev, isLoading: true, error: null }));
    try {
      const response = await fetch('/research/studies/vrp-conditioning/latest');
      if (!response.ok) {
        if (response.status === 503) {
          throw new Error('Research service is unavailable. Is the database running?');
        }
        if (response.status === 404) {
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
      const response = await fetch('/research/studies/vrp-conditioning/run', {
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

  const formatNumber = (num: number, decimals: number = 4): string => {
    if (!Number.isFinite(num)) return '—';
    return num.toFixed(decimals);
  };

  const formatPercent = (percent: number): string => {
    if (!Number.isFinite(percent)) return '—';
    return percent.toFixed(2);
  };

  const formatPercentOne = (percent: number): string => {
    if (!Number.isFinite(percent)) return '—';
    return percent.toFixed(1);
  };

  const formatExponential = (num: number, decimals: number = 2): string => {
    if (!Number.isFinite(num)) return '—';
    return num.toExponential(decimals);
  };

  const renderLimitationsBanners = (): ReactNode => {
    if (!data) return null;

    const { limitations } = data;
    return (
      <section className="vrp-section limitations-section">
        <div className="limitations-headline">{limitations.headline}</div>

        <div className="limitation-item">
          <span className="limitation-label">P&L Proxy</span>
          <p className="limitation-text">{limitations.pnlProxy}</p>
        </div>

        <div className="limitation-item">
          <span className="limitation-label">Inference</span>
          <p className="limitation-text">{limitations.inference}</p>
        </div>

        <div className="limitation-item">
          <span className="limitation-label">Overlap</span>
          <p className="limitation-text">{limitations.overlap}</p>
        </div>

        <div className="limitation-item">
          <span className="limitation-label">Label vs Implied</span>
          <p className="limitation-text">{limitations.labelVersusImplied}</p>
        </div>

        <div className="limitation-item">
          <span className="limitation-label">VIX Source</span>
          <p className="limitation-text">{limitations.vixSource}</p>
        </div>

        <div className="limitation-item">
          <span className="limitation-label">Permitted Claim</span>
          <p className="limitation-text">{limitations.permittedClaim}</p>
        </div>
      </section>
    );
  };

  const renderConditioningSection = (): ReactNode => {
    if (!data) return null;

    return (
      <section className="vrp-section">
        <h2>Arm Conditioning: Quintile Analysis</h2>
        {data.conditioning.map((cond) => {
          const armSummary = data.arms.find((a) => a.key === cond.arm);
          return (
            <div key={cond.arm} className="arm-conditioning-section">
              <div className="arm-title">
                {armSummary?.label || cond.arm} ({cond.arm})
              </div>

              <div className="monotonicity-row">
                <div className={`monotonicity-item ${cond.pnlMonotonicity.isMonotone ? 'monotone' : 'not-monotone'}`}>
                  <span className="monotonicity-label">P&L Monotonicity</span>
                  <div className="monotonicity-verdict">{cond.pnlMonotonicity.shape}</div>
                  <div className="monotonicity-stats">
                    Violations: {cond.pnlMonotonicity.violations} of {cond.pnlMonotonicity.adjacentPairs}
                  </div>
                </div>

                <div className={`monotonicity-item ${cond.premiumMonotonicity.isMonotone ? 'monotone' : 'not-monotone'}`}>
                  <span className="monotonicity-label">Premium Monotonicity</span>
                  <div className="monotonicity-verdict">{cond.premiumMonotonicity.shape}</div>
                  <div className="monotonicity-stats">
                    Violations: {cond.premiumMonotonicity.violations} of {cond.premiumMonotonicity.adjacentPairs}
                  </div>
                </div>

                <div className={`monotonicity-item ${cond.realizedVarianceMonotonicity.isMonotone ? 'monotone' : 'not-monotone'}`}>
                  <span className="monotonicity-label">Realized Variance Monotonicity</span>
                  <div className="monotonicity-verdict">{cond.realizedVarianceMonotonicity.shape}</div>
                  <div className="monotonicity-stats">
                    Violations: {cond.realizedVarianceMonotonicity.violations} of {cond.realizedVarianceMonotonicity.adjacentPairs}
                  </div>
                </div>
              </div>

              <div className="bootstrap-row">
                <div className="bootstrap-item">
                  <span className="bootstrap-label">Bootstrap Monotone Fraction (P&L)</span>
                  <div className="bootstrap-value">{formatPercent(cond.bootstrapMonotoneFractionPnl * 100)}%</div>
                  <div className="bootstrap-hint">Bootstrap resamples in which the ordering held</div>
                </div>
                <div className="bootstrap-item">
                  <span className="bootstrap-label">Bootstrap Monotone Fraction (Premium)</span>
                  <div className="bootstrap-value">{formatPercent(cond.bootstrapMonotoneFractionPremium * 100)}%</div>
                  <div className="bootstrap-hint">Bootstrap resamples in which the ordering held</div>
                </div>
              </div>

              <div className="q5-minus-q1-section">
                <div className="q5-label">Q5 minus Q1 P&L</div>
                <div className="q5-value">
                  {formatNumber(cond.q5MinusQ1Pnl, 4)} [{formatNumber(cond.q5MinusQ1PnlInterval.lower, 4)},{' '}
                  {formatNumber(cond.q5MinusQ1PnlInterval.upper, 4)}] (90% bootstrap)
                </div>
              </div>

              <div className={`conditioning-caveat-callout ${cond.spreadVsVixSpearman >= 0.95 ? 'high-correlation' : ''}`}>
                <div className="caveat-content">
                  <div className="caveat-metric">
                    Spread vs raw VIX level (Spearman): {formatNumber(cond.spreadVsVixSpearman, 4)}
                  </div>
                  <div className="caveat-metric">
                    Buckets matching the do-nothing arm: {formatPercentOne(cond.bucketAgreementWithUnconditional * 100)}%
                  </div>
                  <div className="caveat-explanation">
                    The spread is implied minus forecast, and the implied leg is a function of VIX alone. A rank correlation near 1 means this arm's quintiles are the VIX level relabelled and the forecast leg is not deciding anything — read the table below with that in mind.
                  </div>
                </div>
              </div>

              <div className="bucket-table-wrapper">
                <table className="bucket-table">
                  <thead>
                    <tr>
                      <th>Bucket</th>
                      <th>Days</th>
                      <th>Mean Spread</th>
                      <th>Mean RV</th>
                      <th>Mean Vol %</th>
                      <th>Mean IV</th>
                      <th>Mean Premium</th>
                      <th>Mean P&L / Vega</th>
                    </tr>
                  </thead>
                  <tbody>
                    {cond.buckets.map((bucket) => (
                      <tr key={bucket.bucket}>
                        <td className="label-cell">{bucket.label}</td>
                        <td className="number-cell">{bucket.days}</td>
                        <td className="number-cell">{formatNumber(bucket.meanSpread, 6)}</td>
                        <td className="number-cell">{formatNumber(bucket.meanRealizedVariance, 6)}</td>
                        <td className="number-cell">{formatNumber(bucket.meanRealizedAnnualizedVolPct, 2)}%</td>
                        <td className="number-cell">{formatNumber(bucket.meanImpliedVariance, 6)}</td>
                        <td className="interval-cell">
                          {formatNumber(bucket.meanPremiumCollected, 6)} [{formatNumber(bucket.premiumInterval.lower, 6)},{' '}
                          {formatNumber(bucket.premiumInterval.upper, 6)}]
                        </td>
                        <td className="interval-cell">
                          {formatNumber(bucket.meanPnlPerVegaNotional, 4)} [{formatNumber(bucket.pnlInterval.lower, 4)},{' '}
                          {formatNumber(bucket.pnlInterval.upper, 4)}]
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <div className="bucket-caption">Note: P&L column contains no costs, slippage, margins, or financing.</div>
            </div>
          );
        })}
      </section>
    );
  };

  const renderCorrectionInoperativeWarning = (): ReactNode => {
    if (!data || !data.correctionIsInoperativeNote) return null;

    return (
      <section className="vrp-section correction-inoperative-warning">
        <div className="warning-banner">
          <div className="warning-icon">
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3.05h16.94a2 2 0 0 0 1.71-3.05L13.71 3.86a2 2 0 0 0-3.42 0z" />
              <line x1="12" y1="9" x2="12" y2="13" />
              <line x1="12" y1="17" x2="12.01" y2="17" />
            </svg>
          </div>
          <div className="warning-text">{data.correctionIsInoperativeNote}</div>
        </div>
      </section>
    );
  };

  const renderCorrectionFits = (): ReactNode => {
    if (!data || data.correctionFits.length === 0) return null;

    return (
      <section className="vrp-section">
        <h2>Residual Correction Fit</h2>
        <div className="table-wrapper">
          <table className="correction-fits-table">
            <thead>
              <tr>
                <th>Fold</th>
                <th>Alpha</th>
                <th>Lambda</th>
                <th>Intercept</th>
                <th>Non-Zero Coefficients</th>
                <th>Note</th>
              </tr>
            </thead>
            <tbody>
              {data.correctionFits.map((fit) => (
                <tr key={fit.fold} className={fit.isNullModel ? 'null-model-row' : ''}>
                  <td className="label-cell">{fit.fold}</td>
                  <td className="number-cell">{formatNumber(fit.alpha, 4)}</td>
                  <td className="number-cell">{formatExponential(fit.lambda, 2)}</td>
                  <td className="number-cell">{formatNumber(fit.intercept, 4)}</td>
                  <td className="coefficient-cell">
                    {fit.nonZeroCoefficients} / {fit.totalFeatures}
                    {fit.isNullModel && <span className="null-model-chip">NULL MODEL</span>}
                  </td>
                  <td className="note-cell">{fit.note}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    );
  };

  const renderArmsPooledQlike = (): ReactNode => {
    if (!data) return null;

    return (
      <section className="vrp-section">
        <h2>Arms: Pooled QLIKE and Folds</h2>
        <div className="table-wrapper">
          <table className="arms-table">
            <thead>
              <tr>
                <th>Arm</th>
                <th>Label</th>
                <th>Role</th>
                <th>Pooled QLIKE</th>
                <th>Improvement vs Gate %</th>
              </tr>
            </thead>
            <tbody>
              {data.arms.map((arm) => (
                <tr key={arm.key} className={arm.key === data.gateArmKey ? 'gate-row' : ''}>
                  <td>
                    <code>{arm.key}</code>
                    {arm.key === 'CORRECTED' && data.correctionIsInoperativeNote && (
                      <span className="identical-to-gate-chip">identical to gate by construction — see correction note</span>
                    )}
                  </td>
                  <td>{arm.label}</td>
                  <td>{arm.role}</td>
                  <td className="number-cell">{formatNumber(arm.pooledQlike, 4)}</td>
                  <td className="number-cell">
                    {arm.key === data.gateArmKey ? '—' : `${formatPercent(arm.improvementVsGatePct)}%`}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        {data.arms.some((arm) => arm.folds.length > 0) && (
          <section className="vrp-section">
            <h3>Per-Fold Results</h3>
            {data.arms.map(
              (arm) =>
                arm.folds.length > 0 && (
                  <div key={`folds-${arm.key}`}>
                    <h4 style={{ fontSize: '1rem', marginBottom: '0.75rem', color: '#333' }}>
                      {arm.label} ({arm.key})
                    </h4>
                    <table className="folds-table">
                      <thead>
                        <tr>
                          <th>Fold</th>
                          <th>Train From</th>
                          <th>Train To</th>
                          <th>Test From</th>
                          <th>Test To</th>
                          <th>QLIKE</th>
                          <th>Days</th>
                        </tr>
                      </thead>
                      <tbody>
                        {arm.folds.map((fold) => (
                          <tr key={fold.fold}>
                            <td className="number-cell">{fold.fold}</td>
                            <td className="number-cell">{fold.trainFrom}</td>
                            <td className="number-cell">{fold.trainTo}</td>
                            <td className="number-cell">{fold.testFrom}</td>
                            <td className="number-cell">{fold.testTo}</td>
                            <td className="number-cell">{formatNumber(fold.qlike, 4)}</td>
                            <td className="number-cell">{fold.days}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )
            )}
          </section>
        )}
      </section>
    );
  };

  const renderDieboldMariano = (): ReactNode => {
    if (!data) return null;

    return (
      <section className="vrp-section">
        <h2>Diebold-Mariano Inference</h2>
        <p className="dm-note">p-values here are descriptive diagnostics only — this study makes no significance claims.</p>

        {data.dieboldMariano.map((dm) => (
          <div key={`${dm.arm}-${dm.gateArm}`} style={{ marginBottom: '2rem' }}>
            <h3 style={{ fontSize: '1rem', marginBottom: '1rem', color: '#333' }}>
              {dm.arm} vs {dm.gateArm}
              {dm.samplingsDisagree && <span className="warning-chip">Samplings Disagree</span>}
            </h3>

            <div className="dm-table-wrapper">
              <table className="dm-table">
                <thead>
                  <tr>
                    <th>Sampling</th>
                    <th>Observations</th>
                    <th>HAC Lag</th>
                    <th>Statistic</th>
                    <th>p-value (one-sided)</th>
                    <th>Mean Loss Advantage</th>
                    <th>Note</th>
                  </tr>
                </thead>
                <tbody>
                  <tr className={dm.overlapping.honest ? 'honest-row' : ''}>
                    <td>{dm.overlapping.sampling}</td>
                    <td className="number-cell">{dm.overlapping.degenerate ? '—' : dm.overlapping.observations}</td>
                    <td className="number-cell">{dm.overlapping.degenerate ? '—' : dm.overlapping.hacLag}</td>
                    <td className="number-cell">
                      {dm.overlapping.degenerate ? '—' : formatNumber(dm.overlapping.statistic, 3)}
                    </td>
                    <td className="number-cell">
                      {dm.overlapping.degenerate ? '—' : formatNumber(dm.overlapping.pValueOneSided, 4)}
                    </td>
                    <td className="number-cell">
                      {dm.overlapping.degenerate ? '—' : formatNumber(dm.overlapping.meanLossAdvantage, 4)}
                    </td>
                    <td className="dm-note">{dm.overlapping.note}</td>
                  </tr>
                  <tr className={dm.nonOverlapping.honest ? 'honest-row' : ''}>
                    <td>{dm.nonOverlapping.sampling}</td>
                    <td className="number-cell">{dm.nonOverlapping.degenerate ? '—' : dm.nonOverlapping.observations}</td>
                    <td className="number-cell">{dm.nonOverlapping.degenerate ? '—' : dm.nonOverlapping.hacLag}</td>
                    <td className="number-cell">
                      {dm.nonOverlapping.degenerate ? '—' : formatNumber(dm.nonOverlapping.statistic, 3)}
                    </td>
                    <td className="number-cell">
                      {dm.nonOverlapping.degenerate ? '—' : formatNumber(dm.nonOverlapping.pValueOneSided, 4)}
                    </td>
                    <td className="number-cell">
                      {dm.nonOverlapping.degenerate ? '—' : formatNumber(dm.nonOverlapping.meanLossAdvantage, 4)}
                    </td>
                    <td className="dm-note">{dm.nonOverlapping.note}</td>
                  </tr>
                </tbody>
              </table>
            </div>

            <div style={{ marginTop: '0.75rem', fontSize: '0.85rem', color: '#666' }}>
              Mean advantage interval: [{formatNumber(dm.meanAdvantageInterval.lower, 4)},{' '}
              {formatNumber(dm.meanAdvantageInterval.upper, 4)}]
            </div>
          </div>
        ))}
      </section>
    );
  };

  const renderEffectiveSample = (): ReactNode => {
    if (!data) return null;

    return (
      <section className="vrp-section">
        <h2>Effective Sample</h2>
        <div className="effective-sample-box">
          <div className="effective-sample-row">
            <span className="effective-sample-label">Scored Decision Dates:</span>
            <span className="effective-sample-value">{data.effectiveSample.scoredDecisionDates}</span>
          </div>
          <div className="effective-sample-row">
            <span className="effective-sample-label">Non-Overlapping Windows:</span>
            <span className="effective-sample-value">{data.effectiveSample.nonOverlappingWindows}</span>
          </div>
          <div className="effective-sample-row">
            <span className="effective-sample-label">Label Trading Days:</span>
            <span className="effective-sample-value">{data.effectiveSample.labelTradingDays}</span>
          </div>
          <div className="effective-sample-note">{data.effectiveSample.note}</div>
        </div>
      </section>
    );
  };

  const renderDesign = (): ReactNode => {
    if (!data) return null;

    const design = data.design;
    return (
      <section className="vrp-section">
        <h2>Design Parameters</h2>
        <div className="definition-list">
          <div className="definition-item">
            <div className="definition-term">Label Definition</div>
            <div className="definition-value">{design.labelDefinition}</div>
          </div>
          <div className="definition-item">
            <div className="definition-term">Implied Conversion</div>
            <div className="definition-value">{design.impliedConversion}</div>
          </div>
          <div className="definition-item">
            <div className="definition-term">Decision Timestamp</div>
            <div className="definition-value">{design.decisionTimestamp}</div>
          </div>
          <div className="definition-item">
            <div className="definition-term">Label Trading Days</div>
            <div className="definition-value">{design.labelTradingDays}</div>
          </div>
          <div className="definition-item">
            <div className="definition-term">Overlapping HAC Lag</div>
            <div className="definition-value">{design.overlappingHacLag}</div>
          </div>
          <div className="definition-item">
            <div className="definition-term">Non-Overlapping HAC Lag</div>
            <div className="definition-value">{design.nonOverlappingHacLag}</div>
          </div>
          <div className="definition-item">
            <div className="definition-term">Non-Overlapping Stride</div>
            <div className="definition-value">{design.nonOverlappingStride}</div>
          </div>
          <div className="definition-item">
            <div className="definition-term">Bootstrap Mean Block Length</div>
            <div className="definition-value">{formatNumber(design.bootstrapMeanBlockLength, 2)}</div>
          </div>
          <div className="definition-item">
            <div className="definition-term">Bootstrap Resamples</div>
            <div className="definition-value">{design.bootstrapResamples}</div>
          </div>
          <div className="definition-item">
            <div className="definition-term">Purge Rows</div>
            <div className="definition-value">{design.purgeRows}</div>
          </div>
          <div className="definition-item">
            <div className="definition-term">Quintile Breakpoint Source</div>
            <div className="definition-value">{design.quintileBreakpointSource}</div>
          </div>
        </div>
      </section>
    );
  };

  const renderDailyChart = (): ReactNode => {
    if (!data || data.daily.length === 0) return null;

    const dailyData = data.daily;
    const width = 800;
    const height = 300;
    const margin = { top: 20, right: 20, bottom: 40, left: 60 };
    const plotWidth = width - margin.left - margin.right;
    const plotHeight = height - margin.top - margin.bottom;

    let minVal = Infinity;
    let maxVal = -Infinity;

    dailyData.forEach((day) => {
      minVal = Math.min(minVal, day.impliedVariance, day.realizedVariance);
      maxVal = Math.max(maxVal, day.impliedVariance, day.realizedVariance);
    });

    const padding = (maxVal - minVal) * 0.1;
    minVal -= padding;
    maxVal += padding;

    const scaleX = plotWidth / (dailyData.length - 1 || 1);
    const scaleY = plotHeight / (maxVal - minVal || 1);

    const impliedPoints = dailyData
      .map((day, i) => {
        const x = margin.left + i * scaleX;
        const y = margin.top + plotHeight - (day.impliedVariance - minVal) * scaleY;
        return `${x},${y}`;
      })
      .join(' ');

    const realizedPoints = dailyData
      .map((day, i) => {
        const x = margin.left + i * scaleX;
        const y = margin.top + plotHeight - (day.realizedVariance - minVal) * scaleY;
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

        <line x1={margin.left} y1={margin.top} x2={margin.left} y2={height - margin.bottom} stroke="currentColor" />
        <line
          x1={margin.left}
          y1={height - margin.bottom}
          x2={width - margin.right}
          y2={height - margin.bottom}
          stroke="currentColor"
        />

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
              {formatNumber(val, 6)}
            </text>
          </g>
        ))}

        <polyline points={impliedPoints} fill="none" stroke="#1976d2" strokeWidth="2" />
        <polyline points={realizedPoints} fill="none" stroke="#d32f2f" strokeWidth="2" />

        <g className="legend">
          <rect x={width - 180} y={10} width="170" height="60" fill="white" stroke="currentColor" opacity="0.9" />
          <line x1={width - 170} y1={25} x2={width - 150} y2={25} stroke="#1976d2" strokeWidth="2" />
          <text x={width - 145} y={30} fontSize="12" fill="currentColor" fontWeight="500">
            Implied Variance
          </text>
          <line x1={width - 170} y1={45} x2={width - 150} y2={45} stroke="#d32f2f" strokeWidth="2" />
          <text x={width - 145} y={50} fontSize="12" fill="currentColor">
            Realized Variance
          </text>
        </g>
      </svg>
    );
  };

  const renderPremiumChart = (): ReactNode => {
    if (!data || data.daily.length === 0) return null;

    const dailyData = data.daily;
    const width = 800;
    const height = 250;
    const margin = { top: 20, right: 20, bottom: 40, left: 60 };
    const plotWidth = width - margin.left - margin.right;
    const plotHeight = height - margin.top - margin.bottom;

    let minVal = 0;
    let maxVal = -Infinity;

    dailyData.forEach((day) => {
      minVal = Math.min(minVal, day.premiumCollected);
      maxVal = Math.max(maxVal, day.premiumCollected);
    });

    const padding = Math.max(Math.abs(minVal), Math.abs(maxVal)) * 0.1 || 1;
    minVal -= padding;
    maxVal += padding;

    const scaleX = plotWidth / (dailyData.length - 1 || 1);
    const scaleY = plotHeight / (maxVal - minVal || 1);

    const premiumPoints = dailyData
      .map((day, i) => {
        const x = margin.left + i * scaleX;
        const y = margin.top + plotHeight - (day.premiumCollected - minVal) * scaleY;
        return `${x},${y}`;
      })
      .join(' ');

    const zeroY = margin.top + plotHeight - (0 - minVal) * scaleY;

    const yTicks = 5;
    const yTickValues = Array.from({ length: yTicks }, (_, i) => {
      const ratio = i / (yTicks - 1);
      return minVal + ratio * (maxVal - minVal);
    });

    return (
      <svg width="100%" height={height} viewBox={`0 0 ${width} ${height}`} className="chart">
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

        <line x1={margin.left} y1={zeroY} x2={width - margin.right} y2={zeroY} stroke="currentColor" strokeWidth="1.5" opacity="0.5" />

        <line x1={margin.left} y1={margin.top} x2={margin.left} y2={height - margin.bottom} stroke="currentColor" />
        <line
          x1={margin.left}
          y1={height - margin.bottom}
          x2={width - margin.right}
          y2={height - margin.bottom}
          stroke="currentColor"
        />

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
              {formatNumber(val, 6)}
            </text>
          </g>
        ))}

        <polyline points={premiumPoints} fill="none" stroke="#2e7d32" strokeWidth="2" />

        <g className="legend">
          <rect x={width - 180} y={10} width="170" height="40" fill="white" stroke="currentColor" opacity="0.9" />
          <line x1={width - 170} y1={25} x2={width - 150} y2={25} stroke="#2e7d32" strokeWidth="2" />
          <text x={width - 145} y={30} fontSize="12" fill="currentColor" fontWeight="500">
            Premium Collected
          </text>
        </g>
      </svg>
    );
  };

  const renderDailyTable = (): ReactNode => {
    if (!data || data.daily.length === 0) return null;

    const maxRows = 250;
    const dailyData = data.daily.slice(0, maxRows);
    const omittedCount = data.daily.length - maxRows;

    return (
      <section className="vrp-section">
        <h2>Daily Results ({data.daily.length} rows)</h2>
        {omittedCount > 0 && <div className="daily-table-note">Showing {maxRows} of {data.daily.length} rows</div>}

        <div className="daily-table-wrapper">
          <table className="daily-table">
            <thead>
              <tr>
                <th>Date</th>
                <th>Label From</th>
                <th>Label To</th>
                <th>Fold</th>
                <th>VIX Level</th>
                <th>Implied Variance</th>
                <th>Realized Variance</th>
                <th>Realized Vol %</th>
                <th>Premium Collected</th>
                <th>P&L / Vega Notional</th>
              </tr>
            </thead>
            <tbody>
              {dailyData.map((day, idx) => (
                <tr key={idx}>
                  <td className="date-cell">{day.date}</td>
                  <td className="number-cell">{day.labelFrom}</td>
                  <td className="number-cell">{day.labelTo}</td>
                  <td className="number-cell">{day.fold}</td>
                  <td className="number-cell">{formatNumber(day.vixLevel, 2)}</td>
                  <td className="number-cell">{formatNumber(day.impliedVariance, 6)}</td>
                  <td className="number-cell">{formatNumber(day.realizedVariance, 6)}</td>
                  <td className="number-cell">{formatNumber(day.realizedAnnualizedVolPct, 2)}%</td>
                  <td className="number-cell">{formatNumber(day.premiumCollected, 6)}</td>
                  <td className="number-cell">{formatNumber(day.pnlPerVegaNotional, 4)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    );
  };

  return (
    <div className="vrp-container">
      <header className="vrp-header">
        <h1>Variance Risk Premium: Conditioning Study</h1>
        <div className="header-controls">
          <button className="run-button" onClick={triggerRun} disabled={state.isRunning || state.isLoading}>
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
          {!data.registrable && <div className="not-registrable-chip">NOT REGISTRABLE</div>}

          <div className="dev-banner">
            <strong>DEVELOPMENT RUN</strong>
            <p>This is a development run for research purposes only. Results are not for trading decisions.</p>
            <p>
              Reserved holdout period: <code>{data.reservedHoldout.from}</code> to <code>{data.reservedHoldout.to}</code>{' '}
              (excluded from training and testing)
            </p>
          </div>

          {renderLimitationsBanners()}

          {data.status === 'insufficient-data' && (
            <section className="vrp-section">
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
                    <strong>Decision Dates:</strong> {data.dataWindow.decisionDates}
                  </div>
                </div>
              </div>
            </section>
          )}

          {data.status === 'ok' && (
            <>
              {renderConditioningSection()}
              {renderCorrectionInoperativeWarning()}
              {renderArmsPooledQlike()}
              {renderCorrectionFits()}
              {renderDieboldMariano()}
              {renderEffectiveSample()}
              {renderDesign()}

              {data.daily.length > 0 && (
                <>
                  <section className="vrp-section">
                    <h2>Implied vs Realized Variance</h2>
                    <div className="chart-container">{renderDailyChart()}</div>
                  </section>

                  <section className="vrp-section">
                    <h2>Premium Collected</h2>
                    <div className="chart-container">{renderPremiumChart()}</div>
                  </section>
                </>
              )}

              {renderDailyTable()}
            </>
          )}
        </>
      )}
    </div>
  );
};

export default Vrp;
