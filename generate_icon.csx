using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

// Generate record icon: red circle on dark background
var sizes = new[] { 16, 32, 48, 256 };
var pngFiles = new List<byte[]>();

foreach (var size in sizes)
{
    using var bmp = new Bitmap(size, size);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    
    // Dark background circle
    using (var bgBrush = new SolidBrush(Color.FromArgb(255, 24, 24, 32)))
        g.FillEllipse(bgBrush, 0, 0, size - 1, size - 1);
    
    // Red record circle
    int margin = size / 4;
    using (var redBrush = new SolidBrush(Color.FromArgb(255, 220, 38, 38)))
        g.FillEllipse(redBrush, margin, margin, size - margin * 2, size - margin * 2);
    
    // Highlight
    int hlMargin = margin + size / 8;
    int hlSize = size / 6;
    using (var hlBrush = new SolidBrush(Color.FromArgb(80, 255, 255, 255)))
        g.FillEllipse(hlBrush, hlMargin, hlMargin, hlSize, hlSize);
    
    using var ms = new MemoryStream();
    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
    pngFiles.Add(ms.ToArray());
}

// Build ICO file
using var ico = new FileStream(@"C:\Users\Terrence\recordme\RecordMe\record.ico", FileMode.Create);
using var bw = new BinaryWriter(ico);

// ICO header
bw.Write((short)0);     // reserved
bw.Write((short)1);     // type: icon
bw.Write((short)sizes.Length); // count

int offset = 6 + sizes.Length * 16; // header + directory entries

for (int i = 0; i < sizes.Length; i++)
{
    bw.Write((byte)(sizes[i] < 256 ? sizes[i] : 0)); // width
    bw.Write((byte)(sizes[i] < 256 ? sizes[i] : 0)); // height
    bw.Write((byte)0);   // palette
    bw.Write((byte)0);   // reserved
    bw.Write((short)1);  // color planes
    bw.Write((short)32); // bits per pixel
    bw.Write(pngFiles[i].Length); // size
    bw.Write(offset);    // offset
    offset += pngFiles[i].Length;
}

foreach (var png in pngFiles)
    bw.Write(png);

Console.WriteLine("Icon created successfully");
