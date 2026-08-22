import { useCallback } from 'react'
import { create } from 'zustand'
import { enUS } from './locales/en-US'
import { zhCN, type Messages } from './locales/zh-CN'

/**
 * 轻量 i18n 内核：
 * - 词典为嵌套对象，键用点路径访问；en-US 与 zh-CN 形状编译期强制一致。
 * - 文案内插使用 {name} 占位符，参数由调用方格式化后传入。
 * - 语言偏好持久化在 localStorage，切换时同步 <html lang>；组件渲染路径用 useT()
 *   获得响应式翻译，事件回调等非渲染路径用 getT()。
 * - 服务端下发的游戏内容（物品名、行动名、日志模板等协议数据）不在本模块范围。
 */
export const LOCALES = ['zh-CN', 'en-US'] as const
export type Locale = (typeof LOCALES)[number]
export type { Messages }

// 点路径联合类型：为静态字面量键提供编译期校验与自动补全；动态拼键仍可通过 string 放行。
type PathsOf<T> = T extends string
  ? never
  : { [K in keyof T & string]: T[K] extends string ? K : `${K}.${PathsOf<T[K]>}` }[keyof T & string]
export type TranslationKey = PathsOf<Messages>

export type TParams = Record<string, string | number>
export type TFunction = (key: TranslationKey | (string & {}), params?: TParams) => string

const DICTIONARIES: Record<Locale, Messages> = { 'zh-CN': zhCN, 'en-US': enUS }
const DEFAULT_LOCALE: Locale = 'zh-CN'
const STORAGE_KEY = 'daohua.locale'
const PARAM_PATTERN = /\{(\w+)\}/g

function isLocale(value: unknown): value is Locale {
  return typeof value === 'string' && (LOCALES as readonly string[]).includes(value)
}

function detectLocale(): Locale {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY)
    if (isLocale(stored)) return stored
  } catch {
    // 隐私模式等 localStorage 不可用场景：静默回退默认语言。
  }
  return DEFAULT_LOCALE
}

function applyDocumentLocale(locale: Locale): void {
  document.documentElement.lang = locale
}

interface I18nState {
  locale: Locale
  setLocale: (locale: Locale) => void
}

export const useI18nStore = create<I18nState>(set => ({
  locale: detectLocale(),
  setLocale: locale => {
    if (!isLocale(locale)) return
    try {
      window.localStorage.setItem(STORAGE_KEY, locale)
    } catch {
      // 持久化失败不阻断本次切换。
    }
    applyDocumentLocale(locale)
    set({ locale })
  },
}))

applyDocumentLocale(useI18nStore.getState().locale)

function lookup(dict: Messages, key: string): string | undefined {
  let node: unknown = dict
  for (const part of key.split('.')) {
    if (node === null || typeof node !== 'object') return undefined
    node = (node as Record<string, unknown>)[part]
  }
  return typeof node === 'string' ? node : undefined
}

function interpolate(template: string, params?: TParams): string {
  if (!params) return template
  return template.replace(PARAM_PATTERN, (raw, name: string) => {
    const value = params[name]
    return value === undefined ? raw : String(value)
  })
}

function translate(locale: Locale, key: string, params?: TParams): string {
  const value = lookup(DICTIONARIES[locale], key) ?? lookup(DICTIONARIES[DEFAULT_LOCALE], key) ?? key
  return interpolate(value, params)
}

/** 非响应式翻译：用于事件回调、格式化辅助函数等非渲染路径；渲染期由 useT() 的订阅驱动刷新。 */
export function getT(): TFunction {
  const locale = useI18nStore.getState().locale
  return (key, params) => translate(locale, key, params)
}

/** 响应式翻译：组件渲染路径统一使用；语言切换时触发订阅组件重渲染。 */
export function useT(): TFunction {
  const locale = useI18nStore(state => state.locale)
  return useCallback<TFunction>(
    (key, params) => translate(locale, key, params),
    [locale],
  )
}
