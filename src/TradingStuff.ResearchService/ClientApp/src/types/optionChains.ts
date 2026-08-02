export interface OptionChainJobStatus {
  jobId: number;
  name: string;
  underlying: string;
  tradingClass: string;
  targetFrom: string;
  targetTo: string;
  interval: string;
  priority: number;
  status: string;
  totalRequests: number;
  pendingCount: number;
  inflightCount: number;
  succeededCount: number;
  emptyCount: number;
  retryableCount: number;
  exhaustedCount: number;
  permanentCount: number;
  quotesLanded: number;
  quotesReturned: number;
  percentComplete: number;
}

export interface OptionChainStatusReport {
  enabled: boolean;
  ownerId: string;
  maxAttempts: number;
  jobs: OptionChainJobStatus[];
}

export interface CapabilityProbeResult {
  succeeded: boolean;
  detail: string;
}

export type CapabilityProbeRunResponse = Record<string, CapabilityProbeResult>;
