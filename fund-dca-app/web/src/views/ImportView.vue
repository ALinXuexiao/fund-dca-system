<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import {
  commitImport,
  fetchVersions,
  parseDemoStatement,
  parseStatement,
  rollbackVersion,
  statementTemplateUrl,
} from '../api'
import {
  FUND_TYPE_LABEL,
  type CommitDecision,
  type CommitResult,
  type FundType,
  type ImportPreview,
  type PositionVersion,
  type ReconcileAction,
  type ReconcileItem,
  type ReconcileKind,
} from '../types'

const emit = defineEmits<{ (e: 'go-dashboard'): void }>()

type Step = 1 | 2 | 3

const step = ref<Step>(1)
const busy = ref(false)
const parseError = ref('')
const commitError = ref('')
const preview = ref<ImportPreview | null>(null)
const result = ref<CommitResult | null>(null)
const proofDate = ref('')
const versions = ref<PositionVersion[]>([])
const versionsLoading = ref(false)
const notice = ref('')
const dragging = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)

interface Choice {
  action: ReconcileAction
  sectorId: number | null
  fundType: FundType
  addedCost: number | null
}

const choices = reactive<Record<string, Choice>>({})

const ACTION_LABELS: Record<ReconcileAction, string> = {
  None: '无需处理',
  DividendReinvest: '红利再投（只加份额，不加本金）',
  ManualAdd: '手动加仓（同时增加本金）',
  KeepSystem: '保留系统值（份额异常不覆盖）',
  AcceptFileValue: '接受文件市值（手工维护）',
  CreateFund: '新基金建档并建仓',
}

const KIND_LABELS: Record<ReconcileKind, string> = {
  Matched: '一致',
  ShareIncrease: '份额增加',
  ShareDecrease: '份额异常减少',
  NewFund: '新基金',
  MissingInFile: '证明中缺失',
  ManualValueChange: '手工市值更新',
  UnknownRow: '无法识别',
}

const KIND_CLASS: Record<ReconcileKind, string> = {
  Matched: 'ok',
  ShareIncrease: 'warn',
  ShareDecrease: 'danger',
  NewFund: 'info',
  MissingInFile: 'muted',
  ManualValueChange: 'warn',
  UnknownRow: 'muted',
}

const SOURCE_LABELS: Record<string, string> = {
  baseline: '基线',
  import: '导入',
  rollback: '回滚',
}

const equitySectors = computed(() => (preview.value?.sectors ?? []).filter((s) => s.isEquity))
const differenceItems = computed(() =>
  (preview.value?.items ?? []).filter((i) => i.code && i.kind !== 'Matched'),
)

onMounted(loadVersions)

async function loadVersions() {
  versionsLoading.value = true
  try {
    versions.value = await fetchVersions()
  } finally {
    versionsLoading.value = false
  }
}

function fmtMoney(v: number | null | undefined): string {
  return v == null ? '—' : v.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}

function fmtShares(v: number | null | undefined): string {
  return v == null ? '—' : v.toLocaleString('zh-CN', { maximumFractionDigits: 2 })
}

function choiceOf(item: ReconcileItem): Choice | null {
  return item.code ? (choices[item.code] ?? null) : null
}

function isEquityType(t: FundType | undefined | null): boolean {
  return !!t && t !== 'Bond' && t !== 'Money'
}

function initChoices(p: ImportPreview) {
  for (const key of Object.keys(choices)) {
    delete choices[key]
  }
  for (const item of p.items) {
    if (!item.code) {
      continue
    }
    choices[item.code] = {
      action: item.action,
      sectorId: item.sectorId,
      fundType: item.proposedType ?? item.systemType ?? 'Stock',
      addedCost: item.suggestedAddedCost,
    }
  }
}

async function applyPreview(p: ImportPreview) {
  preview.value = p
  proofDate.value = p.proofDate ?? ''
  initChoices(p)
  parseError.value = ''
  step.value = 2
}

