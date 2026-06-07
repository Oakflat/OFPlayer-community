/**
 * CN: Paper 纸感主题配置
 * CN: 纸张质感的优雅设计
 *
 * EN: Paper texture theme configuration
 * EN: Elegant design with paper texture
 */

export const paperTheme = {
  name: 'paper',
  label: '纸感',
  description: '温润纸质的自然触感',
  icon: 'FileText',

  // CN: 主题特性
  // EN: Theme features
  features: {
    hasGradient: false,
    hasTransparency: false,
    hasSoftShadows: true,
    borderRadius: 'soft',
  },

  // CN: 设计令牌
  // EN: Design tokens
  tokens: {
    // CN: 颜色系统 - 纸质基调
    // EN: Color system - paper tone
    ink: {
      base: '#2c2c2c',
      soft: '#595959',
      muted: '#8c8c8c',
      subtle: '#bfbfbf',
    },

    line: {
      default: 'rgba(0, 0, 0, 0.06)',
      soft: 'rgba(0, 0, 0, 0.04)',
      strong: 'rgba(0, 0, 0, 0.1)',
    },

    surface: {
      shell: '#fafaf8',
      sidebar: '#f5f5f3',
      main: '#ffffff',
      panel: '#fafaf8',
    },

    shadow: {
      shell: '0 20px 60px -15px rgba(0, 0, 0, 0.08)',
      soft: '0 4px 12px -4px rgba(0, 0, 0, 0.06)',
      sm: '0 1px 4px -1px rgba(0, 0, 0, 0.04)',
    },

    // CN: 圆角 - 更柔和
    // EN: Border radius - softer
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

export type PaperTheme = typeof paperTheme
