
<template>
  <!--
    异步 chunk 下载期间显示的骨架屏（App.vue 的 loadingComponent）。
    目的是让「页面已打开但数据/代码还没到位」这段时间里，界面依然有完整结构，
    而不是一片空白或一个转圈图标。
  -->
  <div class="sk">
    <div class="sk-toolbar">
      <span class="sk-line w160"></span>
      <span class="sk-line w260"></span>
    </div>
    <div class="sk-cards">
      <div v-for="i in 4" :key="i" class="sk-card">
        <span class="sk-line w80"></span>
        <span class="sk-line w140 tall"></span>
      </div>
    </div>
    <div class="sk-panel">
      <span class="sk-line w220"></span>
      <span v-for="i in 8" :key="i" class="sk-line wfull"></span>
    </div>
    <p class="sk-hint">正在加载界面…</p>
  </div>
</template>
