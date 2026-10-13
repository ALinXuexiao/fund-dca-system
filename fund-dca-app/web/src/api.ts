import type {
  Dashboard,
  FundListItem,
  RefreshReport,
  Decision,
  ValuationRefreshReport,
  BuyResult,
  PendingTrade,
  CommitDecision,
  CommitResult,
  ImportPreview,
  PositionVersion,
  Intraday,
} from './types'

/**
 * 统一的 fetch 包装：网络层失败（后端未启动/已停止/正在重启）时浏览器只会抛出
 * "Failed to fetch" 这类英文错误，这里换成可操作的中文提示，避免被误认为功能坏了。
 */
async function request(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  try {
    return await fetch(input, init)
  } catch (e) {
    const msg = e instanceof Error ? e.message : String(e)
    if (/failed to fetch|networkerror|load failed|err_connection|network request failed/i.test(msg)) {
      throw new Error('无法连接后端服务（可能已停止或仍在启动），请运行「一键启动.bat」后重试')
    }
    throw e instanceof Error ? e : new Error(msg)
  }
}

async function getJson<T>(url: string): Promise<T> {
  const res = await request(url)
  if (!res.ok) {
    throw new Error(`${url} 请求失败：HTTP ${res.status}`)
  }
  return (await res.json()) as T
}

export function fetchHealth(): Promise<{ status: string; time: string }> {
  return getJson('/health')
}

/** /health 一旦确认可用就置为 true，避免后续每次加载都多打一次健康检查 */
let backendReady = false

/**
 * 等待后端就绪。
 *
 * 启动脚本在前端开发服务器一就绪就打开浏览器，此时后端往往还在编译、执行 EF 迁移
 * 并连接云端 PostgreSQL（首次可能要十几秒，此时 /health 还不可用）。
 * 页面因此先渲染骨架，再由本函数静默轮询 /health：连上即自动填入数据，
 * 而不是把"连不上 /api"直接当成加载失败弹给用户。
 *
 * @param timeoutMs 最长等待毫秒数，超时返回 false
 * @param intervalMs 轮询间隔毫秒数
 */
export async function waitForBackend(timeoutMs = 60_000, intervalMs = 1_000): Promise<boolean> {
  if (backendReady) return true
  const deadline = Date.now() + timeoutMs
  for (;;) {
    try {
      await fetchHealth()
      backendReady = true
      return true
    } catch {
      // 后端还没起来（编译中 / 连库中），继续等
    }
    if (Date.now() >= deadline) return false
    await new Promise((resolve) => setTimeout(resolve, intervalMs))
  }
}

export function fetchFunds(): Promise<FundListItem[]> {
  return getJson('/api/funds')
}

export function fetchDashboard(): Promise<Dashboard> {
  return getJson('/api/dashboard')
}

/**
 * 盘中估算：跟踪指数实时涨跌幅 × 最新确认净值 → 估算净值/市值/今日盈亏。
 * 后端对指数行情缓存 30 秒，前端按 60 秒轮询即可。
 */
export function fetchIntraday(): Promise<Intraday> {
  return getJson('/api/dashboard/intraday')
}

/** 立即拉取全部基金最新净值（后端内部对单只基金静默重试 5 次）。 */
export async function refreshNavs(): Promise<RefreshReport> {
  const res = await request('/api/collect/refresh', { method: 'POST' })
  if (!res.ok) {
    throw new Error(`刷新请求失败：HTTP ${res.status}`)
  }
  return (await res.json()) as RefreshReport
}

/** 货币基金/手工市值标的手工维护市值。 */
export async function updateManualValue(code: string, marketValue: number, valueDate?: string) {
  const res = await request(`/api/funds/${code}/manual-value`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ marketValue, valueDate }),
  })
  if (!res.ok) {
    const msg = await res.text()
    throw new Error(msg || `HTTP ${res.status}`)
  }
  return res.json()
}

/** 今日定投决策（红绿灯 + 四条件判定 + 固定额建议）。 */
export function fetchDecision(): Promise<Decision> {
  return getJson('/api/decisions/today')
}

