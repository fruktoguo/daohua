/// <reference types="vite/client" />
declare module 'javascript-obfuscator' {
  const obfuscator: {
    obfuscate(sourceCode: string, customOptions?: Record<string, unknown>): {
      getObfuscatedCode(): string;
    };
  };
  export default obfuscator;
}

