using System;
using System.IO;
using System.Media;
using System.Speech.Synthesis;
using System.Threading;

namespace SemanticOverlay.NativeHost
{
    internal static class AudioLoopbackTest
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 2) throw new ArgumentException("source and captured WAV paths required");
            using (SpeechSynthesizer voice = new SpeechSynthesizer())
            {
                voice.Rate = 0;
                voice.SetOutputToWaveFile(args[0]);
                voice.Speak("我们明天下午和 One API 的同事举行 boot camp 讨论会。");
            }
            byte[] captured = null;
            string failure = null;
            using (ManualResetEvent ready = new ManualResetEvent(false))
            using (SystemAudioCaptionCapture recorder = new SystemAudioCaptionCapture())
            {
                recorder.AudioChunkReady += delegate(byte[] value) { captured = value; ready.Set(); };
                recorder.Failed += delegate(string value) { failure = value; ready.Set(); };
                recorder.Start();
                Thread.Sleep(300);
                using (SoundPlayer player = new SoundPlayer(args[0])) player.PlaySync();
                ready.WaitOne(6000);
            }
            if (failure != null) throw new Exception(failure);
            if (captured == null || captured.Length < 8000) throw new Exception("No voiced loopback segment was captured.");
            File.WriteAllBytes(args[1], captured);
            Console.WriteLine("PASS loopback captured a voiced WAV chunk: " + captured.Length + " bytes");
            return 0;
        }
    }
}
