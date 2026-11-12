import { dotnet } from './_framework/dotnet.js'

const { getAssemblyExports, getConfig } = await dotnet.create()
const config = getConfig()
const exports = await getAssemblyExports(config.mainAssemblyName)
const entry = exports.DaoHua.Simulator.Browser.SimulatorEntry

globalThis.addEventListener('message', (event) => {
  const { id, op, payload } = event.data ?? {}
  try {
    let result
    if (op === 'fingerprint') result = entry.CatalogFingerprint()
    else if (op === 'createDefaultProfile') result = entry.CreateDefaultProfile()
    else if (op === 'resolveProfiles') result = entry.ResolveProfiles(payload)
    else if (op === 'run') result = entry.Run(payload)
    else throw new Error(`未知的预演操作：${String(op)}`)
    globalThis.postMessage({ id, ok: true, result })
  } catch (error) {
    globalThis.postMessage({ id, ok: false, error: String(error?.message ?? error) })
  }
})

globalThis.postMessage({ type: 'ready' })
