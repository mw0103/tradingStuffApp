export interface DataWindow {
  from: string;
  to: string;
  sessionsAvailable: number;
  sessionsUsed: number;
}

export interface ReservedHoldout {
  from: string;
  to: string;
  excluded: boolean;
}

export interface ModelFold {
  fold: number;
  trainFrom: string;
  trainTo: string;
  testFrom: string;
  testTo: string;
  qlike: number;
  days: number;
}

export interface DieboldMariano {
  tau: number;
  interpretation: string;
  meanLossAdvantage: number;
  statistic: number;
  pValueOneSided: number;
  longRunVariance: number;
  observations: number;
  hacLag: number;
}

export interface H1VerdictFold {
  fold: number;
  days: number;
  gateQlike: number;
  candidateQlike: number;
  improvementPct: number;
  positive: boolean;
}

export interface H1VerdictVixHalf {
  regime: string;
  days: number;
  gateQlike: number;
  candidateQlike: number;
  improvementPct: number;
  positive: boolean;
}

export interface H1VerdictBootstrap {
  sampleMeanAdvantage: number;
  lowerBound: number;
  alpha: number;
  resamples: number;
  meanBlockLength: number;
  seed: number;
  excludesZero: boolean;
}

export interface H1Verdict {
  gateModelKey: string;
  candidateModelKey: string;
  marginPct: number;
  marginPasses: boolean;
  dmStatistic: number;
  dmPValue: number;
  dmPasses: boolean;
  foldsPositive: number;
  foldsTotal: number;
  foldsPass: boolean;
  bootstrapLower: number;
  bootstrapExcludesZero: boolean;
  vixHalvesPositive: boolean;
  verdict: 'pass' | 'fail';
  failedConditions: string[];
  marginAdjusted: DieboldMariano;
  unadjusted: DieboldMariano;
  bootstrap: H1VerdictBootstrap;
  folds: H1VerdictFold[];
  vixHalves: H1VerdictVixHalf[];
  permittedClaim: string;
  claimBasis: string;
}

export interface ExploratoryRung {
  modelKey: string;
  label: string;
  isExploratory: boolean;
  registrable: boolean;
  reason: string;
  permittedClaim: string;
  pooledQlike: number;
  improvementVsGatePct: number;
  marginAdjusted: DieboldMariano;
  unadjusted: DieboldMariano;
  frozenHyperparameters: Record<string, string>;
  positivityFloorHits: number;
  retransformationNote: string;
}

export interface StudyModel {
  key: string;
  label: string;
  role: 'reference' | 'baseline' | 'gate' | 'candidate' | 'exploratory';
  pooledQlike: number;
  improvementVsGatePct: number;
  folds: ModelFold[];
}

export interface DailyForecast {
  date: string;
  fold: number;
  actualRv: number;
  forecasts: {
    HAR: number;
    VIX: number;
    HARX: number;
    CORRECTED: number;
  };
  qlike: {
    HAR: number;
    VIX: number;
    HARX: number;
    CORRECTED: number;
  };
  cumulativeQlikeDiffVsGate: number;
  priorVix?: number;
  vixRegime?: string;
}

export interface VolatilityResidualStudyRun {
  runId: string;
  isDevelopmentRun: boolean;
  generatedAt: string;
  status: 'ok' | 'insufficient-data';
  insufficientReason: string | null;
  dataWindow: DataWindow;
  reservedHoldout: ReservedHoldout;
  gateModelKey: string;
  models: StudyModel[];
  daily: DailyForecast[];
  h1: H1Verdict | null;
  isExploratory: boolean;
  registrable: boolean;
  exploratoryReason: string | null;
  exploratory: ExploratoryRung | null;
}
