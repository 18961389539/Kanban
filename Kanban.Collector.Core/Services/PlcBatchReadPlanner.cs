namespace MainAPP.Services;

public sealed record PlcReadBlock(string StartAddress, ushort Length, PlcAddressType Type);

public static class PlcBatchReadPlanner
{
    public static IReadOnlyList<PlcReadBlock> Plan(
        IEnumerable<string> addresses,
        PlcAddressType type,
        ushort maxLength = 64,
        int? addressStride = null,
        IPlcAddressCodec? addressCodec = null,
        int maxGapSlots = 0)
    {
        if (maxLength == 0) throw new ArgumentOutOfRangeException(nameof(maxLength));
        if (maxGapSlots < 0) throw new ArgumentOutOfRangeException(nameof(maxGapSlots));
        addressCodec ??= new MitsubishiAddressCodec();

        var parsed = addresses
            .Select(addressCodec.Parse)
            .Where(result => result.IsValid && result.Type == type)
            .Select(result => (result.AddressGroup, Address: result.Original,
                Canonical: addressCodec.CanonicalKey(result.Original), Number: result.AddressOffset,
                Stride: result.AddressStride))
            .Where(item => !string.IsNullOrWhiteSpace(item.Canonical))
            .DistinctBy(item => item.Canonical)
            .ToList();
        var blocks = new List<PlcReadBlock>();
        if (parsed.Count == 0) return blocks;

        foreach (var group in parsed.GroupBy(item => item.AddressGroup))
        {
            var ordered = group.OrderBy(item => item.Number).ToList();
            var step = addressStride ?? ordered[0].Stride;
            if (step <= 0) throw new ArgumentOutOfRangeException(nameof(addressStride));
            var start = ordered[0];
            var length = 1;
            for (var index = 1; index < ordered.Count; index++)
            {
                var nextOffset = ordered[index].Number - (start.Number + length * step);
                var aligned = nextOffset >= 0 && nextOffset % step == 0;
                var gapSlots = aligned ? nextOffset / step : int.MaxValue;
                var slotsToAdd = gapSlots == int.MaxValue ? int.MaxValue : gapSlots + 1;
                var canMerge = aligned
                    && gapSlots <= maxGapSlots
                    && length + slotsToAdd <= maxLength;
                if (!canMerge)
                {
                    blocks.Add(new PlcReadBlock(start.Address, (ushort)length, type));
                    start = ordered[index];
                    length = 1;
                }
                else
                {
                    length += slotsToAdd;
                }
            }
            blocks.Add(new PlcReadBlock(start.Address, (ushort)length, type));
        }
        return blocks;
    }

}
