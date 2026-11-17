import { useCallback, useEffect, useRef } from 'react'

/**
 * 卸载安全的定时器调度：返回的调度函数行为与 window.setTimeout 等价，
 * 组件卸载时自动清理全部在途定时器，避免卸载后回调触发 setState 或外部副作用。
 */
export function useMountedTimeout(): (callback: () => void, delayMs: number) => void {
  const timersRef = useRef<Set<number>>(new Set())
  useEffect(() => {
    const timers = timersRef.current
    return () => {
      for (const id of timers) window.clearTimeout(id)
      timers.clear()
    }
  }, [])
  return useCallback((callback: () => void, delayMs: number) => {
    const id = window.setTimeout(() => {
      timersRef.current.delete(id)
      callback()
    }, delayMs)
    timersRef.current.add(id)
  }, [])
}
