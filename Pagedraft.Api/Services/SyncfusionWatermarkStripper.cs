using System.Text.RegularExpressions;

namespace Pagedraft.Api.Services;

internal static class SyncfusionWatermarkStripper
{
    internal static string StripSyncfusionWatermark(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        const StringComparison ci = StringComparison.OrdinalIgnoreCase;
        var result = text;
        var searchStart = 0;

        while (true)
        {
            var startIdx = result.IndexOf("Created with a trial version of Syncfusion", searchStart, ci);
            if (startIdx < 0) break;

            const string keyPhrase = "to obtain the valid key.";
            var keyEnd = result.IndexOf(keyPhrase, startIdx, ci);

            int endIdx;
            if (keyEnd >= 0)
            {
                endIdx = keyEnd + keyPhrase.Length;
            }
            else
            {
                var dotIdx = result.IndexOf(".", startIdx + 1, ci);
                endIdx = dotIdx >= 0 ? dotIdx + 1 : result.Length;
            }

            result = result.Remove(startIdx, endIdx - startIdx);
            searchStart = startIdx;
        }

        result = Regex.Replace(result, @"[\r\n]+", "\n");
        result = Regex.Replace(result, @"[ \t]+", " ").Trim();
        return result;
    }
}

