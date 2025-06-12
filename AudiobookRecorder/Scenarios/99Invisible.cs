using Durandal.Common.Audio;
using Durandal.Common.Logger;
using Durandal.Common.NLP.Language;
using Durandal.Common.NLP;
using Durandal.Common.Time;
using Durandal.Common.Utils.NativePlatform;
using Durandal.Extensions.Vosk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Durandal.API;
using Durandal.Common.Audio.Codecs.Opus;
using Durandal.Common.Audio.Codecs;
using Durandal.Common.Audio.Components;
using Durandal.Common.Collections;
using Durandal.Common.IO;
using Durandal.Common.Speech.SR;
using Durandal.Extensions.NativeAudio.Codecs;
using Durandal.Extensions.NativeAudio;
using Durandal.Common.Tasks;

namespace AudiobookRecorder.Scenarios
{
    internal static class _99Invisible
    {
        private static void RemoveAdsFrom99Invisible(string[] args)
        {
            DefaultRealTimeProvider.HighPrecisionWaitProvider = new Win32HighPrecisionWaitProvider();
            NativePlatformUtils.SetGlobalResolver(new NativeLibraryResolverImpl());
            ILogger srLogger = new ConsoleLogger("SpeechReco");
            AudioSampleFormat format = AudioSampleFormat.Mono(48000);
            NLPToolsCollection nlTools = new NLPToolsCollection();
            using (VoskSpeechRecognizerFactory speechRecoFactory = new VoskSpeechRecognizerFactory(srLogger, nlTools, format.SampleRateHz, maxRecognizersPerModel: 16))
            {
                speechRecoFactory.LoadLanguageModel(@"C:\Code\Durandal\Data\vosk\vosk-model-en-us-0.42-gigaspeech", LanguageCode.EN_US);
                //speechRecoFactory.LoadLanguageModel(@"C:\Code\Durandal\Data\vosk\vosk-model-en-us-0.22-lgraph", LanguageCode.EN_US);

                DirectoryInfo podcastInputDir = new DirectoryInfo(@"C:\Code\AudiobookRecorder\Invisible\orig");
                foreach (FileInfo file in podcastInputDir.EnumerateFiles("*.opus"))
                {
                    Console.WriteLine("Analyzing " + file.Name);
                    TimeSpan? time = FindBreakAtStartOf99InvisibleEpisode(file, srLogger, speechRecoFactory).Await();

                    if (time.HasValue && time.Value > TimeSpan.FromSeconds(5))
                    {
                        string commandLine = $"-ss {time.Value.Minutes}:{time.Value.Seconds}.{time.Value.Milliseconds} -i \"{file.FullName}\" -c:a copy -map 0:a:0 \"C:\\Code\\AudiobookRecorder\\Invisible\\step1\\{file.Name}\"";
                        //Console.WriteLine("ffmpeg " + commandLine);
                        Utils.RunProcess("ffmpeg.exe", commandLine).Await();
                        commandLine = $"\"C:\\Code\\AudiobookRecorder\\Invisible\\step1\\{file.Name}\" 500 \"C:\\Code\\AudiobookRecorder\\Invisible\\finished\\{file.Name}\"";
                        //Console.WriteLine(commandLine);
                        Utils.RunProcess("C:\\Tools\\OpusTimeShift\\OpusTimeShift.exe", commandLine).Await();
                    }
                }
            }
        }

