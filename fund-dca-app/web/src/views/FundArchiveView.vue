<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { createFund, fetchFundOptions, fetchFunds } from '../api'
import {
  FUND_TYPE_LABEL,
  METRIC_LABEL,
  isStableType,
  type CreateFundInput,
  type FundListItem,
  type FundOptions,
  type FundType,
} from '../types'

const loading = ref(true)
const errorMsg = ref('')
const funds = ref<FundListItem[]>([])

const options = ref<FundOptions | null>(null)
const showAdd = ref(false)
const saving = ref(false)
const formError = ref('')

const fundTypes = Object.entries(FUND_TYPE_LABEL) as [FundType, string][]

const form = ref({
  code: '',
  name: '',
  type: 'Stock' as FundType,
  sectorId: null as number | null,
  trackedIndexCode: '',
  feePercent: '',
  dividendMethod: 'REINVEST',
  useManualValue: false,
})

async function loadAll() {
  loading.value = true
  errorMsg.value = ''
  try {
    funds.value = await fetchFunds()
  } catch (e) {
    errorMsg.value = (e as Error).message
  } finally {
    loading.value = false
  }
}

const groups = computed(() => {
  const map = new Map<string, { name: string; sortOrder: number; isEquity: boolean; items: FundListItem[] }>()
  for (const f of funds.value) {
    const key = f.sector ?? '未分类'
    if (!map.has(key)) {
      map.set(key, { name: key, sortOrder: f.sectorSortOrder, isEquity: f.sectorIsEquity, items: [] })
    }
    map.get(key)!.items.push(f)
  }
  return [...map.values()].sort((a, b) => a.sortOrder - b.sortOrder)
})

/** 权益型只能选权益赛道；债券/货币可选稳健赛道 */
const formSectors = computed(() => {
  const all = options.value?.sectors ?? []
  const equityType = form.value.type === 'Stock' || form.value.type === 'Mixed' || form.value.type === 'Qdii'
  return equityType ? all.filter((s) => s.isEquity) : all
})

watch(
  () => form.value.type,
  (type) => {
    const find = (name: string) => options.value?.sectors.find((s) => s.name === name)?.id ?? null
    if (type === 'Money') {
      form.value.useManualValue = true
      form.value.sectorId = find('货币')
    } else if (type === 'Bond') {
      form.value.useManualValue = false
      form.value.sectorId = find('债券')
    } else {
      form.value.useManualValue = false
      if (!formSectors.value.some((s) => s.id === form.value.sectorId)) {
        form.value.sectorId = null
      }
    }
  },
)

function feeText(f: FundListItem): string {
  return f.buyFeeRate == null ? '待录入' : `${(f.buyFeeRate * 100).toFixed(2)}%`
}

async function openAdd() {
  formError.value = ''
  form.value = {
    code: '',
    name: '',
    type: 'Stock',
    sectorId: null,
    trackedIndexCode: '',
    feePercent: '',
    dividendMethod: 'REINVEST',
    useManualValue: false,
  }
  showAdd.value = true
  if (!options.value) {
    try {
      options.value = await fetchFundOptions()
    } catch (e) {
      formError.value = (e as Error).message
    }
  }
}

async function submitAdd() {
  formError.value = ''
  const code = form.value.code.trim().toUpperCase()
  const name = form.value.name.trim()
  if (!code) {
    formError.value = '请填写基金代码'
    return
  }
  if (!name) {
    formError.value = '请填写基金名称'
    return
  }
  if (!form.value.sectorId) {
    formError.value = '请选择赛道'
    return
  }

  const feeText = form.value.feePercent.trim()
  const fee = feeText === '' ? null : Number(feeText) / 100
  if (fee !== null && (!Number.isFinite(fee) || fee < 0 || fee > 1)) {
    formError.value = '申购费率需在 0–100% 之间'
    return
  }

  const input: CreateFundInput = {
    code,
    name,
    type: form.value.type,
    sectorId: form.value.sectorId,
    trackedIndexCode: form.value.trackedIndexCode || null,
    buyFeeRate: fee,
    dividendMethod: form.value.dividendMethod,
    useManualValue: form.value.type === 'Money' || form.value.useManualValue,
  }

  saving.value = true
  try {
    await createFund(input)
    showAdd.value = false
    await loadAll()
  } catch (e) {
    formError.value = (e as Error).message
  } finally {
    saving.value = false
  }
}

onMounted(loadAll)
</script>

