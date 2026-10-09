using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace SemanticOverlay.NativeHost
{
    // Real video playback and production process capture; no provider or credentials.
    internal static class LocalVideoCaptureTest
    {
        public static int Main(string[] args)
        {
            if (args.Length != 1 || !File.Exists(args[0])) return 2;
            if (!ProcessLoopbackAudioClient.IsSupported) {
                Console.WriteLine("process-isolation-unavailable; no playback or system capture");
                return 2;
            }
            var clock = new Stopwatch();
            var gate = new object();
            var emitted = new List<double>();
            var durations = new List<double>();
            string captureError = null;
            Exception playbackError = null;
            try {
                using (var reader = new MediaFoundationReader(args[0]))
                using (var output = new WaveOutEvent())
                using (var capture = new SystemAudioCaptionCapture((uint)Process.GetCurrentProcess().Id)) {
                    capture.AudioChunkReady += delegate(byte[] wav) {
                        using (var memory = new MemoryStream(wav, false))
                        using (var segment = new WaveFileReader(memory)) {
                            lock (gate) {
                                emitted.Add(clock.Elapsed.TotalSeconds);
                                durations.Add(segment.TotalTime.TotalSeconds);
                            }
                        }
                    };
                    capture.Failed += delegate(string message) { lock (gate) captureError = message; };
                    output.PlaybackStopped += delegate(object sender, StoppedEventArgs e) { playbackError = e.Exception; };
                    output.Init(reader);
                    capture.Start();
                    if (!capture.IsProcessIsolated) throw new Exception("Unexpected system-wide capture");
                    Console.WriteLine("local-video-start duration_seconds=" + reader.TotalTime.TotalSeconds.ToString("F2") +
                        " process_isolated=true provider_requests=0");
                    clock.Start(); output.Play();
                    double progressAt = 30;
                    while (output.PlaybackState == PlaybackState.Playing) {
                        Thread.Sleep(100);
                        lock (gate) {
                            if (captureError != null) throw new Exception(captureError);
                            if (clock.Elapsed.TotalSeconds >= progressAt) {
                                Console.WriteLine("local-video-progress seconds=" + clock.Elapsed.TotalSeconds.ToString("F1") +
                                    " chunks=" + emitted.Count);
                                progressAt += 30;
                            }
                        }
                        if (clock.Elapsed.TotalSeconds > reader.TotalTime.TotalSeconds + 10)
                            throw new TimeoutException("Video playback exceeded its duration");
                    }
                    if (playbackError != null) throw playbackError;
                    // Wait for loopback to drain naturally; no speech is synthesized.
                    Thread.Sleep(1600);
                    lock (gate) {
                        if (captureError != null) throw new Exception(captureError);
                        if (emitted.Count == 0) throw new Exception("No real audio segment captured");
                        double longest = 0, sum = 0, gap = 0;
                        for (int i=0; i<durations.Count; i++) {
                            longest = Math.Max(longest, durations[i]); sum += durations[i];
                            if (i > 0) gap = Math.Max(gap, emitted[i]-emitted[i-1]);
                        }
                        Console.WriteLine("local-video-complete played_seconds=" + clock.Elapsed.TotalSeconds.ToString("F2") +
                            " chunks=" + emitted.Count + " segment_seconds_sum=" + sum.ToString("F2") +
                            " max_segment_seconds=" + longest.ToString("F2") +
                            " max_emission_interval_seconds=" + gap.ToString("F2") +
                            " first_emission_seconds=" + emitted[0].ToString("F2") +
                            " last_emission_seconds=" + emitted[emitted.Count-1].ToString("F2") +
                            " provider_requests=0");
                        // Emission intervals include silence/music; they are not ASR gaps.
                    }
                }
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
    }
}
