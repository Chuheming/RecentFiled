// SPDX-FileCopyrightText: 2026 Hemingchu
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using RecentWidget;
class ParticleLayoutTest {
 [STAThread] static int Main() {
  Program.Headless=true; Program.DemoMode=true;
  using(var form=new WidgetForm())
  using(var frame=new Bitmap(500,584))
  using(var g=Graphics.FromImage(frame)) {
   var cache=typeof(WidgetForm).GetField("projCache",BindingFlags.Instance|BindingFlags.NonPublic);
   var hit=typeof(WidgetForm).GetMethod("TileAt",BindingFlags.Instance|BindingFlags.NonPublic);
   foreach(float t in new[]{0f,6.5f,13f,27f}) {
    form.SetPreviewAnim(t);form.Render(g,false);
    foreach(var pr in (List<Proj>)cache.GetValue(form)) {
     var r=pr.LabelBounds;
     if(r.Width!=128 || r.Top<78 || r.Bottom>544 || r.Left<0 || r.Right>500) throw new Exception("Label outside scene");
     var actual=hit.Invoke(form,new object[]{Point.Round(new PointF(r.Left+r.Width/2,r.Top+r.Height/2))});
     if(!Object.ReferenceEquals(actual,pr.It)) throw new Exception("Label selects the wrong file at time="+t);
    }
    Console.WriteLine("PASS bounds and filename hit targets at time="+t);
   }
   var top=(float)typeof(WidgetForm).GetField("pillarSourceTop",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
   var map=typeof(WidgetForm).GetMethod("PillarY",BindingFlags.Instance|BindingFlags.NonPublic);
   if(Math.Abs((float)map.Invoke(form,new object[]{top})-14)>0.01) throw new Exception("Pillar height mismatch");
   Console.WriteLine("PASS pillar begins 14 pixels from card edge");
  }
  return 0;
 }
}