async function onDemo() {
  busy.value = true
  parseError.value = ''
  try {
    await applyPreview(await parseDemoStatement())
  } catch (e) {
    parseError.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

async function onFilePicked(file: File | null) {
  if (!file) {
    return
  }
  busy.value = true
  parseError.value = ''
  try {
    await applyPreview(await parseStatement(file))
  } catch (e) {
    parseError.value = (e as Error).message
  } finally {
    busy.value = false
    if (fileInput.value) {
      fileInput.value.value = ''
    }
  }
}

function onDrop(e: DragEvent) {
  dragging.value = false
  const file = e.dataTransfer?.files?.[0] ?? null
  onFilePicked(file)
}

function backToStep1() {
  step.value = 1
  preview.value = null
  result.value = null
  commitError.value = ''
}

function validateBeforeCommit(): string | null {
  if (!preview.value) {
    return '缺少对账数据'
  }
  for (const item of preview.value.items) {
    const choice = choiceOf(item)
    if (!choice) {
      continue
    }
    if (choice.action === 'CreateFund' && isEquityType(choice.fundType) && choice.sectorId == null) {
      return `新基金「${item.fileFundName}（${item.code}）」是权益类，请选择归属赛道后再导入`
    }
  }
  return null
}

async function onCommit() {
  if (!preview.value) {
    return
  }
  const invalid = validateBeforeCommit()
  if (invalid) {
    commitError.value = invalid
    return
  }
  if (!window.confirm('确认按当前差异处理方式导入？系统将在一个事务内应用并生成持仓快照，可随时回滚。')) {
    return
  }

  busy.value = true
  commitError.value = ''
  try {
    const decisions: CommitDecision[] = preview.value.items
      .filter((i) => i.code)
      .map((i) => {
        const c = choiceOf(i)!
        return {
          code: i.code,
          action: c.action,
          sectorId: c.action === 'CreateFund' ? c.sectorId : null,
          fundType: c.action === 'CreateFund' ? c.fundType : null,
          addedCost: c.action === 'ManualAdd' ? c.addedCost : null,
        }
      })

    result.value = await commitImport(preview.value.token, decisions, proofDate.value || null)
    step.value = 3
    await loadVersions()
  } catch (e) {
    commitError.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

async function onRollback(v: PositionVersion) {
  if (v.isCurrent) {
    return
  }
  if (!window.confirm(`确定回滚到 ${v.version}（证明日期 ${v.proofDate}）？当前看板持仓将被原子替换。`)) {
    return
  }
  busy.value = true
  notice.value = ''
  try {
    const r = await rollbackVersion(v.id)
    notice.value = `已回滚到 ${v.version}：${r.logs.join('；')}`
    await loadVersions()
  } catch (e) {
    notice.value = `回滚失败：${(e as Error).message}`
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="import-view">
    <div class="steps">
      <span :class="{ active: step === 1, done: step > 1 }">1 上传解析</span>
      <i>→</i>
      <span :class="{ active: step === 2, done: step > 2 }">2 对账确认</span>
      <i>→</i>
      <span :class="{ active: step === 3 }">3 完成</span>
    </div>

    <!-- 第一步：上传 -->
    <div v-if="step === 1" class="card">
      <h2 class="card-title">导入资产证明</h2>
      <p class="hint">
        把支付宝「基金持仓证明 / 资产证明」（T+1 出 T 日）导出后上传。系统在本地完成 PDF / Excel / CSV 解析，
        <strong>先对账、后覆盖</strong>：任何份额差异都必须你确认原因，绝不静默改数；货币/理财按文件市值维护。
      </p>

      <div
        class="dropzone"
        :class="{ drag: dragging }"
        @click="fileInput?.click()"
        @dragover.prevent="dragging = true"
        @dragleave.prevent="dragging = false"
        @drop.prevent="onDrop"
      >
        <input
          ref="fileInput"
          type="file"
          accept=".pdf,.xlsx,.xls,.csv"
          hidden
          @change="onFilePicked(($event.target as HTMLInputElement).files?.[0] ?? null)"
        />
        <p class="dz-main">点击选择，或把文件拖拽到这里</p>
        <p class="dz-sub">支持 .pdf · .xlsx · .csv，文件仅在本机解析</p>
      </div>

      <div class="step1-actions">
        <button class="btn" :disabled="busy" @click="onDemo">
          {{ busy ? '解析中…' : '使用演示文件体验' }}
        </button>
        <a class="tpl-link" :href="statementTemplateUrl" download="基金持仓证明导入模板.xlsx">
          下载标准 Excel 模板（PDF 版式识别不了时用它粘贴两列即可）
        </a>
      </div>

      <p v-if="parseError" class="error-text">{{ parseError }}</p>
    </div>

    <!-- 第二步：对账 -->
    <template v-if="step === 2 && preview">
      <div class="card">
        <div class="preview-head">
          <div>
            <h2 class="card-title">对账预览</h2>
            <p class="hint">
              来源：{{ preview.fileName ?? '内置演示文件' }}
              <span v-if="preview.templateVersion">（模板 {{ preview.templateVersion }}）</span>
            </p>
          </div>
          <label class="proof-date">
            证明日期（T 日）
            <input type="date" v-model="proofDate" />
          </label>
        </div>

        <div class="chips">
          <span class="chip">识别 {{ preview.summary.total }} 项</span>
          <span class="chip ok">一致 {{ preview.summary.matched }}</span>
          <span class="chip warn">差异 {{ preview.summary.differences }}</span>
          <span class="chip info">新基金 {{ preview.summary.newFunds }}</span>
          <span class="chip warn">手工市值 {{ preview.summary.manualValueChanges }}</span>
        </div>

        <ul v-if="preview.warnings.length" class="warnings">
          <li v-for="(w, idx) in preview.warnings" :key="idx">{{ w }}</li>
        </ul>
      </div>

      <div class="card">
        <div class="table-wrap">
          <table class="data-table">
            <thead>
              <tr>
                <th>基金</th>
                <th class="num">文件值</th>
                <th class="num">系统现值</th>
                <th>判定</th>
                <th class="col-action">处理方式</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="item in preview.items" :key="item.code ?? item.fileFundName" :class="{ muted: item.kind === 'Matched' }">
                <td>
                  <div class="fund-cell">
                    <span class="fund-name">{{ item.code ? item.fileFundName : item.fileFundName || '无法识别的行' }}</span>
                    <span class="fund-code">{{ item.code ?? '—' }}</span>
                    <span v-if="item.systemType" class="tag muted">{{ FUND_TYPE_LABEL[item.systemType] }}</span>
                  </div>
                  <p v-if="item.message" class="cell-msg">{{ item.message }}</p>
                </td>
                <td class="num">
                  <template v-if="item.fileShares != null">
                    <div>{{ fmtShares(item.fileShares) }} 份</div>
                    <div v-if="item.fileMarketValue != null" class="sub">¥{{ fmtMoney(item.fileMarketValue) }}</div>
                  </template>
                  <template v-else-if="item.fileMarketValue != null">
                    <div>¥{{ fmtMoney(item.fileMarketValue) }}</div>
                    <div class="sub">货币/理财市值</div>
                  </template>
                  <template v-else>—</template>
                </td>
                <td class="num">
                  <template v-if="item.existsInSystem">
                    <template v-if="item.isManual">
                      <div>¥{{ fmtMoney(item.systemMarketValue) }}</div>
                      <div class="sub">手工维护</div>
                    </template>
                    <template v-else>
                      <div>{{ fmtShares(item.systemShares) }} 份</div>
                      <div class="sub">¥{{ fmtMoney(item.systemMarketValue) }}</div>
                    </template>
                  </template>
                  <template v-else>
                    <span class="tag info">系统无此标的</span>
                  </template>
                </td>
                <td><span class="tag" :class="KIND_CLASS[item.kind]">{{ KIND_LABELS[item.kind] }}</span></td>
                <td class="col-action">
                  <template v-if="choiceOf(item)">
                    <select v-model="choiceOf(item)!.action" class="action-select">
                      <option v-for="a in item.allowedActions" :key="a" :value="a">{{ ACTION_LABELS[a] }}</option>
                    </select>

                    <div v-if="choiceOf(item)!.action === 'CreateFund'" class="sub-controls">
                      <select v-model="choiceOf(item)!.fundType" class="action-select small">
                        <option v-for="(label, key) in FUND_TYPE_LABEL" :key="key" :value="key">{{ label }}</option>
                      </select>
                      <select
                        v-if="isEquityType(choiceOf(item)!.fundType)"
                        v-model="choiceOf(item)!.sectorId"
                        class="action-select small"
                      >
                        <option :value="null" disabled>选择权益赛道…</option>
                        <option v-for="s in equitySectors" :key="s.id" :value="s.id">{{ s.name }}</option>
                      </select>
                      <span v-else class="sub">
                        {{ choiceOf(item)!.fundType === 'Money' ? '计入稳健 D，不定投' : '计入稳健 D，仅做仓位占比' }}
                      </span>
                    </div>

                    <div v-if="choiceOf(item)!.action === 'ManualAdd'" class="sub-controls">
                      <label class="cost-input">
                        增加本金 ¥
                        <input
                          v-model.number="choiceOf(item)!.addedCost"
                          type="number"
                          min="0"
                          step="0.01"
                          placeholder="实际加仓金额"
                        />
                      </label>
                    </div>
                  </template>
                  <span v-else class="sub">{{ item.kind === 'UnknownRow' ? '缺少六位代码，已忽略' : '默认保留' }}</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <p v-if="commitError" class="error-text">{{ commitError }}</p>

        <div class="wizard-actions">
          <button class="btn ghost" :disabled="busy" @click="backToStep1">上一步</button>
          <button class="btn primary" :disabled="busy" @click="onCommit">
            {{ busy ? '提交中…' : `确认并导入（${differenceItems.length} 处差异待处理）` }}
          </button>
        </div>
      </div>
    </template>

    <!-- 第三步：结果 -->
    <div v-if="step === 3 && result" class="card result-card">
      <h2 class="card-title ok-text">✓ 导入完成</h2>
      <div class="chips">
        <span class="chip info">当前版本 {{ result.version }}</span>
        <span class="chip">证明日期 {{ result.proofDate }}</span>
        <span class="chip ok">实际应用 {{ result.applied }} 项</span>
      </div>
      <ul class="result-logs">
        <li v-for="(log, idx) in result.logs" :key="idx">{{ log }}</li>
      </ul>
      <div class="wizard-actions">
        <button class="btn ghost" @click="backToStep1">再导入一份</button>
        <button class="btn primary" @click="emit('go-dashboard')">去看看板</button>
      </div>
    </div>

    <!-- 版本历史 -->
    <div class="card">
      <h2 class="card-title">持仓版本与回滚</h2>
      <p class="hint">
        每次导入生成一份全量持仓快照并原子切换看板数据源；导入出错或发现证明有误时，可一键回滚到任意历史版本（回滚本身也会留痕）。
      </p>
      <p v-if="notice" class="error-text">{{ notice }}</p>
      <div class="table-wrap">
        <table class="data-table">
          <thead>
            <tr>
              <th>版本</th>
              <th>来源</th>
              <th>证明日期</th>
              <th class="num">标的</th>
              <th>摘要</th>
              <th>生成时间</th>
              <th>操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="v in versions" :key="v.id" :class="{ current: v.isCurrent }">
              <td>
                <span class="version-no">{{ v.version }}</span>
                <span v-if="v.isCurrent" class="tag ok">当前</span>
              </td>
              <td><span class="tag muted">{{ SOURCE_LABELS[v.source] ?? v.source }}</span></td>
              <td>{{ v.proofDate }}</td>
              <td class="num">{{ v.itemCount }}</td>
              <td class="version-summary">{{ v.summary }}<span v-if="v.fileName"> · {{ v.fileName }}</span></td>
              <td class="sub">{{ v.createdAt }}</td>
              <td>
                <button
                  v-if="!v.isCurrent"
                  class="btn tiny ghost"
                  :disabled="busy"
                  @click="onRollback(v)"
                >
                  回滚到此版本
                </button>
                <span v-else class="sub">—</span>
              </td>
            </tr>
            <tr v-if="!versionsLoading && versions.length === 0">
              <td colspan="7" class="empty">暂无版本</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  </section>
</template>

<style scoped>
.import-view {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.steps {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 14px;
  color: var(--text-3, #94a3b8);
}

.steps span {
  padding: 4px 12px;
  border-radius: 999px;
  background: rgba(148, 163, 184, 0.12);
}

.steps span.active {
  background: var(--primary, #2563eb);
  color: #fff;
}

.steps span.done {
  color: #16a34a;
}

.steps i {
  font-style: normal;
}

.hint {
  font-size: 13px;
  color: var(--text-3, #94a3b8);
  line-height: 1.7;
  margin: 6px 0 0;
}

.dropzone {
  margin-top: 14px;
  border: 2px dashed rgba(148, 163, 184, 0.45);
  border-radius: 12px;
  padding: 40px 20px;
  text-align: center;
  cursor: pointer;
  transition: border-color 0.15s, background 0.15s;
}

.dropzone:hover,
.dropzone.drag {
  border-color: var(--primary, #2563eb);
  background: rgba(37, 99, 235, 0.06);
}

.dz-main {
  font-size: 15px;
  font-weight: 600;
  margin: 0 0 6px;
}

.dz-sub {
  font-size: 12px;
  color: var(--text-3, #94a3b8);
  margin: 0;
}

.step1-actions {
  display: flex;
  align-items: center;
  gap: 16px;
  margin-top: 14px;
  flex-wrap: wrap;
}

.tpl-link {
  font-size: 13px;
  color: var(--primary, #2563eb);
  text-decoration: none;
}

.tpl-link:hover {
  text-decoration: underline;
}

.error-text {
  color: #dc2626;
  font-size: 13px;
  margin: 10px 0 0;
}

.preview-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 16px;
  flex-wrap: wrap;
}

.proof-date {
  font-size: 12px;
  color: var(--text-3, #94a3b8);
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.proof-date input {
  padding: 6px 8px;
  border-radius: 6px;
  border: 1px solid rgba(148, 163, 184, 0.4);
  background: transparent;
  color: inherit;
}

.chips {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 12px;
}

.chip {
  font-size: 12px;
  padding: 4px 10px;
  border-radius: 999px;
  background: rgba(148, 163, 184, 0.14);
}

.chip.ok {
  background: rgba(22, 163, 74, 0.14);
  color: #16a34a;
}

.chip.warn {
  background: rgba(217, 119, 6, 0.14);
  color: #d97706;
}

.chip.info {
  background: rgba(37, 99, 235, 0.14);
  color: #2563eb;
}

.warnings {
  margin: 12px 0 0;
  padding-left: 18px;
  font-size: 12px;
  color: #d97706;
  line-height: 1.8;
}

.fund-cell {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.fund-name {
  font-weight: 600;
}

.fund-code {
  font-size: 12px;
  color: var(--text-3, #94a3b8);
  font-family: ui-monospace, monospace;
}

.cell-msg {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--text-3, #94a3b8);
}

.sub {
  font-size: 12px;
  color: var(--text-3, #94a3b8);
}

tr.muted {
  opacity: 0.72;
}

.col-action {
  min-width: 240px;
}

.action-select {
  width: 100%;
  padding: 6px 8px;
  border-radius: 6px;
  border: 1px solid rgba(148, 163, 184, 0.4);
  background: transparent;
  color: inherit;
  font-size: 13px;
}

.action-select.small {
  width: auto;
  margin-top: 6px;
  margin-right: 6px;
}

.sub-controls {
  margin-top: 6px;
}

.cost-input {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-3, #94a3b8);
}

.cost-input input {
  width: 130px;
  padding: 6px 8px;
  border-radius: 6px;
  border: 1px solid rgba(148, 163, 184, 0.4);
  background: transparent;
  color: inherit;
}

.wizard-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
  margin-top: 16px;
}

.result-card .ok-text {
  color: #16a34a;
}

.result-logs {
  margin: 14px 0 0;
  padding-left: 18px;
  font-size: 13px;
  line-height: 2;
}

tr.current {
  background: rgba(22, 163, 74, 0.05);
}

.version-no {
  font-family: ui-monospace, monospace;
  font-weight: 600;
  margin-right: 6px;
}

.version-summary {
  font-size: 12px;
  color: var(--text-3, #94a3b8);
}

.btn.tiny {
  padding: 4px 10px;
  font-size: 12px;
}

.empty {
  text-align: center;
  color: var(--text-3, #94a3b8);
  padding: 20px;
}
</style>
