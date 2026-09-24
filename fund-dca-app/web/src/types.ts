export type FundType = 'Stock' | 'Mixed' | 'Qdii' | 'Bond' | 'Money'

export interface FundListItem {
  code: string
  name: string
  type: FundType
  sector: string | null
  sectorIsEquity: boolean
  sectorSortOrder: number
  trackedIndexCode: string | null
  trackedIndexName: string | null
  valuationMetric: string
  buyFeeRate: number | null
  dividendMethod: string
  isActive: boolean
}

export interface DashboardRow {
  code: string
  name: string
  type: FundType
  sectorId: number | null
  sectorName: string | null
  sectorIsEquity: boolean
  sectorSortOrder: number
  shares: number | null
  costAmount: number | null
  unitNav: number | null
  navDate: string | null
  dayChangePercent: number | null
  marketValue: number
  totalReturnPercent: number | null
  weightPercent: number
  stale: boolean
  isMoney: boolean
  source: string | null
  manualValueDate: string | null
}

export interface SectorSlice {
  name: string
  marketValue: number
  weightPercent: number
  isEquity: boolean
  sortOrder: number
}

export interface Budget {
  yearMonth: string
  budgetAmount: number
  investedAmount: number
  remainingAmount: number
}

export interface Dashboard {
  asOfDate: string | null
  containsSeedData: boolean
  denominator: number
  equityMarketValue: number
  stableMarketValue: number
  dayPnl: number
  equityCost: number
  equityPnl: number
  equityPnlPercent: number
  budget: Budget | null
  rows: DashboardRow[]
  sectors: SectorSlice[]
}

export interface RefreshResult {
  fundCode: string
  success: boolean
  tradeDate: string | null
  newRow: boolean
  error: string | null
}

export interface RefreshReport {
  finishedAt: string
  successCount: number
  failedCount: number
  results: RefreshResult[]
}

// ---------- M2 定投决策 ----------

export type SignalLevel = 'Green' | 'Yellow' | 'Red' | 'NoData' | 'Stable'

export interface FundDecision {
  code: string
  name: string
  type: FundType
  sectorName: string | null
  marketValue: number
  weightPercent: number
  sectorWeightPercent: number
  signal: SignalLevel
  indexName: string | null
  indexCode: string | null
  metric: string | null
  metricValue: number | null
  percentile: number | null
  valuationDate: string | null
  /** 持有总收益率 %（(市值-本金)/本金）；成本未知为 null */
  totalReturnPercent: number | null
  /** 今日建议：固定额或 0（暂停） */
  suggestedAmount: number
  blockers: string[]
  notes: string[]
}

export interface DecisionOptions {
  /** 单次定投固定金额（元/只/次），全局设置 */
  fixedInvestAmount: number
  singleFundLimitPercent: number
  sectorLimitPercent: number
}

export interface Decision {
  valuationDate: string | null
  denominator: number
  budget: Budget | null
  totalSuggested: number
  remainingAfter: number
  options: DecisionOptions
  funds: FundDecision[]
}

export interface ValuationRefreshResult {
  indexCode: string
  indexName: string
  success: boolean
  tradeDate: string | null
  viaProxy: boolean
  resolvedCode: string | null
  pePercentile: number | null
  pbPercentile: number | null
  error: string | null
}

export interface ValuationRefreshReport {
  finishedAt: string
  successCount: number
  failedCount: number
  results: ValuationRefreshResult[]
}

export const SIGNAL_LABEL: Record<SignalLevel, string> = {
  Green: '绿灯 · 低估',
  Yellow: '黄灯 · 正常区间',
  Red: '红灯 · 高估暂停',
  NoData: '灰灯 · 无估值',
  Stable: '稳健 · 不定投',
}

export const FUND_TYPE_LABEL: Record<FundType, string> = {
  Stock: '股票型',
  Mixed: '混合型',
  Qdii: 'QDII',
  Bond: '债券型',
  Money: '货币型',
}

export const METRIC_LABEL: Record<string, string> = {
  PeTtm: 'PE-TTM',
  Pb: 'PB',
  EarningsYield: '盈利收益率',
}

// ---------- M3 买入流水 ----------

export interface BuyResult {
  id: number
  fundCode: string
  fundName: string
  tradeDate: string
  amount: number
  /** 入账份额（Confirmed=按 T 日净值；Pending=预估） */
  shares: number
  nav: number | null
  estimatedShares: number
  /** 是否已按 T 日净值确认 */
  confirmed: boolean
  summary: string
  note: string
}

export interface PendingTrade {
  id: number
  fundCode: string
  fundName: string
  tradeDate: string
  amount: number
  estimatedShares: number
}

export function isStableType(type: FundType): boolean {
  return type === 'Bond' || type === 'Money'
}

export interface TradeInfo {
  id: number
  fundCode: string
  fundName: string
  type: 'Buy' | 'Sell'
  amount: number
  tradeDate: string
  navDate: string
  unitNav: number | null
  shares: number | null
  status: 'Pending' | 'Confirmed' | 'Cancelled'
  note: string | null
  createdAt: string
}

// ===== M3 资产证明导入与对账 =====

export type ReconcileKind =
  | 'Matched'
  | 'ShareIncrease'
  | 'ShareDecrease'
  | 'NewFund'
  | 'MissingInFile'
  | 'ManualValueChange'
  | 'UnknownRow'

export type ReconcileAction =
  | 'None'
  | 'DividendReinvest'
  | 'ManualAdd'
  | 'KeepSystem'
  | 'AcceptFileValue'
  | 'CreateFund'

export interface ReconcileItem {
  code: string | null
  fileFundName: string
  fileShares: number | null
  fileMarketValue: number | null
  existsInSystem: boolean
  systemName: string | null
  systemType: FundType | null
  sectorId: number | null
  sectorName: string | null
  isManual: boolean
  systemShares: number | null
  systemCostAmount: number | null
  systemMarketValue: number | null
  unitNav: number | null
  kind: ReconcileKind
  action: ReconcileAction
  sharesDelta: number
  suggestedAddedCost: number | null
  proposedType: FundType | null
  allowedActions: ReconcileAction[]
  message: string
}

export interface SectorOption {
  id: number
  name: string
  isEquity: boolean
}

export interface ImportSummary {
  total: number
  matched: number
  differences: number
  newFunds: number
  manualValueChanges: number
}

export interface ImportPreview {
  token: string
  source: string
  fileName: string | null
  templateVersion: string
  proofDate: string | null
  warnings: string[]
  items: ReconcileItem[]
  sectors: SectorOption[]
  summary: ImportSummary
}

export interface CommitDecision {
  code: string | null
  action: ReconcileAction
  sectorId?: number | null
  fundType?: FundType | null
  addedCost?: number | null
}

export interface CommitResult {
  version: string
  source: string
  proofDate: string
  applied: number
  logs: string[]
  summary: ImportSummary
}

export interface PositionVersion {
  id: number
  version: string
  source: 'baseline' | 'import' | 'rollback'
  fileName: string | null
  proofDate: string
  createdAt: string
  isCurrent: boolean
  summary: string | null
  itemCount: number
}
