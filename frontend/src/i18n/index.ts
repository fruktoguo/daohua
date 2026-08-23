// ==============================================================================
// 文件名称: index.ts
// 主要职责: 前端轻量国际化 (i18n) 内核与状态调度中心。
// 架构设计:
//   1. 中文 (zh-CN) 为内置 TypeScript 静态类型源，提供全量编译期点路径检查与自动补全；
//   2. 外语语言包 (如 en-US) 存储为 public/locales/*.csv 静态文件，按需异步拉取与内存缓存；
//   3. 零依赖轻量 RFC 4180 CSV 解析器，未命中或网络加载中无缝回退至中文树状词典。
// ==============================================================================
import { useCallback } from 'react'
import { create } from 'zustand'
import { zhCN, type Messages } from './locales/zh-CN'

export const LOCALES = ['zh-CN'] as const
export type Locale = (typeof LOCALES)[number] | (string & {})
export type { Messages }

// 点路径联合类型：为静态字面量键提供编译期校验与自动补全；动态拼键仍可通过 string 放行。
type PathsOf<T> = T extends string
  ? never
  : { [K in keyof T & string]: T[K] extends string ? K : `${K}.${PathsOf<T[K]>}` }[keyof T & string]
export type TranslationKey = PathsOf<Messages>

export type TParams = Record<string, string | number>
export type TFunction = (key: TranslationKey | (string & {}), params?: TParams) => string

const DEFAULT_LOCALE: Locale = 'zh-CN'
const STORAGE_KEY = 'daohua.locale'
const PARAM_PATTERN = /\{(\w+)\}/g

/** 外部动态加载 CSV 语言包内存缓存 (扁平 key-value 映射) */
const CSV_CACHE: Partial<Record<Locale, Record<string, string>>> = {}
const LOADING_PROMISES: Partial<Record<Locale, Promise<void>>> = {}

/**
 * 零依赖轻量 RFC 4180 标准 CSV 解析器
 * 支持引号转义 ("")、跨行换行、逗号与多余空白容错。
 */
export function parseCsvToMap(csvText: string): Record<string, string> {
  const result: Record<string, string> = {}
  const length = csvText.length
  let cursor = 0

  function readField(): { value: string; isLastInRecord: boolean; isEof: boolean } {
    if (cursor >= length) return { value: '', isLastInRecord: true, isEof: true }

    let field = ''
    if (csvText[cursor] === '"') {
      cursor++ // 跳过起始双引号
      while (cursor < length) {
        if (csvText[cursor] === '"') {
          if (cursor + 1 < length && csvText[cursor + 1] === '"') {
            field += '"'
            cursor += 2
          } else {
            cursor++ // 闭合双引号
            break
          }
        } else {
          field += csvText[cursor]
          cursor++
        }
      }
      while (cursor < length && (csvText[cursor] === ' ' || csvText[cursor] === '\t')) {
        cursor++
      }
    } else {
      while (cursor < length && csvText[cursor] !== ',' && csvText[cursor] !== '\r' && csvText[cursor] !== '\n') {
        field += csvText[cursor]
        cursor++
      }
    }

    let isLastInRecord = false
    if (cursor >= length) {
      isLastInRecord = true
    } else if (csvText[cursor] === ',') {
      cursor++
    } else if (csvText[cursor] === '\r' || csvText[cursor] === '\n') {
      isLastInRecord = true
      if (csvText[cursor] === '\r' && cursor + 1 < length && csvText[cursor + 1] === '\n') {
        cursor += 2
      } else {
        cursor++
      }
    }

    return { value: field, isLastInRecord, isEof: cursor >= length }
  }

  let isHeader = true
  while (cursor < length) {
    const keyField = readField()
    if (keyField.isEof && keyField.value.trim() === '') break

    let valField = { value: '', isLastInRecord: true, isEof: true }
    if (!keyField.isLastInRecord) {
      valField = readField()
    }
    while (!valField.isLastInRecord && !valField.isEof) {
      valField = readField()
    }

    if (isHeader) {
      isHeader = false
      if (keyField.value.trim().toLowerCase() === 'key') {
        continue
      }
    }

    const k = keyField.value.trim()
    if (k) {
      result[k] = valField.value
    }
  }

  return result
}

