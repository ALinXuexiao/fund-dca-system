
<script setup lang="ts">
// Vue 的异步组件会把 errorComponent 渲染到加载失败的位置，
// 并传入 { error, retry } 两个 prop
defineProps<{
  error?: Error
  retry?: () => void
}>()
</script>

<template>
  <div class="error-box">
    <b>界面加载失败：</b>{{ error?.message ?? '未知错误' }}
    <div style="margin-top: 6px; color: #8a6d3b">多为开发服务器瞬时中断，点击重试通常即可恢复。</div>
    <button class="btn" style="margin-top: 10px" @click="retry && retry()">重试</button>
  </div>
</template>
