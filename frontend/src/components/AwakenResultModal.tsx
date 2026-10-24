import React, { memo, useEffect, useId, useMemo, useRef, useState } from 'react'
import { Modal } from './Modal'
import { TRAIT_DEFINITIONS } from '../game/combatConstants'
import { useUiStore } from '../uiStore'
import { TraitBarItem } from './TraitBarItem'

/**
 * 觉醒抽卡独立展示弹窗 (AwakenResultModal)
 * 展示使用觉醒石抽到的特质长条，以每秒5个速度逐个动画弹现
 */
export const AwakenResultModal = memo(function AwakenResultModal(): React.JSX.Element | null {
  const titleId = useId()
  const awakenResults = useUiStore(state => state.awakenResults)
  const closeAwakenResult = useUiStore(state => state.closeAwakenResult)
  const listEndRef = useRef<HTMLDivElement | null>(null)

  // 聚合抽卡结果：按首次出现顺序保留特质长条，汇总数量并标记本次是否产生新觉醒
  const aggregatedList = useMemo(() => {
    if (!awakenResults || awakenResults.length === 0) return []
    const map = new Map<string, {
      traitId: string
      count: number
      level: number
      exp: number
      isNew: boolean
    }>()

    for (const item of awakenResults) {
      const existing = map.get(item.traitId)
      if (existing) {
        existing.count += 1
        existing.level = item.level
        existing.exp = item.exp
        if (item.isNew) existing.isNew = true
      } else {
        map.set(item.traitId, {
          traitId: item.traitId,
          count: 1,
          level: item.level,
          exp: item.exp,
          isNew: item.isNew,
        })
      }
    }

    return Array.from(map.values())
      .map(item => {
        const def = TRAIT_DEFINITIONS[item.traitId]
        if (!def) return null
        return {
          ...item,
          def,
        }
      })
      .filter((item): item is NonNullable<typeof item> => item !== null)
  }, [awakenResults])

  // 动画逐个弹出：每秒 5 个（200ms 一个）
  const [visibleCount, setVisibleCount] = useState(0)
  const total = aggregatedList.length

  useEffect(() => {
    if (total === 0) {
      setVisibleCount(0)
      return
    }
    // 初始展示 1 个，启动每 200ms 一个的定时推进
    setVisibleCount(1)
    const interval = window.setInterval(() => {
      setVisibleCount(prev => {
        if (prev >= total) {
          window.clearInterval(interval)
          return prev
        }
        return prev + 1
      })
    }, 200)

    return () => window.clearInterval(interval)
  }, [total])

  // 每当弹出新条目时自动平滑下滚
  useEffect(() => {
    if (visibleCount > 0 && visibleCount <= total) {
      listEndRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' })
    }
  }, [visibleCount, total])

  if (!awakenResults || aggregatedList.length === 0) return null

  const isAllVisible = visibleCount >= total

  // 点击确定：如果动画未完则瞬间跳过展示全部；若已全部展示则关闭
  const handleConfirmOrSkip = () => {
    if (!isAllVisible) {
      setVisibleCount(total)
    } else {
      closeAwakenResult()
    }
  }

  return (
    <Modal labelledBy={titleId} onClose={closeAwakenResult} open size="medium">
      <div className="awaken-result-dialog">
        <header className="awaken-result-header">
          <h2 id={titleId} className="awaken-result-title">
            先天觉醒
          </h2>
          <p className="awaken-result-subtitle text-dim">
            天地玄机初露，气运造化入体。
          </p>
        </header>

        <div className="awaken-result-body">
          <div className="awaken-result-list">
            {aggregatedList.slice(0, visibleCount).map((item, index) => (
              <TraitBarItem
                key={item.traitId}
                className="awaken-pop-in"
                def={item.def}
                exp={item.exp}
                isNew={item.isNew}
                level={item.level}
                mode="awaken"
                quantity={item.count}
                style={{ animationDelay: `${index === visibleCount - 1 ? 0 : 0}ms` }}
              />
            ))}
            <div ref={listEndRef} />
          </div>
        </div>

        <footer className="awaken-result-actions">
          <button
            className="confirm-primary awaken-confirm-btn"
            onClick={handleConfirmOrSkip}
            type="button"
          >
            {isAllVisible ? '收下' : '跳过'}
          </button>
        </footer>
      </div>
    </Modal>
  )
})
