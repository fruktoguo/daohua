import { useId } from 'react'
import { useT } from '../i18n'
import { useUiStore } from '../uiStore'
import { Icon } from './Icon'
import { Modal } from './Modal'

/**
 * 新手引导完成或跳过后展示的「帮助」引导提示弹窗。
 * 提示玩家可在「其他 → 帮助」随时查阅文本教程，并提供一键直达按钮。
 */
export function HelpPromptModal(): React.JSX.Element | null {
  const t = useT()
  const titleId = useId()
  const open = useUiStore(state => state.helpPromptOpen)
  const setHelpPromptOpen = useUiStore(state => state.setHelpPromptOpen)
  const navigateToHelp = useUiStore(state => state.navigateToHelp)

  if (!open) return null

  const paragraphs = t('app.help.prompt.body')
    .split('\n')
    .map(line => line.trim())
    .filter(Boolean)

  return (
    <Modal
      className="help-prompt-modal"
      labelledBy={titleId}
      onClose={() => setHelpPromptOpen(false)}
      open={open}
      size="medium"
    >
      <div className="help-prompt-container">
        <header className="help-prompt-header">
          <span aria-hidden="true" className="help-prompt-icon">
            <Icon name="help" size={20} />
          </span>
          <div className="help-prompt-title-group">
            <span className="help-prompt-kicker">{t('app.help.prompt.kicker')}</span>
            <h2 className="help-prompt-heading" id={titleId}>
              {t('app.help.prompt.title')}
            </h2>
          </div>
        </header>

        <div className="help-prompt-body">
          {paragraphs.map((line, index) => (
            <p key={index}>{line}</p>
          ))}
        </div>

        <footer className="help-prompt-footer">
          <button
            className="help-btn help-btn--secondary"
            onClick={() => setHelpPromptOpen(false)}
            type="button"
          >
            {t('app.help.prompt.dismiss')}
          </button>
          <button
            className="help-btn help-btn--primary"
            onClick={navigateToHelp}
            type="button"
          >
            <Icon name="help" size={14} />
            <span>{t('app.help.prompt.goHelp')}</span>
          </button>
        </footer>
      </div>
    </Modal>
  )
}
