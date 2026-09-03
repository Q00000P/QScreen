using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;

namespace QScreen
{
    public class HotkeyBinding
    {
        public string Name = "";
        public uint Modifiers;
        public uint Key;
        public string DisplayText = "";

        public static HotkeyBinding FromString(string name, string? str, string defaultStr)
        {
            var def = Parse(defaultStr);
            if (string.IsNullOrWhiteSpace(str)) str = defaultStr;
            var (mod, key) = Parse(str);
            if (key == 0) { (mod, key) = def; str = defaultStr; }
            return new HotkeyBinding { Name = name, Modifiers = mod, Key = key, DisplayText = str };
        }

        private static (uint, uint) Parse(string str)
        {
            uint mod = 0, k = 0;
            foreach (var raw in str.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var p = raw;
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) mod |= Win32.MOD_CONTROL;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mod |= Win32.MOD_SHIFT;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mod |= Win32.MOD_ALT;
                else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase)) mod |= Win32.MOD_WIN;
                else if (p.Length == 1 && char.IsDigit(p[0])) k = (uint)p[0];
                else if (Enum.TryParse<Key>(p, true, out var wpfKey)) k = (uint)KeyInterop.VirtualKeyFromKey(wpfKey);
                else if (p.Length == 1) k = (uint)char.ToUpperInvariant(p[0]);
            }
            return (mod, k);
        }
    }

    public static class AppSettings
    {
        private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QScreen");
        private static string ConfigPath => Path.Combine(Dir, "config.ini");

        // Хоткеи (Cmd на маке → Ctrl на винде)
        public static HotkeyBinding HK_Area = HotkeyBinding.FromString("Area", null, "Ctrl + Shift + 4");
        public static HotkeyBinding HK_Smart = HotkeyBinding.FromString("Smart", null, "Ctrl + Shift + 6");
        public static HotkeyBinding HK_Scroll = HotkeyBinding.FromString("Scroll", null, "Ctrl + Shift + 7");
        public static HotkeyBinding HK_Screen = HotkeyBinding.FromString("Screen", null, "Ctrl + Shift + 3");
        public static HotkeyBinding HK_Record = HotkeyBinding.FromString("Record", null, "Ctrl + Shift + 5");
        public static HotkeyBinding HK_RecordStop = HotkeyBinding.FromString("RecordStop", null, "Ctrl + Alt + S");
        public static HotkeyBinding HK_RecordPause = HotkeyBinding.FromString("RecordPause", null, "Ctrl + Alt + P");

        // Файлы
        public static string DefaultFormat = "png";      // png, heic, jpg, pdf
        public static double JpegQuality = 0.85;
        public static string FilenamePrefix = "QScreen";
        public static string DateFormat = "dd.MM.yyyy_HH.mm.ss"; // или "unix"
        public static string SaveFolder = "";            // пусто = Рабочий стол
        public static bool DirectSave = false;
        public static bool ShowThumbnail = false;   // true = миниатюра в углу ВМЕСТО редактора; по умолчанию редактор сразу

        // Видео
        public static string VideoFormat = "mp4";        // mp4, mov
        public static string VideoCodec = "h264";        // h264 (дефолт на винде: играет везде), hevc
        public static int VideoFps = 60;
        public static bool RecordAudio = false;
        public static bool ShowCursor = true;

        public static IEnumerable<HotkeyBinding> AllHotkeys => new[] { HK_Area, HK_Smart, HK_Scroll, HK_Screen, HK_Record, HK_RecordStop, HK_RecordPause };

        public static void Load()
        {
            if (!File.Exists(ConfigPath)) return;
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(ConfigPath))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2) kv[parts[0].Trim()] = parts[1].Trim();
            }
            string G(string k, string d) => kv.TryGetValue(k, out var v) ? v : d;
            bool B(string k, bool d) => bool.TryParse(G(k, ""), out var b) ? b : d;

            HK_Area = HotkeyBinding.FromString("Area", G("HK_Area", ""), "Ctrl + Shift + 4");
            HK_Smart = HotkeyBinding.FromString("Smart", G("HK_Smart", ""), "Ctrl + Shift + 6");
            HK_Scroll = HotkeyBinding.FromString("Scroll", G("HK_Scroll", ""), "Ctrl + Shift + 7");
            HK_Screen = HotkeyBinding.FromString("Screen", G("HK_Screen", ""), "Ctrl + Shift + 3");
            HK_Record = HotkeyBinding.FromString("Record", G("HK_Record", ""), "Ctrl + Shift + 5");
            HK_RecordStop = HotkeyBinding.FromString("RecordStop", G("HK_RecordStop", ""), "Ctrl + Alt + S");
            HK_RecordPause = HotkeyBinding.FromString("RecordPause", G("HK_RecordPause", ""), "Ctrl + Alt + P");

            DefaultFormat = G("DefaultFormat", "png").ToLowerInvariant();
            if (DefaultFormat == "jpeg") DefaultFormat = "jpg";
            if (DefaultFormat is not ("png" or "jpg" or "heic" or "pdf")) DefaultFormat = "png";
            if (double.TryParse(G("JpegQuality", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var q)) JpegQuality = Math.Clamp(q, 0.5, 1.0);
            FilenamePrefix = G("FilenamePrefix", "QScreen");
            DateFormat = G("DateFormat", "dd.MM.yyyy_HH.mm.ss");
            var folder = G("SaveFolder", "");
            SaveFolder = Directory.Exists(folder) ? folder : "";
            DirectSave = B("DirectSave", false);
            ShowThumbnail = B("ThumbnailInsteadOfEditor", false);

            VideoFormat = G("VideoFormat", "mp4").ToLowerInvariant() == "mov" ? "mov" : "mp4";
            VideoCodec = G("VideoCodec", "h264").ToLowerInvariant() == "hevc" ? "hevc" : "h264";
            if (int.TryParse(G("VideoFps", ""), out var fps)) VideoFps = fps == 30 ? 30 : 60;
            RecordAudio = B("RecordAudio", false);
            ShowCursor = B("ShowCursor", B("RecordCursor", true));
        }

        public static void Save()
        {
            Directory.CreateDirectory(Dir);
            var lines = new[]
            {
                $"HK_Area={HK_Area.DisplayText}",
                $"HK_Smart={HK_Smart.DisplayText}",
                $"HK_Scroll={HK_Scroll.DisplayText}",
                $"HK_Screen={HK_Screen.DisplayText}",
                $"HK_Record={HK_Record.DisplayText}",
                $"HK_RecordStop={HK_RecordStop.DisplayText}",
                $"HK_RecordPause={HK_RecordPause.DisplayText}",
                $"DefaultFormat={DefaultFormat}",
                $"JpegQuality={JpegQuality.ToString(CultureInfo.InvariantCulture)}",
                $"FilenamePrefix={FilenamePrefix}",
                $"DateFormat={DateFormat}",
                $"SaveFolder={SaveFolder}",
                $"DirectSave={DirectSave}",
                $"ThumbnailInsteadOfEditor={ShowThumbnail}",
                $"VideoFormat={VideoFormat}",
                $"VideoCodec={VideoCodec}",
                $"VideoFps={VideoFps}",
                $"RecordAudio={RecordAudio}",
                $"ShowCursor={ShowCursor}",
            };
            File.WriteAllLines(ConfigPath, lines);
        }

        // --- Автозапуск (аналог SMAppService) ---
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool IsLaunchAtLogin()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue("QScreen") != null;
        }
        public static void SetLaunchAtLogin(bool on)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue("QScreen", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("QScreen", false);
        }
    }

    public static class FilenameHelper
    {
        public static string GetDefaultFormat() => AppSettings.DefaultFormat;

        public static string GenerateFilename(string? ext = null)
        {
            var chosenExt = (ext ?? GetDefaultFormat()).ToLowerInvariant();
            if (chosenExt == "jpeg") chosenExt = "jpg";
            string dateStr = AppSettings.DateFormat == "unix"
                ? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
                : DateTime.Now.ToString(AppSettings.DateFormat, CultureInfo.InvariantCulture);
            var prefix = AppSettings.FilenamePrefix.Trim();
            var baseName = prefix.Length == 0 ? dateStr : $"{prefix}_{dateStr}";
            foreach (var c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
            return $"{baseName}.{chosenExt}";
        }

        public static string GetDefaultSaveFolder()
        {
            if (!string.IsNullOrEmpty(AppSettings.SaveFolder) && Directory.Exists(AppSettings.SaveFolder)) return AppSettings.SaveFolder;
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
    }
}
