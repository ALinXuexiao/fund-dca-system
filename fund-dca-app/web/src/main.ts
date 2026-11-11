import { createApp } from 'vue'
import './style.css'
import App from './App.vue'
import { startHostHeartbeat } from './heartbeat'

// 页面心跳与路由无关：挂载前就启动，任何页签下都能让后端知道「还有人看」
startHostHeartbeat()

createApp(App).mount('#app')
