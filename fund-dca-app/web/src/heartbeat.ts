/**
 * 页面存活信号：告诉后端「这个页面还开着」。
 *
 * 两条信号，互补：
 *   ① 页面可见时每 30 秒 POST 一次 /api/system/host/heartbeat（轻量，只写内存时间戳）；
 *   ② 页面整个生命周期保持一条 SSE 长连接 /api/system/host/watch，服务端每 10 秒推一帧。
 *
 * 为什么后台不能只靠定时器：Chrome/Edge 会把隐藏页面的 setInterval 节流到约 1 分钟，
 * 离后端 90 秒的超时阈值太近。SSE 连接与页面可见性无关，切后台、最小化都会保持，
 * 所以「后台待多久都不会被判定关闭」；关掉标签页时连接才会断，后端 90 秒后回收进程，
 * 启动入口随即收掉前端开发服务器（见 scripts\start-app.ps1）。
 *
 * 页面隐藏时只停掉会出网的业务轮询与这条 30 秒心跳，不关闭长连接——后台只暂停、不退出。
 */

const HEARTBEAT_INTERVAL_MS = 30_000
const WATCH_URL = '/api/system/host/watch'

let timer: number | undefined
let watch: EventSource | undefined

function send(): void {
  // 心跳失败无需打扰用户：后端未就绪/正在退出时静默忽略。
  // keepalive 让浏览器在页面即将卸载时也能把请求发出去。
  void fetch('/api/system/host/heartbeat', { method: 'POST', keepalive: true }).catch(() => {})
}

function startBeat(): void {
  if (timer !== undefined) return
  send()
  timer = window.setInterval(send, HEARTBEAT_INTERVAL_MS)
}

function stopBeat(): void {
  if (timer === undefined) return
  window.clearInterval(timer)
  timer = undefined
}

/** 打开（或复用）页面存活长连接。断开由浏览器自动重连，这里不主动干预。 */
function openWatch(): void {
  if (watch !== undefined) return
  watch = new EventSource(WATCH_URL)
}

/** 关闭长连接：只在页面真正卸载时调用，之后后端才能按超时回收。 */
function closeWatch(): void {
  if (watch === undefined) return
  watch.close()
  watch = undefined
}

function syncWithVisibility(): void {
  // 长连接任何时候都要在：它才是「页面还活着」的可靠信号
  openWatch()
  if (document.visibilityState === 'visible') {
    // 回到前台立即补一次心跳，并恢复 30 秒节奏
    startBeat()
  } else {
    // 后台只降请求量，不放弃存活信号
    stopBeat()
  }
}

export function startHostHeartbeat(): void {
  document.addEventListener('visibilitychange', syncWithVisibility)
  // 从 bfcache 恢复时 visibilitychange 不一定触发，补一次同步
  window.addEventListener('pageshow', syncWithVisibility)
  // 页面被关闭/跳转/刷新时断开长连接并停止心跳，后端据此在 90 秒后回收
  window.addEventListener('pagehide', () => {
    stopBeat()
    closeWatch()
  })
  syncWithVisibility()
}
