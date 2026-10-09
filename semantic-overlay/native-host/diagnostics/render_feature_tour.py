"""Assemble native UI captures into a labeled walkthrough, never a live benchmark.

Requires existing Pillow and imageio-ffmpeg installations. Arguments: frames_dir,
output_dir. FeatureTourCapture supplies only synthetic native UI screenshots.
"""
import subprocess
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import imageio_ffmpeg

frames_dir, out = map(Path, sys.argv[1:3])
out.mkdir(parents=True, exist_ok=True)
font_dir = Path('C:/Windows/Fonts')
def font(size, bold=False):
    return ImageFont.truetype(str(font_dir / ('msyhbd.ttc' if bold else 'msyh.ttc')), size)
scenes = [
    ('01-sentence', '点一句消息，读懂它', 'Ctrl + Alt + K 后点击消息。原句和解释放在一起，方便核对。', '核心功能'),
    ('02-word', '点术语，查看此处的意思', '先给短释义；不离开当前阅读浮框。', '核心功能'),
    ('03-expanded', '需要时，再展开', '补充例子与边界；浮框可以调整尺寸，也可以返回整句解释。', '按需阅读'),
    ('04-selected', '没高亮的词，也能查', '在原句里选中「反馈」，点击解释选中词语。', '主动选词'),
    ('05-calendar', '安排变成可确认的日程', '预填事项和北京时间；核对后才创建提醒。本演示没有保存。', '轻量辅助'),
    ('08-caption', '会议字幕浮框', '先保留英文原文；这里用预设字幕展示界面，没有进行语音识别。', '实验功能'),
    ('06-history', '字幕记录，可以回看', '按日期浏览；支持复制、导出，以及对选中内容继续查询。', '实验功能'),
    ('07-translation', '想看中文，再按需翻译', '翻译当前句或选中段。字幕仍在实验阶段，请核对重要内容。', '实验功能'),
]
frames=[]
for index,(name,title,description,badge) in enumerate(scenes):
    canvas=Image.new('RGB',(1000,960),'#eef3f9');d=ImageDraw.Draw(canvas)
    d.rectangle((0,0,1000,10),fill='#2465cc')
    d.text((48,30),'实时字典  /  0.20.4-beta',font=font(20,True),fill='#426184')
    d.text((48,76),title,font=font(36,True),fill='#18304b')
    d.text((48,137),description,font=font(20),fill='#52647a')
    d.rounded_rectangle((48,186,952,827),radius=20,fill='white')
    screenshot=Image.open(frames_dir/(name+'.png')).convert('RGB')
    ratio=min(864/screenshot.width,605/screenshot.height,1.3)
    screenshot=screenshot.resize((round(screenshot.width*ratio),round(screenshot.height*ratio)),Image.Resampling.LANCZOS)
    canvas.paste(screenshot,((1000-screenshot.width)//2,200+(613-screenshot.height)//2))
    d.text((48,850),f'{index+1:02d} / 08   {badge}',font=font(22,True),fill='#2465cc')
    d.text((48,894),'真实程序界面 · 合成数据 · 经剪辑，不代表实测速度或准确率',font=font(19),fill='#65748a')
    d.rectangle((48,936,952,942),fill='#d6e0ed')
    d.rectangle((48,936,48+int(904*(index+1)/8),942),fill='#2465cc')
    frames.append(canvas)
frames[0].save(out/'feature-tour.gif',save_all=True,append_images=frames[1:],duration=5000,loop=0,optimize=True)
command=[imageio_ffmpeg.get_ffmpeg_exe(),'-y','-loglevel','error','-f','rawvideo','-pix_fmt','rgb24',
         '-s','1000x960','-r','10','-i','-','-an','-c:v','libx264','-preset','fast','-crf','20',
         '-pix_fmt','yuv420p','-movflags','+faststart',str(out/'feature-tour.mp4')]
with subprocess.Popen(command,stdin=subprocess.PIPE) as process:
    for frame in frames:
        for _ in range(50): process.stdin.write(frame.tobytes())
    process.stdin.close()
    if process.wait(): raise RuntimeError('Video encoding failed')
frames[0].save(frames_dir/'tour-preview.png')
print('Created 40-second GIF and MP4; 8 labeled native UI scenes.')
