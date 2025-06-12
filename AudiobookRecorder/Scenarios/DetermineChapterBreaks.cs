using Durandal.API;
using Durandal.Common.Audio.Components;
using Durandal.Common.Audio;
using Durandal.Common.Collections;
using Durandal.Common.IO;
using Durandal.Common.Logger;
using Durandal.Common.NLP.Language;
using Durandal.Common.Speech.SR;
using Durandal.Common.Time;
using Durandal.Extensions.NativeAudio.Components;
using Durandal.Extensions.NativeAudio;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AudiobookRecorder.Scenarios
{
    public static class DetermineChapterBreaks
    {
        private static readonly string[] NumberMatchers = new string[]
        {
            "zero",
            "(one|won)",
            "(two|to|too)",
            "three",
            "(for|four)",
            "(five|fife)",
            "(six|sex)",
            "seven",
            "(ate|eight)",
            "nine",
            "ten",
            "eleven",
            "twelve",
            "thirteen",
            "fourteen",
            "fifteen",
            "sixteen",
            "seventeen",
            "eighteen",
            "nine?teen",
            "twenty",
            "twenty[- ]one",
            "twenty[- ]two",
            "twenty[- ]three",
            "twenty[- ]four",
            "twenty[- ]five",
            "twenty[- ]six",
            "twenty[- ]seven",
            "twenty[- ]eight",
            "twenty[- ]nine",
            "thirty",
            "thirty[- ]one",
            "thirty[- ]two",
            "thirty[- ]three",
            "thirty[- ]four",
            "thirty[- ]five",
            "thirty[- ]six",
            "thirty[- ]seven",
            "thirty[- ]eight",
            "thirty[- ]nine",
            "forty",
            "forty[- ]one",
            "forty[- ]two",
            "forty[- ]three",
            "forty[- ]four",
            "forty[- ]five",
            "forty[- ]six",
            "forty[- ]seven",
            "forty[- ]eight",
            "forty[- ]nine",
            "fifty",
            "fifty[- ]one",
            "fifty[- ]two",
            "fifty[- ]three",
            "fifty[- ]four",
            "fifty[- ]five",
            "fifty[- ]six",
            "fifty[- ]seven",
            "fifty[- ]eight",
            "fifty[- ]nine",
            "sixty",
            "sixty[- ]one",
            "sixty[- ]two",
            "sixty[- ]three",
            "sixty[- ]four",
            "sixty[- ]five",
            "sixty[- ]six",
            "sixty[- ]seven",
            "sixty[- ]eight",
            "sixty[- ]nine",
            "seventy",
            "seventy[- ]one",
            "seventy[- ]two",
            "seventy[- ]three",
            "seventy[- ]four",
            "seventy[- ]five",
            "seventy[- ]six",
            "seventy[- ]seven",
            "seventy[- ]eight",
            "seventy[- ]nine",
            "eighty",
            "eighty[- ]one",
            "eighty[- ]two",
            "eighty[- ]three",
            "eighty[- ]four",
            "eighty[- ]five",
            "eighty[- ]six",
            "eighty[- ]seven",
            "eighty[- ]eight",
            "eighty[- ]nine",
            "ninety",
            "ninety[- ]one",
            "ninety[- ]two",
            "ninety[- ]three",
            "ninety[- ]four",
            "ninety[- ]five",
            "ninety[- ]six",
            "ninety[- ]seven",
            "ninety[- ]eight",
            "ninety[- ]nine",
        };

        public static async Task<IEnumerable<ChapterBreak>> GetBreaks(FileInfo fileToAnalyze, ILogger logger, ISpeechRecognizerFactory? speechRecoFactory)
        {
            IList<ChapterBreak>? chaptersFromMetadata = await TryDetermineChapterBreaksFromMetadata(fileToAnalyze, logger).ConfigureAwait(false);
            if (chaptersFromMetadata != null &&
                chaptersFromMetadata.Count > 0)
            {
                return chaptersFromMetadata;
            }

            Tuple<List<SilenceTime>, TimeSpan> overview = await FindSilenceTimes(fileToAnalyze, logger, speechRecoFactory);
            List<SilenceTime> silenceTimes = overview.Item1;
            TimeSpan totalFileLength = overview.Item2;

            List<ChapterBreak> chapterBreaks = DetermineChapterBreaksUsingSpeechReco(silenceTimes, logger);

            // See if speech reco returned way too few chapters
            int minExpectedChapters = Math.Max(1, (int)(totalFileLength / TimeSpan.FromMinutes(40)));
            if (silenceTimes.Count > 0 && (chapterBreaks.Count + 1) < minExpectedChapters)
            {
                logger.Log("Not enough chapter breaks found! Will just use rough silence times...");
                chapterBreaks = DetermineChapterBreaksUsingLongSilenceTimes(silenceTimes, logger, totalFileLength, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(20));
            }

            TimeSpan averageChapterLength = totalFileLength / Math.Max(1, chapterBreaks.Count);
            if (averageChapterLength > TimeSpan.FromMinutes(50))
            {
                logger.Log("Chapters still seem ABSURDLY long! We literally just have to insert periodic breaks arbitrarily");
                chapterBreaks = GenerateMonotonousChapterBreaks(totalFileLength, TimeSpan.FromMinutes(30));
            }

            logger.Log("Final chapter breaks:");
            foreach (var finalBreak in chapterBreaks)
            {
                logger.Log(finalBreak.Start + ": " + finalBreak.ChapterName);
            }

            return chapterBreaks.OrderBy((s) => s.Start);
        }

        private static async Task<IList<ChapterBreak>?> TryDetermineChapterBreaksFromMetadata(FileInfo inputFile, ILogger logger)
        {
            // Call ffprobe on the file
            string ffmpegArgs = $"-hide_banner -i \"{inputFile.FullName}\"";

            ProcessStartInfo processInfo = new ProcessStartInfo
            {
                FileName = "ffprobe.exe",
                Arguments = ffmpegArgs,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8
            };

            Regex chapterBoundsParser = new Regex("Chapter [#\\d:]+? start ([\\d\\.]+), end ([\\d\\.]+)");
            Regex chapterTitleParser = new Regex("title\\s+: (.+)");

            using (Process? ffprobeProcess = Process.Start(processInfo))
            {
                if (ffprobeProcess == null)
                {
                    return null;
                }

                try
                {
                    IList<ChapterBreak> returnVal = new List<ChapterBreak>();

                    StreamReader ffprobeOutput = ffprobeProcess.StandardError;
                    string currentLine = currentLine = (await ffprobeOutput.ReadLineAsync()) ?? string.Empty;
                    while (!ffprobeOutput.EndOfStream)
                    {
                        if (currentLine.Contains("Stream #0:0", StringComparison.Ordinal))
                        {
                            break;
                        }
                        else if (currentLine.Contains("Chapter #"))
                        {
                            // Parse the chapter line, then wait for the title
                            Match chapterMatch = chapterBoundsParser.Match(currentLine);
                            if (chapterMatch.Success)
                            {
                                TimeSpan chapterStartTime = TimeSpan.FromSeconds(double.Parse(chapterMatch.Groups[1].Value));
                                do
                                {
                                    currentLine = (await ffprobeOutput.ReadLineAsync()) ?? string.Empty;
                                } while (!ffprobeOutput.EndOfStream && !currentLine.Contains("title"));

                                Match titleMatch = chapterTitleParser.Match(currentLine);
                                if (titleMatch.Success)
                                {
                                    returnVal.Add(new ChapterBreak()
                                    {
                                        Start = chapterStartTime,
                                        ChapterName = titleMatch.Groups[1].Value
                                    });
                                }
                            }
                        }

                        currentLine = ffprobeOutput.ReadLine() ?? string.Empty;
                    }

                    return returnVal;
                }
                finally
                {
                    if (!ffprobeProcess.HasExited)
                    {
                        ffprobeProcess.Kill();
                    }
                }
            }
        }

        private static async Task<Tuple<List<SilenceTime>, TimeSpan>> FindSilenceTimes(
            FileInfo fileToAnalyze,
            ILogger logger,
            ISpeechRecognizerFactory? speechRecoFactory)
        {
            List<SilenceTime> silenceTimes = new List<SilenceTime>(10000);
            TimeSpan minSilenceTime = TimeSpan.FromMilliseconds(700);
            TimeSpan silenceTimeToTriggerSpeechReco = TimeSpan.FromMilliseconds(1200);
            TimeSpan timeToRunSpeechRecoAfterSilence = TimeSpan.FromSeconds(7);
            const float silenceTransitionThresh = 0.001f;

            TimeSpan totalElapsed = TimeSpan.Zero;

            AudioSampleFormat processingFormat = AudioSampleFormat.Mono(48000);

            Queue<SpeechRecoClosure> activeSpeechRecognizers = new Queue<SpeechRecoClosure>();
            IResamplerFactory resamplerFactory = new NativeSpeexResamplerFactory();
            using (IAudioGraph graph = new AudioGraph(AudioGraphCapabilities.Concurrent))
            using (FfmpegAudioSampleSource fileIn = await FfmpegAudioSampleSource.Create(graph, "Ffmpeg", logger.Clone("Ffmpeg"), fileToAnalyze))
            using (AudioConformer conformer = new AudioConformer(graph, fileIn.OutputFormat, processingFormat, "Conformer", resamplerFactory, logger.Clone("Conformer"), AudioProcessingQuality.Fastest))
            using (AudioGate gate = new AudioGate(graph, processingFormat, "Gate", lookAhead: TimeSpan.FromMilliseconds(100)))
            using (PassiveVolumeMeter volumeMeter = new PassiveVolumeMeter(graph, processingFormat, "VolumeMeter"))
            using (AudioSplitter splitter = new AudioSplitter(graph, processingFormat, "Splitter"))
            using (NullAudioSampleTarget sink = new NullAudioSampleTarget(graph, processingFormat, "Sink"))
            {
                fileIn.ConnectOutput(conformer);
                conformer.ConnectOutput(gate);
                gate.ConnectOutput(volumeMeter);
                volumeMeter.ConnectOutput(splitter);
                splitter.AddOutput(sink);

                bool isSilence = false;
                TimeSpan silenceStartTime = totalElapsed;
                int samplesToReadPerLoop = (int)AudioMath.ConvertTimeSpanToSamplesPerChannel(processingFormat.SampleRateHz, TimeSpan.FromMilliseconds(10));

                while (!fileIn.PlaybackFinished/* && totalElapsed < TimeSpan.FromHours(2)*/)
                {
                    int samples = await sink.ReadSamplesFromInput(samplesToReadPerLoop, CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);
                    totalElapsed += AudioMath.ConvertSamplesPerChannelToTimeSpan(processingFormat.SampleRateHz, samples);

                    float peakVolume = volumeMeter.GetPeakVolume();
                    if (!isSilence && peakVolume < silenceTransitionThresh)
                    {
                        isSilence = true;
                        silenceStartTime = totalElapsed;
                    }
                    else if (isSilence && peakVolume > silenceTransitionThresh)
                    {
                        isSilence = false;
                        TimeSpan silenceTime = totalElapsed - silenceStartTime;
                        if (silenceTime > minSilenceTime)
                        {
                            if (silenceTime > silenceTimeToTriggerSpeechReco && speechRecoFactory != null)
                            {
                                // Is this a particularly long silence? Then attach speech recognition to it
                                using (PooledBuffer<float> silence = BufferPool<float>.Rent(processingFormat.SampleRateHz / 2))
                                {
                                    ArrayExtensions.WriteZeroes(silence.Buffer, 0, silence.Length);
                                    MeasuredAudioPipe measuredPipe = new MeasuredAudioPipe(graph, processingFormat, "MeasuredPipe", timeToRunSpeechRecoAfterSilence);
                                    ISpeechRecognizer speechReco = await speechRecoFactory.CreateRecognitionStream(
                                        graph,
                                        "SpeechReco",
                                        LanguageCode.EN_US,
                                        logger.Clone("SpeechReco"),
                                        CancellationToken.None,
                                        DefaultRealTimeProvider.Singleton).ConfigureAwait(false);

                                    // prime SR with some silence
                                    await speechReco.WriteAsync(silence.Buffer, 0, silence.Length, CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);

                                    AudioConformer srConformer = new AudioConformer(graph, measuredPipe.OutputFormat, speechReco.InputFormat, "SrConformer", resamplerFactory, logger.Clone("SrConformer"));
                                    measuredPipe.ConnectOutput(srConformer);
                                    srConformer.ConnectOutput(speechReco);
                                    splitter.AddOutput(measuredPipe);
                                    measuredPipe.TakeOwnershipOfDisposable(srConformer);
                                    SpeechRecoClosure closure = new SpeechRecoClosure(silenceStartTime, totalElapsed, measuredPipe, speechReco);
                                    activeSpeechRecognizers.Enqueue(closure);
                                }
                            }
                            else
                            {
                                silenceTimes.Add(new SilenceTime(silenceStartTime, totalElapsed, string.Empty));
                            }
                        }
                    }

                    // Prune previously created SR closures
                    while (activeSpeechRecognizers.Count > 0 && (fileIn.PlaybackFinished || activeSpeechRecognizers.Peek().Pipe.ReachedEnd))
                    {
                        SpeechRecoClosure finishedSpeechRecoClosure = activeSpeechRecognizers.Dequeue();
                        finishedSpeechRecoClosure.Pipe.DisconnectInput();
                        SpeechRecognitionResult srResult = await finishedSpeechRecoClosure.Recognizer.FinishUnderstandSpeech(CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);
                        string recognizedPhrase = string.Empty;
                        if (srResult != null &&
                            srResult.RecognitionStatus == SpeechRecognitionStatus.Success &&
                            srResult.RecognizedPhrases != null &&
                            srResult.RecognizedPhrases.Count > 0)
                        {
                            recognizedPhrase = srResult.RecognizedPhrases[0].DisplayText;
                            logger.Log(finishedSpeechRecoClosure.SilenceEnd + ": " + recognizedPhrase);
                        }

                        silenceTimes.Add(new SilenceTime(finishedSpeechRecoClosure.SilenceStart, finishedSpeechRecoClosure.SilenceEnd, recognizedPhrase));
                        finishedSpeechRecoClosure.Pipe.Dispose();
                        finishedSpeechRecoClosure.Recognizer.Dispose();
                    }
                }

                // Sort in file order ascending
                silenceTimes.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
                return new Tuple<List<SilenceTime>, TimeSpan>(silenceTimes, totalElapsed);
            }
        }

        private static List<ChapterBreak> DetermineChapterBreaksUsingSpeechReco(
            IList<SilenceTime> silenceTimes,
            ILogger logger)
        {
            // Now try and figure out the layout of the book. Start by looking for parts or major divisions
            // TODO
            // Redwall is a good test case for this

            // Now try and detect individual chapters in ascending order
            int expectedChapter = 1;

            List<ChapterBreak> chapterBreaks = new List<ChapterBreak>();
            int prevChapterBreakIdx = -1;
            int sequentialMissedChapters = 0;
            while (prevChapterBreakIdx < silenceTimes.Count - 1 && sequentialMissedChapters < 3)
            {
                // Search for the next chapter
                int nextChapterBreakIdx;
                Regex nextChapterMatcher = new Regex("^Chapter " + NumberMatchers[expectedChapter] + " ", RegexOptions.IgnoreCase);
                for (nextChapterBreakIdx = prevChapterBreakIdx + 1;
                    nextChapterBreakIdx < silenceTimes.Count && !nextChapterMatcher.Match(silenceTimes[nextChapterBreakIdx].SpeechAfterwards).Success;
                    nextChapterBreakIdx++) { }

                if (nextChapterBreakIdx == silenceTimes.Count)
                {
                    // Didn't find a "Chapter ___" header. Try to reduce the granularity and just find the number
                    // (to handle cases where we either misrecognized the word "chapter", it's not called "chapter", or it's simply not present)
                    nextChapterMatcher = new Regex("^" + NumberMatchers[expectedChapter] + " ", RegexOptions.IgnoreCase);
                    for (nextChapterBreakIdx = prevChapterBreakIdx + 1;
                        nextChapterBreakIdx < silenceTimes.Count && !nextChapterMatcher.Match(silenceTimes[nextChapterBreakIdx].SpeechAfterwards).Success;
                        nextChapterBreakIdx++) { }
                }

                if (nextChapterBreakIdx == silenceTimes.Count)
                {
                    // We didn't find the next chapter. Whatever. Skip it.
                    logger.Log("Couldn't find chapter " + expectedChapter + ", ignoring...", LogLevel.Wrn);
                    sequentialMissedChapters++;
                }
                else
                {
                    sequentialMissedChapters = 0;
                    prevChapterBreakIdx = nextChapterBreakIdx + 1;
                    SilenceTime actualBreak = silenceTimes[nextChapterBreakIdx];
                    chapterBreaks.Add(new ChapterBreak()
                    {
                        Start = (actualBreak.StartTime + actualBreak.EndTime) / 2,
                        ChapterName = "Chapter " + expectedChapter.ToString(),
                        ChapterOrdinal = expectedChapter,
                    });
                }

                expectedChapter++;
            }

            // Are there any discontinuities in the chapter ordinals?
            // If so, assume speech reco messed up and we need to make a best guess for where the break is
            // TODO

            if (chapterBreaks.Count > 0)
            {
                // See if there's a foreword, author's note, or prologue at the beginning
                TimeSpan fistChapterStart = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(5).Ticks, (chapterBreaks[0].Start - TimeSpan.FromSeconds(10)).Ticks));
                IList<SilenceTime> breaksBeforeFirstChapter = silenceTimes.Where(
                    (s) => s.StartTime < fistChapterStart &&
                    !string.IsNullOrEmpty(s.SpeechAfterwards)).ToList();
                IList<SilenceTime> prologue = breaksBeforeFirstChapter
                    .Where((s) => s.SpeechAfterwards.Contains("prologue", StringComparison.OrdinalIgnoreCase)).ToList();
                IList<SilenceTime> authornote = breaksBeforeFirstChapter
                    .Where((s) => s.SpeechAfterwards.Contains("author's note", StringComparison.OrdinalIgnoreCase)).ToList();
                if (prologue.Count > 0)
                {
                    chapterBreaks.Insert(0, new ChapterBreak()
                    {
                        Start = (prologue[0].StartTime + prologue[0].EndTime) / 2,
                        ChapterName = "Prologue",
                        ChapterOrdinal = null,
                    });
                }
                else if (authornote.Count > 0)
                {
                    chapterBreaks.Insert(0, new ChapterBreak()
                    {
                        Start = (authornote[0].StartTime + authornote[0].EndTime) / 2,
                        ChapterName = "Author's Note",
                        ChapterOrdinal = null,
                    });
                }

                // See if there's an epilogue, postscript, or author's note to tack on the end
                TimeSpan lastChapterEnd = chapterBreaks[chapterBreaks.Count - 1].Start + TimeSpan.FromSeconds(10);
                IList<SilenceTime> breaksAfterLastChapter = silenceTimes.Where(
                    (s) => s.EndTime > lastChapterEnd &&
                    !string.IsNullOrEmpty(s.SpeechAfterwards)).ToList();
                IList<SilenceTime> epilogue = breaksAfterLastChapter
                    .Where((s) => s.SpeechAfterwards.Contains("epilogue", StringComparison.OrdinalIgnoreCase)).ToList();
                authornote = breaksAfterLastChapter
                    .Where((s) => s.SpeechAfterwards.Contains("author's note", StringComparison.OrdinalIgnoreCase)).ToList();
                if (epilogue.Count > 0)
                {
                    chapterBreaks.Add(new ChapterBreak()
                    {
                        Start = (epilogue[0].StartTime + epilogue[0].EndTime) / 2,
                        ChapterName = "Epilogue",
                        ChapterOrdinal = null,
                    });
                }
                else if (authornote.Count > 0)
                {
                    chapterBreaks.Add(new ChapterBreak()
                    {
                        Start = (authornote[0].StartTime + authornote[0].EndTime) / 2,
                        ChapterName = "Author's Note",
                        ChapterOrdinal = null,
                    });
                }
            }

            chapterBreaks.Sort((a, b) => a.Start.CompareTo(b.Start));
            return chapterBreaks;
        }

        private static List<ChapterBreak> DetermineChapterBreaksUsingLongSilenceTimes(
            IList<SilenceTime> silenceTimes,
            ILogger logger,
            TimeSpan totalFileLength,
            TimeSpan minChapterLength,
            TimeSpan maxChapterLength)
        {
            List<ChapterBreak> chapterBreaks = new List<ChapterBreak>();
            if (silenceTimes.Count == 0)
            {
                logger.Log("Silence times == 0", LogLevel.Wrn);
                return chapterBreaks;
            }

            // Just split by long silence times, whatever...
            // Figure out the approximate length of a chapter break. This is determined by the total number of
            // breaks in the file, and assuming a target chapter length of (maxChapterLength / 2);
            int approxNumChapters = Math.Min(silenceTimes.Count - 1, (int)(totalFileLength / (maxChapterLength / 2)));
            if (approxNumChapters <= 0)
            {
                logger.Log("Approx chapters == 0", LogLevel.Wrn);
                return chapterBreaks;
            }

            TimeSpan approxBreakTime = (silenceTimes.OrderByDescending((s) => s.Length).Skip(approxNumChapters).First().Length) * 0.95f;
            logger.Log("Approx break time is " + approxBreakTime.PrintTimeSpan());

            TimeSpan currentChapterStart = TimeSpan.Zero;

            while (true)
            {
                var candidateList = silenceTimes
                    .Where((s) => s.StartTime >= currentChapterStart + minChapterLength && s.EndTime <= currentChapterStart + maxChapterLength)
                    .ToList();

                if (candidateList.Count == 0)
                {
                    return chapterBreaks;
                }

                TimeSpan thisBreakTime = TimeSpan.FromTicks(Math.Min(approxBreakTime.Ticks, candidateList.Max((s) => s.Length).Ticks));
                SilenceTime newBreak = candidateList.Where((s) => s.Length >= thisBreakTime).OrderBy((s) => s.StartTime).First();

                chapterBreaks.Add(new ChapterBreak()
                {
                    Start = (newBreak.StartTime + newBreak.EndTime) / 2,
                    ChapterName = Utils.GetWholeWordSubstringOfMaxLength(newBreak.SpeechAfterwards, 30),
                    ChapterOrdinal = null,
                });

                currentChapterStart = (newBreak.StartTime + newBreak.EndTime) / 2;
            }
        }

        private static List<ChapterBreak> GenerateMonotonousChapterBreaks(
            TimeSpan totalFileLength,
            TimeSpan chapterLength)
        {
            List<ChapterBreak> chapterBreaks = new List<ChapterBreak>();
            TimeSpan currentBreak = TimeSpan.Zero;
            int chapterIndex = 1;

            while (currentBreak < totalFileLength)
            {
                chapterBreaks.Add(new ChapterBreak()
                {
                    Start = currentBreak,
                    ChapterName = "Part " + chapterIndex.ToString(),
                    ChapterOrdinal = chapterIndex,
                });

                currentBreak += chapterLength;
                chapterIndex++;
            }

            return chapterBreaks;
        }
    }
}
