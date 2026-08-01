export interface KillSwitchStatus {
  engaged: boolean;
  engagedAt: string | null;
  reason: string | null;
  durability: string;
}

export interface SessionStatus {
  calendar: string;
  inSession: boolean;
  label: string | null;
  tradingDate: string | null;
  sessionKey: string;
}

export interface ExecutionPlaneConfiguration {
  router: string;
  portfolioSource: string;
  /** The provider MarketDataService RESOLVED ("ibkr-gateway" or the deterministic feed). */
  marketDataSource: string;
  /** The MarketData:Source string ExecutionService was configured with. Display only. */
  marketDataSourceConfigured: string | null;
}

export interface AutomationDecision {
  decisionId: number;
  decidedAt: string;
  trigger: string;
  armed: boolean;
  armState: string;
  armReason: string;
  sessionCalendar: string | null;
  sessionLabel: string | null;
  sessionTradingDate: string | null;
  inSession: boolean;
  signalState: string;
  signalReason: string;
  studyRunId: string | null;
  action: string;
  actionReason: string;
  orderSubmitted: boolean;
  orderId: string | null;
  correlationId: string | null;
  lifecycleStatus: string | null;
  limitPrice: number | null;
  limitPriceSource: string | null;
  ordersThisSession: number;
  orderCap: number;
  detail: string | null;
}

export interface AutomationStatusReport {
  enabled: boolean;
  armed: boolean;
  armState: string;
  armReason: string;
  armCheckedAt: string | null;
  killSwitch: KillSwitchStatus;
  executionPlane: ExecutionPlaneConfiguration | null;
  executionPlaneError: string | null;
  session: SessionStatus;
  signalSource: string;
  lastDecision: AutomationDecision | null;
  recentDecisions: AutomationDecision[];
  submittedThisSession: AutomationDecision[];
  ordersThisSession: number;
  orderCap: number;
  capRemaining: number;
  persistenceError: string | null;
  notes: string;
}
