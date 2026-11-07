/// <reference types="vite/client" />
declare module 'javascript-obfuscator' {
  const obfuscator: {
    obfuscate(sourceCode: string, customOptions?: Record<string, unknown>): {
      getObfuscatedCode(): string;
    };
  };
  export default obfuscator;
}

interface DaoHuaNativeBridge {
  getAppVersion(): string;
  getGitSha(): string;
  checkUpdate(): void;
  getChannel?(): string;
  hasNativeTapTapLogin?(): boolean;
  startNativeTapTapLogin?(): void;
  getCurrentTapTapAccount?(): string | null;
}

interface TapTapNativeLoginResult {
  success: boolean;
  openId?: string;
  unionId?: string;
  name?: string;
  kid?: string;
  macKey?: string;
  error?: string;
}

interface Window {
  DaoHuaNative?: DaoHuaNativeBridge;
  onTapTapNativeLogin?: (result: TapTapNativeLoginResult) => void;
}

