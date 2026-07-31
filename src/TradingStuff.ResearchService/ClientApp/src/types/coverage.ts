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
  closedBy: 'observed' | 'inferred' | null;
}

/** One exchange session clipped to the report window; the union of these is the denominator. */
export interface CoverageSession {
  sessionId: number;
  calendar: string;
  tradingDate: string;
  label: 'RTH' | 'GTH';
  isHalfDay: boolean;
  openUtc: string;
  closeUtc: string;
  measuredFromUtc: string;
  measuredToUtc: string;
  expectedMinutes: number;
}

/**
 * Anything other than 'measured' means the window has no believable denominator, and
 * overallCoverageRatio is null. A weekend is not 0% covered and a stale session table is not 100%
 * covered — render the status, never a number.
 */
export type CoverageStatus =
  | 'measured'
  | 'not-configured'
  | 'no-session-in-window'
  | 'sessions-out-of-sync'
  | 'window-rejected'
  | 'calendar-unknown';

export interface CoverageBasis {
  status: CoverageStatus;
  calendars: string[];
  expectedMinutes: number;
  persistedSessions: number;
  generatedSessions: number;
  sessions: CoverageSession[];
  detail: string | null;
}

export interface CoverageReport {
  from: string;
  to: string;
  basis: CoverageBasis;
  perConId: PerConIdCoverage[];
  overallCoverageRatio: number | null;
  totalMinutes: number;
  gaps: CoverageGap[];
}
