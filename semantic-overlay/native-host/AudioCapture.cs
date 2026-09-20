using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SemanticOverlay.NativeHost
{
    internal sealed class SystemAudioCaptionCapture : IDisposable
    {
        private const int TargetRate = 16000;
        private const double MinimumVoiceThreshold = 0.0038;
        private const double NoiseMultiplier = 3.0;
        private const int SpeechAttackSamples = TargetRate * 12 / 100;
        private const int PreRollSamples = TargetRate * 3 / 10;
        private const int BoundaryOverlapSamples = TargetRate / 4;
        private const int SilenceEndSamples = TargetRate * 7 / 10;
        private const int MinimumVoiceSamples = TargetRate * 35 / 100;
        private const int MaximumSegmentSamples = TargetRate * 8;
        private readonly object gate = new object();
        private readonly List<short> segment = new List<short>();
        private readonly List<short> preRoll = new List<short>();
        private readonly uint? targetProcessId;
        private WasapiLoopbackCapture capture;
        private AudioClient processClient;
        private AudioCaptureClient processCaptureClient;
        private Thread processCaptureThread;
        private volatile bool stopProcessCapture;
        private WaveFormat source;
        private long sourceFrames;
        private long outputFrames;
        private int silentSamples;
        private int voicedSamples;
        private int attackSamples;
        private double noiseFloor = 0.0010;
        private bool active;
        private bool disposed;

        public event Action<byte[]> AudioChunkReady;
        public event Action<string> Failed;

        public bool IsProcessIsolated { get; private set; }

        public SystemAudioCaptionCapture(uint? processId)
        {
            targetProcessId = processId;
        }

        public SystemAudioCaptionCapture() : this(null) { }

        public void Start()
        {
            lock (gate)
            {
                if (capture != null || processClient != null) return;
                if (targetProcessId.HasValue && ProcessLoopbackAudioClient.IsSupported)
                {
                    StartProcessCapture(targetProcessId.Value);
                    IsProcessIsolated = true;
                    return;
                }
                capture = new WasapiLoopbackCapture();
                source = capture.WaveFormat;
                if (source.SampleRate < TargetRate || source.Channels < 1 || source.Channels > 16)
                    throw new InvalidOperationException("当前系统播放设备的音频格式不受支持。");
                capture.DataAvailable += OnData;
                capture.RecordingStopped += OnStopped;
                capture.StartRecording();
            }
        }

        private void StartProcessCapture(uint processId)
        {
            source = new WaveFormat(44100, 16, 2);
            processClient = ProcessLoopbackAudioClient.Activate(
                processId, TimeSpan.FromSeconds(6));
            AudioClientStreamFlags flags = (AudioClientStreamFlags)(
                (int)AudioClientStreamFlags.Loopback |
                unchecked((int)0x80000000) |  // AUTOCONVERTPCM
                0x08000000);                  // SRC_DEFAULT_QUALITY
            processClient.Initialize(
                AudioClientShareMode.Shared, flags, 0, 0, source, Guid.Empty);
            processCaptureClient = processClient.AudioCaptureClient;
            stopProcessCapture = false;
            processClient.Start();
            processCaptureThread = new Thread(ProcessCaptureLoop);
            processCaptureThread.IsBackground = true;
            processCaptureThread.Name = "RealtimeDictionary Process Audio";
            processCaptureThread.Start();
        }

        private void ProcessCaptureLoop()
        {
            AudioCaptureClient client = processCaptureClient;
            try
            {
                while (!stopProcessCapture)
                {
                    int packetFrames = client.GetNextPacketSize();
                    if (packetFrames <= 0)
                    {
                        Thread.Sleep(10);
                        continue;
                    }
                    while (packetFrames > 0 && !stopProcessCapture)
                    {
                        int frames;
                        AudioClientBufferFlags flags;
                        IntPtr data = client.GetBuffer(out frames, out flags);
                        try
                        {
                            int byteCount = frames * source.BlockAlign;
                            byte[] buffer = new byte[byteCount];
                            if ((flags & AudioClientBufferFlags.Silent) == 0 && data != IntPtr.Zero)
                                Marshal.Copy(data, buffer, 0, byteCount);
                            ProcessRawBuffer(buffer, byteCount);
                        }
                        finally
                        {
                            client.ReleaseBuffer(frames);
                        }
                        packetFrames = client.GetNextPacketSize();
                    }
                }
            }
            catch (Exception error)
            {
                if (!disposed && !stopProcessCapture && Failed != null)
                    Failed("会议进程声音采集失败：" + error.Message);
            }
        }

        private void OnData(object sender, WaveInEventArgs args)
        {
            try
            {
                ProcessRawBuffer(args.Buffer, args.BytesRecorded);
            }
            catch (Exception error)
            {
                if (Failed != null) Failed("音频处理失败：" + error.Message);
            }
        }

        private void ProcessRawBuffer(byte[] buffer, int count)
        {
            List<short> mono = Downsample(buffer, count);
            if (mono.Count == 0) return;
            byte[] completed = ProcessMonoSamples(mono);
            if (completed != null && AudioChunkReady != null) AudioChunkReady(completed);
        }

        internal byte[] ProcessMonoSamples(IList<short> mono)
        {
            if (mono == null || mono.Count == 0) return null;
            double sum = 0;
            double peak = 0;
            foreach (short raw in mono)
            {
                double value = Math.Abs(raw / 32768.0);
                sum += value * value;
                if (value > peak) peak = value;
            }
            double rms = Math.Sqrt(sum / mono.Count);
            lock (gate)
            {
                if (disposed) return null;
                double threshold = Math.Max(MinimumVoiceThreshold, noiseFloor * NoiseMultiplier);
                bool speech = rms >= threshold && peak >= threshold * 1.55;
                bool startedNow = false;
                if (!active)
                {
                    AppendRolling(preRoll, mono, PreRollSamples);
                    if (speech)
                        attackSamples += mono.Count;
                    else
                    {
                        attackSamples = 0;
                        noiseFloor = Math.Max(0.0005, Math.Min(0.02,
                            noiseFloor * 0.94 + rms * 0.06));
                    }
                    if (attackSamples < SpeechAttackSamples) return null;
                    active = true;
                    startedNow = true;
                    silentSamples = 0;
                    voicedSamples = attackSamples;
                    attackSamples = 0;
                    segment.Clear();
                    segment.AddRange(preRoll);
                    preRoll.Clear();
                }
                if (!startedNow)
                {
                    segment.AddRange(mono);
                    if (speech) { voicedSamples += mono.Count; silentSamples = 0; }
                    else silentSamples += mono.Count;
                }
                if (silentSamples >= SilenceEndSamples)
                    return FinishSegment(false);
                if (segment.Count >= MaximumSegmentSamples)
                    return FinishSegment(true);
                return null;
            }
        }

        private static void AppendRolling(List<short> target, IList<short> samples, int limit)
        {
            for (int index = 0; index < samples.Count; index++) target.Add(samples[index]);
            int overflow = target.Count - limit;
            if (overflow > 0) target.RemoveRange(0, overflow);
        }

        private List<short> Downsample(byte[] buffer, int count)
        {
            List<short> result = new List<short>();
            int bytesPerSample = source.BitsPerSample / 8;
            int block = bytesPerSample * source.Channels;
            if (bytesPerSample < 2 || block <= 0) return result;
            int frames = count / block;
            for (int frame = 0; frame < frames; frame++, sourceFrames++)
            {
                if (sourceFrames * TargetRate < outputFrames * source.SampleRate) continue;
                double mixed = 0;
                for (int channel = 0; channel < source.Channels; channel++)
                {
                    int offset = frame * block + channel * bytesPerSample;
                    double sample;
                    if (source.BitsPerSample == 32 && source.Encoding != WaveFormatEncoding.Pcm)
                        sample = BitConverter.ToSingle(buffer, offset);
                    else if (source.BitsPerSample == 16)
                        sample = BitConverter.ToInt16(buffer, offset) / 32768.0;
                    else if (source.BitsPerSample == 24)
                    {
                        int value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                        if ((value & 0x800000) != 0) value |= unchecked((int)0xff000000);
                        sample = value / 8388608.0;
                    }
                    else if (source.BitsPerSample == 32)
                        sample = BitConverter.ToInt32(buffer, offset) / 2147483648.0;
                    else continue;
                    mixed += Math.Max(-1.0, Math.Min(1.0, sample));
                }
                mixed /= source.Channels;
                result.Add((short)Math.Max(short.MinValue, Math.Min(short.MaxValue, (int)(mixed * 32767))));
                outputFrames++;
            }
            return result;
        }

        private byte[] FinishSegment(bool preserveBoundary)
        {
            byte[] wav = voicedSamples >= MinimumVoiceSamples ? BuildWav(segment) : null;
            preRoll.Clear();
            if (preserveBoundary && segment.Count > 0)
            {
                int start = Math.Max(0, segment.Count - BoundaryOverlapSamples);
                for (int index = start; index < segment.Count; index++) preRoll.Add(segment[index]);
            }
            segment.Clear(); active = false; silentSamples = voicedSamples = attackSamples = 0;
            return wav;
        }

        internal static byte[] BuildWav(IList<short> samples)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int dataSize = samples.Count * 2;
                writer.Write(new[] {'R','I','F','F'}); writer.Write(36 + dataSize);
                writer.Write(new[] {'W','A','V','E'}); writer.Write(new[] {'f','m','t',' '}); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(TargetRate);
                writer.Write(TargetRate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(new[] {'d','a','t','a'}); writer.Write(dataSize);
                foreach (short sample in samples) writer.Write(sample);
                writer.Flush(); return stream.ToArray();
            }
        }

        private void OnStopped(object sender, StoppedEventArgs args)
        {
            if (!disposed && args.Exception != null && Failed != null)
                Failed("系统声音采集已停止：" + args.Exception.Message);
        }

        public void Dispose()
        {
            WasapiLoopbackCapture systemCapture;
            AudioClient isolatedClient;
            Thread isolatedThread;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                stopProcessCapture = true;
                systemCapture = capture;
                capture = null;
                isolatedClient = processClient;
                processClient = null;
                processCaptureClient = null;
                isolatedThread = processCaptureThread;
                processCaptureThread = null;
                segment.Clear();
                preRoll.Clear();
            }
            if (systemCapture != null)
            {
                systemCapture.DataAvailable -= OnData;
                systemCapture.RecordingStopped -= OnStopped;
                try { systemCapture.StopRecording(); } catch { }
                systemCapture.Dispose();
            }
            if (isolatedClient != null)
            {
                try { isolatedClient.Stop(); } catch { }
            }
            if (isolatedThread != null && isolatedThread.IsAlive)
                isolatedThread.Join(1000);
            if (isolatedClient != null) isolatedClient.Dispose();
        }
    }
}