        private static async Task<TimeSpan?> FindBreakAtStartOf99InvisibleEpisode(FileInfo fileToAnalyze, ILogger logger, ISpeechRecognizerFactory speechRecoFactory)
        {
            TimeSpan minSilenceTime = TimeSpan.FromMilliseconds(800);
            TimeSpan timeToRunSpeechReco = TimeSpan.FromSeconds(12);
            TimeSpan timeToRunSilenceDetection = TimeSpan.FromSeconds(240);
            const float silenceTransitionThresh = 0.001f;

            List<SilenceTime> silenceTimes = new List<SilenceTime>();
            TimeSpan totalElapsed = TimeSpan.Zero;

            AudioSampleFormat processingFormat = AudioSampleFormat.Mono(48000);

            Queue<SpeechRecoClosure> activeSpeechRecognizers = new Queue<SpeechRecoClosure>();
            IResamplerFactory resamplerFactory = new NativeSpeexResamplerFactory();
            IOpusCodecProvider opusCodec = new NativeOpusCodecProvider();
            using (IAudioGraph graph = new AudioGraph(AudioGraphCapabilities.Concurrent))
            using (OggOpusDecoder opusDecoder = new OggOpusDecoder(graph, "OpusIn", null, opusCodec))
            using (FileStream inputFileStream = new FileStream(fileToAnalyze.FullName, FileMode.Open, FileAccess.Read))
            using (AudioGate gate = new AudioGate(graph, processingFormat, "Gate"))
            using (PassiveVolumeMeter volumeMeter = new PassiveVolumeMeter(graph, processingFormat, "VolumeMeter"))
            using (AudioSplitter splitter = new AudioSplitter(graph, processingFormat, "Splitter"))
            using (NullAudioSampleTarget sink = new NullAudioSampleTarget(graph, processingFormat, "Sink"))
            {
                AudioInitializationResult initResult = await opusDecoder.Initialize(inputFileStream, false, CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);
                using (AudioConformer conformer = new AudioConformer(graph, opusDecoder.OutputFormat, processingFormat, "Conformer", resamplerFactory, logger.Clone("Conformer"), AudioProcessingQuality.Fastest))
                {
                    opusDecoder.ConnectOutput(conformer);
                    conformer.ConnectOutput(gate);
                    gate.ConnectOutput(volumeMeter);
                    volumeMeter.ConnectOutput(splitter);
                    splitter.AddOutput(sink);

                    // Create initial SR to record the beginning of the episode
                    {
                        MeasuredAudioPipe measuredPipe = new MeasuredAudioPipe(graph, processingFormat, "MeasuredPipe", timeToRunSpeechReco);
                        ISpeechRecognizer speechReco = await speechRecoFactory.CreateRecognitionStream(
                            graph,
                            "SpeechReco",
                            LanguageCode.EN_US,
                            logger.Clone("SpeechReco"),
                            CancellationToken.None,
                            DefaultRealTimeProvider.Singleton).ConfigureAwait(false);

                        measuredPipe.ConnectOutput(speechReco);
                        splitter.AddOutput(measuredPipe);
                        SpeechRecoClosure closure = new SpeechRecoClosure(TimeSpan.Zero, totalElapsed, measuredPipe, speechReco);
                        activeSpeechRecognizers.Enqueue(closure);
                    }

                    bool isSilence = false;
                    TimeSpan silenceStartTime = totalElapsed;
                    int samplesToReadPerLoop = (int)AudioMath.ConvertTimeSpanToSamplesPerChannel(processingFormat.SampleRateHz, TimeSpan.FromMilliseconds(10));

                    while (!opusDecoder.PlaybackFinished && totalElapsed < timeToRunSilenceDetection)
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
                            if (silenceTime > minSilenceTime && totalElapsed > TimeSpan.FromSeconds(5))
                            {
                                // Is this a particularly long silence? Then attach speech recognition to it
                                using (PooledBuffer<float> silence = BufferPool<float>.Rent(processingFormat.SampleRateHz / 2))
                                {
                                    ArrayExtensions.WriteZeroes(silence.Buffer, 0, silence.Length);
                                    MeasuredAudioPipe measuredPipe = new MeasuredAudioPipe(graph, processingFormat, "MeasuredPipe", timeToRunSpeechReco);
                                    ISpeechRecognizer speechReco = await speechRecoFactory.CreateRecognitionStream(
                                        graph,
                                        "SpeechReco",
                                        LanguageCode.EN_US,
                                        logger.Clone("SpeechReco"),
                                        CancellationToken.None,
                                        DefaultRealTimeProvider.Singleton).ConfigureAwait(false);

                                    // prime SR with some silence
                                    await speechReco.WriteAsync(silence.Buffer, 0, silence.Length, CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);

                                    measuredPipe.ConnectOutput(speechReco);
                                    splitter.AddOutput(measuredPipe);
                                    SpeechRecoClosure closure = new SpeechRecoClosure(silenceStartTime, totalElapsed, measuredPipe, speechReco);
                                    activeSpeechRecognizers.Enqueue(closure);
                                }
                            }
                        }
                    }

                    // Now inspect all our SR closures
                    bool hasAdvertisement = false;
                    TimeSpan? returnVal = null;
                    while (activeSpeechRecognizers.Count > 0)
                    {
                        SpeechRecoClosure finishedSpeechRecoClosure = activeSpeechRecognizers.Dequeue();
                        try
                        {
                            finishedSpeechRecoClosure.Pipe.DisconnectInput();
                            SpeechRecognitionResult srResult = await finishedSpeechRecoClosure.Recognizer.FinishUnderstandSpeech(CancellationToken.None, DefaultRealTimeProvider.Singleton).ConfigureAwait(false);

                            // Find out when roman says "this is ninety nine percent invisible i'm roman mars"
                            // then pick the gap right before that

                            if (srResult == null ||
                                srResult.RecognitionStatus != SpeechRecognitionStatus.Success ||
                                srResult.RecognizedPhrases == null ||
                                srResult.RecognizedPhrases.Count == 0)
                            {
                                continue;
                            }

                            string recognizedPhrase = srResult.RecognizedPhrases[0].DisplayText;
                            logger.Log($"    {finishedSpeechRecoClosure.SilenceEnd.PrintTimeSpan()}: {recognizedPhrase}");

                            if (DoesSubphraseExist(srResult.RecognizedPhrases, "credit", "card", "with", "apple") ||
                                DoesSubphraseExist(srResult.RecognizedPhrases, "some", "companies", "are", "big") ||
                                DoesSubphraseExist(srResult.RecognizedPhrases, "thousands", "of", "new", "podcasts") ||
                                DoesSubphraseExist(srResult.RecognizedPhrases, "the", "best", "savings", "rates", "in", "america") ||
                                DoesSubphraseExist(srResult.RecognizedPhrases, "as", "the", "weather", "gets", "warmer", "bombus") ||
                                DoesSubphraseExist(srResult.RecognizedPhrases, "globally", "ranked", "university", "working") ||
                                DoesSubphraseExist(srResult.RecognizedPhrases, "your", "desk", "chair", "is"))
                            {
                                hasAdvertisement = true;
                                continue;
                            }

                            // Make sure the intro starts with the proper phrase
                            if (!DoesSubphraseExist(srResult.RecognizedPhrases, "ninety", "nine", "percent", "invisible") ||
                                !DoesSubphraseExist(srResult.RecognizedPhrases, "i'm", "roman", "mars"))
                            {
                                continue;
                            }

                            TimeSpan hypothesizedStartTime = finishedSpeechRecoClosure.SilenceEnd - TimeSpan.FromMilliseconds(500);
                            if (!returnVal.HasValue ||
                                returnVal.HasValue && hypothesizedStartTime < returnVal.Value)
                            {
                                // pick either the first found or the earliest timestamp non-advertisement segment to use as the starting break
                                returnVal = hypothesizedStartTime;
                            }
                        }
                        finally
                        {
                            finishedSpeechRecoClosure.Pipe.Dispose();
                            finishedSpeechRecoClosure.Recognizer.Dispose();
                        }
                    }

                    if (returnVal.HasValue)
                    {
                        logger.Log("Found intro at " + returnVal.Value.PrintTimeSpan());
                    }

                    // handle special episodes with a preamble that is not an advertisement
                    if (!hasAdvertisement)
                    {
                        logger.Log("No advertisement in this episode, skipping...");
                        return null;
                    }

                    return returnVal;

                    //Find the break approx. 1.5-2 seconds long just before the commercial break
                    //TimeSpan commercialEnd = TimeSpanExtensions.ParseTimeSpan("2:02");
                    //TimeSpan episodeBreakStart = commercialEnd - TimeSpan.FromSeconds(7);
                    //TimeSpan episodeBreakEnd = commercialEnd + TimeSpan.FromSeconds(3);
                    //List<SilenceTime> afterCommercialBreakCandidates = silenceTimes.Where((s) =>
                    //    s.StartTime > episodeBreakStart &&
                    //    s.EndTime < episodeBreakEnd &&
                    //    s.Length > TimeSpan.FromMilliseconds(1200)).ToList();

                    //if (afterCommercialBreakCandidates.Count != 1)
                    //{
                    //    return null;
                    //}

                    //return afterCommercialBreakCandidates[0].EndTime - TimeSpan.FromSeconds(1);
                }
            }
        }

        private static bool DoesSubphraseExist(IList<SpeechRecognizedPhrase> srResults, params string[] words)
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
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
