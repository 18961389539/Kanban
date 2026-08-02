namespace LicenseManager.Models;

/// <summary>
/// 授权状态枚举：覆盖未激活、试用期、已激活、过期等所有状态。
/// </summary>
public enum LicenseStatus
{
    /// <summary>无任何记录（首次启动或被重置）</summary>
    Unlicensed,

    /// <summary>试用期内</summary>
    Trial,

    /// <summary>试用期已过 30 天</summary>
    TrialExpired,

    /// <summary>检测到时间回拨等篡改行为，试用期立即失效</summary>
    TrialManipulated,

    /// <summary>已激活且签名有效</summary>
    Active,

    /// <summary>已激活但已过期（激活码含过期日期且已到期）</summary>
    Expired,

    /// <summary>已激活但当前机器码与激活码中绑定的机器码不匹配</summary>
    MachineMismatch,
}
