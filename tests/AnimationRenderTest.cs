// SPDX-FileCopyrightText: 2026 Hemingchu
// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
class AnimationRenderTest
{
    static int Main() { try { return Run(); } catch (Exception ex) { Console.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; } }
    static int Run()
    {

        using (var icon = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(icon))
            {
                g.Clear(Color.Transparent);
                g.FillRectangle(Brushes.White, 4, 4, 24, 24);
            }
            foreach (float size in new float[] { 25.7f, 32f, 44f })
            foreach (bool vertical in new bool[] { false, true })
            {
                double previous = -1, first = 0, last = 0;
                for (int i = 0; i <= 20; i++)
                using (var frame = new Bitmap(100, 100))
                {
                    using (var g = Graphics.FromImage(frame))
                    {
                        g.Clear(Color.Black);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        RecentWidget.WidgetForm.DrawMovingImage(g, icon, 25f + (vertical ? 0 : i * .1f), 25f + (vertical ? i * .1f : 0), size, size);
                    }
                    double sum = 0, position = 0;
                    for (int y = 0; y < frame.Height; y++)
                    for (int x = 0; x < frame.Width; x++)
                    {
                        double value = frame.GetPixel(x, y).R;
                        sum += value;
                        position += value * (vertical ? y : x);
                    }
                    double center = position / sum;
                    if (i == 0) first = center;
                    else if (center < previous - .01 || center - previous > .2)
                        throw new Exception("Discontinuous movement: " + previous + " -> " + center);
                    previous = last = center;
                }
                if (Math.Abs(last - first - 2) > .05) throw new Exception("Incorrect displacement");
                Console.WriteLine("PASS size=" + size + " axis=" + (vertical ? "Y" : "X") + " displacement=" + (last - first).ToString("F3"));
            }
        }
        return 0;
    }
}
