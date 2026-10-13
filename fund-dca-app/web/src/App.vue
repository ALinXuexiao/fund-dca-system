<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, ref } from 'vue'
import { fetchHealth } from './api'
import ViewError from './components/ViewError.vue'
import ViewSkeleton from './components/ViewSkeleton.vue'

// 三个页签按路由级别代码分割：首屏只加载持仓看板（含 echarts），
// 导入对账/基金档案在首次点开时才下载对应 chunk。
// 每个异步页签都配了骨架屏（loading）与错误屏（error）：chunk 还在下载时
// 页面已有完整结构，不会白屏；chunk 加载失败时给出可点击的重试。
const DashboardView = defineAsyncComponent({
  loader: () => import('./views/DashboardView.vue'),
  loadingComponent: ViewSkeleton,
  errorComponent: ViewError,
  delay: 0,
})
const FundArchiveView = defineAsyncComponent({
  loader: () => import('./views/FundArchiveView.vue'),
  loadingComponent: ViewSkeleton,
  errorComponent: ViewError,
  delay: 0,
})
const ImportView = defineAsyncComponent({
  loader: () => import('./views/ImportView.vue'),
  loadingComponent: ViewSkeleton,
  errorComponent: ViewError,
  delay: 0,
})

const tab = ref<'dashboard' | 'archive' | 'import'>('dashboard')
// 三态：checking（尚未确认）/ ok（后端可用）/ down（确认不可用，继续重试）
const health = ref<'checking' | 'ok' | 'down'>('checking')
const now = ref('')
let clockTimer: number | undefined
let healthTimer: number | undefined

const statusClass = computed(() =>
  health.value === 'ok' ? 'ok' : health.value === 'down' ? 'bad' : 'pending',
)

function tick() {
  now.value = new Date().toLocaleString('zh-CN', { hour12: false })
}

async function checkHealth(): Promise<boolean> {
  try {
    await fetchHealth()
    health.value = 'ok'
    return true
  } catch {
    return false
  }
}

async function refreshHealth() {
  health.value = (await checkHealth()) ? 'ok' : 'down'
}

onMounted(async () => {
  tick()
  clockTimer = window.setInterval(tick, 1000)

  // 启动脚本在前端就绪时就打开浏览器，而后端此时可能还在编译 + 连云端库：
  // 不能只探一次，否则状态灯会一直停在"正在连接后端"。
  // 后端就绪后也保持低频轮询（5 秒）：后端被关掉或重启时状态灯立刻变红，
  // 用户不会在不知情时点按钮、只看到一句英文的 "Failed to fetch"。
  await refreshHealth()
  healthTimer = window.setInterval(() => void refreshHealth(), 5000)
})

onBeforeUnmount(() => {
  window.clearInterval(clockTimer)
  window.clearInterval(healthTimer)
})
</script>

<template>
  <div class="page">
    <header class="topbar">
      <div>
        <h1>基金持仓看板与定投决策系统</h1>
        <p class="sub">估值采集（蛋卷基金、中证指数） · 真实净值采集（天天基金）</p>
        <nav class="tabs">
          <button :class="{ active: tab === 'dashboard' }" @click="tab = 'dashboard'">持仓看板</button>
          <button :class="{ active: tab === 'import' }" @click="tab = 'import'">导入对账</button>
          <button :class="{ active: tab === 'archive' }" @click="tab = 'archive'">基金档案</button>
        </nav>
      </div>
      <div class="status" :class="statusClass">
        <span class="dot"></span>
        <template v-if="health === 'ok'">后端已连接 · {{ now }}</template>
        <template v-else-if="health === 'checking'">正在连接后端…</template>
        <template v-else>后端未就绪，正在重试…</template>
      </div>
    </header>

    <main>
      <DashboardView v-if="tab === 'dashboard'" />
      <ImportView v-else-if="tab === 'import'" @go-dashboard="tab = 'dashboard'" />
      <FundArchiveView v-else />
    </main>
  </div>
</template>
