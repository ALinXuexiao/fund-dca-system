<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import * as echarts from 'echarts'
import {
  fetchDashboard,
  refreshNavs,
  updateManualValue,
  fetchDecision,
  refreshValuations,
  updateBudget,
  updateFixedInvestAmount,
  recordBuy,
  fetchPendingTrades,
  cancelTrade,
  fetchIntraday,
} from '../api'
import {
  FUND_TYPE_LABEL,
  isStableType,
  SIGNAL_LABEL,
  type Dashboard,
  type DashboardRow,
  type Decision,
  type FundDecision,
  type Intraday,
  type IntradayRow,
  type PendingTrade,
  type RefreshResult,
} from '../types'

const loading = ref(true)
const errorMsg = ref('')
const data = ref<Dashboard | null>(null)
const decision = ref<Decision | null>(null)
const refreshing = ref(false)
const valRefreshing = ref(false)

// 盘中估算（跟踪指数实时行情推算，不落库）：开关打开后每 60 秒自动刷新
const intradayOn = ref(false)
const intraday = ref<Intraday | null>(null)
const intradayLoading = ref(false)
/** 前端轮询间隔；后端指数行情缓存 30 秒，故 60 秒足够 */
const INTRADAY_REFRESH_MS = 60_000
let intradayTimer: number | undefined

const failures = ref<RefreshResult[] | null>(null)
const valFailures = ref<{ indexCode: string; indexName: string; error: string | null }[] | null>(null)
const toast = ref('')
const toastErr = ref(false)
let toastTimer: number | undefined

const donutEl = ref<HTMLDivElement>()
const barEl = ref<HTMLDivElement>()
let donutChart: echarts.ECharts | null = null
let barChart: echarts.ECharts | null = null

// 货币基金手工市值内联编辑
const editingCode = ref<string | null>(null)
const draftValue = ref('')

// 预算内联编辑
const editingBudget = ref(false)
const draftBudget = ref('')
const draftInvested = ref('')

// 单次定投固定金额（全局设置，内联编辑）
const editingFixed = ref(false)
const draftFixed = ref('')

// 记一笔买入（登记即预入账）
const buyingCode = ref<string | null>(null)
const draftBuy = ref('')
const buying = ref(false)
const pendingCount = ref(0)
const pendingTrades = ref<PendingTrade[]>([])
const showPending = ref(false)
const cancelling = ref<number | null>(null)

const SECTOR_COLORS: Record<string, string> = {
  大盘: '#2f6fed', 中盘: '#3aa6e8', 小盘: '#7ed0f5',
  价值: '#8e5bb5', 成长: '#e8743b', 红利: '#c0392b',
  消费: '#d68910', 港股科技: '#6f42c1',
  债券: '#a08a5e', 货币: '#b9b29c',
}

function showToast(msg: string, err = false) {
  toast.value = msg
  toastErr.value = err
  window.clearTimeout(toastTimer)
  toastTimer = window.setTimeout(() => (toast.value = ''), 5000)
}

async function load(silent = false) {
  if (!silent) {
    loading.value = true
    errorMsg.value = ''
  }
  try {
    const [dash, dec] = await Promise.all([fetchDashboard(), fetchDecision().catch(() => null)])
    data.value = dash
    decision.value = dec
    await loadPendingTrades()
  } catch (e) {
    errorMsg.value = (e as Error).message
  } finally {
    if (!silent) {
      loading.value = false
    }
  }
}

async function onRefresh() {
  refreshing.value = true
  try {
    const report = await refreshNavs()
    await load(true)
    if (report.failedCount > 0) {
      failures.value = report.results.filter((r) => !r.success)
    } else {
      showToast(`净值刷新成功：${report.successCount} 只基金已更新至最新交易日`)
    }
  } catch (e) {
    showToast((e as Error).message, true)
  } finally {
    refreshing.value = false
  }
}

function startEdit(row: DashboardRow) {
  editingCode.value = row.code
  draftValue.value = String(row.marketValue || '')
}

async function saveManual(row: DashboardRow) {
  const v = Number(draftValue.value)
  if (!Number.isFinite(v) || v < 0) {
    showToast('请输入正确的市值金额', true)
    return
  }
  try {
    await updateManualValue(row.code, Math.round(v * 100) / 100)
    editingCode.value = null
    await load(true)
    showToast('货币基金市值已更新，分母 D 已重算')
  } catch (e) {
    showToast((e as Error).message, true)
  }
}

// 决策面板：只展示权益类（稳健类不参与定投）
const decisionRows = computed(() =>
  (decision.value?.funds ?? []).filter((f) => f.signal !== 'Stable'),
)

async function onRefreshValuations() {
  valRefreshing.value = true
  try {
    const report = await refreshValuations()
    await load(true)
    if (report.failedCount > 0) {
      valFailures.value = report.results
        .filter((r) => !r.success)
        .map((r) => ({ indexCode: r.indexCode, indexName: r.indexName, error: r.error }))
    } else {
      showToast(`估值刷新成功：${report.successCount} 个指数已更新（${decision.value?.valuationDate ?? ''}）`)
    }
  } catch (e) {
    showToast((e as Error).message, true)
  } finally {
    valRefreshing.value = false
  }
}

// ---------- 盘中估算 ----------

