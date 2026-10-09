using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AntigravityQuotaWidget.Services;

public static class AntigravityBindingService
{
    private const string BlockStart = "' >>> AntigravityQuotaWidget Companion Start >>>";
    private const string BlockEnd = "' <<< AntigravityQuotaWidget Companion End <<<";

    public static string GetVbsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Antigravity",
            "launch-with-proxy.vbs"
        );
    }

    public static bool IsBindingActive()
    {
        try
        {
            var vbsPath = GetVbsPath();
            if (!File.Exists(vbsPath)) return false;
            var content = File.ReadAllText(vbsPath);
            return content.Contains(BlockStart);
        }
        catch (Exception ex)
        {
            App.Log($"[Binding] IsBindingActive check error: {ex.Message}");
            return false;
        }
    }

    public static void ApplyBinding(bool enable)
    {
        try
        {
            var vbsPath = GetVbsPath();
            var dir = Path.GetDirectoryName(vbsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(vbsPath))
            {
                App.Log($"[Binding] launch-with-proxy.vbs not found at: {vbsPath}");
                return;
            }

            var originalContent = File.ReadAllText(vbsPath);

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) ||
                    !File.Exists(exePath) ||
                    !exePath.EndsWith("AntigravityQuotaWidget.exe", StringComparison.OrdinalIgnoreCase))
                {
                    exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AntigravityQuotaWidget.exe");
                }
                if (!File.Exists(exePath) || !exePath.EndsWith("AntigravityQuotaWidget.exe", StringComparison.OrdinalIgnoreCase))
                {
                    exePath = @"E:\workspace\gemini_usage\dist\AntigravityQuotaWidget.exe";
                }

                var blockBuilder = new StringBuilder();
                blockBuilder.AppendLine(BlockStart);
                blockBuilder.AppendLine("Dim widgetExePath, fsoWidget");
                blockBuilder.AppendLine("Set fsoWidget = CreateObject(\"Scripting.FileSystemObject\")");
                blockBuilder.AppendLine($"widgetExePath = \"{exePath}\"");
                blockBuilder.AppendLine("If Not fsoWidget.FileExists(widgetExePath) Then");
                blockBuilder.AppendLine("  widgetExePath = \"E:\\workspace\\gemini_usage\\dist\\AntigravityQuotaWidget.exe\"");
                blockBuilder.AppendLine("End If");
                blockBuilder.AppendLine("If fsoWidget.FileExists(widgetExePath) Then");
                blockBuilder.AppendLine("  CreateObject(\"WScript.Shell\").Run Chr(34) & widgetExePath & Chr(34), 1, False");
                blockBuilder.AppendLine("End If");
                blockBuilder.Append(BlockEnd);

                var companionBlock = blockBuilder.ToString();

                string updatedContent;
                if (originalContent.Contains(BlockStart) && originalContent.Contains(BlockEnd))
                {
                    var pattern = Regex.Escape(BlockStart) + ".*?" + Regex.Escape(BlockEnd);
                    updatedContent = Regex.Replace(originalContent, pattern, companionBlock, RegexOptions.Singleline);
                }
                else
                {
                    var trimmed = originalContent.TrimEnd();
                    updatedContent = trimmed + "\r\n\r\n" + companionBlock + "\r\n";
                }

                // Windows Script Host supports UTF-16 LE with a BOM, but not a UTF-8 BOM.
                File.WriteAllText(vbsPath, updatedContent, Encoding.Unicode);
                App.Log($"[Binding] Antigravity companion launch enabled in: {vbsPath}");
            }
            else
            {
                if (originalContent.Contains(BlockStart))
                {
                    var pattern = @"\r?\n?" + Regex.Escape(BlockStart) + ".*?" + Regex.Escape(BlockEnd) + @"\r?\n?";
                    var updatedContent = Regex.Replace(originalContent, pattern, "\r\n", RegexOptions.Singleline).TrimEnd() + "\r\n";
                    File.WriteAllText(vbsPath, updatedContent, Encoding.Unicode);
                    App.Log($"[Binding] Antigravity companion launch cleanly removed from: {vbsPath}");
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Binding] ApplyBinding error: {ex.Message}");
        }
    }
}
