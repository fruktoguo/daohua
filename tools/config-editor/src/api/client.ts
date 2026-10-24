// ==============================================================================
// 文件名称: client.ts
// 主要职责: 配置编辑器与本地 Vite API 插件的通信客户端
// 说明: /api/config/* 端点由 vite.config.ts 的 configStoragePlugin 中间件提供，
//       仅存在于本地开发 dev server；生产构建不含该插件。
// ==============================================================================

import type { AllGameConfigFiles, ConfigFileName } from '../types/config';

/** Vite API 插件统一响应信封。ok 为业务成败；output/rawStdout/rawStderr 承载 DataTool 校验结果，message/error 为人类可读提示。 */
export interface ApiResponse<T = any> {
  ok: boolean;
  data?: T;
  error?: string;
  message?: string;
  output?: any;
  stderr?: string;
  stdout?: string;
  rawStdout?: string;
  rawStderr?: string;
}

/** 拉取全量配置：Vite 插件递归读取 config/game-data 目录树并按逻辑文件名聚合（子目录条目附加 _sourceFile 归属标记）。失败时抛出带后端错误信息的异常。 */
export async function fetchAllConfigs(): Promise<AllGameConfigFiles> {
  const res = await fetch('/api/config/all');
  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: '网络错误' }));
    throw new Error(err.error || '无法读取游戏配置文件');
  }
  const body = await res.json();
  return body.data;
}

/** 保存单个逻辑配置文件。数组类数据按条目 _sourceFile 分组写回对应子目录文件；注意返回的是响应信封而非抛异常，调用方须检查 ok 字段。 */
export async function saveSingleConfigFile<K extends ConfigFileName>(
  filename: K,
  data: AllGameConfigFiles[K]
): Promise<ApiResponse> {
  const res = await fetch(`/api/config/file/${encodeURIComponent(filename)}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ data })
  });
  return res.json();
}

/** 批量保存多个逻辑配置文件（仅传 dirty 文件），插件写盘前会先对全目录做时间戳快照备份。 */
export async function saveAllConfigFiles(
  files: Partial<AllGameConfigFiles>
): Promise<ApiResponse> {
  const res = await fetch('/api/config/save-all', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ files })
  });
  return res.json();
}

/** 触发后端 DataTool 离线校验（dotnet run --project src/DataTool -- validate config/game-data），返回校验报告 JSON 或失败 stderr。 */
export async function validateDomainConfigs(): Promise<ApiResponse> {
  const res = await fetch('/api/config/validate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' }
  });
  return res.json();
}

/** 手动创建全量配置快照备份到 config/game-data/.backup/（文件名带 ISO 时间戳）。 */
export async function createConfigBackup(): Promise<ApiResponse> {
  const res = await fetch('/api/config/backup', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' }
  });
  return res.json();
}