async function loadIntraday(silent = false) {
  intradayLoading.value = true
  try {
    intraday.value = await fetchIntraday()
  } catch (e) {
    if (silent) {
      console.warn('[intraday] 自动刷新失败', e)
    } else {
      showToast(`盘中估算失败：${(e as Error).message}`, true)
    }
  } finally {
    intradayLoading.value = false
  }
}

async function toggleIntraday() {
  if (intradayOn.value) {
    intradayOn.value = false
    intraday.value = null
    window.clearInterval(intradayTimer)
    intradayTimer = undefined
    return
  }
  intradayOn.value = true
  await loadIntraday()
  intradayTimer = window.setInterval(() => void loadIntraday(true), INTRADAY_REFRESH_MS)
}

const intradayByCode = computed(() => {
  const map = new Map<string, IntradayRow>()
  for (const r of intraday.value?.rows ?? []) {
    map.set(r.fundCode, r)
  }
  return map
})

/** 某只基金的盘中估算行；未开启盘中模式或不可估算时为 null */
function est(row: DashboardRow): IntradayRow | null {
  return intradayByCode.value.get(row.code) ?? null
}

/** 盘中估算今日盈亏：仅累加可估算的行 */
const estDayPnl = computed(() =>
  (intraday.value?.rows ?? []).reduce((s, r) => s + (r.estimatedDayPnl ?? 0), 0),
)

/** 全仓估算市值：可估算的用估算值，其余（债券/货币/无跟踪指数）沿用盘后市值 */
const estMarketValue = computed(() => {
  if (!data.value) return 0
  return data.value.rows.reduce(
    (s, row) => s + (intradayByCode.value.get(row.code)?.estimatedMarketValue ?? row.marketValue),
    0,
  )
})

function hasEst(row: DashboardRow): boolean {
  return est(row)?.estimatedNav != null
}

function estPct(row: DashboardRow): number | null {
  return est(row)?.indexChangePercent ?? null
}

function estNavText(row: DashboardRow): string {
  const e = est(row)
  return e?.estimatedNav != null ? e.estimatedNav.toFixed(4) : '—'
}

/** 明细列悬浮说明：用了哪个指数、是否代理、基准净值日期 */
function estTitle(row: DashboardRow): string {
  const e = est(row)
  if (!e) return ''
  if (e.reason) return `不可估算：${e.reason}`
  const label = `${e.indexName ?? ''}${e.indexCode ? `（${e.indexCode}）` : ''}`
  const via = e.viaProxy ? '代理指数：本基金跟踪指数东财无行情，改用档案代理指数推算 ' : '跟踪指数 '
  return `${via}${label} ${fmtPct(e.indexChangePercent)} · 基准净值 ${e.lastNavDate ?? '—'}`
}

/** 用代理指数推算的基金数：这些行的估算误差可能较大，需在卡片条上显式提示 */
const proxyCount = computed(() => (intraday.value?.rows ?? []).filter((r) => r.viaProxy).length)

function startBudgetEdit() {
  if (!data.value?.budget) return
  draftBudget.value = String(data.value.budget.budgetAmount)
  draftInvested.value = String(data.value.budget.investedAmount)
  editingBudget.value = true
}

async function saveBudget() {
  const budget = Number(draftBudget.value)
  const invested = Number(draftInvested.value)
  if (!Number.isFinite(budget) || !Number.isFinite(invested) || budget < 0 || invested < 0 || invested > budget) {
    showToast('预算不能为负，且已投额不能超过预算额', true)
    return
  }
  try {
    await updateBudget(budget, invested)
    editingBudget.value = false
    await load(true)
    showToast('当月预算已更新，建议金额已重算')
  } catch (e) {
    showToast((e as Error).message, true)
  }
}

function startFixedEdit() {
  if (!decision.value) return
  draftFixed.value = String(decision.value.options.fixedInvestAmount)
  editingFixed.value = true
}

async function saveFixed() {
  const amount = Number(draftFixed.value)
  if (!Number.isFinite(amount) || amount <= 0 || amount > 100000) {
    showToast('单次定投金额需在 0 ~ 100000 元之间', true)
    return
  }
  try {
    await updateFixedInvestAmount(Math.round(amount * 100) / 100)
    editingFixed.value = false
    await load(true)
    showToast('单次定投金额已保存，今日建议已重算')
  } catch (e) {
    showToast((e as Error).message, true)
  }
}

function startBuy(f: FundDecision | { code: string; name: string }) {
  buyingCode.value = f.code
  const suggested = 'suggestedAmount' in f ? f.suggestedAmount : 0
  draftBuy.value = String(suggested || decision.value?.options.fixedInvestAmount || 50)
}

async function confirmBuy(f: FundDecision | { code: string; name: string }) {
  const amount = Number(draftBuy.value)
  if (!Number.isFinite(amount) || amount <= 0) {
    showToast('请输入正确的买入金额', true)
    return
  }
  buying.value = true
  try {
    const r = await recordBuy(f.code, Math.round(amount * 100) / 100)
    buyingCode.value = null
    await load(true)
    showToast(
      r.confirmed
        ? `${f.name} 已登记买入 ¥${fmtMoney(r.amount)}（按 T 日净值 ${r.nav} 确认）`
        : `${f.name} 已登记买入 ¥${fmtMoney(r.amount)}，今晚净值到位后按 T 日收盘价确认份额`,
    )
  } catch (e) {
    showToast((e as Error).message, true)
  } finally {
    buying.value = false
  }
}

