#nullable disable
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Controls;
using MainAPP.Converters;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 安全校验错误转换器：核心目的是在 (Validation.Errors) 为空时返回空串而非抛
/// ArgumentOutOfRangeException（杜绝 "System.Windows.Data Error: 17" 调试噪音）。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ValidationErrorsToMessageConverterTests
{
    private static readonly ValidationErrorsToMessageConverter _conv = new();

    [Fact]
    public void EmptyCollection_ReturnsEmptyString_NoException()
    {
        var errors = new ReadOnlyObservableCollection<ValidationError>(new ObservableCollection<ValidationError>());

        var result = _conv.Convert(errors, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void NullValue_ReturnsEmptyString()
    {
        var result = _conv.Convert(null, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SingleError_ReturnsErrorContentText()
    {
        var inner = new ObservableCollection<ValidationError>
        {
            new(new ExceptionValidationRule(), new object(), "端口必须介于 1-65535", null)
        };
        var errors = new ReadOnlyObservableCollection<ValidationError>(inner);

        var result = _conv.Convert(errors, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal("端口必须介于 1-65535", result);
    }

    [Fact]
    public void ConvertBack_ReturnsDoNothing()
    {
        var result = _conv.ConvertBack("x", typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(System.Windows.Data.Binding.DoNothing, result);
    }
}
