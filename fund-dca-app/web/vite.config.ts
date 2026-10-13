import { createLogger, defineConfig } from 'vite'
import type { ProxyOptions } from 'vite'
import vue from '@vitejs/plugin-vue'

// 启动脚本只等前端就绪就打开浏览器，此时后端仍在编译/连云端库，页面会先显示骨架并
// 每秒轮询 /health；这期间 Vite 代理会按 error 级别打印一堆 AggregateError [ECONNREFUSED]
// 堆栈，看起来像前端崩了。这里把这一类预期错误过滤掉，其余日志原样保留。
const logger = createLogger()
const viteError = logger.error
logger.error = function (msg, options) {
  if (typeof msg === 'string' && /http proxy error/.test(msg) && /ECONNREFUSED|AggregateError/.test(msg)) {
    return
  }
  return viteError.call(logger, msg, options)
}

// 前端只与同源 /api 通信；开发期由 Vite 代理转发到本机 ASP.NET Core (5000)。
// 上云后把构建产物放到任意静态服务器，将 /api 指向云端域名即可，业务代码不变。

type ProxyErrorLike = Error & { code?: string }
type ProxyResLike = {
  headersSent?: boolean
  writeHead?: (statusCode: number, headers?: Record<string, string>) => void
  end?: (body?: string) => void
}

/**
 * 代理到后端，并把「后端还没起来」这种情况静默处理。
 *
 * 启动脚本只等前端端口就绪就打开浏览器，此时后端往往还在编译、执行 EF 迁移并连接
 * 云端 PostgreSQL，页面会先显示骨架并每秒轮询一次 /health。这期间代理必然连不上 5000，
 * 属于预期情况：这里回一个 503 并保持安静，避免 logs\frontend.err.log 里刷出一堆
 * AggregateError [ECONNREFUSED] 堆栈，看起来像前端崩了；其它代理错误仍照常打印。
 */
const backendProxy = (target: string): ProxyOptions => ({
  target,
  configure(proxy) {
    proxy.on('error', (err, _req, res) => {
      const e = err as unknown as ProxyErrorLike | undefined
      const r = res as unknown as ProxyResLike | undefined
      const text = `${e && e.code ? e.code : ''} ${e && e.message ? e.message : String(e)}`
      const starting = /ECONNREFUSED|AggregateError/i.test(text)
      if (!starting) {
        console.error('[vite] proxy error:', err)
      }
      if (r && !r.headersSent && typeof r.writeHead === 'function' && typeof r.end === 'function') {
        r.writeHead(starting ? 503 : 502, { 'Content-Type': 'application/json; charset=utf-8' })
        r.end(starting ? '{"status":"starting"}' : '{"status":"proxy_error"}')
      }
    })
  },
})

export default defineConfig({
  customLogger: logger,
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': backendProxy('http://localhost:5000'),
      '/health': backendProxy('http://localhost:5000'),
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
