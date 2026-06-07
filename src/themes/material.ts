/**
 * CN: Material Design 主题配置
 * CN: 大胆鲜明的 Google Material Design 3 风格
 *
 * EN: Material Design theme configuration
 * EN: Bold and vibrant Google Material Design 3 style
 */

export const materialTheme = {
  name: 'material',
  label: 'Material',
  description: 'Material Design 设计语言',
  icon: 'Layers',

  // CN: 主题特性
  // EN: Theme features
  features: {
    hasGradient: false,
    hasTransparency: false,
    hasSoftShadows: true,
    borderRadius: 'medium',
    hasElevation: true,
    hasColorRoles: true, // Material 3 色彩角色
  },

  // CN: 设计令牌
  // EN: Design tokens
  tokens: {
    // CN: 颜色系统 - Material 3 大胆配色
    // EN: Color system - Material 3 bold color scheme
    primary: {
      main: '#6750A4',      // 深紫色主色
      light: '#D0BCFF',     // 浅紫色
      dark: '#381E72',      // 深紫色
      container: '#EADDFF', // 主色容器
    },

    secondary: {
      main: '#625B71',      // 次要色
      light: '#CCC2DC',
      dark: '#332D41',
      container: '#E8DEF8',
    },

    tertiary: {
      main: '#00BFA5',      // 第三色 - 青绿色强调
      light: '#80CBC4',
      dark: '#00897B',
      container: '#B2DFDB',
    },

    // CN: 文本颜色
    // EN: Text colors
    ink: {
      base: '#1C1B1F',
      soft: '#49454F',
      muted: '#79747E',
      subtle: '#A8A29E',
    },

    // CN: 边框颜色
    // EN: Border colors
    line: {
      default: 'rgba(28, 27, 31, 0.12)',
      soft: 'rgba(28, 27, 31, 0.08)',
      strong: 'rgba(28, 27, 31, 0.20)',
    },

    // CN: 表面颜色 - 带有主色色调
    // EN: Surface colors - with primary color tint
    surface: {
      shell: '#FFFBFE',     // 带有微弱暖色的白
      sidebar: '#F7F2FA',   // 浅紫色背景
      main: '#FFFFFF',
      panel: '#FFFBFE',
      variant: '#E7E0EC',   // 变体表面色
    },

    // CN: 错误色
    // EN: Error colors
    error: {
      main: '#B3261E',
      container: '#F9DEDC',
    },

    // CN: 阴影系统 - 带主色色调
    // EN: Shadow system - with primary color tint
    shadow: {
      shell: '0 8px 10px -5px rgba(103, 80, 164, 0.2), 0 16px 24px 2px rgba(103, 80, 164, 0.14), 0 6px 30px 5px rgba(103, 80, 164, 0.12)',
      soft: '0 3px 5px -1px rgba(103, 80, 164, 0.2), 0 6px 10px 0 rgba(103, 80, 164, 0.14), 0 1px 18px 0 rgba(103, 80, 164, 0.12)',
      sm: '0 2px 4px -1px rgba(103, 80, 164, 0.2), 0 4px 5px 0 rgba(103, 80, 164, 0.14), 0 1px 10px 0 rgba(103, 80, 164, 0.12)',
    },

    // CN: 圆角 - Material 标准
    // EN: Border radius - Material standard
    radius: {
      sm: '0.25rem',
      md: '0.5rem',
      lg: '0.75rem',
      xl: '1rem',
      '2xl': '1.25rem',
      full: '9999px',
    },

    // CN: Material Elevation System - 带主色色调
    // EN: Material Elevation System - with primary color tint
    elevation: {
      0: 'none',
      1: '0 1px 2px rgba(103, 80, 164, 0.3), 0 1px 3px 1px rgba(103, 80, 164, 0.15)',
      2: '0 1px 2px rgba(103, 80, 164, 0.3), 0 2px 6px 2px rgba(103, 80, 164, 0.15)',
      3: '0 4px 8px 3px rgba(103, 80, 164, 0.15), 0 1px 3px rgba(103, 80, 164, 0.3)',
      4: '0 6px 10px 4px rgba(103, 80, 164, 0.15), 0 2px 3px rgba(103, 80, 164, 0.3)',
      5: '0 8px 12px 6px rgba(103, 80, 164, 0.15), 0 4px 4px rgba(103, 80, 164, 0.3)',
    },
  },
}

export type MaterialTheme = typeof materialTheme
