/**
 * CN: 主题系统架构
 * CN: 支持多主题切换，预埋埋点以适配未来扩展
 *
 * EN: Theme system architecture
 * EN: Supports multi-theme switching, with pre-embedded tracking for future expansion
 */

import { mistTheme } from './mist'
import { paperTheme } from './paper'
import { materialTheme } from './material'

export const THEMES = {
  mist: mistTheme,
  paper: paperTheme,
  material: materialTheme,
}

export type ThemeName = keyof typeof THEMES
export type ThemeConfig = (typeof THEMES)[ThemeName]

export interface ThemeMeta {
  name: ThemeConfig['name']
  label: ThemeConfig['label']
  description: ThemeConfig['description']
  icon: ThemeConfig['icon']
}

export const THEME_LIST = Object.keys(THEMES) as ThemeName[]

export function getThemeConfig(themeName: string): ThemeConfig {
  return (THEMES as Record<string, ThemeConfig | undefined>)[themeName] || THEMES.mist
}

export function getThemeMeta(themeName: string): ThemeMeta {
  const config = getThemeConfig(themeName)
  return {
    name: config.name,
    label: config.label,
    description: config.description,
    icon: config.icon,
  }
}

export function getAllThemesMeta(): ThemeMeta[] {
  return THEME_LIST.map(getThemeMeta)
}
