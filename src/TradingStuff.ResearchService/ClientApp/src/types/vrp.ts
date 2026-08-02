export interface ReservedHoldout {
  from: string;
  to: string;
  excluded: boolean;
}

export interface VrpConditioningDataWindow {
  from: string;
  to: string;
  sessionsAvailable: number;
  decisionDates: number;
  firstLabelFrom: string | null;
  lastLabelTo: string | null;
}

export interface VrpConditioningDesign {
  labelTradingDays: number;
  labelDefinition: string;
  impliedConversion: string;
  decisionTimestamp: string;
  overlappingHacLag: number;
  nonOverlappingHacLag: number;
  nonOverlappingStride: number;
  bootstrapMeanBlockLength: number;
  bootstrapResamples: number;
  purgeRows: number;
  quintileBreakpointSource: string;
}

export interface VrpConditioningInterval {
  lower: number;
  upper: number;
  alpha: number;
  draws: number;
}

export interface VrpConditioningMonotonicity {
  shape: string;
  isMonotone: boolean;
  direction: string;
  violations: number;
  adjacentPairs: number;
}

export interface VrpConditioningBucket {
  bucket: number;
  label: string;
  days: number;
  meanSpread: number;
  meanRealizedVariance: number;
  meanRealizedAnnualizedVolPct: number;
  meanImpliedVariance: number;
  meanPremiumCollected: number;
  premiumInterval: VrpConditioningInterval;
  meanPnlPerVegaNotional: number;
  pnlInterval: VrpConditioningInterval;
  realizedVarianceInterval: VrpConditioningInterval;
}

export interface VrpConditioningArmConditioning {
  arm: string;
  trainSpreadBreakpoints: number[];
  buckets: VrpConditioningBucket[];
  pnlMonotonicity: VrpConditioningMonotonicity;
  premiumMonotonicity: VrpConditioningMonotonicity;
  realizedVarianceMonotonicity: VrpConditioningMonotonicity;
  q5MinusQ1Pnl: number;
  q5MinusQ1PnlInterval: VrpConditioningInterval;
  bootstrapMonotoneFractionPnl: number;
  bootstrapMonotoneFractionPremium: number;
  usableResamples: number;
}

export interface VrpConditioningArmFold {
  fold: string;
  trainFrom: string;
  trainTo: string;
  testFrom: string;
  testTo: string;
  qlike: number;
  days: number;
}

export interface VrpConditioningArmSummary {
  key: string;
  label: string;
  role: string;
  pooledQlike: number;
  improvementVsGatePct: number;
  folds: VrpConditioningArmFold[];
}

export interface VrpConditioningDm {
  sampling: string;
  honest: boolean;
  note: string;
  meanLossAdvantage: number;
  statistic: number;
  pValueOneSided: number;
  longRunVariance: number;
  observations: number;
  hacLag: number;
  degenerate: boolean;
}

export interface VrpConditioningDmComparison {
  arm: string;
  gateArm: string;
  overlapping: VrpConditioningDm;
  nonOverlapping: VrpConditioningDm;
  samplingsDisagree: boolean;
  meanAdvantageInterval: VrpConditioningInterval;
}

export interface VrpConditioningEffectiveSample {
  scoredDecisionDates: number;
  nonOverlappingWindows: number;
  labelTradingDays: number;
  note: string;
}

export interface VrpConditioningDailyRow {
  date: string;
  labelFrom: string;
  labelTo: string;
  fold: string;
  vixLevel: number;
  impliedVariance: number;
  realizedVariance: number;
  realizedAnnualizedVolPct: number;
  premiumCollected: number;
  pnlPerVegaNotional: number;
  forecasts: Record<string, number>;
  qlike: Record<string, number>;
  spread: Record<string, number>;
  bucket: Record<string, number>;
}

export interface VrpConditioningLimitations {
  headline: string;
  pnlProxy: string;
  inference: string;
  overlap: string;
  labelVersusImplied: string;
  vixSource: string;
  permittedClaim: string;
}

export interface VrpConditioningRun {
  runId: string;
  isDevelopmentRun: boolean;
  generatedAt: string;
  status: 'ok' | 'insufficient-data';
  insufficientReason: string | null;
  dataWindow: VrpConditioningDataWindow;
  reservedHoldout: ReservedHoldout;
  design: VrpConditioningDesign;
  gateArmKey: string;
  arms: VrpConditioningArmSummary[];
  conditioning: VrpConditioningArmConditioning[];
  dieboldMariano: VrpConditioningDmComparison[];
  effectiveSample: VrpConditioningEffectiveSample;
  daily: VrpConditioningDailyRow[];
  limitations: VrpConditioningLimitations;
  registrable: boolean;
}
