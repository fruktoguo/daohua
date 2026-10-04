// ==============================================================================
// 文件名称: App.tsx
// 主要职责: 配置编辑器主应用容器，路由切换 9 大专有编辑器面板与全局快捷键绑定
// ==============================================================================

import { useEffect } from 'react';
import { ConfigProvider, useConfigContext } from './context/ConfigContext';
import { Sidebar } from './components/layout/Sidebar';
import { Header } from './components/layout/Header';

import { ManifestEditor } from './components/editors/ManifestEditor';
import { ActionsEditor } from './components/editors/ActionsEditor';
import { ItemsEditor } from './components/editors/ItemsEditor';
import { AbilitiesEditor } from './components/editors/AbilitiesEditor';
import { EquipmentEditor } from './components/editors/EquipmentEditor';
import { MonstersEditor } from './components/editors/MonstersEditor';
import { EncountersEditor } from './components/editors/EncountersEditor';
import { MapsEditor } from './components/editors/MapsEditor';
import { ExpectedAttributesEditor } from './components/editors/ExpectedAttributesEditor';

/** 编辑器主布局：装载/错误全屏态、Ctrl+S 保存快捷键与按 activeFile 切换九大编辑面板。 */
function EditorLayout() {
  const { activeFile, loading, error, saveCurrentFile, reloadAllConfigs } = useConfigContext();

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      // 拦截浏览器默认 Ctrl/Cmd+S（打开保存网页对话框），改触当前文件保存。
      if ((e.ctrlKey || e.metaKey) && e.key === 's') {
        e.preventDefault();
        saveCurrentFile();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [saveCurrentFile]);

  if (loading) {
    return (
      <div className="h-screen w-screen bg-zinc-950 flex flex-col items-center justify-center gap-3 text-zinc-400 select-none">
        <div className="w-8 h-8 border-2 border-indigo-500 border-t-transparent rounded-full animate-spin" />
        <span className="text-xs font-medium text-zinc-300">正在加载游戏全量配置文件...</span>
      </div>
    );
  }

  if (error) {
    return (
      <div className="h-screen w-screen bg-zinc-950 flex flex-col items-center justify-center gap-4 text-zinc-300 p-6 select-none">
        <div className="p-6 bg-zinc-900 border border-rose-500/30 rounded-2xl max-w-lg text-center flex flex-col items-center gap-3 shadow-2xl">
          <div className="w-10 h-10 rounded-full bg-rose-500/10 text-rose-400 flex items-center justify-center font-bold text-lg">
            ✕
          </div>
          <span className="text-base font-bold text-zinc-100">无法读取游戏配置</span>
          <p className="text-xs text-zinc-400 leading-relaxed">{error}</p>
          <button
            type="button"
            onClick={reloadAllConfigs}
            className="mt-2 px-4 py-2 bg-indigo-600 hover:bg-indigo-500 text-white text-xs font-semibold rounded-lg transition-colors shadow-sm"
          >
            重试连接
          </button>
        </div>
      </div>
    );
  }

  const renderActiveEditor = () => {
    switch (activeFile) {
      case 'manifest.json':
        return <ManifestEditor />;
      case 'actions.json':
        return <ActionsEditor />;
      case 'items.json':
        return <ItemsEditor />;
      case 'abilities.json':
        return <AbilitiesEditor />;
      case 'equipment.json':
        return <EquipmentEditor />;
      case 'monsters.json':
        return <MonstersEditor />;
      case 'encounters.json':
        return <EncountersEditor />;
      case 'maps.json':
        return <MapsEditor />;
      case 'expected-attributes.json':
        return <ExpectedAttributesEditor />;
      default:
        return <div className="p-8 text-zinc-500 text-sm">未知配置文件类型</div>;
    }
  };

  return (
    <div className="h-screen w-screen flex bg-zinc-950 text-zinc-100 overflow-hidden font-sans">
      <Sidebar />
      <div className="flex-1 flex flex-col min-w-0 overflow-hidden">
        <Header />
        <main className="flex-1 overflow-hidden flex bg-zinc-950">
          {renderActiveEditor()}
        </main>
      </div>
    </div>
  );
}

/** 应用根组件：以 ConfigProvider 包裹编辑器布局，全站共享配置状态。 */
export default function App() {
  return (
    <ConfigProvider>
      <EditorLayout />
    </ConfigProvider>
  );
}
