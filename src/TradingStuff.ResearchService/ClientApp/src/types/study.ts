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

export interface StudyModel {
  key: string;
  label: string;
  role: 'reference' | 'baseline' | 'gate' | 'candidate';
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
}
