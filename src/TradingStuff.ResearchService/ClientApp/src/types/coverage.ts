export interface PerConIdCoverage {
  conId: number;
  minutesWithData: number;
  totalMinutes: number;
  coverageRatio: number;
}

export type GapReason = 'disconnect' | 'tws_restart_window' | 'line_evicted' | 'buffer_overflow' | 'write_failure';

export interface CoverageGap {
  gapId: number;
  scope: string;
  startedAt: string;
  endedAt: string | null;
  reason: GapReason;
}

export interface CoverageReport {
  from: string;
  to: string;
  perConId: PerConIdCoverage[];
  overallCoverageRatio: number;
  totalMinutes: number;
  gaps: CoverageGap[];
}
