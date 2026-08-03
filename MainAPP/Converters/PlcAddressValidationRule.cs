using System.Globalization;
using System.Windows.Controls;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.Resources;

namespace MainAPP.Converters;

/// <summary>
/// PLC 地址格式校验规则，支持按期望类型（DWord / MBit）验证。
/// 为空时不报错（地址可选填），非空时校验格式。
/// </summary>
public class PlcAddressValidationRule : ValidationRule
{
    private static Func<IPlcAddressCodec>? s_codecProvider;

    public static void SetCodecProvider(Func<IPlcAddressCodec> codecProvider) =>
        s_codecProvider = codecProvider ?? throw new ArgumentNullException(nameof(codecProvider));

    /// <summary>
    /// 期望的地址类型
    /// </summary>
    public PlcAddressType ExpectedType { get; set; }

    public override ValidationResult Validate(object? value, CultureInfo cultureInfo)
    {
        var addr = value as string;
        if (string.IsNullOrWhiteSpace(addr))
            return ValidationResult.ValidResult;

        var codec = s_codecProvider?.Invoke() ?? new MitsubishiAddressCodec();
        var result = codec.Parse(addr);
        if (!result.IsValid)
            return new ValidationResult(false, string.Format(Strings.F137, codec.Brand, addr));

        if (result.Type != ExpectedType)
            return new ValidationResult(false,
                string.Format(Strings.F142, ExpectedType, result.Type));

        return ValidationResult.ValidResult;
    }
}
