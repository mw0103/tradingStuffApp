export interface BackfillJobStatus {
  jobId: number;
  name: string;
  kind: string;
  instrumentId: number;
  conId: number | null;
  whatToShow: string;
  barSize: string;
  useRth: boolean;
  targetFrom: string;
  targetTo: string;
  priority: number;
  status: string;
  totalSlices: number;
  pendingCount: number;
  inflightCount: number;
  succeededCount: number;
  emptyCount: number;
  retryableCount: number;
  exhaustedCount: number;
  permanentCount: number;
  nowAnchoredCount: number;
  /** Rows actually persisted to research.bars. The honest figure — render this one. */
  barsLanded: number;
  /** Bars TWS returned, summed before research.bars' primary key deduplicated them. */
  barsReturned: number;
  percentComplete: number;
  lowWaterMarkUtc: string | null;
  highWaterMarkUtc: string | null;
  earliestLeaseExpiry: string | null;
}

export interface BackfillStatusReport {
  enabled: boolean;
  ownerId: string;
  maxAttempts: number;
  jobs: BackfillJobStatus[];
}
