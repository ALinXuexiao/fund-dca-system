<script setup lang="ts">
import { defineAsyncComponent, onMounted, ref } from 'vue'
import { fetchHealth } from './api'

// 三个页签按路由级别代码分割：首屏只加载持仓看板（含 echarts），
// 导入对账/基金档案在首次点开时才下载对应 chunk
const DashboardView = defineAsyncComponent(() => import('./views/DashboardView.vue'))
const FundArchiveView = defineAsyncComponent(() => import('./views/FundArchiveView.vue'))
const ImportView = defineAsyncComponent(() => import('./views/ImportView.vue'))

const tab = ref<'dashboard' | 'archive' | 'import'>('dashboard')
const backendOk = ref(false)
const now = ref('')

function tick() {
  now.value = new Date().toLocaleString('zh-CN', { hour12: false })
}

onMounted(async () => {
  tick()
  setInterval(tick, 1000)
  try {
    await fetchHealth()
    backendOk.value = true
  } catch {
    backendOk.value = false
  }
})
</script>

<template>
  <div class="page">
    <header class="topbar">
      <div>
        <h1>基金持仓看板与定投决策系统</h1>
        <p class="sub">M1 盘后看板 · M2 人工买入 · M3 资产证明导入对账与版本回滚 · 真实净值采集（天天基金）</p>
        <nav class="tabs">
          <button :class="{ active: tab === 'dashboard' }" @click="tab = 'dashboard'">持仓看板</button>
          <button :class="{ active: tab === 'import' }" @click="tab = 'import'">导入对账</button>
          <button :class="{ active: tab === 'archive' }" @click="tab = 'archive'">基金档案</button>
        </nav>
      </div>
      <div class="status" :class="backendOk ? 'ok' : 'bad'">
        <span class="dot"></span>
        <template v-if="backendOk">后端已连接 · {{ now }}</template>
        <template v-else>后端未连接</template>
      </div>
    </header>

    <main>
      <DashboardView v-if="tab === 'dashboard'" />
      <ImportView v-else-if="tab === 'import'" @go-dashboard="tab = 'dashboard'" />
      <FundArchiveView v-else />
    </main>
  </div>
</template>
