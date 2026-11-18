/** 写入剪贴板；局域网 HTTP 等场景保留原生复制回退。 */
export async function copyTextToClipboard(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    // 浏览器拒绝异步写入时使用原生复制命令。
  }
  const input = document.createElement('textarea')
  input.value = text
  input.readOnly = true
  input.style.cssText = 'position:fixed;top:0;left:0;opacity:0;pointer-events:none'
  document.body.appendChild(input)
  const focused = document.activeElement
  try {
    input.select()
    input.setSelectionRange(0, input.value.length)
    return document.execCommand('copy')
  } catch {
    return false
  } finally {
    input.remove()
    if (focused instanceof HTMLElement) focused.focus({ preventScroll: true })
  }
}
