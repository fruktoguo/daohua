// ==============================================================================
// 文件名称: client.ts
// 主要职责: 配置编辑器与本地 Vite API 插件的通信客户端
// ==============================================================================

import type { AllGameConfigFiles, ConfigFileName } from '../types/config';

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

export async function fetchAllConfigs(): Promise<AllGameConfigFiles> {
  const res = await fetch('/api/config/all');
  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: '网络错误' }));
    throw new Error(err.error || '无法读取游戏配置文件');
  }
  const body = await res.json();
  return body.data;
}

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

export async function validateDomainConfigs(): Promise<ApiResponse> {
  const res = await fetch('/api/config/validate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' }
  });
  return res.json();
}

export async function createConfigBackup(): Promise<ApiResponse> {
  const res = await fetch('/api/config/backup', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' }
  });
  return res.json();
}
