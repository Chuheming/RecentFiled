// SPDX-FileCopyrightText: 2026 Hemingchu
// SPDX-License-Identifier: MIT
using System;
using System.IO;
class OpenTargetTest
{
    static int Main()
    {
        string dir = Path.Combine(Environment.CurrentDirectory, "tests", "打开测试 space");
        string file = Path.Combine(dir, "文件 name.txt");
        Directory.CreateDirectory(dir);
        File.WriteAllText(file, "launcher regression test");
        try
        {
            int calls = 0;
            RecentWidget.WidgetForm.OpenTarget(file, target => {
                calls++;
                if (target != file) throw new Exception("Path changed before shell delegation");
            });
            if (calls != 1) throw new Exception("Duplicate open request");
            Console.WriteLine("PASS Unicode path and spaces passed intact in one request");
            RecentWidget.WidgetForm.OpenTarget(dir, target => {
                if (target != dir) throw new Exception("Incorrect folder target");
            });
            Console.WriteLine("PASS folder target");
            try {
                RecentWidget.WidgetForm.OpenTarget(file, target => { throw new InvalidOperationException("Shell unavailable"); });
                throw new Exception("Missing failure");
            } catch (InvalidOperationException ex) { if (ex.Message != "Shell unavailable") throw; }
            Console.WriteLine("PASS shell failure preserved without starting a fallback child");
            try {
                RecentWidget.WidgetForm.OpenTarget(file + ".missing", target => { throw new Exception("Should not launch"); });
                throw new Exception("Missing validation");
            } catch (FileNotFoundException) { }
            Console.WriteLine("PASS missing target rejected");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); return 1; }
        finally { File.Delete(file); Directory.Delete(dir); }
    }
}
