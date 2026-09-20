using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Windows.Forms;

namespace SemanticOverlay.Diagnostics
{
    internal sealed class AudioMeetingTargetForm : Form
    {
        private readonly string speechPath;
        private readonly string screenshotPath;
        private int ticks;
        public AudioMeetingTargetForm(string speech, string screenshot)
        {
            speechPath=speech; screenshotPath=screenshot;
            Text="无原生字幕的会议测试窗口"; StartPosition=FormStartPosition.CenterScreen;
            ClientSize=new Size(1100,700); BackColor=Color.FromArgb(29,32,38); TopMost=true;
            Label title=new Label { Text="Meeting in progress · captions unavailable", ForeColor=Color.White,
                Font=new Font("Segoe UI",18,FontStyle.Bold), AutoSize=true, Location=new Point(40,35) };
            Label message=new Label { Text="这个窗口本身没有字幕。声音由电脑扬声器播放，实时字典应自行听取并生成透明字幕。",
                ForeColor=Color.FromArgb(180,190,205), Font=new Font("Microsoft YaHei UI",12), AutoSize=true, Location=new Point(42,90) };
            Controls.Add(title); Controls.Add(message);
            using(SpeechSynthesizer voice=new SpeechSynthesizer()) {
                voice.SetOutputToWaveFile(speechPath);
                voice.Speak("我们明天下午和 One API 的同事举行 boot camp 讨论会。请确认 Kubernetes 回滚方案。");
            }
            Timer timer=new Timer { Interval=1000 };
            timer.Tick += delegate {
                ticks++;
                if(ticks==2) { TopMost=true; Activate(); BringToFront(); Native.SwitchToThisWindow(Handle,true); Native.SetForegroundWindow(Handle); }
                if(ticks==5) { Activate(); BringToFront(); using(SoundPlayer player=new SoundPlayer(speechPath)) player.Play(); }
                if(ticks==20) {
                    using(Bitmap image=new Bitmap(Width,Height)) using(Graphics graphics=Graphics.FromImage(image)) {
                        graphics.CopyFromScreen(Left,Top,0,0,image.Size); image.Save(screenshotPath,ImageFormat.Png);
                    }
                }
                if(ticks==30) { timer.Stop(); Close(); }
            };
            Shown += delegate { timer.Start(); };
        }
    }
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint type; public InputData data; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputData { [FieldOffset(0)] public KeyboardInput keyboard; }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort key,scan; public uint flags,time; public UIntPtr extra; }
        [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,Input[] inputs,int size);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern void SwitchToThisWindow(IntPtr window,bool altTab);
        internal static void SendHotkey(ushort key) {
            ushort[] keys={0x11,0x12,key,key,0x12,0x11}; Input[] inputs=new Input[keys.Length];
            for(int i=0;i<inputs.Length;i++){inputs[i].type=1;inputs[i].data.keyboard.key=keys[i];inputs[i].data.keyboard.flags=i>=3?2u:0u;}
            if(SendInput((uint)inputs.Length,inputs,Marshal.SizeOf(typeof(Input)))!=inputs.Length) throw new Exception("SendInput failed");
        }
    }
    internal static class AudioMeetingTarget
    {
        [STAThread] static void Main(string[] args) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new AudioMeetingTargetForm(args[0],args[1]));
        }
    }
}
