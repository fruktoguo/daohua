import { useEffect, useMemo, useState, useSyncExternalStore } from 'react'
import { chatStickerService } from '../game/chatStickerService'

const subscribe = chatStickerService.subscribe.bind(chatStickerService)
const getGeneration = () => chatStickerService.getGeneration()

export function useChatImageGeneration(): number {
  return useSyncExternalStore(subscribe, getGeneration, getGeneration)
}

/** 只让当前图片请求使用租约；卸载或换图时释放，迟到的租约立即归还。 */
export function useChatImageSource(url: string | null, generation: number, sha256?: string | null): string | null {
  // 请求标识不是 URL：A → B → A 时也不能再次渲染已释放的第一张 A。
  const request = useMemo(() => ({ url, generation, sha256 }), [url, generation, sha256])
  const [image, setImage] = useState<{ request: typeof request; src: string } | null>(null)

  useEffect(() => {
    const { url, generation, sha256 } = request
    if (!url) return
    let active = true
    let release: (() => void) | undefined
    const isCurrent = () => active && generation === getGeneration()

    void chatStickerService.acquireImage(url, sha256).then(lease => {
      if (!isCurrent()) {
        lease.release()
        return
      }
      release = lease.release
      setImage({ request, src: lease.src })
    }, () => {
      if (isCurrent()) setImage({ request, src: url })
    })

    return () => {
      active = false
      release?.()
    }
  }, [request])

  return image?.request === request ? image.src : null
}
