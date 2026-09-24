namespace FundDca.Core.Domain;

/// <summary>
/// 基金类型：权益三类（参与 B1-B4 定投判定）+ 稳健两类（计入分母 D、不定投）。
/// </summary>
public enum FundType
{
    /// <summary>股票型</summary>
    Stock = 1,

    /// <summary>混合型</summary>
    Mixed = 2,

    /// <summary>QDII 跨境（净值常 T+1/T+2，无盘中代理）</summary>
    Qdii = 3,

    /// <summary>债券型：稳健资产，月底用预算余额一次性申购，不定投</summary>
    Bond = 4,

    /// <summary>货币型：稳健资产，市值手工维护，不采集净值、不模拟收益</summary>
    Money = 5
}

/// <summary>
/// 指数估值参考指标。默认值由系统按指数类型建议，用户可逐指数修改。
/// </summary>
public enum ValuationMetric
{
    /// <summary>无估值口径（REITs/FOF 等不参与 B1 自动判定）</summary>
    None = 0,

    /// <summary>市盈率 TTM（默认：宽基、成长、消费类）</summary>
    PeTtm = 1,

    /// <summary>市净率（默认：红利、金融周期、盈利不稳定类）</summary>
    Pb = 2,

    /// <summary>
    /// 盈利收益率 E/P = 1/PE-TTM（百分数，越高越便宜）。
    /// 不用历史百分位，按绝对值阈值判定：≥ 低估线（默认 10%）绿灯；≤ 高估线（默认 6.4%）红灯。
    /// </summary>
    EarningsYield = 3
}

/// <summary>
/// B1 估值红绿灯（按跟踪指数当前估值历史百分位）。
/// </summary>
public enum SignalLevel
{
    /// <summary>绿灯：百分位低于低估阈值，正常额度 × 低估系数（默认 1.5）</summary>
    Green = 1,

    /// <summary>黄灯：低估与高估阈值之间，按基础额定投</summary>
    Yellow = 2,

    /// <summary>红灯：百分位达到/超过高估阈值，暂停定投</summary>
    Red = 3,

    /// <summary>灰灯：无估值数据/无跟踪指数，不出自动建议（可手工试算）</summary>
    NoData = 4,

    /// <summary>稳健资产（债券/货币）：计入 D，不参与定投建议</summary>
    Stable = 5
}
