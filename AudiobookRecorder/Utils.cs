using Durandal.API;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudiobookRecorder
{
    public static class Utils
    {
        public static async Task RunProcess(string exe, string args)
        {
            ProcessStartInfo processInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WindowStyle = ProcessWindowStyle.Normal,
                UseShellExecute = true,
            };

            using (Process? process = Process.Start(processInfo))
            {
                if (process == null)
                {
                    return;
                }

                await process.WaitForExitAsync();
            }
        }

        public static string SanitizeDirectoryName(string fileName)
        {
            foreach (char c in Path.GetInvalidPathChars())
            {
                fileName = fileName.Replace(c, '_');
            }

            return fileName;
        }

        public static string SanitizeFileName(string fileName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }

            return fileName;
        }

        public static TimeSpan TruncateToMilliseconds(TimeSpan input)
        {
            long fractionalTicks = input.Ticks % TimeSpan.TicksPerMillisecond;
            return TimeSpan.FromTicks(input.Ticks - fractionalTicks);
        }

        public static TimeSpan GetMaximumChapterLength(IList<ChapterBreak> breaks)
        {
            TimeSpan returnVal = TimeSpan.Zero;
            TimeSpan lastChapterStart = TimeSpan.Zero;
            foreach (ChapterBreak b in breaks.OrderBy((s) => s.Start))
            {
                TimeSpan len = b.Start - lastChapterStart;
                if (len > returnVal)
                {
                    returnVal = len;
                }

                lastChapterStart = b.Start;
            }

            return returnVal;
        }

        private static readonly char[] WHITESPACE_CHARS = new char[] { ' ', '\t', '\r', '\n' };

        // Given an input string, returns the longest substring of the input
        // that is equal to or shorter than maxLength and avoids breaks
        // along non-whitespace characters unless absolutely necessary
        public static string GetWholeWordSubstringOfMaxLength(string input, int maxLength)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }
            if (maxLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLength));
            }

            int indexOfLastChar = Math.Min(input.Length - 1, maxLength - 1);
            indexOfLastChar = input.LastIndexOfAny(WHITESPACE_CHARS, indexOfLastChar);

            while (indexOfLastChar > 0 && char.IsWhiteSpace(input[indexOfLastChar]))
            {
                indexOfLastChar--;
            }

            if (indexOfLastChar <= 0)
            {
                // No breaks in the entire length. We have to truncate
                // (Or more commonly the input string is just a single word with no spaces)
                if (input.Length < maxLength)
                {
                    return input;
                }
                else
                {
                    return input.Substring(0, maxLength);
                }
            }

            return input.Substring(0, indexOfLastChar + 1);
        }

        private static TimeSpan? GetPhraseStartTime(IList<SpeechRecognizedPhrase> srResults, params string[] words)
        {
            foreach (SpeechRecognizedPhrase srResult in srResults)
            {
                for (int startIdx = 0; startIdx < srResult.PhraseElements.Count; startIdx++)
                {
                    int wordIdx = 0;
                    while (wordIdx < words.Length &&
                        wordIdx + startIdx < srResult.PhraseElements.Count &&
                        string.Equals(srResult.PhraseElements[startIdx + wordIdx].DisplayText, words[wordIdx], StringComparison.OrdinalIgnoreCase))
                    {
                        wordIdx++;
                    }

                    if (wordIdx == words.Length)
                    {
                        return srResult.PhraseElements[startIdx].AudioTimeOffset;
                    }
                }
            }

            return null;
        }
    }
}