async function loadPendingTrades() {
  try {
    pendingTrades.value = await fetchPendingTrades()
    pendingCount.value = pendingTrades.value.length
  } catch {
    pendingTrades.value = []
    pendingCount.value = 0
  }
}

function openPending() {
  showPending.value = true
  void loadPendingTrades()
}

async function confirmCancel(t: PendingTrade) {
  cancelling.value = t.id
  try {
    await cancelTrade(t.id)
    await loadPendingTrades()
    await load(true)
    showToast(`已撤销 ${t.fundName} 的买入登记（¥${fmtMoney(t.amount)}），预入账已反恢`)
  } catch (e) {
    showToast((e as Error).message, true)
  } finally {
    cancelling.value = null
  }
}

function signalClass(s: string): string {
  return `sig-${s.toLowerCase()}`
}

// 按赛道分组（后端已按赛道排序）
const groups = computed(() => {
  if (!data.value) return []
  const map = new Map<string, { name: string; isEquity: boolean; sortOrder: number; rows: DashboardRow[] }>()
  for (const r of data.value.rows) {
    const key = r.sectorName ?? '未分类'
    if (!map.has(key)) {
      map.set(key, { name: key, isEquity: r.sectorIsEquity, sortOrder: r.sectorSortOrder, rows: [] })
    }
    map.get(key)!.rows.push(r)
  }
  return [...map.values()].sort((a, b) => a.sortOrder - b.sortOrder)
})

function pctClass(v: number | null | undefined): string {
  if (v == null || v === 0) return ''
  return v > 0 ? 'up' : 'down'
}
function fmtPct(v: number | null | undefined, digits = 2): string {
  if (v == null) return '—'
  return `${v > 0 ? '+' : ''}${v.toFixed(digits)}%`
}
function fmtMoney(v: number | null | undefined): string {
  if (v == null) return '—'
  return v.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}
function weightClass(r: DashboardRow): string {
  if (!r.sectorIsEquity) return ''
  return r.weightPercent >= 10 ? 'over-limit' : ''
}
function manualStale(row: DashboardRow): boolean {
  if (!row.manualValueDate) return false
  const days = (Date.now() - new Date(row.manualValueDate).getTime()) / 86400000
  return days > 7
}

function renderCharts() {
  if (!data.value) {
    return
  }
  // 容器可能因 v-if 分支切换被重建（如刷新时 loading 闪屏）；
  // 旧实例绑定的 DOM 已脱离文档，需销毁后在新容器上重建
  if (donutChart && donutEl.value && donutChart.getDom() !== donutEl.value) {
    donutChart.dispose()
    donutChart = null
  }
  if (barChart && barEl.value && barChart.getDom() !== barEl.value) {
    barChart.dispose()
    barChart = null
  }
  if (!donutEl.value || !barEl.value) {
    console.warn('[charts] 容器 ref 未就绪', { donut: !!donutEl.value, bar: !!barEl.value })
    return
  }
  try {
    renderDonut()
    renderBar()
  } catch (e) {
    console.error('[charts] ECharts 初始化失败', e)
  }
}

function renderDonut() {
  if (!data.value || !donutEl.value) return
  donutChart ??= echarts.init(donutEl.value)
  donutChart.setOption({
    tooltip: {
      trigger: 'item',
      formatter: (p: { name: string; value: number; percent: number }) =>
        `${p.name}<br/>市值 ${fmtMoney(p.value)}（${p.percent.toFixed(2)}%）`,
    },
    legend: { bottom: 0, itemWidth: 10, itemHeight: 10, textStyle: { fontSize: 11, color: '#4b5563' } },
    color: data.value.sectors.map((s) => SECTOR_COLORS[s.name] ?? '#9aa3af'),
    series: [
      {
        type: 'pie',
        radius: ['46%', '72%'],
        center: ['50%', '44%'],
        avoidLabelOverlap: true,
        itemStyle: { borderColor: '#fff', borderWidth: 2 },
        label: { show: false },
        data: data.value.sectors.map((s) => ({ name: s.name, value: s.marketValue })),
      },
    ],
  })
}

