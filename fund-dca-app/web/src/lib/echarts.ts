/**
 * ECharts 按需注册：本项目图表只有环形图（pie）与条形图（bar），
 * 全量引入 echarts 会把约 1MB 的未用模块（地图/3D/折线/雷达等）打进主包。
 * 只注册实际用到的图表、组件与 Canvas 渲染器，产物体积下降约 2/3。
 */
import { init, use, type ECharts } from 'echarts/core'
import { BarChart, PieChart } from 'echarts/charts'
import {
  AxisPointerComponent,
  GridComponent,
  LegendComponent,
  MarkLineComponent,
  TooltipComponent,
} from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'

use([
  CanvasRenderer,
  BarChart,
  PieChart,
  GridComponent,
  TooltipComponent,
  AxisPointerComponent,
  LegendComponent,
  MarkLineComponent,
])

export { init }
export type { ECharts }
