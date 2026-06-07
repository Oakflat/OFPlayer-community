/**
 * CN: Mist 主题配置
 * CN: 当前默认主题，薄雾般的现代感
 *
 * EN: Mist theme configuration
 * EN: Current default theme, misty modern feel
 */

export const mistTheme = {
  name: 'mist',
  label: '薄雾',
  description: '柔和现代的设计风格',
  icon: 'Cloud',

  // CN: 主题特性
  // EN: Theme features
  features: {
    hasGradient: true,
    hasTransparency: true,
    hasSoftShadows: true,
    borderRadius: 'rounded',
  },

  // CN: 设计令牌
  // EN: Design tokens
  tokens: {
    // CN: 颜色系统
    // EN: Color system
    ink: {
      base: '#1a1f2e',
      soft: '#3d4556',
      muted: '#6b7280',
      subtle: '#9ca3af',
    },

    line: {
      default: 'rgba(26, 31, 46, 0.08)',
      soft: 'rgba(26, 31, 46, 0.06)',
      strong: 'rgba(26, 31, 46, 0.12)',
    },

    surface: {
      shell: 'rgba(255, 255, 255, 0.95)',
      sidebar: 'rgba(248, 250, 252, 0.9)',
      main: 'rgba(255, 255, 255, 0.75)',
      panel: 'rgba(250, 251, 253, 0.92)',
    },

    shadow: {
      shell: '0 32px 64px -12px rgba(26, 31, 46, 0.12)',
      soft: '0 8px 24px -8px rgba(26, 31, 46, 0.08)',
      sm: '0 2px 8px -2px rgba(26, 31, 46, 0.06)',
    },

    // CN: 圆角
    // EN: Border radius
    radius: {
      sm: '0.375rem',
      md: '0.5rem',
      lg: '0.75rem',
      xl: '1rem',
      '2xl': '1.25rem',
      full: '9999px',
    },
  },
}

export type MistTheme = typeof mistTheme
