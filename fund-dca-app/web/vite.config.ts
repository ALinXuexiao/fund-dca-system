import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 前端只与同源 /api 通信；开发期由 Vite 代理转发到本机 ASP.NET Core (5000)。
// 上云后把构建产物放到任意静态服务器，将 /api 指向云端域名即可，业务代码不变。
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5000' },
      '/health': { target: 'http://localhost:5000' },
    },
  },
  build: {
    rollupOptions: {
      output: {
        // 第三方依赖独立分块并长期缓存：vue/echarts 不随业务代码改动而失效；
        // echarts 体积大且仅看板使用，单独成块后与看板 chunk 并行下载
        manualChunks(id) {
          if (!id.includes('node_modules')) {
            return
          }
          if (id.includes('echarts') || id.includes('zrender')) {
            return 'echarts'
          }
          if (id.includes('@vue') || id.includes('vue')) {
            return 'vue'
          }
          return 'vendor'
        },
      },
    },
  },
})
