// ============================================================
// Balance Chart — TypeScript module for WebView2
// Compiled to balance_chart.js via tsc
// ============================================================

interface ChartPoint {
    time: string;
    value: number;
}

interface EChartsXAxis {
    type: string;
    data: string[];
    axisLine: { lineStyle: { color: string } };
    axisLabel: { color: string; fontSize: number; rotate?: number };
    splitLine: { show: boolean };
}

interface EChartsYAxis {
    type: string;
    axisLine: { lineStyle: { color: string } };
    axisLabel: { color: string; fontSize: number };
    splitLine: { lineStyle: { color: string; type: string } };
    scale: boolean;
}

interface EChartsSeriesStyle {
    width: number;
    color: string;
}

interface EChartsAreaGradient {
    offset: number;
    color: string;
}

interface EChartsAreaStyle {
    color: EChartsAreaGradient[];
}

interface EChartsSeries {
    name: string;
    type: string;
    data: number[];
    smooth: boolean;
    symbol: string;
    symbolSize: number;
    showSymbol: boolean;
    lineStyle: EChartsSeriesStyle;
    areaStyle: EChartsAreaStyle;
    itemStyle: { color: string };
}

interface EChartsOption {
    backgroundColor: string;
    textStyle: { color: string };
    title: {
        text: string;
        left: string;
        top: number;
        textStyle: { color: string; fontSize: number; fontWeight: string };
    };
    tooltip: {
        trigger: string;
        backgroundColor: string;
        borderColor: string;
        textStyle: { color: string; fontSize: number };
        formatter: (params: any) => string;
    };
    grid: { left: number; right: number; top: number; bottom: number };
    xAxis: EChartsXAxis;
    yAxis: EChartsYAxis;
    series: EChartsSeries[];
    animation: boolean;
    animationDuration: number;
    animationEasing: string;
}

declare const echarts: any;

class BalanceChart {
    private chart: any;
    private readonly maxPoints: number = 200;

    constructor(elementId: string) {
        const el: HTMLElement | null = document.getElementById(elementId);
        if (!el) {
            throw new Error(`Element #${elementId} not found`);
        }
        this.chart = echarts.init(el, 'dark');
        this.chart.setOption(this.buildOption());
        window.addEventListener('resize', () => this.chart.resize());
    }

    private buildOption(): EChartsOption {
        return {
            backgroundColor: '#1e1e1e',
            textStyle: { color: '#ecf0f1' },
            title: {
                text: 'Баланс USDC',
                left: 'center',
                top: 10,
                textStyle: { color: '#ecf0f1', fontSize: 16, fontWeight: 'bold' }
            },
            tooltip: {
                trigger: 'axis',
                backgroundColor: 'rgba(44, 62, 80, 0.95)',
                borderColor: '#3498db',
                textStyle: { color: '#ecf0f1', fontSize: 12 },
                formatter: (params: any): string => {
                    const p: any = params[0];
                    const val: number = parseFloat(p.value);
                    return '<b>' + p.axisValue + '</b><br/>Баланс: <b style="color:#2ecc71">' +
                        val.toFixed(2) + ' USDC</b>';
                }
            },
            grid: {
                left: 60, right: 30, top: 50, bottom: 40
            },
            xAxis: {
                type: 'category',
                data: [],
                axisLine: { lineStyle: { color: '#34495e' } },
                axisLabel: { color: '#95a5a6', fontSize: 10, rotate: 30 },
                splitLine: { show: false }
            },
            yAxis: {
                type: 'value',
                axisLine: { lineStyle: { color: '#34495e' } },
                axisLabel: { color: '#95a5a6', fontSize: 11 },
                splitLine: { lineStyle: { color: '#2c3e50', type: 'dashed' } },
                scale: true
            },
            series: [{
                name: 'Баланс',
                type: 'line',
                data: [],
                smooth: true,
                symbol: 'circle',
                symbolSize: 4,
                showSymbol: false,
                lineStyle: { width: 2.5, color: '#2ecc71' },
                areaStyle: {
                    color: [
                        { offset: 0, color: 'rgba(46, 204, 113, 0.35)' },
                        { offset: 1, color: 'rgba(46, 204, 113, 0.02)' }
                    ]
                },
                itemStyle: { color: '#2ecc71' }
            }],
            animation: true,
            animationDuration: 600,
            animationEasing: 'cubicOut'
        };
    }

    public updateFull(times: string[], values: number[]): void {
        this.chart.setOption({
            xAxis: { data: times },
            series: [{ data: values }]
        });
    }

    public updatePoint(time: string, value: number): void {
        const opt: any = this.chart.getOption();
        const xData: string[] = opt.xAxis[0].data;
        const sData: number[] = opt.series[0].data;

        if (xData.length > 0 && xData[xData.length - 1] === time) {
            sData[sData.length - 1] = value;
        } else {
            xData.push(time);
            sData.push(value);
            if (xData.length > this.maxPoints) {
                xData.shift();
                sData.shift();
            }
        }
        this.chart.setOption({
            xAxis: { data: xData },
            series: [{ data: sData }]
        });
    }

    public clear(): void {
        this.chart.setOption({
            xAxis: { data: [] },
            series: [{ data: [] }]
        });
    }
}

// Global instance — called from C# via ExecuteScriptAsync
let _balanceChart: BalanceChart | null = null;

function initChart(): void {
    if (!_balanceChart) {
        _balanceChart = new BalanceChart('chart');
    }
}

function updateChart(timesJson: string, valuesJson: string): void {
    if (!_balanceChart) { initChart(); }
    const times: string[] = JSON.parse(timesJson);
    const values: number[] = JSON.parse(valuesJson);
    _balanceChart!.updateFull(times, values);
}

function updateSinglePoint(time: string, value: number): void {
    if (!_balanceChart) { initChart(); }
    _balanceChart!.updatePoint(time, value);
}

function clearChart(): void {
    if (_balanceChart) { _balanceChart.clear(); }
}
