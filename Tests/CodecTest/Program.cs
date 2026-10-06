using System;
using System.Collections.Generic;
using System.Diagnostics;
using RemoteControl.Client;
using RemoteControl.Server;

// 编解码自测：合成一段"桌面"画面（静态背景 + 移动的方块 + 文字状条纹），
// 用每个可用编码器编码，再用客户端解码器解码，检查帧大小、耗时和画面误差。
const int W = 1920, H = 1080, Fps = 30, Frames = 90;
int stride = W * 4;
var src = new byte[stride * H];

void Render(int t)
{
    for (int y = 0; y < H; y++)
    {
        for (int x = 0; x < W; x++)
        {
            int i = y * stride + x * 4;
            // 背景：浅色桌面 + 每 20 行一行"文字"（短黑线段）
            bool text = y % 20 < 2 && (x / 6) % 3 != 0 && x % 400 < 350;
            byte v = text ? (byte)30 : (byte)235;
            src[i] = v;
            src[i + 1] = v;
            src[i + 2] = v;
            src[i + 3] = 255;
        }
    }
    // 移动的方块（模拟拖动窗口）
    int bx = (t * 16) % (W - 300), by = 300;
    for (int y = by; y < by + 200; y++)
        for (int x = bx; x < bx + 300; x++)
        {
            int i = y * stride + x * 4;
            src[i] = 20; src[i + 1] = 120; src[i + 2] = 250;
        }
}

int failures = 0;
foreach (var only in new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" })
{
    var skip = new HashSet<string> { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
    skip.Remove(only);

    H264Encoder enc;
    try
    {
        enc = H264Encoder.Create(W, H, W, H, Fps, 4_000_000, skip, null);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{only,-11} 不可用: {ex.Message}");
        continue;
    }

    using (enc)
    using (var dec = new H264Decoder())
    {
        var fb = new FrameBuffer();
        long total = 0, keyBytes = 0, maxP = 0;
        int decoded = 0, emitted = 0;
        double encMs = 0, decMs = 0, maxErr = 0;
        var sw = new Stopwatch();

        for (int t = 0; t < Frames; t++)
        {
            Render(t);
            bool forceKey = t == 0 || t == 60; // 第 60 帧模拟客户端请求关键帧

            sw.Restart();
            var data = enc.Encode(src, stride, forceKey, out bool isKey);
            encMs += sw.Elapsed.TotalMilliseconds;
            if (data == null) continue;
            emitted++;
            total += data.Length;
            if (isKey) keyBytes += data.Length; else maxP = Math.Max(maxP, data.Length);
            if (forceKey && !isKey) { Console.WriteLine($"  !! 第 {t} 帧强制关键帧未生效"); failures++; }

            sw.Restart();
            if (dec.Decode(data, fb))
            {
                decMs += sw.Elapsed.TotalMilliseconds;
                decoded++;
                var f = fb.TakeLatest()!;
                if (f.Width != W || f.Height != H) { Console.WriteLine($"  !! 解码尺寸 {f.Width}x{f.Height}"); failures++; }

                // 取方块中心的像素比较，颜色偏差应该很小
                int cx = (t * 16) % (W - 300) + 150, cy = 400;
                int si = cy * stride + cx * 4, di = cy * f.Stride + cx * 4;
                double err = Math.Abs(src[si] - f.Pixels[di]) + Math.Abs(src[si + 1] - f.Pixels[di + 1]) + Math.Abs(src[si + 2] - f.Pixels[di + 2]);
                if (err > 60) Console.WriteLine($"  帧 {t}{(isKey ? "(关键帧)" : "")} 大小 {data.Length / 1024.0:F1} KB 色差 {err:F0} 源 {src[si]},{src[si + 1]},{src[si + 2]} 解码 {f.Pixels[di]},{f.Pixels[di + 1]},{f.Pixels[di + 2]}");
                maxErr = Math.Max(maxErr, err);
            }
        }

        bool ok = decoded == emitted && emitted >= Frames - 2 && maxErr < 60;
        if (!ok) failures++;
        Console.WriteLine($"{enc.Name,-11} {(enc.IsHardware ? "硬件" : "软件")} 出帧 {emitted}/{Frames} 解码 {decoded} " +
                          $"平均 {total / Math.Max(1, emitted) / 1024.0:F1} KB/帧 最大P帧 {maxP / 1024.0:F1} KB " +
                          $"≈{total * 8.0 / Frames * Fps / 1_000_000:F2} Mbps  编码 {encMs / Frames:F1} ms 解码 {decMs / Math.Max(1, decoded):F1} ms " +
                          $"色差 {maxErr:F0}  {(ok ? "OK" : "FAIL")}");
    }
}

// 缩放编码（标准档 75%）+ 静止画面
{
    var skip = new HashSet<string>();
    using var enc = H264Encoder.Create(W, H, W * 3 / 4, H * 3 / 4, Fps, 2_000_000, skip, null);
    using var dec = new H264Decoder();
    var fb = new FrameBuffer();
    Render(0);
    long staticBytes = 0;
    int got = 0;
    for (int t = 0; t < 30; t++)
    {
        var d = enc.Encode(src, stride, t == 0, out _);
        if (d == null) continue;
        if (t > 0) staticBytes += d.Length;
        if (dec.Decode(d, fb)) got++;
    }
    var f = fb.TakeLatest();
    bool ok = f != null && f.Width == 1440 && f.Height == 810 && got >= 28;
    if (!ok) failures++;
    Console.WriteLine($"缩放 75% ({enc.Name}): 解码尺寸 {f?.Width}x{f?.Height}，静止画面 P 帧平均 {staticBytes / 29.0 / 1024:F2} KB  {(ok ? "OK" : "FAIL")}");
}

Console.WriteLine(failures == 0 ? "全部通过" : $"失败 {failures} 项");
return failures == 0 ? 0 : 1;
