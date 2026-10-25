/// <reference types="vite/client" />

declare module '../../../docs/design/战斗演武画廊/vfx/particle-stage.js'
declare module '../../../docs/design/战斗演武画廊/vfx/skill-runtime.js'
declare module 'javascript-obfuscator' {
  const obfuscator: {
    obfuscate(sourceCode: string, customOptions?: Record<string, unknown>): {
      getObfuscatedCode(): string;
    };
  };
  export default obfuscator;
}

