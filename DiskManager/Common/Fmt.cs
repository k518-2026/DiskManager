using System;
using System.Globalization;

namespace DiskManager.Common;

public static class Fmt
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>1024 進で読みやすいサイズ文字列にします。</summary>
    public static string Bytes(ulong bytes)
    {
        if (bytes == 0) return "0 B";
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return v < 10 && i > 0
            ? v.ToString("0.00", CultureInfo.InvariantCulture) + " " + Units[i]
            : v.ToString("0.#", CultureInfo.InvariantCulture) + " " + Units[i];
    }

    public static string Bytes(long bytes) => bytes < 0 ? "-" : Bytes((ulong)bytes);

    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return string.Format("{0}時間{1:00}分{2:00}秒", (int)t.TotalHours, t.Minutes, t.Seconds);
        if (t.TotalMinutes >= 1) return string.Format("{0}分{1:00}秒", t.Minutes, t.Seconds);
        return string.Format("{0:0.0}秒", t.TotalSeconds);
    }

    public static string Rate(long bytes, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0.001) return "-";
        return Bytes((ulong)(bytes / elapsed.TotalSeconds)) + "/秒";
    }
}
