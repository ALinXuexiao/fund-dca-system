<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { fetchFunds } from '../api'
import { FUND_TYPE_LABEL, METRIC_LABEL, isStableType, type FundListItem } from '../types'

const loading = ref(true)
const errorMsg = ref('')
const funds = ref<FundListItem[]>([])

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

function feeText(f: FundListItem): string {
  return f.buyFeeRate == null ? '待录入' : `${(f.buyFeeRate * 100).toFixed(2)}%`
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
        赛道 <b>{{ groups.length }}</b> 个（大盘/中盘/小盘/价值/成长/红利/消费/港股科技/债券/货币）
      </div>
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
  </div>
</template>