<template>
  <div v-if="loading" class="hint-msg">正在加载基金档案…</div>

  <div v-else-if="errorMsg" class="error-box">
    <b>数据加载失败：</b>{{ errorMsg }}
    <button class="btn" @click="loadAll">重试</button>
  </div>

  <div v-else>
    <div class="toolbar">
      <div class="asof">共 <b>{{ funds.length }}</b> 只基金 ·
        权益 <b>{{ funds.filter((f) => !isStableType(f.type)).length }}</b> 只 ·
        稳健 <b>{{ funds.filter((f) => isStableType(f.type)).length }}</b> 只 ·
        赛道 <b>{{ groups.length }}</b> 个（大盘/中盘/小盘/价值/成长/消费/科技/债券/货币）
      </div>
      <button class="btn" @click="openAdd">＋ 新增基金</button>
    </div>

    <div class="panel">
      <div class="panel-head">
        <h3>基金档案（按赛道分组）</h3>
        <span style="font-size:12px;color:var(--muted)">费率待你提供后录入；默认红利再投资</span>
      </div>
      <table>
        <thead>
          <tr>
            <th style="width:82px">赛道</th>
            <th class="l">基金</th>
            <th style="width:84px">类型</th>
            <th class="l">跟踪指数</th>
            <th style="width:90px">估值指标</th>
            <th style="width:90px">申购费率</th>
            <th style="width:100px">分红方式</th>
          </tr>
        </thead>
        <tbody v-for="g in groups" :key="g.name" :class="{ stable: !g.isEquity }">
          <tr v-for="(f, i) in g.items" :key="f.code">
            <td v-if="i === 0" class="sector-cell" :rowspan="g.items.length">{{ g.name }}</td>
            <td class="fname l">
              {{ f.name }}
              <span class="code">{{ f.code }}</span>
            </td>
            <td>
              <span class="tag" :class="isStableType(f.type) ? 'tag-stable' : f.type === 'Qdii' ? 'tag-qdii' : 'tag-equity'">
                {{ FUND_TYPE_LABEL[f.type] }}
              </span>
            </td>
            <td class="l">{{ f.trackedIndexName ? `${f.trackedIndexName}（${f.trackedIndexCode}）` : '—' }}</td>
            <td>
              <span v-if="f.trackedIndexCode" style="color:var(--accent);font-weight:600">
                {{ METRIC_LABEL[f.valuationMetric] ?? f.valuationMetric }}
              </span>
              <span v-else style="color:var(--muted)">—</span>
            </td>
            <td :style="f.buyFeeRate === null ? 'color:var(--warn)' : ''">{{ feeText(f) }}</td>
            <td>{{ f.dividendMethod === 'REINVEST' ? '红利再投资' : '现金分红' }}</td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="showAdd" class="modal-mask" @click.self="showAdd = false">
      <div class="modal">
        <header>新增基金档案</header>
        <div class="body wide">
          <div class="form-grid">
            <label>基金代码</label>
            <input v-model="form.code" type="text" placeholder="如 012345、7R0001" />
            <label>基金名称</label>
            <input v-model="form.name" type="text" placeholder="与基金合同一致" />
            <label>类型</label>
            <select v-model="form.type">
              <option v-for="[key, label] in fundTypes" :key="key" :value="key">{{ label }}</option>
            </select>
            <label>赛道</label>
            <select v-model="form.sectorId">
              <option :value="null">请选择赛道</option>
              <option v-for="s in formSectors" :key="s.id" :value="s.id">{{ s.name }}</option>
            </select>
            <label>跟踪指数</label>
            <select v-model="form.trackedIndexCode">
              <option value="">不跟踪指数</option>
              <option v-for="i in options?.indexes ?? []" :key="i.code" :value="i.code">
                {{ i.name }}（{{ i.code }}）
              </option>
            </select>
            <label>申购费率</label>
            <div>
              <input v-model="form.feePercent" type="number" min="0" max="100" step="0.01" placeholder="如 0.06" />
              <span style="margin-left:6px;color:var(--ink-2)">%</span>
            </div>
            <label>分红方式</label>
            <select v-model="form.dividendMethod">
              <option value="REINVEST">红利再投资</option>
              <option value="CASH">现金分红</option>
            </select>
            <label>手工市值</label>
            <label class="check">
              <input v-model="form.useManualValue" type="checkbox" :disabled="form.type === 'Money'" />
              不采集净值，手工维护市值
            </label>
          </div>
          <div v-if="formError" class="form-error">{{ formError }}</div>
        </div>
        <footer>
          <button class="btn ghost sm" @click="showAdd = false">取消</button>
          <button class="btn sm" :disabled="saving" @click="submitAdd">{{ saving ? '保存中…' : '保存' }}</button>
        </footer>
      </div>
    </div>
  </div>
</template>
