// 宿主机维护/异常兜底页脚本：同步主题、周期探测服务恢复并遵守同源 CSP（无内联脚本）。
(() => {
  let theme = 'light'
  try {
    if (localStorage.getItem('daohua.theme') === 'dark') theme = 'dark'
  } catch {
    // 本地存储不可用时回退默认浅色主题。
  }
  document.documentElement.dataset.theme = theme
  document.documentElement.style.colorScheme = theme
  const colorSchemeMeta = document.querySelector('meta[name="color-scheme"]')
  if (colorSchemeMeta) colorSchemeMeta.setAttribute('content', theme)
  const themeColorMeta = document.querySelector('meta[name="theme-color"]')
  if (themeColorMeta) themeColorMeta.setAttribute('content', theme === 'dark' ? '#070604' : '#f1ecdc')

  const MODE_COPY = {
    SERVER_UPDATING: {
      badge: '版本更新 / 停机维护',
      title: '仙界更新维护中',
      subtitle: '服务器正在进行版本更新与数据迁移维护，暂不可接入。',
      note: '更新完成后将自动恢复开放，您的角色离线挂机与存档进度不受影响。',
      alert: false,
    },
    SERVER_UNAVAILABLE: {
      badge: '服务暂不可用 / 正在恢复',
      title: '灵脉暂歇 · 服务恢复中',
      subtitle: '服务器当前响应异常或正在重启恢复，暂无法建立连接。',
      note: '离线修为与存档安全保留，服务一旦恢复，本页将自动重新进入游戏。',
      alert: true,
    },
  }

  function applyMode(code) {
    const copy = MODE_COPY[code]
    if (!copy) return
    const badgeEl = document.getElementById('status-badge')
    const badgeTextEl = document.getElementById('status-badge-text')
    const titleEl = document.getElementById('status-title')
    const subtitleEl = document.getElementById('status-subtitle')
    const noteEl = document.getElementById('status-note')
    if (badgeTextEl) badgeTextEl.textContent = copy.badge
    if (badgeEl) badgeEl.classList.toggle('status-badge--alert', copy.alert)
    if (titleEl) titleEl.textContent = copy.title
    if (subtitleEl) subtitleEl.textContent = copy.subtitle
    if (noteEl) noteEl.textContent = copy.note
  }

  window.addEventListener('DOMContentLoaded', () => {
    const probeLine = document.getElementById('status-probe-line')
    const retryBtn = document.getElementById('status-retry-btn')
    const reloadBtn = document.getElementById('status-reload-btn')

    let nextDelaySec = 5
    let countdownSec = nextDelaySec
    let timerId = null
    let probing = false
    let recovered = false

    function setProbeText(text, isRecovered = false) {
      if (!probeLine) return
      probeLine.textContent = text
      probeLine.classList.toggle('is-recovered', isRecovered)
    }

    function clearLoop() {
      if (timerId !== null) {
        window.clearInterval(timerId)
        timerId = null
      }
    }

    async function probeServer(manual = false) {
      if (probing || recovered) return
      probing = true
      if (retryBtn) retryBtn.disabled = true
      setProbeText(manual ? '正在立即检测服务端状态…' : '正在自动检测服务端状态…')

      try {
        const controller = new AbortController()
        const timeout = window.setTimeout(() => controller.abort(), 5000)
        const response = await fetch('/api/v1/version', {
          method: 'GET',
          cache: 'no-store',
          headers: { Accept: 'application/json' },
          signal: controller.signal,
        })
        window.clearTimeout(timeout)

        if (response.ok) {
          const contentType = response.headers.get('content-type') || ''
          if (contentType.includes('application/json')) {
            recovered = true
            clearLoop()
            setProbeText('灵脉已重续，正在重入仙途…', true)
            window.setTimeout(() => {
              window.location.reload()
            }, 600)
            return
          }
        }

        if (response.status === 502 || response.status === 503 || response.status === 504) {
          try {
            const payload = await response.json()
            if (payload && typeof payload.code === 'string') {
              applyMode(payload.code)
            }
          } catch {
            // 非 JSON 错误体忽略覆盖，沿用当前页默认状态文案。
          }
        }
      } catch {
        // 网络或超时异常保持倒计时退避重试。
      } finally {
        probing = false
        if (!recovered) {
          if (retryBtn) retryBtn.disabled = false
          nextDelaySec = Math.min(12, nextDelaySec + 2)
          countdownSec = nextDelaySec + Math.floor(Math.random() * 2)
          setProbeText(`服务端尚未恢复，将在 ${countdownSec} 秒后再次检测…`)
        }
      }
    }

    function startLoop() {
      clearLoop()
      setProbeText(`将在 ${countdownSec} 秒后自动检测恢复状态…`)
      timerId = window.setInterval(() => {
        if (probing || recovered) return
        countdownSec -= 1
        if (countdownSec <= 0) {
          void probeServer(false)
        } else {
          setProbeText(`将在 ${countdownSec} 秒后自动检测恢复状态…`)
        }
      }, 1000)
    }

    if (retryBtn) {
      retryBtn.addEventListener('click', () => {
        nextDelaySec = 5
        void probeServer(true)
      })
    }
    if (reloadBtn) {
      reloadBtn.addEventListener('click', () => {
        window.location.reload()
      })
    }

    startLoop()
  })
})()