function renderBar() {
  if (!data.value || !barEl.value) return
  // 单只权益基金占比条形图（B3 红线 10%）
  const equityRows = [...data.value.rows]
    .filter((r) => r.sectorIsEquity && !isStableType(r.type))
    .sort((a, b) => b.weightPercent - a.weightPercent)
  barChart ??= echarts.init(barEl.value)
  barChart.setOption({
    grid: { left: 150, right: 40, top: 24, bottom: 28 },
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      formatter: (p: { name: string; value: number }[]) =>
        `${p[0].name}<br/>占比 <b>${p[0].value.toFixed(2)}%</b>（红线 10%）`,
    },
    xAxis: { type: 'value', max: 18, axisLabel: { formatter: '{value}%', color: '#9aa3af' }, splitLine: { lineStyle: { color: '#f0f1f3' } } },
    yAxis: {
      type: 'category',
      inverse: true,
      data: equityRows.map((r) => r.name.replace(/(ETF联接|指数|增强|A|\(LOF\)|\(QDII\))/g, '').slice(0, 12)),
      axisLabel: { color: '#4b5563', fontSize: 11.5 },
      axisLine: { show: false },
      axisTick: { show: false },
    },
    series: [
      {
        type: 'bar',
        barWidth: 13,
        data: equityRows.map((r) => ({
          value: r.weightPercent,
          itemStyle: {
            color: r.weightPercent >= 10 ? '#c0392b' : SECTOR_COLORS[r.sectorName ?? ''] ?? '#2f6fed',
            borderRadius: [0, 3, 3, 0],
          },
        })),
        label: { show: true, position: 'right', formatter: '{c}%', fontSize: 11, color: '#4b5563' },
        markLine: {
          symbol: 'none',
          lineStyle: { color: '#c0392b', type: 'dashed', width: 1.5 },
          label: { formatter: 'B3 红线 10%', color: '#c0392b', fontSize: 11, position: 'end' },
          data: [{ xAxis: 10 }],
        },
      },
    ],
  })
}

function onResize() {
  donutChart?.resize()
  barChart?.resize()
}

onMounted(async () => {
  await load()
  window.addEventListener('resize', onResize)
})
onBeforeUnmount(() => {
  window.removeEventListener('resize', onResize)
  window.clearInterval(intradayTimer)
  donutChart?.dispose()
  barChart?.dispose()
})
// 数据变化且 v-if 分支（loading=false）patch 完成后再渲染图表；
// flush:'post' 确保此时图表容器已挂载、ref 已绑定。
// 首次加载时 data 先赋值、loading 后置 false（中间还 await 了 pendingTrades 请求），
// 若只 watch data，回调执行时容器尚未挂载；故需同时 watch loading，待其变 false 后再渲染
watch(
  [data, loading],
  () => {
    if (!data.value || loading.value) return
    nextTick(() => requestAnimationFrame(() => requestAnimationFrame(renderCharts)))
  },
  { flush: 'post' },
)
</script>

