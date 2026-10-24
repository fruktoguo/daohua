#!/usr/bin/env node
/**
 * scripts/optimize-svg.mjs - 道化太初矢量图专属自动化清洗与高保真压缩工具
 *
 * 职责：
 * 1. 批量清理 AI 生成平台（Recraft, Midjourney, Figma 等）注入的冗余 C2PA 凭证、数字签名与元数据；
 * 2. 完整提取并保全全部 DOM 图层、z-order 与内联渐变（<linearGradient>, <radialGradient>, <defs>, 蒙版等）；
 * 3. 紧凑化贝塞尔曲线指令与坐标精度（默认 0~1 位小数），实现 60%~75% 的体积缩减并杜绝结构丢失；
 * 4. 支持单文件、文件夹目录、通配符批量处理与就地/指定目录导出。
 */

import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createRequire } from 'node:module'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const require = createRequire(import.meta.url)

let optimize
try {
  const svgoPath = require.resolve('svgo', { paths: [path.join(__dirname, '../frontend'), process.cwd()] })
  const svgo = require(svgoPath)
  optimize = svgo.optimize
} catch (e) {
  console.error('未找到 svgo 模块，请先在 frontend 目录执行 npm install')
  process.exit(1)
}

/**
 * 清洗并优化单个 SVG 字符串
 * @param {string} svgContent 原始 SVG 文本
 * @param {number} precision 坐标浮点精度（0 为整数，1 为 1 位小数）
 * @returns {string} 优化后的 SVG 文本
 */
export function optimizeSvgString(svgContent, precision = 0) {
  // 1. 强力剥离 AI 平台生成的冗余元数据与命名空间
  let clean = svgContent
    .replace(/<metadata>[\s\S]*?<\/metadata>/gi, '')
    .replace(/xmlns:c2pa="[^"]*"/gi, '')
    .replace(/<\?xml[\s\S]*?\?>/gi, '')
    .replace(/<!DOCTYPE[\s\S]*?>/gi, '')

  // 2. 采用针对游戏 UI 与角色立绘调优的 SVGO 流水线
  const result = optimize(clean, {
    multipass: true,
    plugins: [
      {
        name: 'preset-default',
        params: {
          overrides: {
            mergePaths: false,
            removeUselessDefs: true,
            cleanupNumericValues: {
              floatPrecision: precision,
            },
            convertPathData: {
              floatPrecision: precision,
              transformPrecision: precision,
              curveSmoothShorthands: true,
              makeArcs: {
                threshold: 2.5,
                tolerance: 0.5,
              },
            },
          },
        },
      },
    ],
  })

  return result.data
}

/**
 * 递归检索目录下的所有 .svg 文件
 */
function findSvgFiles(targetPath) {
  const stat = fs.statSync(targetPath)
  if (stat.isFile()) {
    return targetPath.endsWith('.svg') ? [targetPath] : []
  }
  if (stat.isDirectory()) {
    const files = []
    const entries = fs.readdirSync(targetPath, { withFileTypes: true })
    for (const entry of entries) {
      const fullPath = path.join(targetPath, entry.name)
      if (entry.isDirectory()) {
        files.push(...findSvgFiles(fullPath))
      } else if (entry.isFile() && entry.name.endsWith('.svg')) {
        files.push(fullPath)
      }
    }
    return files
  }
  return []
}

// 命令行执行入口
async function main() {
  const args = process.argv.slice(2)
  if (args.length === 0 || args.includes('-h') || args.includes('--help')) {
    console.log(`
道化太初 · 矢量图批量自动化清洗与优化工具

用法:
  node scripts/optimize-svg.mjs <文件或目录路径> [选项]

选项:
  -o, --out <path>         指定单文件输出路径
  --out-dir <dir>          指定批量输出目录
  -p, --precision <num>    设置浮点小数位数 (默认: 0, 适用于 1024x1024 标准画布; 细节极多时可设 1)
  --in-place               直接覆盖原文件
  -h, --help               显示帮助信息
`)
    process.exit(0)
  }

  let inputPattern = ''
  let outPath = ''
  let outDir = ''
  let precision = 0
  let inPlace = false

  for (let i = 0; i < args.length; i++) {
    const arg = args[i]
    if (arg === '-o' || arg === '--out') {
      outPath = args[++i]
    } else if (arg === '--out-dir') {
      outDir = args[++i]
    } else if (arg === '-p' || arg === '--precision') {
      precision = parseInt(args[++i], 10)
    } else if (arg === '--in-place') {
      inPlace = true
    } else if (!inputPattern) {
      inputPattern = arg
    }
  }

  if (!inputPattern) {
    console.error('错误: 请指定输入的 SVG 文件或目录')
    process.exit(1)
  }

  const resolvedInput = path.resolve(process.cwd(), inputPattern)
  if (!fs.existsSync(resolvedInput)) {
    console.error(`错误: 路径不存在 -> ${resolvedInput}`)
    process.exit(1)
  }

  const svgFiles = findSvgFiles(resolvedInput)
  if (svgFiles.length === 0) {
    console.log('未找到任何 .svg 文件')
    return
  }

  console.log(`\n🚀 开始批量处理 ${svgFiles.length} 个 SVG 文件 (精度: ${precision})...\n`)

  let totalOriginal = 0
  let totalOptimized = 0

  for (const filePath of svgFiles) {
    const rawContent = fs.readFileSync(filePath, 'utf8')
    const originalSize = Buffer.byteLength(rawContent, 'utf8')
    totalOriginal += originalSize

    const optimizedContent = optimizeSvgString(rawContent, precision)
    const optimizedSize = Buffer.byteLength(optimizedContent, 'utf8')
    totalOptimized += optimizedSize

    const savings = (((originalSize - optimizedSize) / originalSize) * 100).toFixed(1)

    let targetFile = filePath
    if (outPath && svgFiles.length === 1) {
      targetFile = path.resolve(process.cwd(), outPath)
    } else if (outDir) {
      const rel = path.relative(resolvedInput, filePath)
      targetFile = path.join(path.resolve(process.cwd(), outDir), rel || path.basename(filePath))
    } else if (!inPlace && !outPath) {
      const dir = path.dirname(filePath)
      const ext = path.extname(filePath)
      const base = path.basename(filePath, ext)
      targetFile = path.join(dir, `${base}_optimized${ext}`)
    }

    const targetDir = path.dirname(targetFile)
    if (!fs.existsSync(targetDir)) {
      fs.mkdirSync(targetDir, { recursive: true })
    }

    fs.writeFileSync(targetFile, optimizedContent, 'utf8')

    console.log(`✅ [${(optimizedSize / 1024).toFixed(1)} KB / 减小 ${savings}%] ${path.basename(filePath)} -> ${path.basename(targetFile)}`)
  }

  const overallSavings = (((totalOriginal - totalOptimized) / totalOriginal) * 100).toFixed(1)
  console.log(`\n🎉 处理完成！总大小: ${(totalOriginal / 1024).toFixed(1)} KB -> ${(totalOptimized / 1024).toFixed(1)} KB (整体缩减 ${overallSavings}%)\n`)
}

main().catch(err => {
  console.error('处理失败:', err)
  process.exit(1)
})
