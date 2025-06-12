using Durandal.Common.Audio;
using Durandal.Common.Audio.Codecs;
using Durandal.Common.Audio.Codecs.Opus;
using Durandal.Common.Audio.Components;
using Durandal.Common.Logger;
using Durandal.Common.MathExt;
using Durandal.Common.NLP.Language;
using Durandal.Common.NLP;
using Durandal.Common.Tasks;
using Durandal.Common.Time;
using Durandal.Common.Utils;
using Durandal.Common.Utils.NativePlatform;
using Durandal.Extensions.BassAudio;
using Durandal.Extensions.NativeAudio;
using Durandal.Extensions.NativeAudio.Codecs;
using Durandal.Extensions.NativeAudio.Components;
using Durandal.Extensions.Vosk;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Durandal.Common.Speech.SR;
using Durandal.API;
using Durandal.Common.IO;
using Durandal.Common.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;
using AudiobookRecorder.Scenarios;
using static Org.BouncyCastle.Bcpg.Attr.ImageAttrib;
using Durandal.Common.Speech.SR.Azure;
using Durandal.Common.Net.WebSocket;
using Durandal.Common.Net.Http;

namespace AudiobookRecorder
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("USAGE:");
                Console.WriteLine("AudioBookRecorder.exe -capture {-t 10}");
                Console.WriteLine("   Captures single-segment audio into the /recordings folder");
                Console.WriteLine("   -t parameter is the optional amount of silence required to stop recording (default 10 sec)");
                Console.WriteLine("AudioBookRecorder.exe -split {directory} {-vosk /path/to/voskmodel} {-azure azureapikey}");
                Console.WriteLine("   Processes all audio files in {directory} and splits them into component chapters");
                Console.WriteLine("   -vosk parameter refers to a Vosk model file to use for speech recognition");
                Environment.ExitCode = -1;
                return;
            }

            var argsDict = CommandLineParser.ParseArgs(args);
            List<string>? argsVal;
            if (argsDict.ContainsKey("capture"))
            {
                TimeSpan silenceTimeForCapture;
                if (!argsDict.TryGetValue("t", out argsVal) || !TimeSpanExtensions.TryParseTimeSpan(argsVal.Single(), out silenceTimeForCapture))
                {
                    silenceTimeForCapture = TimeSpan.FromSeconds(10);
                }

                LiveRecording.RecordAudiobookLiveSingleSegment(silenceTimeForCapture).Await();
            }
            else if (argsDict.TryGetValue("split", out argsVal))
            {
                string audioPath = argsVal.Single();

                ISpeechRecognizerFactory? speechRecoFactory = null;
                DefaultRealTimeProvider.HighPrecisionWaitProvider = new Win32HighPrecisionWaitProvider();
                NativePlatformUtils.SetGlobalResolver(new NativeLibraryResolverImpl());
                ILogger srLogger = new ConsoleLogger("SpeechReco");
                AudioSampleFormat format = AudioSampleFormat.Mono(48000);

                if (argsDict.TryGetValue("vosk", out argsVal))
                {
                    string? voskModelPath = argsVal.Single();
                    if (!string.IsNullOrEmpty(voskModelPath) && Directory.Exists(voskModelPath))
                    {
                        NLPToolsCollection nlTools = new NLPToolsCollection();
                        VoskSpeechRecognizerFactory vosk = new VoskSpeechRecognizerFactory(srLogger, nlTools, format.SampleRateHz, maxRecognizersPerModel: 4);
                        vosk.LoadLanguageModel(voskModelPath, LanguageCode.EN_US);
                        speechRecoFactory = vosk;
                    }
                    else
                    {
                        Console.WriteLine("Invalid Vosk model path (does the directory exist?)");
                        Environment.ExitCode = -1;
                        return;
                    }
                }
                else if (argsDict.TryGetValue("azure", out argsVal))
                {
                    string? azureSpeechRecoKey = argsVal.Single();
                    IWebSocketClientFactory webSocketClientFactory = new SystemWebSocketClientFactory();
                    IHttpClientFactory srTokenRefreshClientFactory = new PortableHttpClientFactory();
                    speechRecoFactory = new AzureSpeechRecognizerFactory(srTokenRefreshClientFactory, webSocketClientFactory, srLogger, azureSpeechRecoKey, DefaultRealTimeProvider.Singleton);
                }

                using (speechRecoFactory)
                {
                    SplitIntoChapters.SplitSingleAudiobookIntoChapters(audioPath, speechRecoFactory).Await();
                }
            }
            else
            {
                Console.WriteLine("Unknown or invalid action");
            }
        }
    }
}