<template>
  <div v-if="loading" class="hint-msg">正在加载盘后看板…</div>

  <div v-else-if="errorMsg" class="error-box">
    <b>看板加载失败：</b>{{ errorMsg }}
    <div style="margin-top:6px;color:#a66;">正式版此处将执行"静默重试 5 次，仍失败弹窗 + 系统通知"。</div>
    <button class="btn" @click="() => load()">重试</button>
  </div>

  <template v-else-if="data">
    <!-- 工具条 -->
    <div class="toolbar">
      <div class="asof">
        净值日期：<b>{{ data.asOfDate ?? '—' }}</b>（盘后正式净值）
        <span v-if="data.containsSeedData" class="badge badge-seed">演示数据 · 未采集真实净值</span>
      </div>
      <button class="btn ghost" :disabled="valRefreshing" @click="onRefreshValuations">
        <span v-if="valRefreshing" class="spinner"></span>{{ valRefreshing ? '正在采集指数估值…' : '刷新估值' }}
      </button>
      <button class="btn ghost" :class="{ on: intradayOn }" :disabled="intradayLoading" @click="toggleIntraday">
        {{ intradayOn ? (intradayLoading ? '盘中估算更新中…' : '盘中估算：开（60 秒自动）') : '盘中估算：关' }}
      </button>
      <button class="btn" :disabled="refreshing" @click="onRefresh">
        <span v-if="refreshing" class="spinner"></span>{{ refreshing ? '正在采集天天基金净值…' : '刷新今日净值' }}
      </button>
    </div>

    <!-- 指标卡 -->
    <div class="cards">
      <div class="card">
        <div class="label">持仓总市值（分母 D）</div>
        <div class="value">¥{{ fmtMoney(data.denominator) }}</div>
        <div class="sub2">权益 {{ fmtMoney(data.equityMarketValue) }} · 稳健 {{ fmtMoney(data.stableMarketValue) }}（含债券/货币）</div>
      </div>
      <div class="card k-pnl">
        <div class="label">今日盈亏（股票/QDII）</div>
        <div class="value" :class="pctClass(data.dayPnl)">{{ data.dayPnl >= 0 ? '+' : '' }}¥{{ fmtMoney(data.dayPnl) }}</div>
        <div class="sub2">货币基金不计；QDII 滞后行不计</div>
      </div>
      <div class="card k-equity">
        <div class="label">权益累计收益</div>
        <template v-if="data.equityPnl != null">
          <div class="value" :class="pctClass(data.equityPnl)">
            {{ data.equityPnl >= 0 ? '+' : '' }}¥{{ fmtMoney(data.equityPnl) }}
            <span style="font-size:16px">({{ fmtPct(data.equityPnlPercent) }})</span>
          </div>
          <div class="sub2">本金 ¥{{ fmtMoney(data.equityCost) }}（当前持仓周期）</div>
        </template>
        <template v-else>
          <div class="value" style="color:var(--muted)">—</div>
          <div class="sub2">资产证明不含成本，导入交易流水后显示累计收益</div>
        </template>
      </div>
      <div class="card k-budget">
        <div class="label">银行卡待投预算</div>
        <template v-if="!editingBudget">
          <div class="value" style="color:var(--down)">¥{{ fmtMoney(data.budget?.remainingAmount) }}</div>
          <div class="sub2" v-if="data.budget">
            {{ data.budget.yearMonth }} 预算 ¥{{ fmtMoney(data.budget.budgetAmount) }}，已投 ¥{{ fmtMoney(data.budget.investedAmount) }}
            <button class="btn sm ghost" style="margin-left:6px" @click="startBudgetEdit">修改</button>
          </div>
        </template>
        <template v-else>
          <div style="display:flex;gap:6px;margin:6px 0;align-items:center;flex-wrap:wrap">
            <label style="font-size:12px;color:var(--muted)">预算 <input v-model="draftBudget" class="num" type="number" min="0" style="width:80px" /></label>
            <label style="font-size:12px;color:var(--muted)">已投 <input v-model="draftInvested" class="num" type="number" min="0" style="width:80px" /></label>
          </div>
          <div>
            <button class="btn sm" @click="saveBudget">保存</button>
            <button class="btn sm ghost" style="margin-left:4px" @click="editingBudget = false">取消</button>
          </div>
        </template>
      </div>
    </div>

    <!-- 盘中估算条：跟踪指数实时涨跌幅 × 最新确认净值，非实际净值 -->
    <div v-if="intradayOn && intraday" class="intraday-bar" :class="{ stale: intraday.quoteStale }">
      <div class="ib-item">
        <div class="label">盘中估算今日盈亏</div>
        <div class="value" :class="pctClass(estDayPnl)">{{ estDayPnl >= 0 ? '+' : '' }}¥{{ fmtMoney(estDayPnl) }}</div>
      </div>
      <div class="ib-item">
        <div class="label">全仓估算市值</div>
        <div class="value">¥{{ fmtMoney(estMarketValue) }}</div>
      </div>
      <div class="ib-meta">
        <div>
          指数行情：<b>{{ intraday.quotedAt ?? '—' }}</b>
          <span v-if="intraday.quoteStale" class="badge badge-stale">行情未更新（休市/午休或数据源异常）</span>
        </div>
        <div>可估算 {{ intraday.coveredCount }} 只 · 不可估算 {{ intraday.unavailableCount }} 只（见明细「盘中估算」列悬浮说明）</div>
        <div v-if="proxyCount > 0" class="warn-line">
          其中 {{ proxyCount }} 只用的是档案里的代理指数（本基金跟踪指数东财无行情），误差可能较大，请以实际净值为准
        </div>
        <div class="disc">
          口径：跟踪指数实时涨跌幅 × 最新确认净值推算，非实际净值。指数基金贴合度高；指数增强/联接有跟踪误差，
          QDII 叠加汇率与净值滞后，仅供参考，实际以基金公司披露净值为准。
        </div>
      </div>
    </div>

    <!-- 图表 -->
    <div class="charts">
      <div class="chart-card">
        <h3>赛道占比（含稳健资产）</h3>
        <div class="hint">债券/货币以灰褐色区分，计入 D 但不参与 B4</div>
        <div ref="donutEl" class="chart-box"></div>
      </div>
      <div class="chart-card">
        <h3>单只基金占比（B3 红线 10%，严格小于）</h3>
        <div class="hint">超线基金红色显示，定投判定不通过</div>
        <div ref="barEl" class="chart-box"></div>
      </div>
    </div>

    <!-- 今日定投建议：四条件同时满足才投固定额 -->
    <div class="panel" v-if="decision">
      <div class="panel-head panel-head-col">
        <h3>今日定投建议（四条件同时满足才投固定额）</h3>
        <div class="head-meta">
          <span>估值日期 {{ decision.valuationDate ?? '—' }}</span>
          <span class="meta-sep">·</span>
          <template v-if="!editingFixed">
            <span>单次定投 <b>¥{{ fmtMoney(decision.options.fixedInvestAmount) }}</b>/只</span>
            <button class="btn sm ghost" style="margin-left:4px;padding:2px 10px" @click="startFixedEdit">修改</button>
          </template>
          <template v-else>
            <label style="font-size:12px;color:var(--muted)">单次定投
              <input v-model="draftFixed" class="num" type="number" min="1" step="1" style="width:72px;margin:0 4px" />元
            </label>
            <button class="btn sm" style="padding:2px 10px" @click="saveFixed">保存</button>
            <button class="btn sm ghost" style="margin-left:4px;padding:2px 10px" @click="editingFixed = false">取消</button>
          </template>
          <span class="meta-sep">·</span>
          <span>合计建议 <b style="color:var(--up)">¥{{ fmtMoney(decision.totalSuggested) }}</b></span>
          <span class="meta-sep">·</span>
          <span>投后预算剩余 ¥{{ fmtMoney(decision.remainingAfter) }}</span>
          <span v-if="pendingCount > 0" class="pending-badge" @click="openPending" title="点击查看/撤销待确认的买入登记">
            待确认买入 ×{{ pendingCount }}（点击管理）
          </span>
        </div>
      </div>
      <table class="decision-table">
        <colgroup>
          <col style="width:64px" />
          <col />
          <col style="width:86px" />
          <col style="width:78px" />
          <col style="width:78px" />
          <col style="width:60px" />
          <col style="width:92px" />
          <col style="width:124px" />
          <col style="width:300px" />
        </colgroup>
        <thead>
          <tr>
            <th>信号</th>
            <th class="l">基金 / 跟踪指数</th>
            <th>总收益率<span class="th-sub">条件②</span></th>
            <th>单只占比<span class="th-sub">&lt;10% ③</span></th>
            <th>赛道占比<span class="th-sub">&lt;15% ④</span></th>
            <th>口径</th>
            <th>估值水平<span class="th-sub">低估 ①</span></th>
            <th style="text-align:center">今日建议</th>
            <th class="l">红线与说明</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="f in decisionRows" :key="f.code" :class="{ rowzero: f.suggestedAmount === 0 }">
            <td><span class="dot" :class="signalClass(f.signal)"></span>{{ SIGNAL_LABEL[f.signal].split(' ')[0] }}</td>
            <td class="fname l">
              {{ f.name }}
              <span class="code">
                {{ f.code }} · {{ f.sectorName ?? '—' }} · {{ f.indexName ?? '无跟踪指数' }}
              </span>
            </td>
            <td :class="f.totalReturnPercent == null ? '' : f.totalReturnPercent < 0 ? 'down' : 'up'">
              {{ fmtPct(f.totalReturnPercent) }}
            </td>
            <td :class="f.weightPercent >= 10 ? 'over-limit' : ''">{{ f.weightPercent.toFixed(2) }}%</td>
            <td :class="f.sectorWeightPercent >= 15 ? 'over-limit' : ''">{{ f.sectorWeightPercent.toFixed(2) }}%</td>
            <td>{{ f.metric === 'PeTtm' ? 'PE' : f.metric === 'Pb' ? 'PB' : f.metric === 'EarningsYield' ? 'E/P' : '—' }}</td>
            <td>
              <span v-if="f.metric === 'EarningsYield' && f.metricValue != null" style="font-weight:600">{{ f.metricValue.toFixed(2) }}%</span>
              <span v-else-if="f.percentile != null" style="font-weight:600">{{ f.percentile.toFixed(1) }}%</span>
              <span v-else style="color:var(--muted)">无数据</span>
            </td>
            <td style="text-align:center">
              <template v-if="buyingCode === f.code">
                <input v-model="draftBuy" class="num" type="number" min="1" step="1"
                  style="width:64px;text-align:right" />
                <div style="display:flex;gap:4px;margin-top:4px">
                  <button class="btn sm buy-btn" :disabled="buying" @click="confirmBuy(f)">记买入</button>
                  <button class="btn sm ghost" style="padding:2px 8px" @click="buyingCode = null">取消</button>
                </div>
              </template>
              <template v-else>
                <b :style="{ color: f.suggestedAmount > 0 ? 'var(--up)' : 'var(--muted)' }">
                  {{ f.suggestedAmount > 0 ? '¥' + fmtMoney(f.suggestedAmount) : '¥0 建议' }}
                </b>
                <button class="btn sm ghost buy-btn"
                  style="display:block;margin-top:4px" @click="startBuy(f)">记一笔买入</button>
              </template>
            </td>
            <td class="l wrap">
              <div v-if="f.blockers.length" class="blockers">{{ f.blockers.join('；') }}</div>
              <div v-if="f.notes.length" class="notes">{{ f.notes.join('；') }}</div>
            </td>
          </tr>
        </tbody>
      </table>
      <div class="rule-hint">
        规则：① 指数低估（PE/PB 看历史百分位 &lt; 30%；盈利收益率看 E/P ≥ 10%，≤ 6.4% 为高估）② 总收益率为负 ③ 单只市值占比 &lt; 10% ④ 所属赛道占比 &lt; 15%，
        四条同时满足本周才定投 ¥{{ fmtMoney(decision.options.fixedInvestAmount) }}，任一不满足即不操作；全部建议合计不超过当月剩余预算。
      </div>
    </div>

    <!-- 估值刷新失败弹窗 -->
    <div v-if="valFailures" class="modal-mask" @click.self="valFailures = null">
      <div class="modal">
        <header>指数估值刷新部分失败</header>
        <div class="body">
          <p style="margin:0 0 8px;color:var(--ink-2);font-size:13px">
            以下指数本次未取到估值（代理指数也无数据），相关基金将显示灰灯：
          </p>
          <div v-for="f in valFailures" :key="f.indexCode" class="fail-row">
            <code>{{ f.indexCode }}</code>
            <span>{{ f.indexName }} · {{ f.error ?? '无数据' }}</span>
          </div>
        </div>
        <footer>
          <button class="btn" @click="valFailures = null">我知道了</button>
        </footer>
      </div>
    </div>

    <!-- 待确认买入管理弹窗 -->
    <div v-if="showPending" class="modal-mask" @click.self="showPending = false">
      <div class="modal">
        <header>待确认买入（今晚净值到位后自动按 T 日收盘价确认份额）</header>
        <div class="body">
          <template v-if="pendingTrades.length">
            <p style="margin:0 0 10px;color:var(--ink-2);font-size:13px">
              下面这些是先登记、尚未按当日净值确认的买入。如属误记，可撤销（会同步反恢本金与预算预占）。
            </p>
            <div class="pending-list">
              <div v-for="t in pendingTrades" :key="t.id" class="pending-row">
                <div class="pending-info">
                  <span class="pending-fund">{{ t.fundName }}</span>
                  <code>{{ t.fundCode }}</code>
                  <span class="pending-meta">{{ t.tradeDate }} · ¥{{ fmtMoney(t.amount) }} · 预占 {{ t.estimatedShares }} 份</span>
                </div>
                <button class="btn sm danger" :disabled="cancelling === t.id" @click="confirmCancel(t)">
                  {{ cancelling === t.id ? '撤销中…' : '撤销' }}
                </button>
              </div>
            </div>
          </template>
          <p v-else style="margin:4px 0;color:var(--muted)">当前没有待确认的买入。</p>
        </div>
        <footer>
          <button class="btn" @click="showPending = false">关闭</button>
        </footer>
      </div>
    </div>

    <!-- 持仓明细（赛道第一列分组） -->
    <div class="panel">
      <div class="panel-head">
        <h3>持仓明细（按赛道分组）</h3>
        <span style="font-size:12px;color:var(--muted)">份额 × 单位净值 = 市值 · 红涨绿跌</span>
      </div>
      <table>
        <colgroup>
          <col style="width:70px" />
          <col />
          <col style="width:92px" />
          <col style="width:86px" />
          <col style="width:74px" />
          <col style="width:96px" />
          <col style="width:96px" />
          <col style="width:104px" />
          <col style="width:74px" />
          <col style="width:96px" />
          <col style="width:112px" />
        </colgroup>
        <thead>
          <tr>
            <th>赛道</th>
            <th class="l">基金</th>
            <th>净值日期</th>
            <th>单位净值</th>
            <th>日涨跌</th>
            <th>盘中估算<span class="th-sub">指数推算</span></th>
            <th>份额</th>
            <th>当前市值</th>
            <th>占比 D</th>
            <th>累计收益</th>
            <th style="text-align:center">操作</th>
          </tr>
        </thead>
        <tbody v-for="g in groups" :key="g.name" :class="{ stable: !g.isEquity }">
          <tr v-for="(r, i) in g.rows" :key="r.code">
            <td v-if="i === 0" class="sector-cell" :rowspan="g.rows.length">{{ g.name }}</td>
            <td class="fname l">
              {{ r.name }}
              <span class="code">{{ r.code }} · {{ FUND_TYPE_LABEL[r.type] }}</span>
            </td>
            <td>
              {{ r.isMoney ? '手工维护' : (r.navDate ?? '—') }}
              <span v-if="r.stale" class="badge badge-stale">QDII 滞后</span>
            </td>
            <td>{{ r.unitNav != null ? r.unitNav.toFixed(4) : '—' }}</td>
            <td :class="pctClass(r.dayChangePercent)">{{ r.isMoney ? '—' : fmtPct(r.dayChangePercent) }}</td>
            <td :title="estTitle(r)">
              <template v-if="hasEst(r)">
                <span :class="pctClass(estPct(r))">{{ fmtPct(estPct(r)) }}</span>
                <div class="est-nav">{{ estNavText(r) }}</div>
              </template>
              <span v-else style="color:var(--muted)">{{ intradayOn ? '—' : '' }}</span>
            </td>
            <td>{{ r.shares != null ? r.shares.toLocaleString('zh-CN', { maximumFractionDigits: 2 }) : '—' }}</td>
            <td>
              <template v-if="r.isMoney && editingCode === r.code">
                <input v-model="draftValue" class="num" type="number" min="0" step="0.01" style="width:88px"
                       @keyup.enter="saveManual(r)" />
                <button class="btn sm" style="margin-left:4px;padding:3px 10px" @click="saveManual(r)">保存</button>
                <button class="btn sm ghost" style="margin-left:2px;padding:3px 10px" @click="editingCode = null">取消</button>
              </template>
              <template v-else>
                {{ fmtMoney(r.marketValue) }}
                <div v-if="r.isMoney && manualStale(r)" style="color:var(--warn);font-size:11px;margin-top:2px">
                  已超过 7 天未更新（{{ r.manualValueDate }}）
                </div>
              </template>
            </td>
            <td :class="weightClass(r)">{{ r.weightPercent.toFixed(2) }}%</td>
            <td :class="pctClass(r.totalReturnPercent)">
              {{ r.isMoney ? '—' : fmtPct(r.totalReturnPercent) }}
            </td>
            <td style="text-align:center">
              <template v-if="!r.isMoney">
                <template v-if="buyingCode === r.code">
                  <input v-model="draftBuy" class="num" type="number" min="1" step="1"
                         style="width:60px;text-align:right" />
                  <button class="btn sm buy-btn" style="margin-left:4px;padding:3px 8px" :disabled="buying"
                          @click="confirmBuy(r)">记买入</button>
                  <button class="btn sm ghost" style="margin-left:2px;padding:3px 8px" @click="buyingCode = null">取消</button>
                </template>
                <button v-else class="btn sm ghost" style="padding:3px 12px" @click="startBuy(r)">记一笔买入</button>
              </template>
              <button v-else-if="editingCode !== r.code" class="btn sm ghost"
                      style="padding:3px 12px" @click="startEdit(r)">编辑</button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- 采集失败弹窗（5 次重试仍失败） -->
    <div v-if="failures" class="modal-mask" @click.self="failures = null">
      <div class="modal">
        <header>净值刷新部分失败</header>
        <div class="body">
          <p style="margin:0 0 8px;color:var(--ink-2);font-size:13px">
            以下 {{ failures.length }} 只基金已静默重试 5 次仍失败，本次看板保留其上次净值：
          </p>
          <div v-for="f in failures" :key="f.fundCode" class="fail-row">
            <code>{{ f.fundCode }}</code>
            <span>{{ f.error }}</span>
          </div>
        </div>
        <footer>
          <button class="btn" @click="failures = null">我知道了</button>
        </footer>
      </div>
    </div>

    <div v-if="toast" class="toast" :class="{ err: toastErr }">{{ toast }}</div>
  </template>