/** 动态异步加载外部 CSV 语言包 */
export async function loadLocale(locale: Locale): Promise<void> {
  if (locale === 'zh-CN' || CSV_CACHE[locale]) return
  if (LOADING_PROMISES[locale]) return LOADING_PROMISES[locale]

  LOADING_PROMISES[locale] = (async () => {
    try {
      const res = await fetch(`/locales/${locale}.csv`)
      if (!res.ok) {
        throw new Error(`HTTP ${res.status} ${res.statusText}`)
      }
      const text = await res.text()
      CSV_CACHE[locale] = parseCsvToMap(text)
    } catch (err) {
      console.warn(`[i18n] 加载语言包 "${locale}" 失败，将回退至中文:`, err)
    } finally {
      delete LOADING_PROMISES[locale]
    }
  })()

  return LOADING_PROMISES[locale]
}

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
  loadedLocale: Locale
  setLocale: (locale: Locale) => Promise<void>
}

const initialLocale = detectLocale()

export const useI18nStore = create<I18nState>((set, get) => ({
  locale: initialLocale,
  loadedLocale: initialLocale === 'zh-CN' ? 'zh-CN' : (CSV_CACHE[initialLocale] ? initialLocale : 'zh-CN'),
  setLocale: async locale => {
    if (!isLocale(locale)) return
    try {
      window.localStorage.setItem(STORAGE_KEY, locale)
    } catch {
      // 持久化失败不阻断本次切换。
    }
    applyDocumentLocale(locale)
    set({ locale })

    if (locale !== 'zh-CN' && !CSV_CACHE[locale]) {
      await loadLocale(locale)
    }
    if (get().locale === locale) {
      set({ loadedLocale: locale })
    }
  },
}))

applyDocumentLocale(initialLocale)

// 若首屏初始化为外语，立即在后台发起静态 CSV 预拉取
if (initialLocale !== 'zh-CN') {
  loadLocale(initialLocale).then(() => {
    if (useI18nStore.getState().locale === initialLocale) {
      useI18nStore.setState({ loadedLocale: initialLocale })
    }
  })
}

function flattenDict(obj: unknown, prefix = ''): Record<string, string> {
  const result: Record<string, string> = {}
  if (obj === null || typeof obj !== 'object' || Array.isArray(obj)) return result
  for (const [key, value] of Object.entries(obj)) {
    const fullKey = prefix ? `${prefix}.${key}` : key
    if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
      Object.assign(result, flattenDict(value, fullKey))
    } else if (value !== undefined) {
      result[fullKey] = String(value)
    }
  }
  return result
}

const ZH_FLAT_CACHE: Record<string, string> = flattenDict(zhCN)

function lookup(dict: Messages, key: string): string | undefined {
  if (ZH_FLAT_CACHE[key] !== undefined) {
    return ZH_FLAT_CACHE[key]
  }
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
  let template: string | undefined
  if (locale !== 'zh-CN' && CSV_CACHE[locale]) {
    template = CSV_CACHE[locale][key]
  }
  if (template === undefined) {
    template = lookup(zhCN, key) ?? key
  }
  return interpolate(template, params)
}

/** 非响应式翻译：用于事件回调、格式化辅助函数等非渲染路径；渲染期由 useT() 的订阅驱动刷新。 */
export function getT(): TFunction {
  const locale = useI18nStore.getState().locale
  return (key, params) => translate(locale, key, params)
}

/** 响应式翻译：组件渲染路径统一使用；语言切换或 CSV 完成异步加载时触发订阅组件重渲染。 */
export function useT(): TFunction {
  const locale = useI18nStore(state => state.locale)
  const loadedLocale = useI18nStore(state => state.loadedLocale)
  return useCallback<TFunction>(
    (key, params) => translate(locale, key, params),
    [locale, loadedLocale],
  )
}
