namespace BerTool.TrayAgent.Utilities;

internal static class SizeFormatter
{
    public static string ToFriendlyGb(double gigabytes)
    {
        if (gigabytes >= 1024)
        {
            return $"{gigabytes / 1024:0.##} TB";
        }

        return $"{gigabytes:0.##} GB";
    }

    public static string ToFriendlySize(ulong bytes)
    {
        var gb = bytes / 1024d / 1024d / 1024d;
        return ToFriendlyGb(gb);
    }

    public static double BytesToGigabytes(ulong bytes)
    {
        return bytes / 1024d / 1024d / 1024d;
    }
}
