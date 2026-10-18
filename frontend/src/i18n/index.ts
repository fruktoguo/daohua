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

// 构建时只注册实际存在的语言包；模板本身不是可选语言。
const localeFiles = typeof (import.meta as any).glob === 'function'
  ? (import.meta as any).glob(['/public/locales/*.csv', '!/public/locales/template.csv'], { query: '?url', import: 'default' })
  : {}
/** 全部可选语言标识（zh-CN 内置 + 构建期扫描到的 CSV 语言包文件名） */
export const LOCALES: readonly string[] = ['zh-CN', ...Object.keys(localeFiles)
  .map(path => path.split('/').at(-1)!.slice(0, -4))
  .filter(locale => locale !== 'template' && locale !== 'zh-CN')
  .sort()]
/** 语言标识（以注册表为准，宽松放行 string 以兼容运行时动态值） */
export type Locale = (typeof LOCALES)[number] | (string & {})
export type { Messages }

// 点路径联合类型：为静态字面量键提供编译期校验与自动补全；动态拼键仍可通过 string 放行。
type PathsOf<T> = T extends string
  ? never
  : { [K in keyof T & string]: T[K] extends string ? K : `${K}.${PathsOf<T[K]>}` }[keyof T & string]
/** 词条点路径键类型（由 zh-CN 词典推导出的全部合法路径联合） */
export type TranslationKey = PathsOf<Messages>

/** 词条插值参数表（对应模板中的 {name} 占位符） */
export type TParams = Record<string, string | number>
/** 翻译函数签名：点路径键查词条并做 {name} 参数插值 */
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
      let closed = false
      cursor++ // 跳过起始双引号
      while (cursor < length) {
        if (csvText[cursor] === '"') {
          if (cursor + 1 < length && csvText[cursor + 1] === '"') {
            field += '"'
            cursor += 2
          } else {
            cursor++ // 闭合双引号
            closed = true
            break
          }
        } else {
          field += csvText[cursor]
          cursor++
        }
      }
      if (!closed) throw new Error('语言包 CSV 引号未闭合。')
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
    } else {
      throw new Error('语言包 CSV 字段分隔符无效。')
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
    if (k && valField.value.trim()) {
      result[k] = valField.value
    }
  }

  return result
}

/** 动态异步加载外部 CSV 语言包 */
export async function loadLocale(locale: Locale): Promise<void> {
  if (!isLocale(locale)) throw new Error(`未注册的语言包：${locale}`)
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
      throw new Error(`语言包 ${locale} 加载失败。`, { cause: err })
    } finally {
      delete LOADING_PROMISES[locale]
    }
  })()

  return LOADING_PROMISES[locale]
}

/** 校验运行时值是否为已注册语言标识 */
function isLocale(value: unknown): value is Locale {
  return typeof value === 'string' && (LOCALES as readonly string[]).includes(value)
}

/** 读取持久化的语言偏好，缺失或非法时回退默认语言 */
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
  if (typeof document !== 'undefined') {
    document.documentElement.lang = locale
  }
}

/** i18n 内核状态 */
interface I18nState {
  /** 用户当前选择的语言（切换立即生效并持久化） */
  locale: Locale
  /** 词条包实际就绪的语言：外语 CSV 异步加载完成前保持 'zh-CN'，作为 useT 的重渲染触发信号 */
  loadedLocale: Locale
  /** 最近一次语言包加载失败信息 */
  error: string | null
  /** 切换语言：持久化选择并异步加载对应 CSV 包；加载失败回退中文并记录 error */
  setLocale: (locale: Locale) => Promise<void>
}

const initialLocale = detectLocale()

/** i18n 状态 Store（语言选择、就绪状态与加载错误） */
export const useI18nStore = create<I18nState>((set, get) => ({
  locale: initialLocale,
  error: null,
  loadedLocale: initialLocale === 'zh-CN' ? 'zh-CN' : (CSV_CACHE[initialLocale] ? initialLocale : 'zh-CN'),
  setLocale: async locale => {
    if (!isLocale(locale)) return
    try {
      window.localStorage.setItem(STORAGE_KEY, locale)
    } catch {
      // 持久化失败不阻断本次切换。
    }
    applyDocumentLocale(locale)
    set({ locale, error: null })

    if (locale !== 'zh-CN' && !CSV_CACHE[locale]) {
      try {
        await loadLocale(locale)
      } catch (error) {
        if (get().locale === locale) set({ loadedLocale: 'zh-CN', error: (error as Error).message })
        return
      }
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
  }).catch(error => {
    if (useI18nStore.getState().locale === initialLocale) useI18nStore.setState({ error: (error as Error).message })
  })
}

/** 将嵌套词典树展平为点路径→词条的扁平映射（换取 O(1) 查询） */
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

/** 查词条并插值：外语 CSV 未命中（未加载或缺键）时逐级回退中文词典，最终回显键名本身 */
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
