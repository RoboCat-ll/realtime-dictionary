using System;
using System.Diagnostics;
using System.Threading;
using NAudio.Wave;

namespace SemanticOverlay.NativeHost
{
    internal static class ProcessLoopbackTest
    {
        private static byte[] ToneWithSilence()
        {
            const int rate = 44100;
            const int seconds = 2;
            byte[] data = new byte[rate * seconds * 4];
            for (int frame = 0; frame < rate * seconds; frame++)
            {
                double time = (double)frame / rate;
                short value = time >= 0.25 && time < 1.15
                    ? (short)(Math.Sin(time * 2 * Math.PI * 523.25) * 7000) : (short)0;
                int offset = frame * 4;
                data[offset] = (byte)value;
                data[offset + 1] = (byte)(value >> 8);
                data[offset + 2] = (byte)value;
                data[offset + 3] = (byte)(value >> 8);
            }
            return data;
        }

        public static int Main()
        {
            if (!ProcessLoopbackAudioClient.IsSupported)
            {
                Console.WriteLine("process-loopback-unsupported");
                return 2;
            }
            ManualResetEvent received = new ManualResetEvent(false);
            byte[] chunk = null;
            using (SystemAudioCaptionCapture capture = new SystemAudioCaptionCapture(
                (uint)Process.GetCurrentProcess().Id))
            using (WaveOutEvent output = new WaveOutEvent())
            {
                capture.AudioChunkReady += delegate(byte[] wav) { chunk = wav; received.Set(); };
                capture.Start();
                if (!capture.IsProcessIsolated)
                    throw new InvalidOperationException("Capture did not use the target process.");
                BufferedWaveProvider provider = new BufferedWaveProvider(new WaveFormat(44100, 16, 2));
                provider.BufferLength = 44100 * 4 * 3;
                provider.DiscardOnBufferOverflow = false;
                byte[] audio = ToneWithSilence();
                provider.AddSamples(audio, 0, audio.Length);
                output.Init(provider);
                output.Play();
                if (!received.WaitOne(8000))
                    throw new TimeoutException("No isolated audio segment was captured.");
            }
            if (chunk == null || chunk.Length < 12000)
                throw new InvalidOperationException("Isolated audio segment was invalid.");
            Console.WriteLine("process-loopback-ok bytes=" + chunk.Length);
            return 0;
        }
    }
}
