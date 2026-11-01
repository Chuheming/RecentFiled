// SPDX-FileCopyrightText: 2026 Hemingchu
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using RecentWidget;
class ResizeTest {
 [STAThread] static int Main() { try { return Run(); } catch(Exception ex) { Console.WriteLine("FAIL " + ex.Message); return 1; } }
 static int Run() {
  Program.Headless=true; Program.DemoMode=true;
  using(var form=new WidgetForm()) {
   var flags=BindingFlags.Instance|BindingFlags.NonPublic;
   var cache=typeof(WidgetForm).GetField("projCache",flags);
   var hit=typeof(WidgetForm).GetMethod("TileAt",flags);
   var pillar=typeof(WidgetForm).GetField("pillarCache",flags);
   var proc=typeof(WidgetForm).GetMethod("WndProc",flags);
   foreach(var size in new[]{new Size(500,584),new Size(760,820),new Size(420,380),new Size(900,480),new Size(500,584)}) {
    form.ClientSize=size;
    using(var frame=new Bitmap(size.Width,size.Height)) using(var g=Graphics.FromImage(frame)) {
     form.SetPreviewAnim(6.5f);form.Render(g,false);
     frame.Save("preview-resize-"+size.Width+"x"+size.Height+".png");
    }
    var bitmap=(Bitmap)pillar.GetValue(form);
    if(bitmap.Size!=size) throw new Exception("Stale pillar cache requested="+size+" actual="+form.ClientSize+" cache="+bitmap.Size);
    var points=(List<Proj>)cache.GetValue(form);
    foreach(var pr in points) {
     var r=pr.LabelBounds;
     if(r.Left<0 || r.Right>size.Width || r.Top<78 || r.Bottom>size.Height-40) throw new Exception("Label outside resized scene");
     var selected=hit.Invoke(form,new object[]{Point.Round(new PointF(r.Left+r.Width/2,r.Top+r.Height/2))});
     if(!Object.ReferenceEquals(selected,pr.It)) throw new Exception("Wrong filename hit at "+size);
    }
    int[] expected={10,11,12,15,13,14,16,17};
    Point[] edges={new Point(3,size.Height/2),new Point(size.Width-3,size.Height/2),new Point(size.Width/2,3),new Point(size.Width/2,size.Height-3),new Point(7,7),new Point(size.Width-7,7),new Point(7,size.Height-7),new Point(size.Width-7,size.Height-7)};
    for(int i=0;i<edges.Length;i++) {
     var screen=form.PointToScreen(edges[i]);
     var msg=Message.Create(form.Handle,0x84,IntPtr.Zero,new IntPtr(((screen.Y&65535)<<16)|(screen.X&65535)));
     object[] args={msg};proc.Invoke(form,args);
     if(((Message)args[0]).Result.ToInt32()!=expected[i]) throw new Exception("Resize border hit mismatch");
    }
    Console.WriteLine("PASS "+size+" particles, "+points.Count+" files, labels, eight resize handles");
   }
  }
  return 0;
 }
}
