// SPDX-FileCopyrightText: 2026 Hemingchu
// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using RecentWidget;
class RefreshIntervalTest {
 [STAThread] static int Main() {
  try { Run(); return 0; } catch(Exception ex){Console.WriteLine("FAIL "+ex.Message);return 1;}
 }
 static void Run() {
  Program.Headless=true; Program.DemoMode=true;
  using(var form=new WidgetForm())
  using(var timer=new System.Windows.Forms.Timer()) {
   var flags=BindingFlags.Instance|BindingFlags.NonPublic;
   typeof(WidgetForm).GetField("refresh",flags).SetValue(form,timer);
   var menu=(ToolStripMenuItem)typeof(WidgetForm).GetMethod("CreateRefreshMenu",flags).Invoke(form,null);
   typeof(WidgetForm).GetField("trayRefreshMenu",flags).SetValue(form,menu);
   foreach(int seconds in new[]{30,60,180,300}) {
    foreach(ToolStripItem child in menu.DropDownItems) {
     var option=child as ToolStripMenuItem;
     if(option!=null && (int)option.Tag==seconds) option.PerformClick();
    }
    if(timer.Interval!=seconds*1000 || !timer.Enabled) throw new Exception("Preset did not restart timer");
    int checkedCount=0;
    foreach(ToolStripItem child in menu.DropDownItems) {
     var option=child as ToolStripMenuItem;
     if(option!=null && option.Checked) {checkedCount++;if((int)option.Tag!=seconds)throw new Exception("Wrong checked preset");}
    }
    if(checkedCount!=1)throw new Exception("Preset selection not exclusive");
    Console.WriteLine("PASS preset "+seconds+" seconds, live timer and menu");
   }
   form.SetRefreshInterval(75);
   if(timer.Interval!=75000 || !((ToolStripMenuItem)menu.DropDownItems[5]).Checked)throw new Exception("Custom interval not applied");
   Console.WriteLine("PASS custom 75 seconds and selected custom menu");
   foreach(int invalid in new[]{0,-1,86401,int.MaxValue}) {
    try {form.SetRefreshInterval(invalid);throw new Exception("Invalid accepted");}
    catch(ArgumentOutOfRangeException){}
    if(timer.Interval!=75000)throw new Exception("Invalid input changed existing timer");
   }
   form.SetRefreshInterval(86400);
   if(timer.Interval!=86400000)throw new Exception("24 hour interval overflow");
   Console.WriteLine("PASS validation and 24 hour maximum");
   int ticks=0;timer.Tick+=(s,e)=>ticks++;
   form.SetRefreshInterval(1);
   var watch=Stopwatch.StartNew();
   while(watch.ElapsedMilliseconds<1450){Application.DoEvents();Thread.Sleep(5);}
   if(ticks<1)throw new Exception("Timer did not actually fire");
   form.SetRefreshInterval(30);
   int before=ticks;
   watch.Restart();while(watch.ElapsedMilliseconds<1100){Application.DoEvents();Thread.Sleep(5);}
   if(ticks!=before)throw new Exception("Old interval continued firing");
   Console.WriteLine("PASS real timer tick and countdown reset on interval change");
   menu.Dispose();
  }
 }
}
