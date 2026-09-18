import { Component, type ErrorInfo, type ReactNode } from 'react'
import { getT } from '../i18n'
import { resetAllLocalData } from '../network/account'

interface ErrorBoundaryProps {
  children: ReactNode
}

interface ErrorBoundaryState {
  hasError: boolean
  error: Error | null
  errorInfo: ErrorInfo | null
  showDetails: boolean
  copied: boolean
}

/**
 * 道化太初 全局顶层错误边界 (ErrorBoundary)
 * 
 * 职责：
 * 1. 拦截全组件树渲染异常，杜绝全页面白屏；
 * 2. 提供仙侠风友好降级屏，告知修士数据安全；
 * 3. 提供一键重新显化 (Re-render) 与 一键清理损坏缓存并重启 (Self-heal) 选项；
 * 4. 提供错误堆栈折叠展示与一键复制功能便于排障。
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  constructor(props: ErrorBoundaryProps) {
    super(props)
    this.state = {
      hasError: false,
      error: null,
      errorInfo: null,
      showDetails: false,
      copied: false,
    }
  }

  static getDerivedStateFromError(error: Error): Partial<ErrorBoundaryState> {
    return { hasError: true, error }
  }

  override componentDidCatch(error: Error, errorInfo: ErrorInfo): void {
    this.setState({ errorInfo })
    console.error('[ErrorBoundary] 捕获到未处理的渲染异常:', error, errorInfo)
  }

  handleReset = (): void => {
    this.setState({
      hasError: false,
      error: null,
      errorInfo: null,
      showDetails: false,
      copied: false,
    })
  }

  handleClearCacheAndReload = (): void => {
    resetAllLocalData()
    window.location.reload()
  }

  handleCopyError = (): void => {
    const { error, errorInfo } = this.state
    const content = [
      `【道化太初 异常报告】`,
      `时间: ${new Date().toISOString()}`,
      `错误: ${error?.name ?? 'Error'}: ${error?.message ?? 'Unknown'}`,
      `堆栈:\n${error?.stack ?? '无'}`,
      `组件调用栈:\n${errorInfo?.componentStack ?? '无'}`,
    ].join('\n\n')

    if (navigator.clipboard) {
      navigator.clipboard.writeText(content).then(() => {
        this.setState({ copied: true })
        setTimeout(() => this.setState({ copied: false }), 3000)
      }).catch(() => {
        // 剪贴板权限被拒或不可用时静默跳过：复制失败不应让降级屏自身再抛异常。
      })
    }
  }

  override render(): ReactNode {
    if (this.state.hasError) {
      const t = getT()
      const { error, errorInfo, showDetails, copied } = this.state

      return (
        <div className="status-screen error-screen" role="alert">
          <div className="motes" aria-hidden="true">
            <i /><i /><i /><i /><i /><i />
          </div>
          <div className="status-card panel error-boundary-card">
            <div className="status-error-icon" aria-hidden="true">
              <span>⚠️</span>
            </div>
            <h2 className="status-title">{t('app.errorBoundary.title')}</h2>
            <p className="status-subtitle">
              {t('app.errorBoundary.subtitle')}
            </p>

            <div className="status-actions">
              <button
                type="button"
                className="status-btn primary"
                onClick={this.handleReset}
              >
                {t('app.errorBoundary.reRenderBtn')}
              </button>
              <button
                type="button"
                className="status-btn secondary"
                onClick={this.handleClearCacheAndReload}
              >
                {t('app.errorBoundary.resetCacheBtn')}
              </button>
            </div>

            <div className="error-boundary-extra">
              <button
                type="button"
                className="error-details-toggle"
                onClick={() => this.setState(prev => ({ showDetails: !prev.showDetails }))}
              >
                {showDetails
                  ? `▼ ${t('app.errorBoundary.collapseDetails')}`
                  : `▶ ${t('app.errorBoundary.detailsLabel')}`}
              </button>

              <button
                type="button"
                className="error-copy-btn"
                onClick={this.handleCopyError}
              >
                {copied ? t('app.errorBoundary.copiedNotice') : t('app.errorBoundary.copyErrorBtn')}
              </button>
            </div>

            {showDetails && (
              <div className="error-stack-container">
                <pre className="error-stack-text">
                  {error?.toString()}
                  {'\n\n'}
                  {error?.stack}
                  {errorInfo?.componentStack}
                </pre>
              </div>
            )}
          </div>
        </div>
      )
    }

    return this.props.children
  }
}