</template>

<style scoped>
.badge-stale {
  background: #fff7e6;
  color: #b26a00;
  border-color: #ffd591;
}

/* M2 红绿灯 */
.dot {
  display: inline-block;
  width: 9px;
  height: 9px;
  border-radius: 50%;
  margin-right: 5px;
  vertical-align: 0;
}
.sig-green { background: #16a34a; box-shadow: 0 0 0 3px rgba(22, 163, 74, .15); }
.sig-yellow { background: #d97706; box-shadow: 0 0 0 3px rgba(217, 119, 6, .15); }
.sig-red { background: #dc2626; box-shadow: 0 0 0 3px rgba(220, 38, 38, .15); }
.sig-nodata { background: #9ca3af; }
.sig-stable { background: #8b8378; }
tr.rowzero td { opacity: .62; }
.btn.ghost {
  background: transparent;
  border: 1px solid #c9d4e5;
  color: var(--ink-2);
}
.btn.ghost:hover {
  background: #f3f6fb;
}

/* 定投建议面板：头部信息可换行，避免窄屏挤压 */
.panel-head-col {
  flex-wrap: wrap;
  gap: 8px 12px;
}
.head-meta {
  font-size: 12px;
  color: var(--muted);
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
}
.meta-sep { color: #c9ced8; }

/* 待确认买入角标 */
.pending-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 10px;
  border-radius: 10px;
  background: #fff7e6;
  color: #b45309;
  border: 1px solid #f4c98a;
  font-size: 12px;
  cursor: pointer;
  transition: filter .12s;
}
.pending-badge:hover { filter: brightness(.96); }

/* 待确认买入弹窗 */
.pending-list { display: flex; flex-direction: column; gap: 8px; }
.pending-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: #fff;
}
.pending-info { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; min-width: 0; }
.pending-fund { font-weight: 600; }
.pending-info code { font-size: 11px; color: var(--muted); }
.pending-meta { font-size: 12px; color: var(--muted); }
.btn.sm.danger {
  background: #fff1f0;
  border: 1px solid #ffc6c2;
  color: #c0392b;
}
.btn.sm.danger:hover:not(:disabled) { background: #ffe3e1; }

/* 记一笔买入按钮 */
.buy-btn {
  font-size: 12px;
  padding: 2px 10px;
  border-radius: 6px;
  cursor: pointer;
}
.btn.sm.ghost.buy-btn { padding: 2px 8px; }

/* 表头两行：列名 + 条件小字 */
.th-sub {
  display: block;
  font-size: 10.5px;
  font-weight: 400;
  color: #9aa3af;
  white-space: normal;
  line-height: 1.35;
}

/* 红线与说明列：允许中文长句自动换行（覆盖全局 td 的 nowrap） */
.decision-table td.wrap {
  white-space: normal;
  word-break: break-word;
  line-height: 1.6;
  font-size: 11.5px;
  min-width: 0;
}
.decision-table .blockers { color: #b4541f; }
.decision-table .notes { color: var(--muted); }

.rule-hint {
  padding: 10px 18px 14px;
  font-size: 12px;
  color: var(--muted);
  line-height: 1.6;
  border-top: 1px dashed var(--line);
  background: #fafbfc;
}

/* ---------- 盘中估算 ---------- */
.btn.ghost.on {
  background: #eef3fe;
  border-color: var(--accent);
  color: var(--accent);
  font-weight: 600;
}

.intraday-bar {
  display: flex;
  align-items: stretch;
  gap: 22px;
  flex-wrap: wrap;
  background: var(--card-bg);
  border-radius: var(--radius);
  box-shadow: var(--shadow);
  border-left: 3px solid var(--accent);
  padding: 14px 20px;
  margin-bottom: 18px;
}
.intraday-bar.stale { border-left-color: var(--warn); }

.ib-item { min-width: 168px; }
.ib-item .label { color: var(--muted); font-size: 13px; margin-bottom: 6px; }
.ib-item .value { font-size: 22px; font-weight: 700; font-variant-numeric: tabular-nums; line-height: 1.15; }

.ib-meta {
  flex: 1;
  min-width: 260px;
  font-size: 12px;
  color: var(--ink-2);
  line-height: 1.7;
}
.ib-meta b { font-variant-numeric: tabular-nums; }
.ib-meta .warn-line { color: var(--warn); }
.ib-meta .disc {
  margin-top: 4px;
  padding-top: 4px;
  border-top: 1px dashed var(--line);
  color: var(--muted);
}

/* 盘中估算列：主行涨跌幅 + 副行估算净值 */
.est-nav {
  font-size: 11px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
  margin-top: 2px;
}
</style>