/** 更新全局"单次定投固定金额"（如 50 元，涨工资后改为 100 元）。 */
export async function updateFixedInvestAmount(fixedInvestAmount: number) {
  const res = await request('/api/decisions/settings', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ fixedInvestAmount }),
  })
  if (!res.ok) {
    throw new Error(`定投设置更新失败：HTTP ${res.status}`)
  }
  return (await res.json()) as { fixedInvestAmount: number }
}

/** 更新当月预算。 */
export async function updateBudget(budgetAmount: number, investedAmount: number) {
  const res = await request('/api/decisions/budget', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ budgetAmount, investedAmount }),
  })
  if (!res.ok) {
    throw new Error(`预算更新失败：HTTP ${res.status}`)
  }
  return res.json()
}

/** 拉取全部跟踪指数的最新估值（蛋卷）。 */
export function refreshValuations(): Promise<ValuationRefreshReport> {
  return postJson('/api/collect/refresh-valuations')
}

// ---------- M3 买入流水 ----------

/** 登记一笔买入（登记即预入账；T 日净值到位后自动校正）。 */
export async function recordBuy(code: string, amount: number): Promise<BuyResult> {
  const { res, json } = await postJsonRaw<BuyResult & { message?: string }>('/api/trades', {
    fundCode: code,
    amount,
  })
  if (!res.ok) {
    throw new Error(json?.message || `买入登记失败：HTTP ${res.status}`)
  }
  return json
}

/** 未确认（等 T 日净值）的买入流水，用于"待确认 ×N"角标与撤销列表。 */
export function fetchPendingTrades(): Promise<PendingTrade[]> {
  return getJson('/api/trades/pending')
}

/** 撤销一笔待确认买入（误记买入时反恢预入账，置 Cancelled）。 */
export async function cancelTrade(id: number): Promise<{ cancelled: boolean; id: number }> {
  const res = await request(`/api/trades/${id}/cancel`, { method: 'POST' })
  let json: { message?: string } | null = null
  try {
    json = (await res.json()) as { message?: string }
  } catch {
    json = null
  }
  if (!res.ok) {
    throw new Error(json?.message || `撤销失败：HTTP ${res.status}`)
  }
  return json as unknown as { cancelled: boolean; id: number }
}

// ---------- M3 资产证明导入与对账 ----------

export async function parseDemoStatement(): Promise<ImportPreview> {
  return postJson<ImportPreview>('/api/import/parse-demo', {})
}

export async function parseStatement(file: File): Promise<ImportPreview> {
  const form = new FormData()
  form.append('file', file)
  const res = await request('/api/import/parse', { method: 'POST', body: form })
  const json = (await res.json().catch(() => null)) as { message?: string } | null
  if (!res.ok) {
    throw new Error(json?.message ?? `解析失败（HTTP ${res.status}）`)
  }
  return json as unknown as ImportPreview
}

export async function commitImport(
  token: string,
  decisions: CommitDecision[],
  proofDate?: string | null,
): Promise<CommitResult> {
  const { res, json } = await postJsonRaw<CommitResult & { message?: string }>('/api/import/commit', {
    token,
    proofDate: proofDate ?? null,
    decisions,
  })
  if (!res.ok) {
    throw new Error(json?.message ?? `导入失败：HTTP ${res.status}`)
  }
  return json
}

export function fetchVersions(): Promise<PositionVersion[]> {
  return getJson('/api/import/versions')
}

export async function rollbackVersion(versionId: number): Promise<CommitResult> {
  const res = await request(`/api/import/versions/${versionId}/rollback`, { method: 'POST' })
  const json = (await res.json().catch(() => null)) as { message?: string } | null
  if (!res.ok) {
    throw new Error(json?.message ?? '回滚失败')
  }
  return json as unknown as CommitResult
}

export const statementTemplateUrl = '/api/import/template'

async function postJson<T>(url: string, body?: unknown): Promise<T> {
  const { res, json } = await postJsonRaw<T>(url, body)
  if (!res.ok) {
    throw new Error(`${url} 请求失败：HTTP ${res.status}`)
  }
  return json
}

/** POST 并返回原始 Response，便于调用方读取错误 message。 */
async function postJsonRaw<T>(url: string, body?: unknown): Promise<{ res: Response; json: T }> {
  const res = await request(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body ?? {}),
  })
  let json: T
  try {
    json = (await res.json()) as T
  } catch {
    json = {} as T
  }
  return { res, json }
}
