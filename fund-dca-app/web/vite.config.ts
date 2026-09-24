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
})
