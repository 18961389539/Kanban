using HslCommunication.Core;
using Kanban.Core.Models;

namespace Kanban.Core.Services;

internal static class HslDataFormatMapper
{
    public static DataFormat ToHsl(PlcDataFormat format) => format switch
    {
        PlcDataFormat.ABCD => DataFormat.ABCD,
        PlcDataFormat.BADC => DataFormat.BADC,
        PlcDataFormat.CDAB => DataFormat.CDAB,
        PlcDataFormat.DCBA => DataFormat.DCBA,
        _ => DataFormat.ABCD,
    };
}
