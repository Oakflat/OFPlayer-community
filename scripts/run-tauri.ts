import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'

function withCargoBin(env: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
  const cargoBin = path.join(os.homedir(), '.cargo', 'bin')
  const currentPath = env.PATH ?? env.Path ?? ''

  if (!existsSync(cargoBin)) {
    return env
  }

  const pathEntries = currentPath.split(path.delimiter).filter(Boolean)

  if (pathEntries.includes(cargoBin)) {
    return env
  }

  return {
    ...env,
    PATH: `${cargoBin}${path.delimiter}${currentPath}`,
  }
}

function resolveNpmExecPath(env: NodeJS.ProcessEnv): string {
  if (env.npm_execpath) {
    return env.npm_execpath
  }

  throw new Error('Unable to locate npm CLI entrypoint from npm_execpath.')
}

async function main(): Promise<void> {
  const args = process.argv.slice(2)
  const env = withCargoBin(process.env)
  const npmExecPath = resolveNpmExecPath(env)

  const child = spawn(process.execPath, [npmExecPath, 'exec', 'tauri', '--', ...args], {
    stdio: 'inherit',
    env,
    cwd: process.cwd(),
  })

  child.on('exit', (code: number | null, signal: NodeJS.Signals | null) => {
    if (signal) {
      process.kill(process.pid, signal)
      return
    }

    process.exit(code ?? 0)
  })

  child.on('error', (error: Error) => {
    console.error(error)
    process.exit(1)
  })
}

main().catch((error: unknown) => {
  console.error(error)
  process.exit(1)
})
