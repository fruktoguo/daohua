// 首屏在样式与 React 加载前恢复主题，使用独立脚本以遵守同源 CSP。
(() => {
  let theme = 'light'
  try {
    if (localStorage.getItem('daohua.theme') === 'dark') theme = 'dark'
  } catch {
    // 本地存储不可用时以浅色进入，后续切换仍可在当前会话生效。
  }
  document.documentElement.dataset.theme = theme
  document.documentElement.style.colorScheme = theme
  document.querySelector('meta[name="color-scheme"]').content = theme
  document.querySelector('meta[name="theme-color"]').content = theme === 'dark' ? '#070604' : '#f1ecdc'
})()
