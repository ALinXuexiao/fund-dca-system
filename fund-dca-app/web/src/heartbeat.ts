/**
 * 页面心跳：告诉后端「还有人看着这个页面」。
 *
 * 页面可见时每 30 秒 POST 一次 /api/system/host/heartbeat（接口只写内存时间戳，
 * 不查库、不访问行情）；页面隐藏（切标签/最小化）即停止，回前台立刻补一次。
 * 关闭标签页后心跳自然消失，后端 90 秒内判定无人查看并优雅退出，
 * 启动入口随后把前端开发服务器一并收掉（见 scripts\start-app.ps1）。
 */

const HEARTBEAT_INTERVAL_MS = 30_000

let timer: number | undefined

function send(): void {
  // 心跳失败无需打扰用户：后端未就绪/正在退出时静默忽略。
  // keepalive 让浏览器在页面即将卸载时也能把请求发出去。
  void fetch('/api/system/host/heartbeat', { method: 'POST', keepalive: true }).catch(() => {})
}

function start(): void {
  if (timer !== undefined) return
  send()
  timer = window.setInterval(send, HEARTBEAT_INTERVAL_MS)
}

function stop(): void {
  if (timer === undefined) return
  window.clearInterval(timer)
  timer = undefined
}

function syncWithVisibility(): void {
  if (document.visibilityState === 'visible') {
    // 回到前台立即补一次，避免隐藏期间积累的静默被误判成「页面已关闭」
    start()
  } else {
    stop()
  }
}

export function startHostHeartbeat(): void {
  document.addEventListener('visibilitychange', syncWithVisibility)
  // 从 bfcache 恢复时 visibilitychange 不一定触发，补一次同步
  window.addEventListener('pageshow', syncWithVisibility)
  // 页面被关闭/跳转时停止定时器（此后浏览器不再发心跳）
  window.addEventListener('pagehide', stop)
  syncWithVisibility()
}
