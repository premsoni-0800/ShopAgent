namespace PrintlyAgent.Printing;

/// <summary>
/// Turning "1-5,8" into the page numbers to print.
///
/// Port of resolvePages from printing/PrintSubmission.kt.
/// </summary>
public static class PageRange
{
    /// <summary>
    /// 1-based page numbers, in the order given.
    ///
    /// An empty or unreadable range falls back to the whole document rather than
    /// to nothing. That direction is deliberate: printing everything when the
    /// range could not be understood is wrong in a way the shop can see and fix,
    /// whereas printing nothing looks exactly like a job that succeeded and
    /// leaves the student holding a receipt for blank air.
    /// </summary>
    public static IReadOnlyList<int> Resolve(string? pageRange, int documentPageCount)
    {
        if (string.IsNullOrWhiteSpace(pageRange)) return AllPages(documentPageCount);

        var pages = new List<int>();
        foreach (var part in pageRange.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;

            int start, end;
            var dash = trimmed.IndexOf('-');
            if (dash >= 0)
            {
                if (!int.TryParse(trimmed[..dash].Trim(), out start)) continue;
                if (!int.TryParse(trimmed[(dash + 1)..].Trim(), out end)) continue;
            }
            else
            {
                if (!int.TryParse(trimmed, out start)) continue;
                end = start;
            }

            for (var page = start; page <= end; page++)
            {
                if (page >= 1 && page <= documentPageCount) pages.Add(page);
            }
        }

        return pages.Count > 0 ? pages : AllPages(documentPageCount);
    }

    private static List<int> AllPages(int documentPageCount)
    {
        var all = new List<int>(Math.Max(0, documentPageCount));
        for (var page = 1; page <= documentPageCount; page++) all.Add(page);
        return all;
    }
}
