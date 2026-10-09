using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using NAudio.Wave;

namespace SemanticOverlay.Diagnostics
{
    internal sealed class VideoMeetingTarget : Form
    {
        private readonly MediaFoundationReader reader;
        private readonly WaveOutEvent output = new WaveOutEvent();
        private readonly Timer timer = new Timer { Interval = 250 };
        private readonly Label status = new Label { AutoSize = true, Location = new Point(25, 80) };
        private readonly DateTime opened = DateTime.UtcNow;
        private bool started;

        private VideoMeetingTarget(string video)
        {
            reader = new MediaFoundationReader(video);
            output.Init(reader);
            Text = "实时字典 · 指定英文视频音频实测";
            ClientSize = new Size(750, 180);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 12);
            Controls.Add(new Label { Text = "播放用户指定视频的原音；字幕由正在运行的实时字典生成。",
                AutoSize = true, Location = new Point(25, 30) });
            Controls.Add(status);
            timer.Tick += delegate {
                double elapsed = (DateTime.UtcNow - opened).TotalSeconds;
                if (!started && elapsed >= 12) { started = true; output.Play(); }
                status.Text = started ? "原音播放 " + reader.CurrentTime.ToString(@"mm\:ss") +
                    " / " + reader.TotalTime.ToString(@"mm\:ss") : "准备字幕采集…";
                if (started && output.PlaybackState == PlaybackState.Stopped &&
                    elapsed > reader.TotalTime.TotalSeconds + 22) Close();
            };
            Shown += delegate { timer.Start(); };
            FormClosed += delegate { timer.Stop(); output.Dispose(); reader.Dispose(); timer.Dispose(); };
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length != 1 || !File.Exists(args[0])) throw new ArgumentException("Expected local video path");
            Application.EnableVisualStyles();
            Application.Run(new VideoMeetingTarget(args[0]));
        }
    }
}
